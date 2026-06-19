using System;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// End-of-match result screen — driver MatchResultsScreenOverlay. Online / vs-people matches end with
// this animated outro (win/loss splash → victor catchphrase → match stats → rewards → return to main
// menu). It's not a battle prompt and BattleScreen has already torn down, so without a handler the
// player heard nothing and had no way to advance past the stats page (which waits indefinitely for a
// "tap").
//
// We hook Show(...) — it carries `won` + the localized game-end reason — to announce the outcome, and
// drive the screen's own advance: Enter taps through the stages (SetButtonTappedFlag, the same flag a
// click sets) and, once the Return-to-main-menu button is up, presses it (its onClick = Continue).
internal sealed class MatchResultScreen : IScreenHandler
{
    private static MatchResultScreen? _instance;

    private readonly ScreenRouter _router;
    private MatchResultsScreenOverlay? _overlay;
    private bool _active;
    private string _outcome = "";

    private static FieldInfo? _returnButtonField;

    public string Name => "Match result";

    public MatchResultScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        try
        {
            new MatchResultScreen(router);
            var show = AccessTools.Method(typeof(MatchResultsScreenOverlay), nameof(MatchResultsScreenOverlay.Show));
            if (show == null) { Log.Error("MatchResultScreen: MatchResultsScreenOverlay.Show not found"); return; }
            harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchResultScreen), nameof(Show_Postfix))));
            Log.Info("MatchResultScreen installed");
        }
        catch (Exception ex) { Log.Error($"MatchResultScreen.Install threw: {ex.Message}"); }
    }

    private static void Show_Postfix(MatchResultsScreenOverlay __instance, bool won, string gameEndReasonLocID)
    {
        if (_instance == null) return;
        try { _instance.OnShow(__instance, won, gameEndReasonLocID); }
        catch (Exception ex) { Log.Error($"MatchResultScreen.OnShow threw: {ex.Message}"); }
    }

    private void OnShow(MatchResultsScreenOverlay overlay, bool won, string gameEndReasonLocID)
    {
        _overlay = overlay;
        var reason = LocalizationHook.Get(gameEndReasonLocID);
        _outcome = won ? "You won." : "You lost.";
        if (!string.IsNullOrWhiteSpace(reason)) _outcome += " " + reason;
        if (!_active) { _active = true; _router.Activate(this); }
        else Announce();
    }

    public void OnEnter() => Announce();
    public void OnResume() => Announce();
    public void OnExit() { }
    public void RespeakAll() => Announce();

    public string StateSignature() => _overlay == null ? "MatchResult|dead" : $"MatchResult|{_outcome}";

    private void Announce() => _router.Speech.Say(_outcome, interrupt: true);

    public void Tick()
    {
        // The overlay lives on the Match scene; when it unloads (return to menu) the reference dies.
        if (_overlay == null || _overlay.gameObject == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
            || UnityEngine.Input.GetKeyDown(KeyCode.Space))
            Advance();
    }

    // Move the result screen forward: if the Return-to-main-menu button is up, press it (its onClick
    // runs Continue → back to the HUB); otherwise tap through the current stage (the stats page waits
    // on exactly this flag). Both are the game's own advance paths — no parallel transition.
    private void Advance()
    {
        if (_overlay == null) return;
        try
        {
            var btn = ReturnButton(_overlay);
            if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
            {
                _router.Speech.Say("Returning to menu", interrupt: true);
                btn.onClick.Invoke();
                return;
            }
            _overlay.SetButtonTappedFlag();
        }
        catch (Exception ex) { Log.Error($"MatchResultScreen.Advance threw: {ex.Message}"); }
    }

    private static Button? ReturnButton(MatchResultsScreenOverlay overlay)
    {
        _returnButtonField ??= typeof(MatchResultsScreenOverlay).GetField(
            "returnToMainMenuButton", BindingFlags.Instance | BindingFlags.NonPublic);
        return _returnButtonField?.GetValue(overlay) as Button;
    }
}
