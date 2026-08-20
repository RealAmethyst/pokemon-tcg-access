using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using RainierClientSDK.source.Friend;
using SharedLogicUtils.source.Services.Query.Contexts;
using UnityEngine;
using _Rainier.Scripts.Utils.Tweening;

namespace PokemonTCGLiveAccessibility.Screens;

// Friends panel — the slide-out on the Profile screen (driver: FriendsHUBScreenController, owned by
// HUBProfileScreenController.friendsHubScreenController). In landscape it's NOT a navigated screen:
// it's always present and slides in/out via a scene-wired tween toggled by the FRIENDS button, so
// there's no clean C# open/close callback. We detect it by GEOMETRY — the close tween moves the
// panel root's RectTransform off-screen, so "open" = the panel root is on-screen. Poll() (driven from
// Mod.OnUpdate, like CurrencyReader) activates us when it slides in; Tick deactivates when it slides out.
//
// The friend list is a virtualized EnhancedScroller, so — like the deck editor / battle-pass track —
// we navigate the DATA (FriendsHUBScreenController.cachedFriends), not the scroller cells, and drive
// the controller's own callbacks (the exact methods each cell button fires): SelectFriend (options),
// AcceptFriendRequestAsync / OnSelectDeclineFriendRequest (incoming requests), OpenSendJoinMatchOverlay
// (accept an incoming challenge), OpenAddFriendOverlay (the ADD FRIEND button). Each opens its own
// overlay, handled by AddFriendOverlayScreen / FriendOptionsOverlayScreen / SendJoinMatchOverlayScreen /
// ConfirmOverlayScreen, which stack on top of us.
internal sealed class FriendsPanelScreen : IScreenHandler
{
    private enum Kind { AddFriend, Options, AcceptRequest, DeclineRequest, AcceptChallenge, Info }

    private struct Row
    {
        public Kind Kind;
        public string Label;
        public string Id;
        public FriendInfo Friend;
    }

    private static FriendsPanelScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<Row> _rows = new();
    private int _row = -1;

    private HUBProfileScreenController? _profile;
    private FriendsHUBScreenController? _hub;
    private RectTransform? _panelRoot;
    private bool _active;
    // Only activate on a real closed→open transition. Guards against the panel reading as "open"
    // before we've ever seen it closed (e.g. a default-open scene state), which would otherwise
    // hijack focus the instant Profile opens. The notification auto-open still qualifies — its slide
    // tween starts from the closed position, so the first poll sees it closed.
    private bool _sawClosed;

    private static readonly MethodInfo? M_SelectFriend = AccessTools.Method(typeof(FriendsHUBScreenController), "SelectFriend", new[] { typeof(string) });
    private static readonly MethodInfo? M_Accept = AccessTools.Method(typeof(FriendsHUBScreenController), "AcceptFriendRequestAsync", new[] { typeof(string) });
    private static readonly MethodInfo? M_Decline = AccessTools.Method(typeof(FriendsHUBScreenController), "OnSelectDeclineFriendRequest", new[] { typeof(string) });
    private static readonly MethodInfo? M_OpenSendJoin = AccessTools.Method(typeof(FriendsHUBScreenController), "OpenSendJoinMatchOverlay");
    private static readonly MethodInfo? M_Prestige = AccessTools.Method(typeof(FriendsHUBScreenController), "GetPrestigeForFriend");

    public string Name => "Friends";

    public FriendsPanelScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new FriendsPanelScreen(router);

