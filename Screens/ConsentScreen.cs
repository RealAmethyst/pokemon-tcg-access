using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// ConsentFlow drives multiple sub-screens (one per ConsentStep: Terms / Analytics / Migration /
// MigrationProcessing) — each is a MenuGroup under the same ConsentFlow GameObject. The game
// shows exactly one at a time by calling MenuGroup.Open() on the active step and Close() on
// the others.
//
// MenuGroup.Close() does NOT GameObject.SetActive(false) — it just sets isOpen=false and
// disables blocksRaycasts on the CanvasGroup. So `GetComponentsInChildren(includeInactive:false)`
// from ConsentFlow.gameObject keeps returning Selectables from *every* step (migrate / agree /
// etc.) even when only one step is visible. We filter by MenuGroup.IsOpen so the navigator only
// sees the items the player can actually see + interact with right now.
//
// As the player advances through the flow (Terms accept → Analytics → Migration → Done), the
// currently-open MenuGroup changes. Tick detects that and rebuilds the groups so the announce
// reflects the new step. When consent is fully done, the root canvasGroup.blocksRaycasts flips
// to false and we deactivate the handler.
internal sealed class ConsentScreen : IScreenHandler
{
    private static ConsentScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private ConsentFlow? _consent;
    private MenuGroup? _lastOpenMenuGroup;
    private bool _active;

    public string Name => "Consent";

    public ConsentScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ConsentScreen(router);

        var init = AccessTools.Method(typeof(ConsentFlow), "InitializeConsentUI");
        if (init == null)
        {
            Log.Error("ConsentScreen: could not resolve ConsentFlow.InitializeConsentUI");
            return;
        }
        harmony.Patch(init, postfix: new HarmonyMethod(AccessTools.Method(typeof(ConsentScreen), nameof(Init_Postfix))));

