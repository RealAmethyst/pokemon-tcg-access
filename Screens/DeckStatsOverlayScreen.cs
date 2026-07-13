using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Deck Stats overlay (NEW in game 1.5.0) — driver: DeckStatsOverlayController (an OverlayScreenGroup)
// opened from the deck editor's Stats button (HUBEditDeckController.OpenStatsMenu -> SetDeckInfo ->
// Activate). It shows the deck's win/loss record (DeckInfoHelper.GetWinLossInfo) plus a Reset Stats
// button whose confirm is a ModalElementContainer (already handled by ModalContainerScreen). Silent
// without a handler, so we announce the record on open and navigate its buttons.
//
// DeckStatsOverlayController DOES override OnActivate, so we patch that override directly — no
// base-cast hazard (see [[feedback-overlay-hook-hazard]]). Dismissal is the Tick backstop (the
// overlay GameObject goes inactive on close).
internal sealed class DeckStatsOverlayScreen : IScreenHandler
{
    private static DeckStatsOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private DeckStatsOverlayController? _overlay;
    private bool _active;

    public string Name => "Deck stats";

    public DeckStatsOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new DeckStatsOverlayScreen(router);

        var activate = AccessTools.Method(typeof(DeckStatsOverlayController), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("DeckStatsOverlayScreen: could not resolve DeckStatsOverlayController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(DeckStatsOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("DeckStatsOverlayScreen installed");
    }

    private static void Activate_Postfix(DeckStatsOverlayController __instance)
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

    public string StateSignature() => _overlay == null ? "DeckStats|dead" : $"DeckStats|{_nav.Index}|{Stats()}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _overlay = null;
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    // Escape = cancel/close: the overlay's own teardown.
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

        _router.Speech.Say(Stats(), interrupt: true);
        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // "Deck stats. <wins> wins, <losses> losses, win rate <ratio>." The numbers/percentage are the
    // overlay's own localized labels; the connective words are mod scaffolding pending the broader
    // speech-localization pass (the same convention used elsewhere in the mod).
    private string Stats()
    {
        if (_overlay == null) return Name;
        var wins = LocOf(ReadField<LocText>(_overlay, "winsLabel"));
        var losses = LocOf(ReadField<LocText>(_overlay, "lossesLabel"));
        var ratio = LocOf(ReadField<LocText>(_overlay, "winLossRatioLabel"));
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(wins)) parts.Add($"{wins} wins");
        if (!string.IsNullOrWhiteSpace(losses)) parts.Add($"{losses} losses");
        if (!string.IsNullOrWhiteSpace(ratio)) parts.Add($"win rate {ratio}");
        return parts.Count == 0 ? Name : $"{Name}. {string.Join(", ", parts)}";
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
