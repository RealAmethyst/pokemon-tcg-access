using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Rainier.Features.BuildAndBattle;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The Build & Battle RUN LOBBY — the hex-path event map you land on after buying in (driver
// HUBBnbLobbyRunController, loaded into its own scene by BuildAndBattleLobbySpawner). The hex path is a
// visual match-progression track (you don't navigate nodes individually — PLAY starts the next match),
// so the actionable surface is the buttons: PLAY (StartNextMatch), EDIT DECK, START NEW RUN / COLLECT
// REWARDS (when the run is done), plus the top bar's home / info / settings. Without a handler it's
// silent and unusable.
//
// It's a plain MonoBehaviour with no OnActivate hook, so we detect it by polling for the controller from
// Main.OnUpdate (like the friends panel). We sweep the lobby canvas's active buttons and present them,
// and announce the header + match progress (BnbDataManager match count) + the "ends in" timer on entry.
internal sealed class BnbLobbyScreen : IScreenHandler
{
    private static BnbLobbyScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private HUBBnbLobbyRunController? _controller;
    private bool _active;
    private string _lastButtonSig = "";

    public string Name => "Build and Battle lobby";

    public BnbLobbyScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
    }

    public static void Install(ScreenRouter router) => new BnbLobbyScreen(router);

    public static void Poll()
    {
        var inst = _instance;
        if (inst == null || inst._active) return;
        HUBBnbLobbyRunController? ctrl = null;
        try { ctrl = Object.FindAnyObjectByType<HUBBnbLobbyRunController>(); } catch { }
        if (ctrl == null || !ctrl.gameObject.activeInHierarchy) return;
        inst._controller = ctrl;
        inst._active = true;
        inst._router.Activate(inst);
    }

    public void OnEnter() { Rebuild(); _lastButtonSig = ButtonSig(); Announce(); }
    public void OnResume() { Rebuild(); _lastButtonSig = ButtonSig(); Announce(); }
    public void OnExit() { }
    public void RespeakAll() { Rebuild(); Announce(); }

    public string StateSignature() => _controller == null ? "BnbLobby|dead" : $"BnbLobby|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_controller == null || !_controller.gameObject.activeInHierarchy)
        {
            if (_active) { _active = false; _router.Deactivate(this); _controller = null; }
            return;
        }
        if (!Application.isFocused) return;

        // Buttons swap as the run state changes (PLAY → START NEW RUN / COLLECT REWARDS when finished);
        // re-present when the set changes.
        var sig = ButtonSig();
        if (sig != _lastButtonSig) { _lastButtonSig = sig; var i = _nav.Index; Rebuild(); _nav.FocusIndex(Mathf.Clamp(i, 0, _nav.Count - 1), announce: false); return; }
        _nav.Tick();
    }

    private void Rebuild()
    {
        var buttons = LobbyButtons().Cast<Selectable>().OrderBy(s => ReadingOrder(s.transform)).ToList();
        _nav.SetItems(buttons, LobbyLabel);
        _nav.ForceFlatVertical();   // a small set of actions — predictable Up/Down through all of them
    }

    private void Announce()
    {
        if (_controller == null) return;
        var parts = new List<string> { Name };
        var header = TmpText(ReadField<TMP_Text>(_controller, "lobbyHeaderText"));
        if (!string.IsNullOrWhiteSpace(header) && header != "BUILD & BATTLE") parts.Add(header!);
        var progress = ProgressText();
        if (!string.IsNullOrWhiteSpace(progress)) parts.Add(progress!);
        var timer = TimerText();
        if (!string.IsNullOrWhiteSpace(timer)) parts.Add(timer!);
        _router.Speech.Say(string.Join(". ", parts), interrupt: true);
        if (_nav.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"BnB lobby: {_nav.Count} buttons [{string.Join(", ", LobbyButtons().Select(b => b.gameObject.name))}]");
    }

    // Active, interactable buttons of the lobby canvas. The hex nodes aren't buttons, so this is the
    // PLAY / EDIT DECK / START NEW RUN / COLLECT REWARDS actions + the top bar (home / info / settings).
    private List<Button> LobbyButtons()
    {
        if (_controller == null) return new List<Button>();
        var canvas = _controller.GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas.transform : _controller.transform.root;
        return root.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.IsInteractable())
            .ToList();
    }

    private string ButtonSig()
    {
        var bs = LobbyButtons();
        return bs.Count + ":" + string.Join(",", bs.Select(b => b.gameObject.name));
    }

    // "<done> of <total> matches" from the run data.
    private string? ProgressText()
    {
        try
        {
            var mgr = ManagerSingleton<BnbDataManager>.instance;
            if (mgr == null) return null;
            int total = mgr.eventMatchCount;
            int done = mgr.matchResults?.Count ?? 0;
            if (total > 0) return $"{done} of {total} matches";
        }
        catch { }
        return null;
    }

    private string? TimerText()
    {
        var timer = ReadField<Component>(_controller!, "timerDisplay");
        var title = timer != null ? TmpText(ReadField<TMP_Text>(timer, "titleText")) : null;
        var value = timer != null ? TmpText(ReadField<TMP_Text>(timer, "timeLeftText")) : null;
        var parts = new[] { title, value }.Where(s => !string.IsNullOrWhiteSpace(s));
        return parts.Any() ? string.Join(" ", parts) : null;
    }

    // Worded buttons (PLAY, EDIT DECK, …) keep their text; the icon-only top-bar buttons get a name.
    private static string LobbyLabel(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (!string.IsNullOrEmpty(t.text) && t.text.Any(char.IsLetter)) return KeyboardNavigator.DefaultLabel(s);
        var n = s.gameObject.name.ToLowerInvariant();
        if (n.Contains("home")) return "Home";
        if (n.Contains("setting")) return "Menu";
        if (n.Contains("info")) return "Information";
        return KeyboardNavigator.DefaultLabel(s);
    }

    private static string? TmpText(TMP_Text? t)
        => t != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text) ? t.text.Trim() : null;

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, flags);
            if (f != null) return f.GetValue(instance) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
