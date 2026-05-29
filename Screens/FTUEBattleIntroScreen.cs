using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// FTUE tutorial battle intro — the "BATTLE X OUT OF Y" overlay shown before each of the
// six tutorial battles (and on resume of a partially-completed tutorial).
//
// Driver: FTUEManager (TPCI.RainierClient/FTUEManager.cs).
//   - `ChangeBattleText()` opens the panel (SetActive(true) + OverlayManager.OpenOverlay
//     + sets battle count header and description text).
//   - `CloseBattleCanvas()` dismisses it (SetActive(false) + OverlayManager.CloseOverlay).
//   - The three buttons each trigger their own dismiss-and-advance flow:
//       PlayButton → PlayBattle()         — starts the tutorial battle
//       SkipThisButton → SkipThisBattle() — advances one checkpoint
//       SkipAllButton → SkipAllBattles()  — skips through all remaining tutorial battles
//
// Text and buttons live on `FTUEManagerParts` (a MonoBehaviour wrapper accessed via the
// private `FTUEManager.CurrentParts` property which picks between portrait and landscape
// variants based on `GameManager.IsLandscapeAspectRatio()`). All its fields are public, so
// once we resolve CurrentParts we can read battleCountText / battleText (LocText.CurrentText
// gives the already-formatted display string, e.g. "BATTLE 1 OUT OF 6") and grab the three
// Buttons directly.
internal sealed class FTUEBattleIntroScreen : IScreenHandler
{
    private static FTUEBattleIntroScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private FTUEManager? _manager;
    private bool _active;

    public string Name => "FTUEBattleIntro";

    public FTUEBattleIntroScreen(ScreenRouter router)
    {
        _router = router;
        _nav = new KeyboardNavigator(router.Speech);
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new FTUEBattleIntroScreen(router);

        var change = AccessTools.Method(typeof(FTUEManager), nameof(FTUEManager.ChangeBattleText));
        if (change == null)
        {
            Log.Error("FTUEBattleIntroScreen: ChangeBattleText not found");
            return;
        }
        harmony.Patch(change, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(FTUEBattleIntroScreen), nameof(ChangeBattleText_Postfix))));

        var close = AccessTools.Method(typeof(FTUEManager), "CloseBattleCanvas");
        if (close != null)
            harmony.Patch(close, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(FTUEBattleIntroScreen), nameof(CloseBattleCanvas_Postfix))));

        Log.Info("FTUEBattleIntroScreen installed");
    }

    private static void ChangeBattleText_Postfix(FTUEManager __instance)
    {
        if (_instance == null) return;
        _instance._manager = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
        else
        {
            // Already active — ChangeBattleText was called again for the next battle in the
            // sequence (e.g. after PlayBattle finished and the next intro fires). Rebuild +
            // re-announce so the user hears the new battle's text.
            _instance.BuildNav();
            _instance.Announce();
        }
    }

    private static void CloseBattleCanvas_Postfix(FTUEManager __instance)
    {
        if (_instance == null) return;
        if (_instance._manager != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._manager = null;
    }

    public void OnEnter()
    {
        BuildNav();
        Announce();
    }

    public void OnResume()
    {
        // Re-entry (e.g. if a modal popped on top and just closed). Refresh nav in case
        // button states changed and re-announce the focused button.
        BuildNav();
        _nav.RefocusCurrent(announce: true);
    }

    public void OnExit() { /* preserve _manager across pause */ }

    public void RespeakAll() => Announce();

    public string StateSignature()
    {
        if (_manager == null) return "FTUEBattleIntro|dead";
        var (header, description) = ReadTexts();
        return $"FTUEBattleIntro|{header}|{description}|{_nav.Index}";
    }

    public void Tick()
    {
        if (_manager == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }
        _nav.Tick();
    }

    private void BuildNav()
    {
        var parts = GetCurrentParts();
        var items = new List<Selectable>();
        if (parts != null)
        {
            if (parts.playButton != null && parts.playButton.gameObject.activeInHierarchy)
                items.Add(parts.playButton);
            if (parts.skipThisButton != null && parts.skipThisButton.gameObject.activeInHierarchy)
                items.Add(parts.skipThisButton);
            if (parts.skipAllButton != null && parts.skipAllButton.gameObject.activeInHierarchy)
                items.Add(parts.skipAllButton);
        }
        _nav.SetItems(items);
        // Visual layout in the prefab is PLAY on top (large, full width) with SKIP THIS and
        // SKIP ALL side by side below it. The natural reading flow is vertical (primary
        // action → secondary → most-destructive), so we override the auto-detected axes to
        // pin nav to Up/Down arrows. Tab is intentionally disabled — arrow nav is enough
        // and reserving Tab keeps it consistent with screens that genuinely use it.
        _nav.AllowVertical = true;
        _nav.AllowHorizontal = false;
        _nav.AllowTab = false;
    }

    private void Announce()
    {
        var (header, description) = ReadTexts();
        var preamble = string.IsNullOrWhiteSpace(header)
            ? description
            : (string.IsNullOrWhiteSpace(description) ? header : $"{header}. {description}");
        if (!string.IsNullOrWhiteSpace(preamble))
            _router.Speech.Say(preamble!, interrupt: true);
        // FocusFirst with interruptPrior=false so the description plays in full before the
        // button label queues — matches the SimpleMessageScreen pattern.
        _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private (string? header, string? description) ReadTexts()
    {
        var parts = GetCurrentParts();
        return (parts?.battleCountText?.CurrentText, parts?.battleText?.CurrentText);
    }

    // FTUEManager.CurrentParts is a private property that lazily resolves between
    // landscapeParts / portraitParts based on aspect ratio. Read via reflection so we get
    // the same instance the game itself uses for button.onClick wiring and text updates.
    private FTUEManagerParts? GetCurrentParts()
    {
        if (_manager == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pi = typeof(FTUEManager).GetProperty("CurrentParts", flags);
        return pi?.GetValue(_manager) as FTUEManagerParts;
    }
}
