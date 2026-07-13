using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes;
using TPCI.Coroutines;
using TPCI.Localization;
using TPCI.Rainier.BattlePass;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// BATTLE PASS screen — driver HUBExpansionChallengeScreenController (a HUBScreenController, so the
// lifecycle hook is the standard postfix OnActivate / OnDeactivate, same as HOME / Shop / Profile).
//
// The screen has no sub-tabs; it's a single stacked layout we present as a section stack (Up/Down
// escapes between sections, Left/Right within, Tab cycles — the same model as HOME / Shop):
//   1. HUB top tab strip (HOME / DECKS / … / CARD DEX) + overflow "⋮".
//   2. Battle Pass header controls (the "(i)" info button / premium-pass button). The pass STATUS
//      itself — name, time left, level, experience — is pure read-out, so it's spoken as the screen
//      intro and on every window-refocus, and the info button re-reads it when focused.
//   3. Daily quests (the three quest cards). Each focuses the card's full-panel button and reads
//      "<description>, <progress>, reward …, <state>"; Enter does the card's native action
//      (collect when ready, replace/reroll otherwise — exactly what clicking the card does).
//   4. Rewards: the COLLECT ALL button (when there's more than one reward to claim) plus the tier
//      reward buttons. Each tier reads "Tier <n>, <reward>, <locked / ready to collect / claimed>";
//      Enter invokes the tier's real reward button (collect or unlock).
//
// Everything is the game's own buttons + KeyboardNavigator; Enter fires each button's wired onClick,
// so there's no parallel "ops" path (see [[feedback-drive-game-ui-not-ops]]). All spoken text is
// read live from the game's LocTexts / battle-pass data.
//
// NOTE: the tier track is a virtualized EnhancedScroller, so only the cells the game currently has
// spawned (the screenful around the player's level) expose reward buttons — the rest of a long pass
// isn't keyboard-reachable yet (same limitation as the shop's "view all" overlay). COLLECT ALL
// claims every earned reward regardless, which covers the common case.
internal sealed class HUBExpansionChallengeScreen : IScreenHandler
{
    private static HUBExpansionChallengeScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private HUBExpansionChallengeScreenController? _controller;
    private bool _active;

    private bool _hasAnnouncedAtAll;
    private string _structureAtPause = string.Empty;
    private int _indexAtPause = -1;
    private string _lastContentSig = string.Empty;
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;

    private int _questsGroupIndex = -1;
    private int _firstContentGroupIndex = -1;

    public string Name => "Battle Pass";

    public HUBExpansionChallengeScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBExpansionChallengeScreen(router);

        var activate = AccessTools.Method(typeof(HUBExpansionChallengeScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBExpansionChallengeScreen: could not resolve HUBExpansionChallengeScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBExpansionChallengeScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBExpansionChallengeScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBExpansionChallengeScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBExpansionChallengeScreen installed");
    }

    private static void Activate_Postfix(HUBExpansionChallengeScreenController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBExpansionChallengeScreenController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter()
    {
        SubscribeToFtue();

        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }
        Announce();
    }

    public void OnResume()
    {
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }

        if (StructureSignature() != _structureAtPause) Announce();
        else RestoreGroupFocus();
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
        _indexAtPause = (_groups.Count > 0 && _activeGroup >= 0 && _activeGroup < _groups.Count)
            ? _groups[_activeGroup].Index : -1;
    }

    private void RestoreGroupFocus()
    {
        var g = _groups[_activeGroup];
        if (_indexAtPause >= 0 && _indexAtPause < g.Count)
            g.FocusIndex(_indexAtPause, announce: false, interrupt: true);
        else
            g.RefocusCurrent(announce: false);
    }

    public void RespeakAll()
    {
        if (_groups.Count == 0) return;
        _router.Speech.Say(PassStatus(), interrupt: true);
        _groups[_activeGroup].RefocusCurrent(announce: true, interrupt: false);
    }

