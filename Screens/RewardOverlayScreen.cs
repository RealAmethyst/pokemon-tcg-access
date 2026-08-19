using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
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
    private Selectable? _lastFtueTarget;

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

        // The tutorial coaches this overlay via FTUERewardsPopupIndicator → RewardsPopupButtonController,
        // whose Focus() starts a coroutine that waits for OverlayManager.currentOverlay to BE the reward
        // overlay before it highlights COLLECT REWARDS. So the focus always lands after we've opened and
        // announced (measured: 180 ms later) — checking for it in OnEnter would always miss it.
        FTUEHook.FocusedElementChanged += () => _instance?.OnFtueFocusChanged();

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
        if (_controller == null) { _active = false; _router.Deactivate(this); return; }
        if (!Application.isFocused) return;
        // Backstop: let go so the HUB underneath regains focus once the reward overlay is no longer
        // showing. CRITICAL: a HUB overlay closed via OverlayManager (OnDeActivate) only disables its
        // CANVAS — the GameObject stays activeInHierarchy — so that flag alone is unreliable (a nested
        // reward, e.g. claiming on Casual → an item-grant reveal pops back to here, left us STUCK).
        // The authoritative signal is OverlayManager: when our overlay is no longer the current open
        // overlay, the flow is done. (Reward overlays register with the global OverlayManager — same
        // ManagerSingleton<OverlayManager>.currentOverlay the game's own IsCurrentOverlay checks.)
        var ov = _controller.activeOverlay;
        if (ov == null || !ov.gameObject.activeInHierarchy || !IsOverlayCurrent(ov))
        {
            _active = false; _router.Deactivate(this); _controller = null; return;
        }
        // SECOND stranding mode (post-match reward freeze, from a real log): collecting a reward opens a
        // nested item-grant reveal OVER this overlay; when that reveal finishes the controller tears down
        // THIS overlay's reward content (its COLLECT button + slots) but does NOT fire CloseScreen or
        // deactivate the GameObject — so we resume onto an overlay that's still active + the current
        // overlay yet has nothing left to read or press (log: "Screen resume: Rewards" then dead silence,
        // and no "RewardOverlay:" line = empty title). A live reward overlay always rebuilt at least one
        // button (COLLECT / close); zero after a Rebuild means it's spent — let go so Home resumes.
        if (_nav.Count == 0) { _active = false; _router.Deactivate(this); _controller = null; return; }
        _nav.Tick();
    }

    private static bool IsOverlayCurrent(OverlayScreenGroup ov)
    {
        try { return ManagerSingleton<OverlayManager>.instance?.currentOverlay == ov; }
        catch { return true; }   // if the manager isn't reachable, don't yank focus prematurely
    }

    // The one button the tutorial wants pressed on THIS overlay, or null when it isn't coaching us.
    //
    // MenuButtonController.Focus REPARENTS the coached Selectable under an input blocker and leaves every
    // other button behind it — unclickable. Two consequences: CollectButtons() can no longer even find
    // COLLECT REWARDS (it's no longer a child of the controller or the overlay), and focusing anything
    // else hands the player a button the game will ignore. So while we're coached, we present exactly the
    // coached button and nothing else.
    //
    // isOverlay is what tells us the focus is OURS. When this overlay opens, the FTUE is still pointing at
    // Btn_TierReward on the Battle Pass screen behind us (reparented under the MAIN blocker); following
    // that blindly would focus a button on another screen. The overlay's coached button goes under the
    // OVERLAY blocker instead, and FocusedElement.isOverlay records which.
    private static Selectable? FtueTarget()
    {
        if (!FTUEHook.FtueIsRunning || !FTUEHook.FocusedElementIsOverlay) return null;
        var target = FTUEHook.FocusedElement;
        return target != null && target.isActiveAndEnabled ? target : null;
    }

    private void OnFtueFocusChanged()
    {
        if (!_active || _controller == null) return;
        var target = FtueTarget();
        if (ReferenceEquals(target, _lastFtueTarget)) return;
        // Focus released — the coached button was pressed and the flow is moving on (an item-grant reveal
        // opens over us). Restore the full button set silently rather than talking over what comes next.
        if (target == null) { Rebuild(announce: false); return; }
        // The title + reward contents were spoken when the overlay opened; only the new focus is news.
        Rebuild(announce: true, speakTitle: false);
    }

    private void Rebuild(bool announce, bool speakTitle = true)
    {
        if (_controller == null) return;

        var ftue = FtueTarget();
        _lastFtueTarget = ftue;

        var items = ftue != null ? new List<Selectable> { ftue } : CollectButtons();
        _nav.SetItems(items, RewardButtonLabel);
        _nav.AllowHorizontal = ftue == null;   // reward buttons sit in a row; a coached one is alone
        if (!announce) return;

        var title = speakTitle ? OverlayTitle() : null;
        if (!string.IsNullOrWhiteSpace(title))
        {
            _router.Speech.Say(title!, interrupt: true);
            Log.Info($"RewardOverlay: {title} ({items.Count} buttons{(ftue != null ? ", tutorial-focused" : "")})");
        }
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: string.IsNullOrWhiteSpace(title));
        else _router.Speech.Say(Name, interrupt: string.IsNullOrWhiteSpace(title));
    }

    // "<header>, <rewards>, <status>". The header + status come from the active custom view when it's
    // a single-tier reward detail (its tierTxt "TIER 1" + bottom message "already collected"); other
    // reward overlays (generic COLLECT, league, level-up, …) fall back to the generic overlay's title.
    // The rewards themselves are read here by name instead of being navigated as bare "x1" slots.
    private string? OverlayTitle()
    {
        var parts = new List<string>();
        var tier = _controller?.activeCustomView as SingleTierCustomView;
        if (tier != null)
        {
            var t = TmpText(tier.tierTxt);
            if (!string.IsNullOrWhiteSpace(t)) parts.Add(NumericToTier(t!));
        }
        else if (_controller?.activeOverlay is ContentItemRewardOverlay genericOv)
        {
            var title = TmpText(genericOv.titleTxt);
            if (!string.IsNullOrWhiteSpace(title)) parts.Add(title!);
            var count = TmpText(genericOv.contentCountTxt);
            if (!string.IsNullOrWhiteSpace(count)) parts.Add(count!);
        }

        var rewards = RewardsSummary();
        if (!string.IsNullOrWhiteSpace(rewards))
        {
            // Frame a bare item list. These tiles ARE visible to a sighted player before collecting,
            // but without a leading word the list read as if the mod were leaking the pack's hidden
            // contents (reported 2026-07-23 — the loose reward cards were mistaken for pack cards).
            if (parts.Count == 0) parts.Add("Rewards");
            parts.Add(rewards!);
        }

        if (tier != null)
        {
            var msg = TmpText(tier.bottomMessageTxt);
            if (!string.IsNullOrWhiteSpace(msg)) parts.Add(msg!);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // The reward items, read by name (card / booster / currency) from the spawned reward slots, so
    // the contents are spoken in the announcement rather than focused as bare "x1" count buttons.
    private string? RewardsSummary()
    {
        if (_controller?.activeOverlay is not ContentItemRewardOverlay ov) return null;
        return SummarizeRewards(ov.rewardHolder);
    }

    // Read a reward holder's spawned ProductItemSummarySlots by name + count. Shared with the season
    // ladder overlay (SeasonResetOverlayScreen), which builds the same slots from its own rewardHolder.
    internal static string? SummarizeRewards(Transform? rewardHolder)
    {
        if (rewardHolder == null) return null;
        var parts = new List<string>();
        foreach (var slot in rewardHolder.GetComponentsInChildren<ProductItemSummarySlot>(includeInactive: false))
        {
            var item = ReadFieldObj(slot, "currentItem") is ItemData d ? d : default;
            if (item.itemType == ItemData.ItemType.None) continue;
            int n = ReadFieldObj(slot, "numberOfItems") is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
            var name = ItemName(item);
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(n > 1 ? $"{name} times {n}" : name);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // The overlay's actionable buttons (COLLECT REWARDS / UNLOCK, DROP RATES, close). The reward
    // slots themselves are excluded — their contents are spoken via RewardsSummary instead of being
    // navigated as bare "x1" buttons. Ordered top-to-bottom, left-to-right.
    private List<Selectable> CollectButtons()
    {
        var result = new List<Selectable>();
        if (_controller == null) return result;
        var seen = new HashSet<Selectable>();
        void Sweep(GameObject? root)
        {
            if (root == null) return;
            foreach (var b in root.GetComponentsInChildren<Button>(includeInactive: false))
                if (b.isActiveAndEnabled && b.GetComponentInParent<ProductItemSummarySlot>() == null && seen.Add(b))
                    result.Add(b);
        }
        Sweep(_controller.gameObject);
        if (_controller.activeOverlay != null) Sweep(_controller.activeOverlay.gameObject);
        return result.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    // Worded buttons (DROP RATES, COLLECT REWARDS, UNLOCK) keep their text; the icon-only "X" reads
    // "Close".
    private static string RewardButtonLabel(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
            if (!string.IsNullOrEmpty(t.text) && t.text.Any(char.IsLetter)) return KeyboardNavigator.DefaultLabel(s);
        return "Close";
    }

    private static string NumericToTier(string s)
        => s.Any(char.IsLetter) ? s : $"Tier {s}";

    private static string ItemName(ItemData item)
    {
        if (item.itemType == ItemData.ItemType.Card && !string.IsNullOrEmpty(item.clientId))
        {
            try
            {
                var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(item.clientId);
                if (row != null) return CardText.NameWithType(row);
            }
            catch { }
        }
        switch (item.itemType)
        {
            case ItemData.ItemType.HardCurrency: return ShopLabels.CurrencyWord(CurrencyType.HardCurrency);
            case ItemData.ItemType.TraderCurrency: return ShopLabels.CurrencyWord(CurrencyType.TradeCurrency);
            case ItemData.ItemType.TokenCurrency: return ShopLabels.CurrencyWord(CurrencyType.TokenCurrency);
            case ItemData.ItemType.SoftCurrency: return ShopLabels.CurrencyWord(CurrencyType.SoftCurrency);
        }
        var loc = Loc(item.contentLocId);
        if (loc != null) return loc;
        // A booster's display name: the game titles it by using its clientId AS the loc key
        // (ContentItemRewardUI.SetTitle default case → SetTextByID(clientId)) — resolve the same key
        // and speak it VERBATIM: the loc string is the full display name and already ends in
        // "Booster Pack" ("Mega Evolution—Pitch Black Booster Pack", screenshot-verified 2026-07-23),
        // so appending anything would double it.
        if (item.itemType == ItemData.ItemType.Booster)
        {
            var packName = Loc(item.clientId);
            if (packName != null) return packName;
        }
        return item.itemType switch
        {
            ItemData.ItemType.Booster => "booster pack",
            ItemData.ItemType.Coin => "coin",
            ItemData.ItemType.Sleeve => "card sleeve",
            ItemData.ItemType.DeckBox => "deck box",
            ItemData.ItemType.Deck => "deck",
            ItemData.ItemType.Avatar => "avatar item",
            _ => "item",
        };
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) return null;
        return s.Trim();
    }

    private static object? ReadFieldObj(object? instance, string name)
    {
        if (instance == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    internal static string? TmpText(Component? c)
    {
        if (c == null || !c.gameObject.activeInHierarchy) return null;
        var tmp = c.GetComponent<TMPro.TMP_Text>() ?? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        return tmp != null ? tmp.text : null;
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
