using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.UI.PlaySubGroups;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Friendly-battle request overlay — driver: SendJoinMatchOverlayScreenGroup (an OverlayScreenGroup).
// Opened both ways: challenging a friend (SEND) and accepting a friend's challenge (JOIN). It shows
// the deck you'll bring (deckNameText + a validity icon), a turn-timer toggle (when sending), a confirm
// button (SendMatchRequest), a change-deck button (OpenDeckManager), and a close. Confirming kicks off
// matchmaking (MatchmakingOverlay -> NetworkMatchController).
//
// We hook the SHARED base OverlayScreenGroup.OnActivate with an `is` guard (uniform with the other
// overlay handlers; see [[feedback-overlay-hook-hazard]]). SendJoinMatch doesn't override OnDeActivate,
// so dismissal is the Tick backstop.
internal sealed class SendJoinMatchOverlayScreen : IScreenHandler
{
    private static SendJoinMatchOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private SendJoinMatchOverlayScreenGroup? _overlay;
    private bool _active;

    public string Name => "Battle request";

    public SendJoinMatchOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new SendJoinMatchOverlayScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("SendJoinMatchOverlayScreen: could not resolve OverlayScreenGroup.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(SendJoinMatchOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("SendJoinMatchOverlayScreen installed");
    }

    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not SendJoinMatchOverlayScreenGroup ov) return;
        _instance._overlay = ov;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "BattleRequest|dead" : $"BattleRequest|{_nav.Index}|{DeckLine()}";

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

    private void Close() => _overlay?.Close();

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        // Buttons (confirm / change deck / close) + the turn-timer toggle, in reading order.
        var items = _overlay.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s is Button || s is Toggle)
            .Where(s => s.isActiveAndEnabled)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        _nav.SetItems(items);
        _nav.AllowHorizontal = true;
        if (!announce) return;

        var deck = DeckLine();
        _router.Speech.Say(string.IsNullOrWhiteSpace(deck) ? Name : $"{Name}. {deck}", interrupt: true);
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"BattleRequest overlay: {deck} | {items.Count} items");
    }

    // "Deck <name>[, invalid]" — the deck you'll bring + whether it fails validation.
    private string DeckLine()
    {
        if (_overlay == null) return "";
        var name = TmpOf(ReadField<TMP_Text>(_overlay, "deckNameText"));
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = ReadField<GameObject>(_overlay, "invalidIcon");
        var invalidActive = invalid != null && invalid.activeInHierarchy;
        return invalidActive ? $"Deck {name}, invalid" : $"Deck {name}";
    }

    private static string? TmpOf(TMP_Text? t)
    {
        var s = t != null ? t.text : null;
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

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