        Log.Info("ConsentScreen installed");
    }

    private static void Init_Postfix(ConsentFlow __instance)
    {
        if (_instance == null) return;
        _instance._consent = __instance;
    }

    public static void Poll() => _instance?.TryActivate();

    private void TryActivate()
    {
        if (_active || _consent == null) return;
        var canvasGroup = ReadField<CanvasGroup>(_consent, "canvasGroup");
        if (canvasGroup == null || !canvasGroup.blocksRaycasts) return;
        if (FindOpenMenuGroup() == null) return;

        _active = true;
        _router.Activate(this);
    }

    public void OnEnter()
    {
        BuildForCurrentStep();
    }

    public void OnResume()
    {
        if (!_active) return;
        // A modal closed over us — the step may have advanced while we were paused.
        var openMg = FindOpenMenuGroup();
        if (openMg != _lastOpenMenuGroup)
        {
            BuildForCurrentStep();
        }
        else if (_groups.Count > 0)
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    public void OnExit() { /* preserve state */ }

    public void RespeakAll()
    {
        if (!_active || _consent == null) return;
        var openMg = FindOpenMenuGroup();
        if (openMg != _lastOpenMenuGroup)
        {
            BuildForCurrentStep();
            return;
        }
        if (_lastOpenMenuGroup != null)
        {
            var description = BuildDescription(_lastOpenMenuGroup.gameObject);
            if (!string.IsNullOrWhiteSpace(description))
                _router.Speech.Say(description, interrupt: true);
        }
        if (_groups.Count > 0)
            _groups[_activeGroup].RefocusCurrent(announce: true);
    }

    public string StateSignature()
    {
        if (_consent == null) return "Consent|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        var mg = _lastOpenMenuGroup != null ? _lastOpenMenuGroup.name : "none";
        return $"Consent|{mg}|{_activeGroup}|{idx}";
    }

    public void Tick()
    {
        if (_consent == null)
        {
            _active = false;
            _router.Deactivate(this);   // no-ops unless we're on top
            return;
        }

        var canvasGroup = ReadField<CanvasGroup>(_consent, "canvasGroup");
        if (canvasGroup == null || !canvasGroup.blocksRaycasts)
        {
            // Consent flow has finished — close the handler. Pol will reopen us if the
            // ConsentFlow ever becomes active again (it doesn't in normal flow, but harmless).
            _active = false;
            _router.Deactivate(this);   // no-ops unless we're on top
            return;
        }

        // Step transition detection — Terms → Analytics → Migration etc.
        var openMg = FindOpenMenuGroup();
        if (openMg != null && openMg != _lastOpenMenuGroup)
        {
            BuildForCurrentStep();
            return;
        }

        if (_groups.Count == 0) return;
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            // Tab is a no-op when there's only one group — nothing to cycle to.
            if (_groups.Count > 1)
            {
                var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        _groups[_activeGroup].Tick();
    }

    // Find the MenuGroup that's currently displayed. ConsentFlow opens exactly one at a time.
    private MenuGroup? FindOpenMenuGroup()
    {
        if (_consent == null) return null;
        foreach (var mg in _consent.GetComponentsInChildren<MenuGroup>(includeInactive: false))
        {
            if (mg.IsOpen) return mg;
        }
        return null;
    }

    // Build (or rebuild) the focus groups for whichever ConsentStep is currently visible.
    // Speaks the new step's description (header + body) and focuses the first item.
    private void BuildForCurrentStep()
    {
        if (_consent == null) return;
        var openMg = FindOpenMenuGroup();
        if (openMg == null) return;
        _lastOpenMenuGroup = openMg;

        var allSelectables = openMg.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s.gameObject.activeInHierarchy)
            .ToList();

        _groups.Clear();

        // Detect "is this the Terms step?" by checking whether ConsentFlow's known fields
        // (privacy / tos / tosConfirm) live inside this MenuGroup. If yes → 3 sub-groups.
        var privacy = ReadField<Toggle>(_consent, "privacy");
        var tos = ReadField<Toggle>(_consent, "tos");
        var tosConfirm = ReadField<Button>(_consent, "tosConfirm");

        var isTermsStep = privacy != null && tos != null && tosConfirm != null
                       && privacy.GetComponentInParent<MenuGroup>() == openMg;

        if (isTermsStep)
        {
            AddTermsSubGroups(allSelectables, privacy!, tos!, tosConfirm!);
        }
        else
        {
            AddMenuGroupAsSingleGroup(openMg, allSelectables);
        }

        if (_groups.Count == 0) return;
        _activeGroup = 0;

        var description = BuildDescription(openMg.gameObject);
        _router.SetCurrentTitle(this, description);
        if (!string.IsNullOrWhiteSpace(description))
            _router.Speech.Say(description, interrupt: true);
        _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
    }

    private void AddTermsSubGroups(List<Selectable> termsSelectables, Toggle privacy, Toggle tos, Button tosConfirm)
    {
        var docs = termsSelectables
            .Where(s => s is Button && s != tosConfirm)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (docs.Count > 0)
        {
            var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
            g.SetItems(docs);  // axes auto-detected from positions (review buttons sit side-by-side → horizontal)
            _groups.Add(g);
        }

        var toggles = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
        toggles.SetItems(new List<Selectable> { tos, privacy });  // stacked → vertical
        _groups.Add(toggles);

        var cont = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
        cont.SetItems(new List<Selectable> { tosConfirm });  // single item, axes don't matter
        _groups.Add(cont);
    }

    private void AddMenuGroupAsSingleGroup(MenuGroup menuGroup, List<Selectable> selectables)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
        g.SetItems(selectables.OrderBy(s => ReadingOrder(s.transform)).ToList());  // axes auto-detected
        _groups.Add(g);
    }

    private static T? ReadField<T>(ConsentFlow consent, string name) where T : class
    {
        var field = typeof(ConsentFlow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(consent) as T;
    }

    private static string BuildDescription(GameObject scope)
    {
        var allTmps = scope.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Where(t => !string.IsNullOrWhiteSpace(t.text))
            .Where(t => t.GetComponentInParent<Selectable>() == null)
            .ToList();
        if (allTmps.Count == 0) return string.Empty;

        var maxFont = allTmps.Max(t => t.fontSize);
        var headerCluster = allTmps
            .Where(t => t.fontSize >= maxFont * 0.4f)
            .OrderBy(t => ReadingOrder(t.transform))
            .Select(t => t.text.Trim());
        return string.Join(". ", headerCluster);
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
