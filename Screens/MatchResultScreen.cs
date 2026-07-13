using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// End-of-match result screen — driver MatchResultsScreenOverlay. Online / vs-people matches end with
// this animated outro (win/loss splash → victor catchphrase → match stats → rewards → return to main
// menu). It's not a battle prompt and BattleScreen has already torn down, so without a handler the
// player heard nothing and had no way to advance past the stats page (which waits indefinitely for a
// "tap").
//
// We hook Show(...) — it carries `won` + the localized game-end reason — to announce the outcome, and
// drive the screen's own advance: Enter taps through the stages (SetButtonTappedFlag, the same flag a
// click sets) and, once the Return-to-main-menu button is up, presses it (its onClick = Continue).
//
// The rewards page (the "MATCH REWARDS" stage near the end — XP / Battle Pass XP / rank points) has no
// readable label text (the values sit under icon-only hexes), so we also hook MatchRewardView.SetupAndShow
// — the game's own "rewards now shown" call — and read the three count LocTexts (whichever are visible
// for this mode) plus any Build-and-Battle item rewards. CONTINUE on that page is returnToMainMenuButton,
// already driven by Advance().
//
// The match STATS page (the screen with both players' attack-damage / coin-flips panels + CONTINUE /
// BATTLE LOG / ADD FRIEND buttons) is its own stage — MatchResultsStats.ShowStatsPage. We hook that to
// read the stats out loud and present its buttons as a real list (CONTINUE advances, BATTLE LOG opens the
// log, ADD FRIEND sends a request), instead of the blind Enter-tap that drove the other stages.
internal sealed class MatchResultScreen : IScreenHandler
{
    private static MatchResultScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly Navigation.KeyboardNavigator _statsNav;
    private MatchResultsScreenOverlay? _overlay;
    private bool _active;
    private string _outcome = "";
    private bool _onStatsPage;
    private string _lastStatsButtonSig = "";
    private bool _returnAnnounced;

    private static FieldInfo? _returnButtonField;

    public string Name => "Match result";

