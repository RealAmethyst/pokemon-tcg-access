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
// opened when a friend is selected in the panel (FriendsHUBScreenController.SelectFriend, usePopupOptions).
// The two real choices are CHALLENGE (interactable only when the friend is online) and REMOVE — known
// fields challengeOption / removeOption. The prefab also carries several close affordances (a visible X,
// a bottom close, and an invisible tap-outside blocker), so a blind GetComponentsInChildren<Button> sweep
// listed three "Close" entries. We present the two options + exactly ONE close (the others are filtered:
// full-screen blockers, and icon children nested inside another button). Escape also closes (Close()).
//
// Hooks the shared base OverlayScreenGroup.OnActivate with an `is` guard (uniform with the other overlay
// handlers; see [[feedback-overlay-hook-hazard]]). Dismissal is the Tick backstop.
internal sealed class FriendOptionsOverlayScreen : IScreenHandler
{
    private static FriendOptionsOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private FriendOptionsOverlayScreenGroup? _overlay;
    private Selectable? _closeButton;
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

        // Enter on our "Close" item routes through the overlay's real Close() — the same call Escape
        // makes — rather than the button's own onClick, which (on the X we surface) does nothing.
        if (_closeButton != null && _nav.Current == _closeButton
            && (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)))
        {
            Close();
            return;
        }
        _nav.Tick();
    }

    private void Close() => _overlay?.Close();

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        // The two real choices, from the known fields (Challenge greys out when the friend is offline —
        // KeyboardNavigator still lists it and says "unavailable").
        var items = new List<Selectable>();
        var challenge = ReadField<Button>(_overlay, "challengeOption");
        var remove = ReadField<Button>(_overlay, "removeOption");
        var known = new HashSet<Button>();
        if (challenge != null && challenge.isActiveAndEnabled) { items.Add(challenge); known.Add(challenge); }
        if (remove != null && remove.isActiveAndEnabled) { items.Add(remove); known.Add(remove); }

        // Exactly one close. Candidates = real buttons that aren't the options, dropping the invisible
        // full-screen blocker and icon children nested in another button. Prefer the one actually named
        // "close" (the prefab also has a styled red button we don't want); else the topmost.
        var candidates = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && !known.Contains(b))
            .Where(b => !IsNested(b, _overlay.transform))
            .Where(b => !IsFullScreen(b))
            .OrderBy(b => ReadingOrder(b.transform))
            .ToList();
        _closeButton = candidates.FirstOrDefault(b => b.gameObject.name.ToLowerInvariant().Contains("close"))
                       ?? candidates.FirstOrDefault();
        if (_closeButton != null) items.Add(_closeButton);

        _nav.SetItems(items, OptionLabel);
        _nav.AllowVertical = true;
        _nav.AllowHorizontal = true;   // small button set — both axes so nothing is stranded by auto-detect
        if (!announce) return;

        var header = Header();
        _router.Speech.Say(string.IsNullOrWhiteSpace(header) ? Name : header!, interrupt: true);
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        LogButtons(known);
    }

    // The close item reads "Close" regardless of its underlying GameObject name (could be a bare "Close"
    // or a styled DynamicButton); the option buttons keep their own labels.
    private string OptionLabel(Selectable s)
        => s == _closeButton ? "Close" : KeyboardNavigator.DefaultLabel(s);

    private string Header()
    {
        var t = ReadField<TMP_Text>(_overlay!, "headerText");
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? Name : s!.Trim();
    }

    // A button nested inside another button is a decorative icon child, not a separate control.
    private static bool IsNested(Button b, Transform root)
    {
        for (var t = b.transform.parent; t != null && t != root; t = t.parent)
            if (t.GetComponent<Button>() != null) return true;
        return false;
    }

    // An invisible "tap outside to dismiss" blocker covers most of the screen — never a real control.
    private static bool IsFullScreen(Button b)
    {
        if (b.transform is not RectTransform rt) return false;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        var canvas = b.GetComponentInParent<Canvas>();
        Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        float w = Mathf.Abs(max.x - min.x), h = Mathf.Abs(max.y - min.y);
        return w * h > 0.6f * Screen.width * Screen.height;
    }

    private void LogButtons(HashSet<Button> known)
    {
        if (_overlay == null) return;
        var all = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Select(b => $"{b.gameObject.name}{(known.Contains(b) ? "(opt)" : IsNested(b, _overlay.transform) ? "(nested)" : IsFullScreen(b) ? "(blocker)" : "")}");
        Log.Info($"FriendOptions: {Header()} | buttons=[{string.Join(", ", all)}]");
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
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
