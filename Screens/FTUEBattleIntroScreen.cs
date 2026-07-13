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
// Driver: FTUEManager (TPCI.RainierClient/FTUEManager.cs). Everything hangs off one piece of
// game state: `FTUEManagerParts.battleCanvas`, the GameObject the panel lives on. Three methods
// toggle it, and each is hooked here so we open and close exactly when the game does:
//   - `ChangeBattleText()` — battleCanvas.SetActive(true) + OverlayManager.OpenOverlay, then fills
//     in the battle count header and description. But only while battles remain: once the last one
//     is done it falls through to CloseBattleCanvas() instead. So "ChangeBattleText ran" does NOT
//     mean "the panel is up" — ask the canvas.
//   - `CloseBattleCanvas()` — battleCanvas.SetActive(false) + OverlayManager.CloseOverlay. Reached
//     from that else-branch and from the SkipThis / SkipAll completion callbacks.
//   - `PlayBattle()` — battleCanvas.SetActive(false) directly, WITHOUT calling CloseBattleCanvas,
//     then triggers the animator into the match.
//
// That last one is why this handler used to strand the player. Hooking only CloseBattleCanvas
// meant pressing PLAY never popped us: we sat on the router stack under Battle for the whole
// tutorial match (HOME never resumed beneath us), and after the final battle ChangeBattleText's
// else-branch closed the panel while the postfix promptly re-activated us over a now-hidden
// canvas — an empty screen that swallowed every keypress.
//
// So there is no `_active` mirror and no cached manager here: `FTUEManager.instance` and
// `battleCanvas.activeInHierarchy` already are the game's answer. It's a plain GameObject
// SetActive, not an OverlayManager canvas-disable, so activeInHierarchy is trustworthy (cf.
// [[feedback-overlay-close-signal]]).
//
// Text and buttons live on `FTUEManagerParts`, reached through the private `FTUEManager.CurrentParts`
// property, which picks between the portrait and landscape variants and memoises the result. All its
// fields are public, so once we resolve CurrentParts we can read battleCountText / battleText
// (LocText.CurrentText gives the formatted display string, e.g. "BATTLE 1 OUT OF 6") and grab the
// three Buttons directly.
internal sealed class FTUEBattleIntroScreen : IScreenHandler
{
    private static FTUEBattleIntroScreen? _instance;

    // Handle on the game's own lazily-resolved parts property — not a copy of its value.
    private static readonly MethodInfo? CurrentPartsGetter =
        AccessTools.PropertyGetter(typeof(FTUEManager), "CurrentParts");

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;

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
            AccessTools.Method(typeof(FTUEBattleIntroScreen), nameof(CanvasShown_Postfix))));

        // Both hide paths. PlayBattle bypasses CloseBattleCanvas, so it needs its own hook or
        // pressing PLAY leaves this handler parked on the stack for the whole match.
        foreach (var name in new[] { "CloseBattleCanvas", nameof(FTUEManager.PlayBattle) })
        {
            var hide = AccessTools.Method(typeof(FTUEManager), name);
            if (hide == null) { Log.Error($"FTUEBattleIntroScreen: {name} not found"); continue; }
            harmony.Patch(hide, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(FTUEBattleIntroScreen), nameof(CanvasHidden_Postfix))));
        }

        Log.Info("FTUEBattleIntroScreen installed");
    }

    // ChangeBattleText both opens the panel and refreshes it for the next battle in the sequence
    // (SkipThisBattle advances a checkpoint without ever hiding the canvas). Re-announce on a
    // refresh so the user hears the new battle's header and description.
    private static void CanvasShown_Postfix()
    {
        if (_instance == null) return;
        if (!_instance.IsOpen) { _instance.Close(); return; }   // the else-branch: last battle done

        if (_instance.IsActiveHandler) { _instance.BuildNav(); _instance.Announce(); }
        else _instance._router.Activate(_instance);                   // → OnEnter
    }

    private static void CanvasHidden_Postfix() => _instance?.Close();

    private bool IsActiveHandler => ReferenceEquals(_router.Active, this);

    public void OnEnter()
    {
        BuildNav();
        Announce();
    }

    public void OnResume()
    {
        // Re-entry (e.g. a modal popped on top and just closed). Refresh nav in case button
        // states changed and re-announce the focused button.
        BuildNav();
        _nav.RefocusCurrent(announce: true);
    }

    public void OnExit() { }

    public void RespeakAll() => Announce();

    public string StateSignature()
    {
        if (!IsOpen) return "FTUEBattleIntro|dead";
        var (header, description) = ReadTexts();
        return $"FTUEBattleIntro|{header}|{description}|{_nav.Index}";
    }

    public void Tick()
    {
        // Backstop for hide paths we don't hook — FTUEManager also blanks both parts' canvases
        // directly when it tears the tutorial down. Never keep ticking a hidden panel: it would
        // own the router stack and swallow every keypress.
        if (!IsOpen) { Close(); return; }
        _nav.Tick();
    }

    // Deactivate no-ops unless we're the handler on top, so this is safe to call from any hide path.
    private void Close() => _router.Deactivate(this);

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
        // SKIP ALL side by side below it. That block is far wider than it is tall, so the
        // bounding-box auto-detect reads it as a horizontal strip; the natural reading flow is
        // vertical (primary action → secondary → most-destructive), so force a flat list.
        // Tab is intentionally disabled — arrow nav is enough and reserving Tab keeps it
        // consistent with screens that genuinely use it.
        _nav.ForceFlatVertical();
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

    // The game's own "is the intro panel showing" flag.
    private bool IsOpen
    {
        get
        {
            var canvas = GetCurrentParts()?.battleCanvas;
            return canvas != null && canvas.activeInHierarchy;
        }
    }

    private FTUEManagerParts? GetCurrentParts()
    {
        var manager = FTUEManager.instance;
        if (manager == null || CurrentPartsGetter == null) return null;
        return CurrentPartsGetter.Invoke(manager, null) as FTUEManagerParts;
    }
}
