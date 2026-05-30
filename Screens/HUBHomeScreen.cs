using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Coroutines;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// HOME screen — the "Play" screen. Driver: HUBPlayScreenController (global namespace,
// TPCI.RainierClient/HUBPlayScreenController.cs), a HUBScreenController like the Profile
// screen, so the lifecycle hook is identical: postfix OnActivate(HUBGroupController) /
// OnDeactivate(HUBGroupController).
//
// Layout, in visual top-to-bottom order (each section navigated left/right; Up/Down escapes
// between sections, Tab cycles them — the same section-stack model as HUBProfileScreen):
//   1. HUB top tab strip (HOME / DECKS / PROFILE / SHOP / BATTLE PASS / CARD DEX) + the
//      overflow "⋮" button — all on HUBTopController_L (discovered exactly as Profile does).
//   2. Game-mode sub-tabs (RANKED / CASUAL / LEARN / BUILD & BATTLE) — PageDragTab toggles
//      under gameModeController. Radio/tab semantics (Enter selects, never un-toggles).
//   3. Main content — PLAY button (HUBRankedController.playButton; interactable only with a
//      valid active deck) plus any other active buttons under the play screen.
//   4. Bottom shortcuts (PROFILE / DECKS / CARD DEX) under gameModeController.BottomElements.
//
// PHASE 1 covers every standard uGUI Selectable (buttons + the tab toggles). The format-hex
// carousel (MenuCarouselUi — not a Selectable), the currency readouts, and the rank/season +
// deck-validity status line are info/non-standard surfaces handled in a later phase; the
// per-section logging below is what tells us their exact runtime structure.
internal sealed class HUBHomeScreen : IScreenHandler
{
    private static HUBHomeScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private HUBPlayScreenController? _controller;
    private bool _active;

    private bool _hasAnnouncedAtAll;
    private string _structureAtPause = string.Empty;
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;

    private int _tabGroupIndex = -1;
    private int _mainGroupIndex = -1;

    public string Name => "Home";

    public HUBHomeScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBHomeScreen(router);

        var activate = AccessTools.Method(typeof(HUBPlayScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBHomeScreen: could not resolve HUBPlayScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBHomeScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBPlayScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBHomeScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBHomeScreen installed");
    }

    private static void Activate_Postfix(HUBPlayScreenController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBPlayScreenController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter()
    {
        SubscribeToFtue();

        // FTUE-focused element wins: if the tutorial has highlighted a specific button on us,
        // point our nav at just that (mirrors HUBProfileScreen).
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning)
        {
            StartFtueSettlementWait();
            return;
        }

        Announce();
    }

    public void OnResume()
    {
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        // Rebuild because the underlying screen may have changed while we were paused (a sub-tab
        // switched the content, the HUB top strip became active, a popup closed, etc.).
        RebuildGroups();
        if (_groups.Count == 0) return;

        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning)
        {
            StartFtueSettlementWait();
            return;
        }

        var nowStructure = StructureSignature();
        if (nowStructure != _structureAtPause)
            Announce();
        else
            _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
    }

    public void RespeakAll()
    {
        if (_groups.Count == 0) return;
        Announce();
    }

