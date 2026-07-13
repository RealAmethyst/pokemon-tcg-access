using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Generic modal popup — ModalDrawer / ModalPopup, both subclasses of abstract ModalElementContainer.
// Built via RainierManager.genericModalDrawer / genericModalPopup with SetupModalDrawerBlur(...).
// Examples: "SKIP MIGRATION?" with CANCEL / DON'T MIGRATE + "DON'T SHOW THIS AGAIN" toggle;
// in-settings language-change confirm; misc action drawers.
//
// We split the modal's Selectables into two Tab groups:
//   1. Content area — toggles, custom Selectables in the body (e.g. "DON'T SHOW THIS AGAIN").
//   2. Bottom action buttons — from the `bottomButtons` serialized list (CANCEL / CONFIRM / etc.).
// Tab cycles between them so a toggle's state is clearly separate from a button press.
//
// Excluded from nav:
//   - backgroundBlockerButton (tap-outside affordance, not a real action — handled via OnBack)
//   - Scrollbar instances (scroll handles aren't menu items; they'd otherwise show up because
//     ModalElementContainer has a ScrollRect for content overflow)
internal sealed class ModalContainerScreen : IScreenHandler
{
    private static ModalContainerScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private ModalElementContainer? _modal;

    public string Name => "Modal";

    public ModalContainerScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ModalContainerScreen(router);