    public MatchResultScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _statsNav = new Navigation.KeyboardNavigator(_router.Speech) { AllowTab = false };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        try
        {
            new MatchResultScreen(router);
            var show = AccessTools.Method(typeof(MatchResultsScreenOverlay), nameof(MatchResultsScreenOverlay.Show));
            if (show == null) { Log.Error("MatchResultScreen: MatchResultsScreenOverlay.Show not found"); return; }
            harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchResultScreen), nameof(Show_Postfix))));

            // Rewards page: announce the breakdown when the game populates + shows it.
            var rewardsShow = AccessTools.Method(typeof(MatchRewardView), nameof(MatchRewardView.SetupAndShow));
            if (rewardsShow != null)
                harmony.Patch(rewardsShow, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchResultScreen), nameof(Rewards_Postfix))));
            else Log.Warn("MatchResultScreen: MatchRewardView.SetupAndShow not found");

            // Stats page: read the stats + present its buttons; revert to Enter-tap when it hides.
            var statsShow = AccessTools.Method(typeof(MatchResultsStats), nameof(MatchResultsStats.ShowStatsPage));
            if (statsShow != null)
                harmony.Patch(statsShow, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchResultScreen), nameof(StatsShow_Postfix))));
            var statsHide = AccessTools.Method(typeof(MatchResultsStats), nameof(MatchResultsStats.HideStatsPage));
            if (statsHide != null)
                harmony.Patch(statsHide, postfix: new HarmonyMethod(AccessTools.Method(typeof(MatchResultScreen), nameof(StatsHide_Postfix))));

            Log.Info("MatchResultScreen installed");
        }
        catch (Exception ex) { Log.Error($"MatchResultScreen.Install threw: {ex.Message}"); }
    }

    private static void Show_Postfix(MatchResultsScreenOverlay __instance, bool won, string gameEndReasonLocID)
    {
        if (_instance == null) return;
        try { _instance.OnShow(__instance, won, gameEndReasonLocID); }
        catch (Exception ex) { Log.Error($"MatchResultScreen.OnShow threw: {ex.Message}"); }
    }

    private static void StatsShow_Postfix(MatchResultsStats __instance)
    {
        if (_instance == null) return;
        try { _instance.OnStatsPage(__instance); }
        catch (Exception ex) { Log.Error($"MatchResultScreen.OnStatsPage threw: {ex.Message}"); }
    }

    private static void StatsHide_Postfix()
    {
        if (_instance != null) _instance._onStatsPage = false;
    }

    // The stats page appeared: read both players' stats + the outcome, then present its action buttons
    // (CONTINUE / BATTLE LOG / ADD FRIEND) as a navigable list instead of the blind Enter-tap.
    private void OnStatsPage(MatchResultsStats stats)
    {
        _onStatsPage = true;
        var line = StatsAnnouncement(stats);
        if (!string.IsNullOrWhiteSpace(line)) _router.Speech.Say(line, interrupt: true);
        RebuildStatsButtons();
        _lastStatsButtonSig = StatsButtonSig();
        if (_statsNav.Count > 0) FocusStatsDefault(interruptPrior: string.IsNullOrWhiteSpace(line));
    }

    // Land on the SAFE button — the last one (the result screen's primary "move on": Continue / Close)
    // — and never on Add Friend. Focus first landing on Add Friend let an Enter fire a friend request
    // at the opponent by accident (Amethyst's call). Add Friend stays reachable by arrowing up.
    private void FocusStatsDefault(bool interruptPrior)
    {
        if (_statsNav.Count == 0) return;
        var addFriend = AddFriendButton();
        int target = _statsNav.Count - 1;
        while (target > 0 && ReferenceEquals(_statsNav.Items[target], addFriend)) target--;
        _statsNav.FocusIndex(target, announce: true, interrupt: interruptPrior);
    }

    // The "send friend request" button on the stats page (AddFriendFromMatchResults._sendRequestButton),
    // so the default focus can avoid it.
    private Button? AddFriendButton()
    {
        if (_overlay == null) return null;
        try
        {
            var comp = _overlay.GetComponentInChildren<AddFriendFromMatchResults>(includeInactive: true);
            return comp != null ? ReadField<Button>(comp, "_sendRequestButton") : null;
        }
        catch { return null; }
    }

    private string StatsAnnouncement(MatchResultsStats stats)
    {
        var parts = new List<string>();
        var header = LocOf(ReadField<LocText>(stats, "headerText"));            // VICTORY / DEFEAT
        if (!string.IsNullOrWhiteSpace(header)) parts.Add(header!);
        var reason = LocOf(ReadField<LocText>(stats, "gameEndReasonText"));     // "Knocked Out with no Benched Pokémon"
        if (!string.IsNullOrWhiteSpace(reason)) parts.Add(reason!);
        var p = StatLine(ReadField<Component>(stats, "playerStats"));
        if (!string.IsNullOrWhiteSpace(p)) parts.Add(p!);
        var o = StatLine(ReadField<Component>(stats, "opponentStats"));
        if (!string.IsNullOrWhiteSpace(o)) parts.Add(o!);
        return string.Join(". ", parts);
    }

    // "<player name>: <stat label> <value>, <stat label> <value>" for one MatchResultsStatsPage.
    private static string? StatLine(Component? page)
    {
        if (page == null) return null;
        var name = LocOf(ReadField<LocText>(page, "playerNameDisplay"));
        var entries = new[] { "totalDamageStat", "coinFlipStat" }
            .Select(f => StatEntry(ReadField<Component>(page, f)))
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var stat = string.Join(", ", entries);
        if (string.IsNullOrWhiteSpace(name)) return string.IsNullOrWhiteSpace(stat) ? null : stat;
        return string.IsNullOrWhiteSpace(stat) ? name : $"{name}: {stat}";
    }

    // "<stat label> <value>" for a StatPageEntry (label + count are both LocTexts).
    private static string? StatEntry(Component? entry)
    {
        if (entry == null) return null;
        var label = LocOf(ReadField<LocText>(entry, "statNameDisplay"));
        var value = LocOf(ReadField<LocText>(entry, "statCountDisplay"));
        if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value)) return null;
        return $"{label} {value}".Trim();
    }

    // The stats page's action buttons (CONTINUE / BATTLE LOG / ADD FRIEND), in reading order.
    private void RebuildStatsButtons()
    {
        if (_overlay == null) return;
        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.IsInteractable())
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        _statsNav.SetItems(buttons);
        _statsNav.ForceFlatVertical();   // CONTINUE / BATTLE LOG / ADD FRIEND — a small vertical list
    }

    private string StatsButtonSig()
    {
        if (_overlay == null) return "";
        var bs = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.IsInteractable()).Select(b => b.gameObject.name);
        return string.Join(",", bs);
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }

    private static void Rewards_Postfix(MatchRewardView __instance)
    {
        if (_instance == null || !_instance._active) return;
        try { _instance.AnnounceRewards(__instance); }
        catch (Exception ex) { Log.Error($"MatchResultScreen.AnnounceRewards threw: {ex.Message}"); }
    }

    // Read the rewards page and speak its breakdown. The three XP counts are private SerializeField
    // LocTexts whose own icons carry no words; we name each and read its formatted count ("+200"),
    // including only the tiles actually visible for this mode (rank points show only in Ranked, the
    // Battle-Pass tile only when there's an active challenge). Build-and-Battle item rewards (rare
    // mode) are read by name from the spawned slots so nothing is silently dropped.
    private void AnnounceRewards(MatchRewardView view)
    {
        var parts = new List<string>();
        AddCount(parts, view, "playerXPCount", "Experience");
        AddCount(parts, view, "challengeXPCount", "Battle Pass experience");
        AddCount(parts, view, "rankXPCount", "Rank points");

        var bnb = ReadField<Transform>(view, "buildAndBattleRewardsHolder");
        if (bnb != null && bnb.gameObject.activeInHierarchy)
            foreach (var slot in bnb.GetComponentsInChildren<ProductItemSummarySlot>(includeInactive: false))
            {
                var item = ReadField<object>(slot, "currentItem") is ItemData d ? d : default;
                if (item.itemType == ItemData.ItemType.None) continue;
                int n = ReadField<object>(slot, "numberOfItems") is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
                var name = ItemName(item);
                if (!string.IsNullOrWhiteSpace(name)) parts.Add(n > 1 ? $"{name} times {n}" : name);
            }

        var header = LocalizationHook.Get("match_results_match_rewards");
        if (string.IsNullOrWhiteSpace(header)) header = "Match rewards";
        var body = parts.Count > 0 ? $"{header}. {string.Join(". ", parts)}" : header!;
        _router.Speech.Say(body, interrupt: true);
        Log.Info($"Match rewards: {string.Join(" | ", parts)}");
    }

    // Append "<label>, <count>" if that count tile is visible and non-empty. The game formats the count
    // text itself ("+200" / "0"); we read it verbatim so the spoken value matches the screen.
    private static void AddCount(List<string> parts, MatchRewardView view, string field, string label)
    {
        var loc = ReadField<LocText>(view, field);
        if (loc == null || !loc.gameObject.activeInHierarchy) return;
        var text = loc.CurrentText;
        if (string.IsNullOrWhiteSpace(text)) return;
        parts.Add($"{label}, {text.Trim()}");
    }

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
        var loc = LocalizationHook.Get(item.contentLocId);
        if (!string.IsNullOrWhiteSpace(loc) && !loc!.Contains("(Missing)")) return loc.Trim();
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

    private void OnShow(MatchResultsScreenOverlay overlay, bool won, string gameEndReasonLocID)
    {
        _overlay = overlay;
        _returnAnnounced = false;
        var reason = LocalizationHook.Get(gameEndReasonLocID);
        _outcome = won ? "You won." : "You lost.";
        if (!string.IsNullOrWhiteSpace(reason)) _outcome += " " + reason;
        if (!_active) { _active = true; _router.Activate(this); }
        else Announce();
    }

    public void OnEnter() => Announce();
    public void OnResume() => Announce();
    public void OnExit() { }
    public void RespeakAll() => Announce();

    public string StateSignature() => _overlay == null ? "MatchResult|dead" : $"MatchResult|{_outcome}";

    private void Announce() => _router.Speech.Say(_outcome, interrupt: true);

    public void Tick()
    {
        // The overlay lives on the Match scene; when it unloads (return to menu) the reference dies.
        if (_overlay == null || _overlay.gameObject == null)
        {
            _active = false; _router.Deactivate(this);
            return;
        }
        if (!Application.isFocused) return;

        // On the stats page, navigate its real buttons (CONTINUE / BATTLE LOG / ADD FRIEND) — they can
        // appear a frame after ShowStatsPage, so re-present + announce when the set first fills in.
        if (_onStatsPage)
        {
            var sig = StatsButtonSig();
            if (sig != _lastStatsButtonSig)
            {
                bool hadNone = _statsNav.Count == 0;
                _lastStatsButtonSig = sig;
                RebuildStatsButtons();
                if (hadNone && _statsNav.Count > 0) FocusStatsDefault(interruptPrior: true);
                return;
            }
            _statsNav.Tick();
            return;
        }

        // When the return-to-game button appears (rewards / final page), announce it once by its real
        // label so the player hears the result is over and what advances — instead of pressing blind and
        // only getting feedback after. Enter still advances through the game's own flow (Advance).
        var rb = ReturnButton(_overlay);
        bool rbAvail = rb != null && rb.isActiveAndEnabled && rb.IsInteractable();
        if (rbAvail && !_returnAnnounced) { _returnAnnounced = true; _router.Speech.Say(Navigation.KeyboardNavigator.DefaultLabel(rb!), interrupt: false); }
        else if (!rbAvail) _returnAnnounced = false;

        // Other stages (win/loss splash, victor catchphrase, rewards) advance on the game's own tap flag.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
            || UnityEngine.Input.GetKeyDown(KeyCode.Space))
            Advance();
    }

    // Move the result screen forward: if the Return-to-main-menu button is up, press it (its onClick
    // runs Continue → back to the HUB); otherwise tap through the current stage (the stats page waits
    // on exactly this flag). Both are the game's own advance paths — no parallel transition.
    private void Advance()
    {
        if (_overlay == null) return;
        try
        {
            var btn = ReturnButton(_overlay);
            if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
            {
                // No speech here: the button was already announced when it appeared, and pressing it
                // transitions to the HUB (which announces itself). Speaking only on press was the bug.
                btn.onClick.Invoke();
                return;
            }
            _overlay.SetButtonTappedFlag();
        }
        catch (Exception ex) { Log.Error($"MatchResultScreen.Advance threw: {ex.Message}"); }
    }

    private static Button? ReturnButton(MatchResultsScreenOverlay overlay)
    {
        _returnButtonField ??= typeof(MatchResultsScreenOverlay).GetField(
            "returnToMainMenuButton", BindingFlags.Instance | BindingFlags.NonPublic);
        return _returnButtonField?.GetValue(overlay) as Button;
    }
}
