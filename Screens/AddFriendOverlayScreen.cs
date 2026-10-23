using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Add-friend search overlay — driver: AddFriendOverlayScreenGroup. It has one text field
// (_inputField, the friend's screen name), a SEND button (sendRequestButton), a clear (X) and a back
// arrow. Sending fires SendFriendRequestAsync and reports via OnAddFriendSuccess / OnAddFriendFail (a
// loc id), which we speak.
//
// IMPORTANT: unlike the other friend overlays, this one is NOT opened through OverlayManager —
// FriendsHUBScreenController.OpenAddFriendOverlay() just fires the scene-wired public `OnOpenAddFriend`
// UnityEvent, so OverlayScreenGroup.OnActivate is never called. So we drive it off the controller's
// PUBLIC events: `OnOpenAddFriend` opens, the overlay's own `OnClose` closes (activeInHierarchy backstop).
//
// Nav model: two groups — the text FIELD on its own (arrows are its caret), and
// the BUTTONS together (clear / send / back, navigated by arrows). TAB switches between the two groups;
// it does NOT step through each control. See [[feedback-arrows-over-tab]].
internal sealed class AddFriendOverlayScreen : IScreenHandler
{
    private static AddFriendOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup;
    private AddFriendOverlayScreenGroup? _overlay;
    private FriendsHUBScreenController? _wiredHub;
    private AddFriendOverlayScreenGroup? _wiredOverlay;
    private bool _active;
    private bool _open;   // set by OnOpenAddFriend, cleared by OnClose / visibility backstop

    public string Name => "Add friend";

    public AddFriendOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new AddFriendOverlayScreen(router);