    public string StateSignature()
    {
        if (_controller == null) return "Home|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"Home|{_activeGroup}|{idx}|{StructureSignature()}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            if (_active)
            {
                _active = false;
                _router.Deactivate(this);
            }
            return;
        }
        if (_groups.Count == 0) return;
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: groups are built in visual top-to-bottom order. Up at the top row of
        // a group jumps to the section above and lands on its bottom row (preserving column);
        // Down at the bottom row jumps to the section below and lands on its top row.
        var nav = _groups[_activeGroup];
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        nav.Tick();
    }

    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            // Speak the screen name once, then queue the focused item after it.
            _router.Speech.Say(Name, interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    private string StructureSignature() => string.Join(",", _groups.Select(g => g.Count));

    // ---- Group discovery -------------------------------------------------------------------

    private void RebuildGroups()
    {
        _groups.Clear();
        _tabGroupIndex = -1;
        _mainGroupIndex = -1;
        if (_controller == null) return;

        var gmc = ReadField<HUBGameModeController>(_controller, "gameModeController");
        var known = new HashSet<Selectable>();

        // --- Group 1: HUB top tab strip + overflow "⋮" button (top bar, HUBTopController_L). ---
        // Same discovery HUBProfileScreen uses: each PersistentNavButtonGroup.navButton is a
        // public Button; filter on the button's own active/interactable state (not the
        // container's) because FTUE animates the strip's container while leaving HOME clickable.
        var topItems = new List<Selectable>();
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            if (navGroups != null)
            {
                foreach (var pnb in navGroups)
                {
                    var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                    if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
                        topItems.Add(btn);
                }
            }
            // Overflow "⋮": settingsButton is a Transform that carries the Button.
            var settingsT = ReadField<Transform>(topController, "settingsButton");
            var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
            if (overflow != null && overflow.isActiveAndEnabled && overflow.IsInteractable())
                topItems.Add(overflow);
        }
        if (topItems.Count > 0)
        {
            var ordered = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();
            foreach (var s in ordered) known.Add(s);
            AddGroup(ordered);
        }

        // --- Group 2: game-mode sub-tabs (Ranked / Casual / Learn / Build & Battle). ---
        // Each PageDragTab sits next to a real Toggle (PageDragTab.toggle); they share a
        // ToggleGroup, so radio/tab semantics apply.
        if (gmc != null)
        {
            var tabs = gmc.GetComponentsInChildren<PageDragTab>(includeInactive: false)
                .Select(t => t.toggle)
                .Where(t => t != null && t.isActiveAndEnabled)
                .Cast<Selectable>()
                .OrderBy(s => ReadingOrder(s.transform))
                .ToList();
            foreach (var s in tabs) known.Add(s);
            if (tabs.Count > 0)
            {
                AddGroup(tabs, resolver: TabLabel, radioMode: true, groupName: "Game mode");
                _tabGroupIndex = _groups.Count - 1;
            }
        }

        // Bottom-shortcuts subtree (Profile / Decks / Card Dex) — split out into its own
        // section and excluded from the main sweep. Hidden in Learn / Build & Battle.
        Transform? bottomT = null;
        if (gmc != null)
        {
            var bottom = ReadField<GameObject>(gmc, "BottomElements");
            if (bottom != null && bottom.activeInHierarchy) bottomT = bottom.transform;
        }

        // --- Group 3: main content (PLAY + carousel arrows + anything else). ---
        // Sweep the play screen and the game-mode controller (it may be a sibling rather than a
        // child), plus belt-and-suspenders lookups for the PLAY button, which is wired on
        // HUBRankedController and parented under HUBGameModeController.PlayButtonContainer.
        var candidates = new List<Selectable>();
        candidates.AddRange(_controller.GetComponentsInChildren<Selectable>(includeInactive: false));
        if (gmc != null)
            candidates.AddRange(gmc.GetComponentsInChildren<Selectable>(includeInactive: false));
        var playContainer = gmc != null ? ReadField<GameObject>(gmc, "PlayButtonContainer") : null;
        if (playContainer != null && playContainer.activeInHierarchy)
            candidates.AddRange(playContainer.GetComponentsInChildren<Selectable>(includeInactive: false));
        var ranked = Object.FindAnyObjectByType<HUBRankedController>();
        if (ranked != null)
        {
            var play = ReadField<Button>(ranked, "playButton");
            if (play != null) candidates.Add(play);
        }

        var main = candidates
            .Where(s => s != null)
            .Distinct()
            .Where(s => !known.Contains(s))
            .Where(s => s is Button || s is Toggle)
            .Where(s => s.isActiveAndEnabled)
            .Where(s => bottomT == null || !s.transform.IsChildOf(bottomT))
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (main.Count > 0)
        {
            foreach (var s in main) known.Add(s);
            AddGroup(main);
            _mainGroupIndex = _groups.Count - 1;
        }

        // --- Group 4: bottom shortcuts (Profile / Decks / Card Dex). ---
        if (bottomT != null)
        {
            var bottomItems = bottomT.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => s is Button || s is Toggle)
                .Where(s => s.isActiveAndEnabled)
                .Where(s => !known.Contains(s))
                .OrderBy(s => ReadingOrder(s.transform))
                .ToList();
            if (bottomItems.Count > 0)
                AddGroup(bottomItems);
        }
    }

    // FTUE has highlighted a specific button on this screen: collapse to a single-item nav of
    // just that Selectable, matching what the tutorial's input blocker allows clicking.
    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;
        _tabGroupIndex = -1;
        _mainGroupIndex = -1;

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        AddGroup(new List<Selectable> { target });
        _activeGroup = 0;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    // Default focus: the game-mode tab strip if present (tells the user their current mode),
    // else the main content (PLAY), else the first group.
    private int PickDefaultGroup()
    {
        if (_tabGroupIndex >= 0 && _tabGroupIndex < _groups.Count) return _tabGroupIndex;
        if (_mainGroupIndex >= 0 && _mainGroupIndex < _groups.Count) return _mainGroupIndex;
        return 0;
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null,
                          bool radioMode = false, string? groupName = null)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, RadioToggleMode = radioMode, GroupName = groupName };
        g.SetItems(items, resolver);
        _groups.Add(g);
        var names = string.Join(", ", items.Take(20).Select(s => s != null ? s.gameObject.name : "<null>"));
        var extra = items.Count > 20 ? $" (+{items.Count - 20} more)" : string.Empty;
        Log.Info($"Home group {_groups.Count}: {items.Count} items — {names}{extra}");
    }

    // Tab label: localized tab name + selected-state ("Ranked, selected" / "Casual, not
    // selected"), plus ", unavailable" for a feature-gated tab (e.g. Build & Battle with no
    // active event). The Selectable is the Toggle living on the PageDragTab GameObject.
    private static string TabLabel(Selectable s)
    {
        string? label = null;
        var tab = s.GetComponent<PageDragTab>();
        if (tab != null)
        {
            var id = tab.GetLocTextID();
            if (!string.IsNullOrEmpty(id))
            {
                var loc = LocalizationManager.Instance?.GetString(id);
                if (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)")) label = loc;
            }
        }
        if (string.IsNullOrEmpty(label))
        {
            var tmp = s.GetComponentInChildren<TMP_Text>(includeInactive: false);
            label = tmp != null && !string.IsNullOrEmpty(tmp.text) ? tmp.text : s.gameObject.name;
        }

        var state = (s is Toggle t && t.isOn) ? "selected" : "not selected";
        return s.interactable ? $"{label}, {state}" : $"{label}, {state}, unavailable";
    }

    // ---- FTUE coordination (ported from HUBProfileScreen) ----------------------------------

    private void SubscribeToFtue()
    {
        if (_subscribedToFtue) return;
        FTUEHook.FocusedElementChanged += OnFtueFocusChanged;
        _subscribedToFtue = true;
    }

    private void OnFtueFocusChanged()
    {
        if (!_active) return;
        if (_controller == null) return;
        if (FTUEHook.FocusedElement == _lastFtueTarget) return;
        if (FTUEHook.FtueIsRunning)
            RebuildForFtue();
    }

    private void StartFtueSettlementWait()
    {
        CancelFtueSettlementWait();
        _ftueWaitCoroutine = AnywhereCoroutine.Instance.unmanaged.StartCoroutine(WaitForFtueToSettle());
    }

    private void CancelFtueSettlementWait()
    {
        if (_ftueWaitCoroutine != null)
        {
            AnywhereCoroutine.Instance.unmanaged.StopCoroutine(_ftueWaitCoroutine);
            _ftueWaitCoroutine = null;
        }
    }

    // Wait for the FTUE state machine to commit to one of two outcomes after we enter HOME:
    // a speech-claiming state takes over (stay silent, FTUE owns speech) or the animator
    // settles with nothing running (announce normally). No frame counting — yield until a
    // native condition resolves, the way the game's own FTUE coroutines coordinate.
    private IEnumerator WaitForFtueToSettle()
    {
        var mgr = FTUEManager.instance;
        var animator = mgr != null ? mgr.GetComponent<Animator>() : null;

        while (FTUEHook.FtueIsRunning && animator != null && !animator.enabled)
            yield return null;

        yield return null;

        if (FTUEHook.FtueSpeechClaimingStateActive)
        {
            Log.Info("Home: FTUE speech-claim active, staying silent");
            _ftueWaitCoroutine = null;
            yield break;
        }

        Log.Info("Home: FTUE settled with no speech-claim, announcing");
        _ftueWaitCoroutine = null;
        if (_active && _groups.Count > 0) Announce();
    }

    // ---- helpers ---------------------------------------------------------------------------

    // Walk the type hierarchy reading the field at any visibility — serialized Unity fields are
    // conventionally [SerializeField] private but plenty of game refs are plain public
    // (PersistentNavButtonGroup.navButton). Reading one visibility silently misses the others.
    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
