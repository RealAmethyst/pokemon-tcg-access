using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Reward overlays — the season-start "NEW LADDER" popup, plus league / tier / level-up / Build-and-
// Battle reward overlays. Driver: RewardOverlaysController activates a generic ContentItemRewardOverlay
// (an OverlayScreenGroup: title `titleTxt`, reward items in `rewardHolder`, a call-to-action button
// whose text is `buttonTxt`, e.g. "COLLECT REWARDS") plus a per-type custom view. COLLECT REWARDS
// routes through RewardOverlaysController.CloseScreen(true) → activeCustomView.OnCallToAction.
//
// These pop OVER the HUB without pausing it (they're overlays, not screen transitions), so without a
// handler the underlying screen (Home) keeps keyboard focus *behind* the modal and the button is
// unreachable — observed right after the FTUE as a season-start ladder reward you couldn't collect.
//
// We hook ActivateCustomView (present) / CloseScreen (dismiss), read the title + reward count, and
// present the overlay's real buttons (COLLECT REWARDS, close, booster odds) for Enter.
internal sealed class RewardOverlayScreen : IScreenHandler
{
    private static RewardOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private RewardOverlaysController? _controller;
    private bool _active;
    private readonly KeyboardNavigator _nav;

    public string Name => "Rewards";

    public RewardOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new RewardOverlayScreen(router);

        var activate = AccessTools.Method(typeof(RewardOverlaysController), "ActivateCustomView");
        if (activate == null)
        {
            Log.Error("RewardOverlayScreen: could not resolve RewardOverlaysController.ActivateCustomView");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(RewardOverlayScreen), nameof(Activate_Postfix))));

        var close = AccessTools.Method(typeof(RewardOverlaysController), "CloseScreen");
        if (close != null)
            harmony.Patch(close, postfix: new HarmonyMethod(AccessTools.Method(typeof(RewardOverlayScreen), nameof(Close_Postfix))));

        Log.Info("RewardOverlayScreen installed");
    }

    private static void Activate_Postfix(RewardOverlaysController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    private static void Close_Postfix(RewardOverlaysController __instance)
    {
        if (_instance == null || _instance._controller != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _controller == null ? "Rewards|dead" : $"Rewards|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_controller == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;
        // Backstop: if the overlay closed without going through CloseScreen (e.g. CloseAllOverlays),
        // let go so the HUB underneath regains focus.
        var ov = _controller.activeOverlay;
        if (ov == null || !ov.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _controller = null; return;
        }
        _nav.Tick();
    }

    private void Rebuild(bool announce)
    {
        if (_controller == null) return;
        var items = CollectButtons();
        _nav.SetItems(items);
        _nav.AllowHorizontal = true;   // reward buttons sit in a row
        if (!announce) return;

        var title = OverlayTitle();
        if (!string.IsNullOrWhiteSpace(title))
        {
            _router.Speech.Say(title!, interrupt: true);
            Log.Info($"RewardOverlay: {title} ({items.Count} buttons)");
        }
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: string.IsNullOrWhiteSpace(title));
        else _router.Speech.Say(Name, interrupt: string.IsNullOrWhiteSpace(title));
    }

    private string? OverlayTitle()
    {
        if (_controller?.activeOverlay is not ContentItemRewardOverlay ov) return null;
        var parts = new List<string>();
        var title = TmpText(ov.titleTxt);
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title!);
        var count = TmpText(ov.contentCountTxt);
        if (!string.IsNullOrWhiteSpace(count)) parts.Add(count!);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // All active buttons in the overlay + its controller (default COLLECT button lives on the
    // controller; close / reward slots live on the overlay). Ordered top-to-bottom, left-to-right.
    private List<Selectable> CollectButtons()
    {
        var result = new List<Selectable>();
        if (_controller == null) return result;
        var seen = new HashSet<Selectable>();
        void Sweep(GameObject? root)
        {
            if (root == null) return;
            foreach (var b in root.GetComponentsInChildren<Button>(includeInactive: false))
                if (b.isActiveAndEnabled && seen.Add(b)) result.Add(b);
        }
        Sweep(_controller.gameObject);
        if (_controller.activeOverlay != null) Sweep(_controller.activeOverlay.gameObject);
        return result.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    private static string? TmpText(Component? c)
    {
        if (c == null || !c.gameObject.activeInHierarchy) return null;
        var tmp = c.GetComponent<TMPro.TMP_Text>() ?? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        return tmp != null ? tmp.text : null;
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