    public string StateSignature()
    {
        if (_controller == null) return "BattlePass|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"BattlePass|{_activeGroup}|{idx}|{ContentSig()}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            _active = false; _router.Deactivate(this);
            return;
        }
        if (!Application.isFocused) return;

        // React to a content change (quests refreshed, a reward claimed, a level-up scrolling the
        // tier track) by rebuilding the section list. Gated off during FTUE (tutorial-driven focus).
        if (!FTUEHook.FtueIsRunning)
        {
            var sig = ContentSig();
            if (sig != _lastContentSig) { _lastContentSig = sig; OnContentChanged(); return; }
        }

        if (_groups.Count == 0) return;

        var nav = _groups[_activeGroup];

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (_groups.Count > 1)
            {
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        nav.Tick();
    }

    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            _router.Speech.Say(PassStatus(), interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    private string StructureSignature() => string.Join(",", _groups.Select(g => g.Count));

    // ---- group discovery -------------------------------------------------------------------

    private void RebuildGroups()
    {
        _groups.Clear();
        _questsGroupIndex = -1;
        _firstContentGroupIndex = -1;
        if (_controller == null) { _lastContentSig = ContentSig(); return; }

        var known = new HashSet<Selectable>();

        // 1. HUB top tab strip + overflow "⋮" (same discovery as HOME / Shop).
        var topItems = new List<Selectable>();
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            if (navGroups != null)
                foreach (var pnb in navGroups)
                {
                    var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                    if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) topItems.Add(btn);
                }
            var settingsT = ReadField<Transform>(topController, "settingsButton");
            var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
            if (overflow != null && overflow.isActiveAndEnabled && overflow.IsInteractable()) topItems.Add(overflow);
        }
        if (topItems.Count > 0)
        {
            var ordered = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();
            foreach (var s in ordered) known.Add(s);
            AddGroup(ordered, resolver: UiLabels.TopNavLabel);
        }

        // Identify the content pieces first so the header-controls scan can exclude them.
        var questButtons = QuestButtons();
        foreach (var s in questButtons) known.Add(s);

        var collectAll = CollectAllButton();
        var tierButtons = TierButtons();
        foreach (var s in tierButtons) known.Add(s);

        // 2. Header controls (the "(i)" info button, premium-pass button). Everything interactable
        //    that isn't the top strip, a quest card, or a tier reward — i.e. the header region.
        var passItems = HeaderControls(known, collectAll);
        if (passItems.Count > 0)
        {
            foreach (var s in passItems) known.Add(s);
            AddGroup(passItems, resolver: PassLabel, groupName: "Battle Pass");
            if (_firstContentGroupIndex < 0) _firstContentGroupIndex = _groups.Count - 1;
        }

        // 3. Daily quests.
        if (questButtons.Count > 0)
        {
            AddGroup(questButtons, resolver: QuestLabel, groupName: "Daily quests");
            _questsGroupIndex = _groups.Count - 1;
            if (_firstContentGroupIndex < 0) _firstContentGroupIndex = _groups.Count - 1;
        }

        // 4. Rewards — COLLECT ALL (when shown) then the visible tier reward buttons.
        var rewardItems = new List<Selectable>();
        if (collectAll != null && collectAll.isActiveAndEnabled) rewardItems.Add(collectAll);
        rewardItems.AddRange(tierButtons);
        if (rewardItems.Count > 0)
        {
            AddGroup(rewardItems, resolver: TierLabel, groupName: "Rewards");
            if (_firstContentGroupIndex < 0) _firstContentGroupIndex = _groups.Count - 1;
        }