        var openMethod = AccessTools.Method(typeof(ModalElementContainer), nameof(ModalElementContainer.Open));
        if (openMethod == null)
        {
            Log.Error("ModalContainerScreen: could not resolve ModalElementContainer.Open");
            return;
        }
        harmony.Patch(openMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(ModalContainerScreen), nameof(Open_Postfix))));

        var closeMethod = AccessTools.Method(typeof(ModalElementContainer), nameof(ModalElementContainer.Close));
        if (closeMethod != null)
            harmony.Patch(closeMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(ModalContainerScreen), nameof(Close_Postfix))));

        Log.Info("ModalContainerScreen installed");
    }

    private static void Open_Postfix(ModalElementContainer __instance)
    {
        if (_instance == null) return;
        _instance._modal = __instance;
        _instance._router.Activate(_instance);
    }

    private static void Close_Postfix(ModalElementContainer __instance)
    {
        if (_instance == null || _instance._modal != __instance) return;
        _instance._router.Deactivate(_instance);
        _instance._modal = null;
    }

    public void OnEnter()
    {
        if (_modal == null) return;

        var titleText = ReadField<LocText>(_modal, "titleText");
        var title = ReadTmpText(titleText) ?? "";
        _router.SetCurrentTitle(this, title);

        var bodyText = BuildBodyText(titleText);

        var description = BuildPreamble(title, bodyText);

        // Discover Selectables; exclude tap-outside blocker and scroll handles.
        var blockerButton = ReadField<Button>(_modal, "backgroundBlockerButton");
        var bottomButtonList = ReadField<List<Button>>(_modal, "bottomButtons") ?? new List<Button>();
        var bottomSet = new HashSet<Selectable>(bottomButtonList.Cast<Selectable>());

        var contentSelectables = _modal.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s != blockerButton)
            .Where(s => !(s is Scrollbar))
            .Where(s => s.gameObject.activeInHierarchy)
            .Where(s => !bottomSet.Contains(s))
            // Skip card-graphic selectables: the Mulligans reveal (and any modal embedding card images)
            // puts clickable CardUI cards in the body. They're click-to-zoom graphics, NOT menu controls
            // — navigating them read "Card UI" junk and buried the real DONE button (the player couldn't
            // tell there was a Done or how to proceed). We read card text elsewhere (MulliganRevealScreen
            // announces the revealed cards), so drop them here; the bottom DONE/action buttons remain.
            .Where(s => s.GetComponentInParent<CardUI>() == null)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();

        var bottomSelectables = bottomButtonList
            .Where(b => b != null && b.gameObject.activeInHierarchy)
            .Cast<Selectable>()
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();

        _groups.Clear();

        // Reading order: content first (typically toggles/text near the top), buttons at the bottom.
        if (contentSelectables.Count > 0)
        {
            var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
            g.SetItems(contentSelectables);  // axes auto-detected from positions
            _groups.Add(g);
        }
        if (bottomSelectables.Count > 0)
        {
            var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = null };
            g.SetItems(bottomSelectables);  // bottom-button row → horizontal in most modals
            _groups.Add(g);
        }

        // Esc / Backspace = dismiss only when the game's design intent allows it.
        var state = ReadBlockerState(_modal);
        Action? onBack = state == ModalElementContainer.BlockerState.CLOSE
            ? () => _modal.Close()
            : (Action?)null;
        foreach (var g in _groups) g.OnBack = onBack;

        if (_groups.Count == 0) return;
        _activeGroup = 0;

        // Speak the body (barging in), then QUEUE the focused button after it as its own announce —
        // two separate utterances, so the button reads as a distinct, independently-navigable item.
        // FocusFirst's interruptPrior:false is what makes the button queue rather than cut the body off.
        if (!string.IsNullOrWhiteSpace(description))
            _router.Speech.Say(description!, interrupt: true);
        _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
    }

    public void OnResume()
    {
        if (_modal == null || _groups.Count == 0) return;
        _groups[_activeGroup].RefocusCurrent(announce: true);
    }

    public void OnExit() { /* preserve state if another modal stacks over us */ }

    public void RespeakAll()
    {
        if (_modal == null || _groups.Count == 0) return;
        var titleText = ReadField<LocText>(_modal, "titleText");
        var title = ReadTmpText(titleText) ?? "";
        var bodyText = BuildBodyText(titleText);
        var description = BuildPreamble(title, bodyText);
        if (!string.IsNullOrWhiteSpace(description))
            _router.Speech.Say(description!, interrupt: true);
        _groups[_activeGroup].RefocusCurrent(announce: true, interrupt: false);
    }

    public string StateSignature()
    {
        if (_modal == null) return "Modal|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"Modal|{_activeGroup}|{idx}";
    }

    public void Tick()
    {
        if (_modal == null)
        {
            _router.Deactivate(this);
            return;
        }
        if (_groups.Count == 0) return;
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            // Tab no-op when only one group.
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

    private string BuildPreamble(string title, string body)
    {
        var parentTitle = _router.PausedTitle;
        var titleRedundant = string.IsNullOrWhiteSpace(title)
            || string.Equals(title.Trim(), parentTitle?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (titleRedundant) return string.IsNullOrWhiteSpace(body) ? title : body;
        if (string.IsNullOrWhiteSpace(body)) return title;
        return $"{title}. {body}";
    }

    // The modal's message text, gathered from three sources and deduped by GameObject:
    //  0. The dedicated `bodyText` LocText field. This is the authoritative body for popups built via
    //     SetupModalPopup with a bodyLocID (e.g. the logout confirm — "overflow_loggedout_body"). It
    //     MUST be read directly: the whole modal sits under the blocker Button, so the heuristic
    //     container scan below (which rejects any text under a Selectable, to skip button labels) drops
    //     it, and it isn't a CurrentOption either — so without this the body went unspoken.
    //  1. Plain TMP children of the container — excludes button labels via the Selectable-ancestor filter.
    //  2. The modal's CurrentOptions — the spawned message options (SimpleMessageModalOption, etc.). The
    //     match-action confirmations ("you still have selections", concede warning) and the
    //     language/resolution confirms put their body in such an option, and the container scan misses it
    //     for the same under-the-blocker reason. For PASSIVE options (no Selectable on the option root =
    //     not a button/toggle) we take the text directly, ignoring that ancestor filter.
    //  In every case, if a LocText hasn't pushed to its TMP yet we resolve it from its resourceId, and
    //  hidden text is never read — see Consider.
    private string BuildBodyText(LocText? titleText)
    {
        if (_modal == null) return "";
        var seen = new HashSet<int>();
        var parts = new List<(float order, string text)>();

        void Consider(TMP_Text t, bool allowUnderSelectable)
        {
            if (t == null) return;
            // A hidden text isn't on screen, so it isn't part of the modal's message. The CurrentOptions
            // pass reaches inactive children by design (it looks for text the option never activated), and
            // an option routinely hides whole sub-widgets — an AttackOption with no energy cost to show
            // does energyCostDisplay.SetActive(false). Speaking those would invent content the sighted
            // player can't see.
            if (!t.gameObject.activeInHierarchy) return;
            if (titleText != null && t.gameObject == titleText.gameObject) return;
            // Filter BEFORE marking seen: the container pass (allowUnderSelectable:false) rejects a
            // message that sits under the blocker Button, and the CurrentOptions pass must still be able
            // to pick it up — so a rejected text must NOT be recorded as already-seen.
            if (!allowUnderSelectable && t.GetComponentInParent<Selectable>() != null) return;
            if (!seen.Add(t.GetInstanceID())) return;
            string txt = t.text;
            if (string.IsNullOrWhiteSpace(txt))
            {
                // The loc id is the only fallback worth trying. NOT LocText.CurrentText: it lives on this
                // same GameObject, so it returns this very TMP's .text — the empty string we already have —
                // and when its TMP field is unset it dereferences a `uiText` that only LocText.Initialize()
                // assigns. On a modal option that was Instantiated this frame and never had text pushed
                // into it, both fields are null and the getter throws. That NRE unwound out of OnEnter,
                // through ModalElementContainer.Open, and killed the match coroutine that was opening the
                // overlay (see ScreenRouter's class comment).
                var loc = t.GetComponent<LocText>();
                if (loc != null && !string.IsNullOrEmpty(loc.resourceId))
                    txt = LocalizationHook.Get(loc.resourceId) ?? "";
            }
            if (!string.IsNullOrWhiteSpace(txt)) parts.Add((ReadingOrder(t.transform), txt.Trim()));
        }

        // 0. Dedicated body field (allow under-Selectable — it lives under the blocker like everything else).
        var bodyLoc = ReadField<LocText>(_modal, "bodyText");
        if (bodyLoc != null)
        {
            var bodyTmp = bodyLoc.GetComponent<TMP_Text>() ?? bodyLoc.GetComponentInChildren<TMP_Text>(includeInactive: true);
            if (bodyTmp != null) Consider(bodyTmp, allowUnderSelectable: true);
        }

        foreach (var t in _modal.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            Consider(t, allowUnderSelectable: false);

        if (_modal.CurrentOptions != null)
            foreach (var opt in _modal.CurrentOptions)
            {
                if (opt == null || opt.GetComponent<Selectable>() != null) continue;   // skip buttons / toggles
                foreach (var t in opt.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                    Consider(t, allowUnderSelectable: true);
            }

        return string.Join(". ", parts.OrderBy(p => p.order).Select(p => p.text));
    }

    private static T? ReadField<T>(ModalElementContainer modal, string name) where T : class
    {
        var field = typeof(ModalElementContainer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(modal) as T;
    }

    private static ModalElementContainer.BlockerState ReadBlockerState(ModalElementContainer modal)
    {
        var field = typeof(ModalElementContainer).GetField("backgroundBlockerState", BindingFlags.Instance | BindingFlags.NonPublic);
        var val = field?.GetValue(modal);
        return val is ModalElementContainer.BlockerState bs ? bs : ModalElementContainer.BlockerState.BLOCK;
    }

    private static string? ReadTmpText(Component? c)
    {
        if (c == null) return null;
        var tmp = c.GetComponent<TMP_Text>() ?? c.GetComponentInChildren<TMP_Text>();
        return tmp?.text;
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
