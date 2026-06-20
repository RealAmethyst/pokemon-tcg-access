using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The InfoPopup family — a header + body (+ optional extra blocks) overlay with a close X. Covers:
//   • InfoPopup (base) — the League Tower season info (ⓘ → HUBLeagueTowerController.OpenSeasonInfo).
//   • CasualModeInfoPopup : InfoPopup — the "WELCOME TO CASUAL MODE" popup, auto-shown on entering
//     Casual mode (HUBGameModeController) and via the mode info button. Adds a third text block
//     (bottomText) describing the format.
//
// One hook covers both: a postfix on OverlayScreenGroup.Activate() with a base-typed, guarded
// `__instance is InfoPopup` — the approved pattern from [[feedback-overlay-hook-hazard]] (NOT a
// subclass-typed patch, which would bad-cast for other overlays). CasualModeInfoPopup overrides
// Activate() to set its three texts THEN calls base.Activate(), so by the time the base method (the
// patched one) runs, every block is populated. We read ALL active LocText under the popup in reading
// order, so the extra blocks (bottomText, and any future subclass field) are spoken without per-type
// reflection. Close = the popup's own Deactivate (Escape / the X); a Tick backstop releases us.
internal sealed class InfoPopupScreen : IScreenHandler
{
    private static InfoPopupScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private InfoPopup? _popup;
    private bool _active;

    public string Name => "Information";

    public InfoPopupScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new InfoPopupScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "Activate");
        if (activate == null) { Log.Error("InfoPopupScreen: OverlayScreenGroup.Activate not found"); return; }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(InfoPopupScreen), nameof(Activate_Postfix))));
        Log.Info("InfoPopupScreen installed");
    }

    // Base-typed + guarded: the patch lands on OverlayScreenGroup.Activate (shared by every overlay),
    // so the parameter MUST be the base type; we act only for InfoPopup instances.
    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not InfoPopup popup) return;
        _instance._popup = popup;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _popup == null ? "Info|dead" : $"Info|{AllText()}";

    public void Tick()
    {
        if (_popup == null || !_popup.gameObject.activeInHierarchy)
        {
            if (_active) { _active = false; _router.Deactivate(this); _popup = null; }
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    private void Close() => _popup?.Deactivate();

    private void Rebuild(bool announce)
    {
        if (_popup == null || !_popup.gameObject.activeInHierarchy) return;

        var buttons = _popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled).Cast<Selectable>()
            .OrderBy(s => ReadingOrder(s.transform)).ToList();
        _nav.SetItems(buttons);
        _nav.ForceFlatVertical();
        if (!announce) return;

        var text = AllText();
        _router.Speech.Say(string.IsNullOrWhiteSpace(text) ? Name : text, interrupt: true);
        // Keep the close button reachable (Enter / Escape) without reading it loudly after the content.
        if (buttons.Count > 0) _nav.FocusFirst(announce: false, interruptPrior: false);
    }

    // Every readable text block of the popup (header, body, and any extra like the Casual format note),
    // in reading order so the title comes first. Reading all active LocText keeps this subclass-agnostic.
    private string AllText()
    {
        if (_popup == null) return "";
        var seen = new HashSet<string>();
        var parts = new List<string>();
        foreach (var loc in _popup.GetComponentsInChildren<LocText>(includeInactive: false)
                                  .OrderBy(t => ReadingOrder(t.transform)))
        {
            if (loc == null || !loc.gameObject.activeInHierarchy) continue;
            var s = loc.CurrentText;
            if (string.IsNullOrWhiteSpace(s)) continue;
            s = s.Trim();
            if (seen.Add(s)) parts.Add(s);
        }
        return string.Join(". ", parts);
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