        var activate = AccessTools.Method(typeof(HUBProfileScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("AddFriendOverlayScreen: could not resolve HUBProfileScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(AddFriendOverlayScreen), nameof(ProfileActivated_Postfix))));
        Log.Info("AddFriendOverlayScreen installed");
    }

    private static void ProfileActivated_Postfix(HUBProfileScreenController __instance)
    {
        if (_instance == null) return;
        var hub = ReadField<FriendsHUBScreenController>(__instance, "friendsHubScreenController");
        if (hub == null || _instance._wiredHub == hub) return;
        _instance._wiredHub = hub;
        hub.OnOpenAddFriend?.AddListener(_instance.OnOpenRequested);
    }

    private void OnOpenRequested()
    {
        _overlay = FindOverlay();
        WireOverlayEvents(_overlay);
        _open = true;
    }

    public static void Poll()
    {
        var self = _instance;
        if (self == null || self._active || !self._open) return;
        if (self._overlay == null) self._overlay = self.FindOverlay();
        self._active = true;
        self._router.Activate(self);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => !_open ? "AddFriend|dead" : $"AddFriend|{_activeGroup}|{CurrentNav?.Index ?? -1}";

    private KeyboardNavigator? CurrentNav =>
        _activeGroup >= 0 && _activeGroup < _groups.Count ? _groups[_activeGroup] : null;

    public void Tick()
    {
        if (!_open || !IsVisible(_overlay))
        {
            _active = false; _open = false; _router.Deactivate(this);
            return;
        }
        if (!Application.isFocused) return;
        if (_groups.Count == 0) return;

        // TAB switches GROUPS (field <-> buttons), never steps through individual controls. Arrows are
        // handled inside the active group (the field's caret, or moving among the buttons).
        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        CurrentNav?.Tick();
    }

    private void Close() => _overlay?.Close();

    private void Rebuild(bool announce)
    {
        _groups.Clear();
        if (_overlay == null) return;

        // Group 1: the text field on its own (arrows are its caret; Tab leaves to the buttons).
        var field = ReadField<TMP_InputField>(_overlay, "_inputField");
        if (field != null && field.isActiveAndEnabled)
            AddGroup(new List<Selectable> { field });

        // Group 2: the real buttons (clear / send / back) navigated by arrows. Drop decorative/junk
        // buttons (an icon child nested in another button, or a label-less "Icn"/"Bg"-style stray that
        // duplicates the close) so the user gets exactly the three controls, not a phantom 4th.
        var send = ReadField<Button>(_overlay, "sendRequestButton");
        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Where(b => IsRealButton(b, send, _overlay.transform))
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        if (buttons.Count > 0)
        {
            // Back/Clear sit on the top row with SEND below — a small grid. Force BOTH axes: auto-detect
            // measures the bounding box as wider than tall and picks horizontal-only, which strands SEND
            // in the second row (Down disabled). Both axes => Left/Right across the top, Down to SEND.
            var g = AddGroup(buttons);
            g.AllowVertical = true;
            g.AllowHorizontal = true;
        }

        if (_groups.Count == 0) return;
        _activeGroup = 0;   // land in the field
        if (!announce) return;

        _router.Speech.Say(Name, interrupt: true);
        _groups[0].FocusFirst(announce: true, interruptPrior: false);
        LogButtons();
    }

    private KeyboardNavigator AddGroup(List<Selectable> items)
    {
        // AllowTab=false: the handler owns Tab (group switching). Arrows move within the group.
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        g.SetItems(items);
        g.OnBack = Close;
        _groups.Add(g);
        return g;
    }

    // A real, navigable control: the SEND button always; otherwise a button that isn't nested inside
    // another button and isn't a label-less decorative icon. PTCGL prefabs sometimes put a clickable
    // "Icn"/"Image"/"Bg" child alongside the real button — those have no meaningful label and (here)
    // just re-trigger the close, so they're noise.
    private static bool IsRealButton(Button b, Button? send, Transform overlayRoot)
    {
        if (send != null && b == send) return true;
        for (var t = b.transform.parent; t != null && t != overlayRoot; t = t.parent)
            if (t.GetComponent<Button>() != null) return false;   // nested inside another button
        if (IsDecorativeName(b.gameObject.name) && !HasWordedLabel(b)) return false;
        return true;
    }

    private static bool IsDecorativeName(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n is "icn" or "icon" or "image" or "img" or "bg" or "background" or "blocker";
    }

    private static bool HasWordedLabel(Component b)
    {
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (!string.IsNullOrWhiteSpace(t.text) && t.text.Any(char.IsLetter)) return true;
        return false;
    }

    private void LogButtons()
    {
        if (_overlay == null) return;
        var all = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Select(b => $"{b.gameObject.name}{(IsRealButton(b, ReadField<Button>(_overlay, "sendRequestButton"), _overlay.transform) ? "" : "(dropped)")}");
        Log.Info($"AddFriend overlay: groups={_groups.Count}, buttons=[{string.Join(", ", all)}]");
    }

    private AddFriendOverlayScreenGroup? FindOverlay()
        => Object.FindAnyObjectByType<AddFriendOverlayScreenGroup>(FindObjectsInactive.Include);

    private void WireOverlayEvents(AddFriendOverlayScreenGroup? ov)
    {
        if (ov == null || _wiredOverlay == ov) return;
        _wiredOverlay = ov;
        ov.OnClose?.AddListener(() => _open = false);
        ov.OnAddFriendSuccess?.AddListener(SpeakResult);
        ov.OnAddFriendFail?.AddListener(SpeakResult);
    }

    // Speak the send-request outcome. The game reports it as a loc id; if that key isn't in the loaded
    // table (resolves to "(Missing)…"), fall back to a plain-English message so the user always hears
    // whether it worked. interrupt:true so it's spoken at once; on success the overlay then closes and
    // the panel's resume announce QUEUES after this (FriendsPanelScreen.OnResume uses interrupt:false).
    private void SpeakResult(string locId)
    {
        var loc = LocalizationManager.Instance?.GetString(locId);
        string spoken = (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)")) ? loc! : Fallback(locId);
        Log.Info($"AddFriend result: {locId} -> loc='{loc}' spoken='{spoken}'");
        if (!string.IsNullOrWhiteSpace(spoken)) _router.Speech.Say(spoken, interrupt: true);
    }

    private static string Fallback(string locId) => locId switch
    {
        "friend_add_friend_success" => "Friend request sent",
        "friend_add_friend_already_friends" => "Already friends, or a request is already pending",
        "friend_max_friend_title" => "Your friend list is full",
        _ => "Could not send friend request",
    };

    private static bool IsVisible(AddFriendOverlayScreenGroup? ov)
    {
        if (ov == null || !ov.gameObject.activeInHierarchy) return false;
        var cg = ov.GetComponentInParent<CanvasGroup>();
        return cg == null || cg.alpha > 0.1f;
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
