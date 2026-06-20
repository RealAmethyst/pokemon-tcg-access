using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The Build & Battle popup family — the dialog-style OverlayScreenGroups the buy-in / event flow throws
// up (BuildAndBattlePopupManager). All are title/body/buttons over a marketing splash, so one handler
// reads them generically instead of one bespoke handler per popup:
//   • BuildAndBattlePurchaseOverlay     — "CONFIRM EXCHANGE" buy-in (title/body/cost/BACK/EXCHANGE)
//   • BuildAndBattleGenericOverlay      — event closed / no active event / forfeit confirm / almost over
//   • BuildAndBattleBoxOpeningPopup     — box-opening reveal; its proceed button appears AFTER the anim
//   • BuildAndBattleDeckInspectorController — the starter-deck inspector (advance-able; full card review TODO)
// Booster opening uses ContentItemGrantingOverlay → already handled by ItemGrantScreen.
//
// One guarded postfix on OverlayScreenGroup.Activate() (the [[feedback-overlay-hook-hazard]]-safe
// base-typed pattern). We read every non-button text (title, body, cost, revealed card name) and present
// every active button; a per-tick button poll catches buttons that only appear after an animation, and
// the first announce is deferred one tick so values set right after Activate (the cost) are populated.
internal sealed class BnbPopupScreen : IScreenHandler
{
    private static BnbPopupScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private OverlayScreenGroup? _overlay;
    private bool _active;
    private bool _announcePending;
    private string _lastButtonSig = "";

    public string Name => "Build and Battle";

    public BnbPopupScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Cancel;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new BnbPopupScreen(router);
        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "Activate");
        if (activate == null) { Log.Error("BnbPopupScreen: OverlayScreenGroup.Activate not found"); return; }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(BnbPopupScreen), nameof(Activate_Postfix))));
        Log.Info("BnbPopupScreen installed");
    }

    private static bool IsHandled(OverlayScreenGroup o)
        => o is BuildAndBattlePurchaseOverlay or BuildAndBattleGenericOverlay
             or BuildAndBattleBoxOpeningPopup or BuildAndBattleDeckInspectorController;

    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || !IsHandled(__instance)) return;
        _instance._overlay = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else { _instance.Rebuild(); _instance.Announce(); }
    }

    public void OnEnter() { Rebuild(); _lastButtonSig = ButtonSig(); _announcePending = true; }
    public void OnResume() { Rebuild(); _lastButtonSig = ButtonSig(); Announce(); }
    public void OnExit() { }
    public void RespeakAll() { Rebuild(); Announce(); }

    public string StateSignature() => _overlay == null ? "Bnb|dead" : $"Bnb|{_overlay.GetType().Name}|{_nav.Count}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            if (_active) { _active = false; _router.Deactivate(this); _overlay = null; }
            return;
        }
        if (!Application.isFocused) return;

        // Deferred first announce (so the cost — set right after Activate — is ready).
        if (_announcePending) { _announcePending = false; Announce(); return; }

        // Buttons can appear after an animation (the box-opening proceed button), so re-scan; when an
        // action first becomes available, re-read the full content + that action (by then the card is
        // revealed). Stable button set → ordinary navigation.
        var sig = ButtonSig();
        if (sig != _lastButtonSig)
        {
            bool hadNone = _nav.Count == 0;
            _lastButtonSig = sig;
            Rebuild();
            if (hadNone && _nav.Count > 0) Announce();
            return;
        }
        _nav.Tick();
    }

    // Escape: prefer an explicit close/cancel/back button; otherwise a no-op (don't auto-proceed a buy-in).
    private void Cancel()
    {
        var close = Buttons().FirstOrDefault(b => IsCloseName(b.gameObject.name));
        if (close != null && close.isActiveAndEnabled) close.onClick.Invoke();
    }

    private void Rebuild()
    {
        if (_overlay == null) return;
        var buttons = Buttons().Cast<Selectable>().OrderBy(s => ReadingOrder(s.transform)).ToList();
        _nav.SetItems(buttons);
        _nav.AllowVertical = true; _nav.AllowHorizontal = true;   // dialogs vary (side-by-side or stacked)
    }

    private void Announce()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;
        var text = HeaderText();
        _router.Speech.Say(string.IsNullOrWhiteSpace(text) ? Name : text, interrupt: true);
        if (_nav.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"BnB popup [{_overlay.GetType().Name}]: {text} | {_nav.Count} buttons");
    }

    private List<Button> Buttons()
        => _overlay == null ? new List<Button>()
           : _overlay.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled).ToList();

    private string ButtonSig()
    {
        var bs = Buttons();
        return bs.Count + ":" + string.Join(",", bs.Select(b => b.gameObject.name));
    }

    // Every readable text that isn't a button/control label (title, body, cost, revealed card name), in
    // reading order. Skipping text under a Selectable keeps button labels out (they're presented as
    // buttons) so they aren't read twice.
    private string HeaderText()
    {
        if (_overlay == null) return "";
        var seen = new HashSet<string>();
        var parts = new List<string>();
        foreach (var t in _overlay.GetComponentsInChildren<TMP_Text>(includeInactive: false)
                                  .OrderBy(t => ReadingOrder(t.transform)))
        {
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            if (t.GetComponentInParent<Selectable>() != null) continue;
            var s = t.text?.Trim();
            if (string.IsNullOrWhiteSpace(s) || !s.Any(char.IsLetterOrDigit)) continue;
            if (seen.Add(s!)) parts.Add(s!);
        }
        return string.Join(". ", parts);
    }

    private static bool IsCloseName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("close") || n.Contains("cancel") || n.Contains("back") || n.Contains("deny") || n.Contains("exit");
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
