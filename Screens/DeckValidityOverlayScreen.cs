using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Deck validation drawer (enhanced in 1.5.0) — driver: DeckValidityOverlay (an OverlayScreenGroup)
// shown on Test, or on Exit when the deck is invalid (HUBEditDeckController.OpenDeckValidationPopup
// WithMode / CanExit). Previously a blind player only heard "invalid"; this reads WHY — the bottom
// summary plus the per-rule / per-card detail rows the drawer now spawns (DeckValidationRowTextBuilder,
// the 1.5.0 addition). Buttons: a left cancel/close and (on exit-invalid) a red Exit button.
//
// DeckValidityOverlay DOES override OnActivate, so we patch that override directly (no base-cast
// hazard — see [[feedback-overlay-hook-hazard]]). Dismissal is the Tick backstop.
internal sealed class DeckValidityOverlayScreen : IScreenHandler
{
    private static DeckValidityOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private DeckValidityOverlay? _overlay;
    private bool _active;

    public string Name => "Deck validation";

    public DeckValidityOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new DeckValidityOverlayScreen(router);

        var activate = AccessTools.Method(typeof(DeckValidityOverlay), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("DeckValidityOverlayScreen: could not resolve DeckValidityOverlay.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(DeckValidityOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("DeckValidityOverlayScreen installed");
    }

    private static void Activate_Postfix(DeckValidityOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "DeckValidity|dead" : $"DeckValidity|{_nav.Index}|{Message()}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            if (_active) { _active = false; _router.Deactivate(this); _overlay = null; }
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    // Escape = cancel (stay in the editor); the red Exit button (when present) is reached by arrow + Enter.
    private void Close() => _overlay?.Deactivate();

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        _nav.SetItems(buttons);
        _nav.AllowHorizontal = true;
        if (!announce) return;

        var msg = Message();
        _router.Speech.Say(string.IsNullOrWhiteSpace(msg) ? Name : msg!, interrupt: true);
        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The high-level summary ("This deck isn't legal… please address the following") plus the detail
    // rows the drawer spawns (each invalid rule / card). Both are the game's own localized text.
    private string Message()
    {
        if (_overlay == null) return Name;
        var sb = new StringBuilder();
        Append(sb, LocOf(ReadField<LocText>(_overlay, "bottomSummary")));

        var drawer = ReadField<Component>(_overlay, "deckValidityDrawer");
        if (drawer != null)
            foreach (var t in drawer.GetComponentsInChildren<TMP_Text>(includeInactive: false))
                Append(sb, t.text);

        return sb.Length == 0 ? Name : $"{Name}. {sb}";
    }

    private static void Append(StringBuilder sb, string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (sb.Length > 0) sb.Append(". ");
        sb.Append(s!.Trim());
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static T? ReadField<T>(object inst, string name) where T : class
    {
        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = inst.GetType(); t != null; t = t.BaseType)
        {
            var fi = t.GetField(name, f);
            if (fi != null) return fi.GetValue(inst) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000 + p.x;
    }
}