        _lastContentSig = ContentSig();
    }

    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;
        _questsGroupIndex = -1;
        _firstContentGroupIndex = -1;
        _lastContentSig = ContentSig();

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        AddGroup(new List<Selectable> { target });
        _activeGroup = 0;
        // A coached Toggle is a tab, so Enter must SELECT it — see FTUEHook.FocusedElement.
        _groups[0].RadioToggleMode = target is Toggle;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private int PickDefaultGroup()
    {
        if (_questsGroupIndex >= 0 && _questsGroupIndex < _groups.Count) return _questsGroupIndex;
        if (_firstContentGroupIndex >= 0 && _firstContentGroupIndex < _groups.Count) return _firstContentGroupIndex;
        return 0;
    }

    // ---- content-change integration --------------------------------------------------------

    // Signature of the meaningful screen state: quest button count + each quest's claim/ready state,
    // the unlocked/redeemed tier marks, the visible tier-cell count, and whether COLLECT ALL shows.
    // Changes on a quest refresh / claim, a level-up, or the tier track scrolling — the events that
    // alter the section list — so comparing it each tick is a cheap rebuild trigger.
    private string ContentSig()
    {
        if (_controller == null) return "none";
        int unlocked = -1, claimed = -1, tiles = 0;
        bool collectAll = false;
        try { unlocked = _controller.serverData != null ? _controller.serverData.highestUnlockedRank : -1; } catch { }
        try { claimed = _controller.serverData?.highestRedeemedRank != null ? _controller.serverData.highestRedeemedRank.GetHighestClaimedRewardsTier() : -1; } catch { }
        try { var sc = _controller.tiers?.Scroller; if (sc != null) tiles = sc.ActiveCellViews.Count; } catch { }
        try { var ca = CollectAllButton(); collectAll = ca != null && ca.isActiveAndEnabled; } catch { }
        string quests = string.Empty;
        try
        {
            foreach (var p in QuestPanels())
                quests += p != null && p.gameObject.activeInHierarchy
                    ? (HolderActive(p, "readyToCollectHolder") ? "R" : HolderActive(p, "comeBackLaterHolder") ? "C" : "P")
                    : "-";
        }
        catch { }
        return $"{quests}|{unlocked}|{claimed}|{tiles}|{collectAll}";
    }

    private void OnContentChanged()
    {
        int prev = _activeGroup;
        RebuildGroups();
        if (_groups.Count == 0) { _activeGroup = -1; return; }
        _activeGroup = Mathf.Clamp(prev < 0 ? PickDefaultGroup() : prev, 0, _groups.Count - 1);
        _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    // ---- piece discovery -------------------------------------------------------------------

    private List<DailyQuestPanelParts> QuestPanels()
    {
        var list = new List<DailyQuestPanelParts>();
        var q = _controller != null ? _controller.quests : null;
        if (q == null) return list;
        foreach (var name in new[] { "questPanelA", "questPanelB", "questPanelC" })
        {
            var p = ReadField<DailyQuestPanelParts>(q, name);
            if (p != null) list.Add(p);
        }
        return list;
    }

    // Each quest card's full-panel button (its wired onClick = collect when ready / replace
    // otherwise). Kept even when non-interactable (a claimed card) so its state is still readable.
    private List<Selectable> QuestButtons()
    {
        var items = new List<Selectable>();
        foreach (var p in QuestPanels())
        {
            if (p == null || !p.gameObject.activeInHierarchy) continue;
            var btn = ReadField<Button>(p, "_fullPanelButton");
            if (btn != null && btn.isActiveAndEnabled) items.Add(btn);
        }
        return items;
    }

    private Button? CollectAllButton()
    {
        var tiers = _controller != null ? _controller.tiers : null;
        if (tiers == null) return null;
        var holder = ReadField<GameObject>(tiers, "collectAllHolder");
        return holder != null ? holder.GetComponentInChildren<Button>(includeInactive: false) : null;
    }

    // The reward buttons of the tier cells the scroller currently has spawned (the screenful around
    // the player's level), left-to-right.
    private List<Selectable> TierButtons()
    {
        var items = new List<Selectable>();
        var sc = _controller != null && _controller.tiers != null ? _controller.tiers.Scroller : null;
        if (sc == null) return items;
        var cells = sc.ActiveCellViews;
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells.data[i] is ExpansionChallengeTierView view && view.tierRewardController != null)
            {
                var b = view.tierRewardController.RewardButton;
                if (b != null && b.isActiveAndEnabled) items.Add(b);
            }
        }
        return items.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    // The header region's interactable buttons (info "(i)", premium pass), discovered by exclusion:
    // anything under the controller that isn't the top strip, a quest card, a tier reward, or
    // COLLECT ALL. No hardcoded names — just "what's left in the header".
    private List<Selectable> HeaderControls(HashSet<Selectable> known, Button? collectAll)
    {
        var items = new List<Selectable>();
        if (_controller == null) return items;
        var scrollerT = _controller.tiers != null && _controller.tiers.Scroller != null ? _controller.tiers.Scroller.transform : null;
        var questTfs = QuestPanels().Where(p => p != null).Select(p => p.transform).ToList();
        // The level / XP meter (ExpansionChallengeExpController) carries a Button on its group object
        // but isn't an action — its level + experience are already read in the pass status — so leave
        // it out of the header section. Identify it structurally as the button wrapping the exp
        // controller's progress bar; that's never the "(i)" info button we DO want to keep.
        var expBarT = _controller.exp != null ? _controller.exp.progressBar : null;
        foreach (var b in _controller.GetComponentsInChildren<Button>(includeInactive: false))
        {
            if (b == null || !b.isActiveAndEnabled || !b.IsInteractable()) continue;
            if (known.Contains(b) || b == collectAll) continue;
            if (scrollerT != null && b.transform.IsChildOf(scrollerT)) continue;
            if (questTfs.Any(t => b.transform.IsChildOf(t))) continue;
            if (expBarT != null && expBarT.IsChildOf(b.transform)) continue;
            items.Add(b);
        }
        return items.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    // ---- labels ----------------------------------------------------------------------------

    // "Battle Pass, <name>, ends in <time>, level <n>, <exp> experience" — all read live.
    private string PassStatus()
    {
        var parts = new List<string> { "Battle Pass" };
        if (_controller != null)
        {
            try
            {
                var bp = _controller.builder != null ? _controller.builder.LoadedBattlePassData : null;
                var name = bp != null ? Loc("tcg_" + (bp.ExpansionId ?? string.Empty).ToLowerInvariant()) : null;
                if (!string.IsNullOrWhiteSpace(name)) parts.Add(name!);
            }
            catch { }
            try
            {
                var tl = _controller.header != null ? _controller.header.timeLeft : null;
                var t = Clean(ReadField<LocText>(tl, "timeLeftText"));
                if (!string.IsNullOrWhiteSpace(t)) parts.Add($"ends in {t}");
            }
            catch { }
            try
            {
                var level = _controller.exp != null ? Clean(_controller.exp.rankText) : "";
                if (!string.IsNullOrWhiteSpace(level)) parts.Add($"level {level}");
            }
            catch { }
            try
            {
                var exp = _controller.exp != null ? Clean(_controller.exp.expText) : "";
                if (!string.IsNullOrWhiteSpace(exp)) parts.Add($"{exp.Replace("/", " of ")} experience");
            }
            catch { }
        }
        return string.Join(", ", parts);
    }

    private string PassLabel(Selectable s)
    {
        // A text-bearing header button (e.g. the premium "BATTLE PASS" button) keeps its own label;
        // the icon-only "(i)" reads the live pass status instead of a bare "info".
        if (HasLetterText(s)) return KeyboardNavigator.DefaultLabel(s);
        return PassStatus() + ", information";
    }

    private string QuestLabel(Selectable s)
    {
        var panel = s.GetComponentInParent<DailyQuestPanelParts>();
        if (panel == null) return KeyboardNavigator.DefaultLabel(s);

        var parts = new List<string>();
        var desc = Clean(ReadField<LocText>(panel, "questDescriptionText"));
        if (!string.IsNullOrWhiteSpace(desc)) parts.Add(desc);
        var prog = Clean(ReadField<LocText>(panel, "questProgressText"));
        if (!string.IsNullOrWhiteSpace(prog)) parts.Add(prog.Replace("/", " of "));

        var reward = QuestReward(panel);
        if (!string.IsNullOrWhiteSpace(reward)) parts.Add("reward " + reward);

        var state = QuestState(panel);
        if (!string.IsNullOrWhiteSpace(state)) parts.Add(state);

        return parts.Count > 0 ? string.Join(", ", parts) : "Quest";
    }

    // The card's two rewards: battle-pass experience (the star) plus the item set (the token/currency).
    private string QuestReward(DailyQuestPanelParts panel)
    {
        var bits = new List<string>();
        var xp = Clean(ReadField<LocText>(panel, "xpRewardAmountText"));
        if (!string.IsNullOrWhiteSpace(xp)) bits.Add($"{xp} experience");
        var slot = ReadField<Component>(panel, "itemSetSummarySlot");
        if (slot != null)
        {
            var item = ReadFieldObj(slot, "currentItem") is ItemData d ? d : default;
            if (item.itemType != ItemData.ItemType.None)
            {
                int n = ReadFieldObj(slot, "numberOfItems") is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
                var name = ItemName(item);
                if (!string.IsNullOrWhiteSpace(name)) bits.Add(n > 1 ? $"{name} times {n}" : name);
            }
        }
        return string.Join(" and ", bits);
    }

    private string QuestState(DailyQuestPanelParts panel)
    {
        if (HolderActive(panel, "comeBackLaterHolder"))
        {
            var t = Clean(ReadField<LocText>(panel, "comeBackLaterTimerLocText"));
            return string.IsNullOrWhiteSpace(t) ? "claimed" : $"claimed, available in {t}";
        }
        if (HolderActive(panel, "readyToCollectHolder")) return "ready to collect";
        if (HolderActive(panel, "canRerollIndicator")) return "can be replaced";
        return "";
    }

    private string TierLabel(Selectable s)
    {
        if (s == CollectAllButton()) return "Collect all rewards";

        var view = s.GetComponentInParent<ExpansionChallengeTierView>();
        if (view == null) return KeyboardNavigator.DefaultLabel(s);

        var parts = new List<string>();
        var num = Clean(view.indexTxt_Unlocked);
        if (string.IsNullOrWhiteSpace(num)) num = Clean(view.indexTxt_Locked);
        parts.Add(string.IsNullOrWhiteSpace(num) ? "Tier" : $"Tier {num}");

        try
        {
            var rewards = DescribeRewards(_controller != null && _controller.tiers != null
                ? _controller.tiers.GetRewards(view.rank, view.rank) : null);
            if (!string.IsNullOrWhiteSpace(rewards)) parts.Add(rewards);
        }
        catch { }

        parts.Add(TierState(view.rank));
        return string.Join(", ", parts);
    }

    private string TierState(int rank)
    {
        try
        {
            var sd = _controller != null ? _controller.serverData : null;
            if (sd == null) return "";
            if (sd.highestRedeemedRank != null && sd.highestRedeemedRank.GetHighestClaimedRewardsTier() >= rank) return "claimed";
            if (sd.highestUnlockedRank >= rank) return "ready to collect";
            return "locked";
        }
        catch { return ""; }
    }

    // ---- reward / item description ---------------------------------------------------------

    private static string DescribeRewards(ItemData[]? items)
    {
        if (items == null || items.Length == 0) return "";
        int hard = 0, trade = 0, token = 0;
        var others = new List<string>();
        foreach (var item in items)
        {
            switch (item.itemType)
            {
                case ItemData.ItemType.HardCurrency: hard += item.amount; break;
                case ItemData.ItemType.TraderCurrency: trade += item.amount; break;
                case ItemData.ItemType.TokenCurrency: token += item.amount; break;
                default:
                    var n = ItemName(item);
                    if (!string.IsNullOrWhiteSpace(n)) others.Add(item.amount > 1 ? $"{n} times {item.amount}" : n);
                    break;
            }
        }
        var parts = new List<string>();
        if (hard > 0) parts.Add($"{hard} {ShopLabels.CurrencyWord(CurrencyType.HardCurrency)}");
        if (trade > 0) parts.Add($"{trade} {ShopLabels.CurrencyWord(CurrencyType.TradeCurrency)}");
        if (token > 0) parts.Add($"{token} {ShopLabels.CurrencyWord(CurrencyType.TokenCurrency)}");
        parts.AddRange(others);
        return string.Join(", ", parts);
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
        var loc = Loc(item.contentLocId);
        if (loc != null) return loc;
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

    // ---- helpers ---------------------------------------------------------------------------

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null, string? groupName = null)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = groupName };
        g.SetItems(items, resolver);
        g.OnBack = OpenOverflowMenu;
        _groups.Add(g);
        var names = string.Join(", ", items.Take(20).Select(s => s != null ? s.gameObject.name : "<null>"));
        var extra = items.Count > 20 ? $" (+{items.Count - 20} more)" : string.Empty;
        Log.Info($"Battle Pass group {_groups.Count} [{groupName ?? "-"}]: {items.Count} items — {names}{extra}");
    }

    private static bool HolderActive(object instance, string field)
    {
        var go = ReadField<GameObject>(instance, field);
        return go != null && go.activeInHierarchy;
    }

    private static bool HasLetterText(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
        {
            if (string.IsNullOrEmpty(t.text)) continue;
            foreach (var c in t.text) if (char.IsLetter(c)) return true;
        }
        return false;
    }

    private static string Clean(LocText? t)
    {
        if (t == null) return "";
        var s = t.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? "" : StripTags(s!).Trim();
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) return null;
        return StripTags(s).Trim();
    }

    private static string StripTags(string s)
    {
        // Energy / card-property symbols (e.g. a daily quest's "{G} Energy") render as TMP sprite tags;
        // speak them as words BEFORE the tag strip below would silently delete them.
        s = CardText.SpeakIcons(s);
        if (s.IndexOf('<') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- FTUE coordination (same model as the other HUB handlers) --------------------------

    private void SubscribeToFtue()
    {
        if (_subscribedToFtue) return;
        FTUEHook.FocusedElementChanged += OnFtueFocusChanged;
        _subscribedToFtue = true;
    }

    private void OnFtueFocusChanged()
    {
        if (!_active || _controller == null) return;
        // Paused beneath an overlay: that focus belongs to whatever is on top of us (the reward popup
        // coaches Btn_Collect), and the overlay's own handler follows it. Adopting it here would leave
        // this screen's only navigable item pointing at a button that lives on another screen — and one
        // the FTUE has reparented out from under us. We re-read the focus in OnResume anyway.
        if (!ReferenceEquals(_router.Active, this)) return;
        if (FTUEHook.FocusedElement == _lastFtueTarget) return;
        if (FTUEHook.FtueIsRunning) RebuildForFtue();
    }

    private void StartFtueSettlementWait()
    {
        CancelFtueSettlementWait();
        _ftueWaitCoroutine = AnywhereCoroutine.Instance.unmanaged.StartCoroutine(WaitForFtueToSettle());
    }

    private void CancelFtueSettlementWait()
    {
        if (_ftueWaitCoroutine != null)
        {
            AnywhereCoroutine.Instance.unmanaged.StopCoroutine(_ftueWaitCoroutine);
            _ftueWaitCoroutine = null;
        }
    }

    private IEnumerator WaitForFtueToSettle()
    {
        var mgr = FTUEManager.instance;
        var animator = mgr != null ? mgr.GetComponent<Animator>() : null;
        while (FTUEHook.FtueIsRunning && animator != null && !animator.enabled) yield return null;
        yield return null;
        if (FTUEHook.FtueSpeechClaimingStateActive) { _ftueWaitCoroutine = null; yield break; }
        _ftueWaitCoroutine = null;
        if (_active && _groups.Count > 0) Announce();
    }

    private static T? ReadField<T>(object? instance, string name) where T : class
    {
        if (instance == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
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

    private static void OpenOverflowMenu() => HUBOverflowMenuScreen.TryOpen();

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
