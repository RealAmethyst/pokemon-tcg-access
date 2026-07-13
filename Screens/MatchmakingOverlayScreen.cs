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

// Matchmaking / "searching for opponent" overlay — driver: MatchmakingOverlay (an exclusive
// OverlayScreenGroup). Shown for ranked/quick search AND the friend-battle flow: sending a challenge
// (FriendsHUBScreenController.OnJoinMatch -> OpenMatchmakingOverlay) and accepting one both land here,
// with a CANCEL button. Without a handler it's silent and — being exclusive — leaves the screen
// underneath navigable, which is exactly the "cancel a friend battle request popup isn't speaking"
// report. When an opponent is found the cancel button hides and the game transitions into the match
// (BattleScreen takes over), so the Tick backstop pops us when the overlay deactivates.
//
// We hook MatchmakingOverlay.OnActivate(OverlayManager) — its OWN override — rather than the shared
// base OnActivate(): the searching text / header are set in the back half of that override, AFTER the
// base call, so a base-method postfix would read them stale. Hooking the override fires once it's all
// populated, and only for this overlay (no [[feedback-overlay-hook-hazard]] base-cast risk).
internal sealed class MatchmakingOverlayScreen : IScreenHandler
{
    private static MatchmakingOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private MatchmakingOverlay? _overlay;
    private bool _active;

    public string Name => "Matchmaking";

    public MatchmakingOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Cancel;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new MatchmakingOverlayScreen(router);

        var activate = AccessTools.Method(typeof(MatchmakingOverlay), "OnActivate", new[] { typeof(OverlayManager) });
        if (activate == null)
        {
            Log.Error("MatchmakingOverlayScreen: could not resolve MatchmakingOverlay.OnActivate(OverlayManager)");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchmakingOverlayScreen), nameof(Activate_Postfix))));

        // "Opponent found": the game signals this only with an audio stinger + the cancel button
        // hiding, then it loads the versus scene — no on-screen text — so a blind player otherwise
        // can't tell the search succeeded. Announce it off MatchCreated().
        var matchCreated = AccessTools.Method(typeof(MatchmakingOverlay), "MatchCreated");
        if (matchCreated != null)
            harmony.Patch(matchCreated, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchmakingOverlayScreen), nameof(MatchCreated_Postfix))));

        Log.Info("MatchmakingOverlayScreen installed");
    }

    private static void MatchCreated_Postfix(MatchmakingOverlay __instance)
    {
        if (_instance == null || _instance._overlay != __instance) return;
        if (!__instance.gameObject.activeInHierarchy) return;
        _instance._router.Speech.Say("Opponent found, starting match", interrupt: true);
        Log.Info("Matchmaking: opponent found");
    }

    private static void Activate_Postfix(MatchmakingOverlay __instance)
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

    public string StateSignature() => _overlay == null ? "Matchmaking|dead" : $"Matchmaking|{_nav.Count}|{Status()}";

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

    // Escape cancels the search — but only while the cancel button is still up (it hides the moment an
    // opponent is found and we're committing to the match).
    private void Cancel()
    {
        if (_overlay != null && CancelButton() is { } b && b.gameObject.activeInHierarchy)
            _overlay.CancelMatchmaking();
    }

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        var items = new List<Selectable>();
        if (CancelButton() is { } cancel && cancel.isActiveAndEnabled) items.Add(cancel);
        _nav.SetItems(items);

        if (!announce) return;
        var status = Status();
        _router.Speech.Say(string.IsNullOrWhiteSpace(status) ? "Searching for opponent" : status!, interrupt: true);
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"Matchmaking overlay: {status} | {items.Count} buttons");
    }

    // The game-mode/friend header + the "searching…/waiting for <friend>…" line.
    private string Status()
    {
        if (_overlay == null) return "";
        var parts = new List<string>(2);
        var header = LocOf(ReadField<LocText>(_overlay, "gameplayTypeHeader"));
        if (!string.IsNullOrWhiteSpace(header)) parts.Add(header!);
        var searching = LocOf(ReadField<LocText>(_overlay, "searchingText"));
        if (!string.IsNullOrWhiteSpace(searching)) parts.Add(searching!);
        return string.Join(". ", parts);
    }

    private Button? CancelButton()
    {
        var go = ReadField<GameObject>(_overlay!, "cancelButton");
        return go != null ? go.GetComponent<Button>() : null;
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

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
}
