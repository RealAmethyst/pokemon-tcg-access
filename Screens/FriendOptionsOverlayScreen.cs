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

// Friend-options popup (landscape) — driver: FriendOptionsOverlayScreenGroup (an OverlayScreenGroup),
// opened when a friend is selected in the panel (FriendsHUBScreenController.SelectFriend, when
// usePopupOptions). It shows the friend's name as a header and two buttons: CHALLENGE (interactable
// only when the friend is online) and REMOVE, plus a close. The buttons' onClicks fire OnChallengeFriend
// / OnRemoveFriend, which route back into the controller (challenge -> SendJoinMatch overlay; remove ->
// confirm overlay).
//
// We hook the SHARED base OverlayScreenGroup.OnActivate with an `is` guard (uniform with the other
// overlay handlers; see [[feedback-overlay-hook-hazard]]). Dismissal is the Tick backstop. A disabled
// CHALLENGE (offline friend) still navigates and announces ", unavailable" (KeyboardNavigator).
internal sealed class FriendOptionsOverlayScreen : IScreenHandler
{
    private static FriendOptionsOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private FriendOptionsOverlayScreenGroup? _overlay;
    private bool _active;

    public string Name => "Friend options";

    public FriendOptionsOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new FriendOptionsOverlayScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("FriendOptionsOverlayScreen: could not resolve OverlayScreenGroup.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(FriendOptionsOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("FriendOptionsOverlayScreen installed");
    }

    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not FriendOptionsOverlayScreenGroup ov) return;
        _instance._overlay = ov;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "FriendOptions|dead" : $"FriendOptions|{_nav.Index}|{Header()}";

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

        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        _nav.SetItems(buttons);
        _nav.AllowHorizontal = true;
        if (!announce) return;

        var header = Header();
        _router.Speech.Say(string.IsNullOrWhiteSpace(header) ? Name : header!, interrupt: true);
        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"FriendOptions overlay: {header} | {buttons.Count} buttons");
    }

    private string Header()
    {
        var t = ReadField<TMP_Text>(_overlay!, "headerText");
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? Name : s!.Trim();
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