        var activate = AccessTools.Method(typeof(HUBProfileScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("FriendsPanelScreen: could not resolve HUBProfileScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(FriendsPanelScreen), nameof(ProfileActivated_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBProfileScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(FriendsPanelScreen), nameof(ProfileDeactivated_Postfix))));

        Log.Info("FriendsPanelScreen installed");
    }

    // Capture the friends controller + panel root whenever the Profile screen activates. The panel
    // itself opens later (FRIENDS button / auto-open); Poll() catches that via geometry.
    private static void ProfileActivated_Postfix(HUBProfileScreenController __instance)
    {
        if (_instance == null) return;
        _instance._profile = __instance;
        _instance._hub = ReadField<FriendsHUBScreenController>(__instance, "friendsHubScreenController");
        _instance._panelRoot = _instance.ResolvePanelRoot();
        _instance._sawClosed = false;
    }

    private static void ProfileDeactivated_Postfix(HUBProfileScreenController __instance)
    {
        if (_instance == null || _instance._profile != __instance) return;
        if (_instance._active) { _instance._active = false; _instance._router.Deactivate(_instance); }
        _instance._profile = null;
        _instance._hub = null;
        _instance._panelRoot = null;
    }

    // Frame poll (from Mod.OnUpdate). Activate when the panel has slid into view and we aren't already
    // on the stack. Closing is handled by Tick (we're the top handler while open).
    public static void Poll()
    {
        var self = _instance;
        if (self == null || self._active || self._hub == null) return;
        if (!self.PanelOpen()) { self._sawClosed = true; return; }
        if (self._sawClosed) { self._active = true; self._router.Activate(self); }
    }

    public void OnEnter()
    {
        Rebuild();
        _router.Speech.Say(Name, interrupt: true);
        if (_rows.Count > 0) { _row = 0; AnnounceRow(interrupt: false); }
    }

    public void OnResume()
    {
        Rebuild();
        if (_row < 0 || _row >= _rows.Count) _row = _rows.Count > 0 ? 0 : -1;
        // interrupt:false so that when a sub-overlay closed right after speaking something (e.g. Add
        // Friend's "request sent"), this row announce QUEUES after it instead of cutting it off. When
        // nothing's speaking it plays immediately anyway. See [[feedback_speech_queue_single_utterance]].
        if (_row >= 0) AnnounceRow(interrupt: false);
    }

    public void OnExit() { }

    public void RespeakAll()
    {
        Rebuild();
        _router.Speech.Say(Name, interrupt: true);
        if (_row >= 0 && _row < _rows.Count) AnnounceRow(interrupt: false);
    }

    public string StateSignature() => _hub == null ? "Friends|dead" : $"Friends|{_row}|{_rows.Count}";

    public void Tick()
    {
        if (_hub == null) { _active = false; _router.Deactivate(this); return; }
        if (!Application.isFocused) return;

        // The panel slid back out (our Escape's close tween, the X, or a flow that closes it) — leave.
        if (!PanelOpen()) { _active = false; _router.Deactivate(this); return; }

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)
            || (UnityEngine.Input.GetKeyDown(KeyCode.Backspace) && !IsTextInputFocused()))
        {
            ClosePanel();
            return;
        }
        if (_rows.Count == 0) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { Move(1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { Move(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
            Activate();
    }

    private void Move(int dir)
    {
        if (_rows.Count == 0) return;
        int next = Mathf.Clamp(_row + dir, 0, _rows.Count - 1);
        if (next == _row) return;
        _row = next;
        AnnounceRow(interrupt: true);
    }

    private void Activate()
    {
        if (_row < 0 || _row >= _rows.Count || _hub == null) return;
        var row = _rows[_row];
        switch (row.Kind)
        {
            case Kind.AddFriend:
                _hub.OpenAddFriendOverlay();
                break;
            case Kind.Options:
                Invoke(M_SelectFriend, row.Id);
                break;
            case Kind.AcceptRequest:
                Invoke(M_Accept, row.Id);
                break;
            case Kind.DeclineRequest:
                Invoke(M_Decline, row.Id);
                break;
            case Kind.AcceptChallenge:
                AcceptChallenge(row);
                break;
            case Kind.Info:
                break;   // the no-friends message — nothing to activate
        }
    }

    // Accept an incoming challenge: same call the friend cell makes (onAcceptChallenge =
    // OpenSendJoinMatchOverlay(id, outgoing:true, context)).
    private void AcceptChallenge(Row row)
    {
        if (M_OpenSendJoin == null || _hub == null) return;
        FriendDirectMatchContext? ctx = null;
        try { ctx = row.Friend.GetFriendDirectMatchContext(); } catch { ctx = null; }
        try { M_OpenSendJoin.Invoke(_hub, new object?[] { row.Id, true, ctx }); }
        catch (System.Exception e) { Log.Warn($"FriendsPanel: accept challenge failed: {e.Message}"); }
    }

    private void Invoke(MethodInfo? m, string id)
    {
        if (m == null || _hub == null) return;
        try { m.Invoke(_hub, new object[] { id }); }
        catch (System.Exception e) { Log.Warn($"FriendsPanel: {m.Name} failed: {e.Message}"); }
    }

    // Close the slide-out the way the game does — play the close tween. Tick then sees it off-screen
    // and pops us back to the Profile handler.
    private void ClosePanel()
    {
        var tween = ReadField<TweenRectTransform>(_hub!, "tweenerClose");
        if (tween != null) tween.Play();
        else Log.Warn("FriendsPanel: no tweenerClose to close the panel");
    }

    private void Rebuild()
    {
        _rows.Clear();
        if (_hub == null) return;

        _rows.Add(new Row { Kind = Kind.AddFriend, Label = "Add friend" });

        var friends = ReadField<List<FriendInfo>>(_hub, "cachedFriends");
        if (friends == null || friends.Count == 0)
        {
            var msg = NoFriendsMessage();
            if (!string.IsNullOrWhiteSpace(msg))
                _rows.Add(new Row { Kind = Kind.Info, Label = msg! });
            return;
        }

        foreach (var f in friends)
        {
            if (string.IsNullOrEmpty(f.id)) continue;
            var name = string.IsNullOrWhiteSpace(f.screenName) ? "Unknown" : f.screenName;
            if (f.requestType == FriendRequestType.Incoming)
            {
                _rows.Add(new Row { Kind = Kind.AcceptRequest, Label = $"Accept friend request from {name}", Id = f.id, Friend = f });
                _rows.Add(new Row { Kind = Kind.DeclineRequest, Label = $"Decline friend request from {name}", Id = f.id, Friend = f });
            }
            else if (f.challengeRequest != null)
            {
                _rows.Add(new Row { Kind = Kind.AcceptChallenge, Label = $"{name}, battle challenge", Id = f.id, Friend = f });
            }
            else
            {
                _rows.Add(new Row { Kind = Kind.Options, Label = FriendLabel(f, name), Id = f.id, Friend = f });
            }
        }
    }

    private string FriendLabel(FriendInfo f, string name)
    {
        var label = $"{name}, {(f.online ? "online" : "offline")}";
        int rank = Prestige(f);
        if (rank > 0) label = $"{label}, rank {rank}";
        return label;
    }

    private int Prestige(FriendInfo f)
    {
        if (M_Prestige == null || _hub == null) return -1;
        try { return M_Prestige.Invoke(_hub, new object[] { f }) is int n ? n : -1; }
        catch { return -1; }
    }

    private void AnnounceRow(bool interrupt)
    {
        if (_row < 0 || _row >= _rows.Count) return;
        var row = _rows[_row];
        if (row.Kind == Kind.AddFriend || row.Kind == Kind.Info)
        {
            _router.Speech.Say(row.Label, interrupt);
            return;
        }
        // Index/total over the friend rows only (the ADD FRIEND action and the no-friends message
        // aren't list entries).
        int total = 0, index = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind == Kind.AddFriend || _rows[i].Kind == Kind.Info) continue;
            total++;
            if (i <= _row) index = total;
        }
        _router.Speech.Say(Announce.Item(row.Label, description: null, index: index, total: total), interrupt);
    }

    private string? NoFriendsMessage()
    {
        var go = ReadField<GameObject>(_hub!, "noFriendsMessage");
        if (go == null) return null;
        foreach (var t in go.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
            if (!string.IsNullOrWhiteSpace(t.text)) return t.text.Trim();
        return null;
    }

    // The sliding panel root = the RectTransform the close tween animates. Fall back to the
    // controller's own transform if the tween/field isn't resolvable.
    private RectTransform? ResolvePanelRoot()
    {
        if (_hub == null) return null;
        var tween = ReadField<TweenRectTransform>(_hub, "tweenerClose");
        if (tween != null)
        {
            var rt = ReadField<RectTransform>(tween, "_rectTransform");
            if (rt != null) return rt;
        }
        return _hub.transform as RectTransform;
    }

    // "Open" = the panel root is active and substantially on-screen. The close tween slides it off the
    // left edge, so its center crosses off-screen when closed. A near-zero size means layout hasn't run
    // yet (treat as closed) so we don't false-trigger before the panel is real.
    private bool PanelOpen()
    {
        if (_hub == null) return false;
        if (!_hub.gameObject.activeInHierarchy) return false;
        var root = _panelRoot ??= ResolvePanelRoot();
        if (root == null) return _hub.gameObject.activeInHierarchy;

        var corners = new Vector3[4];
        root.GetWorldCorners(corners);
        var cam = ScreenCamera(root);
        var c0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        var c2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        if (Mathf.Abs(c2.x - c0.x) < 2f || Mathf.Abs(c2.y - c0.y) < 2f) return false;   // not laid out
        var center = (c0 + c2) * 0.5f;
        return center.x >= 0f && center.x <= Screen.width && center.y >= 0f && center.y <= Screen.height;
    }

    private static Camera? ScreenCamera(RectTransform rt)
    {
        var canvas = rt.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            return canvas.worldCamera;
        return null;
    }

    private static bool IsTextInputFocused()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<UnityEngine.UI.InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
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
