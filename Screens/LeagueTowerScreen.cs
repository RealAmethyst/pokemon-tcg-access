using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using RainierClientSDK.source.SeasonRank;
using SharedLogicUtils.source.DataTypes.RankProgression.DataObjects;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// League Tower — the full rank ladder, reached by activating the HOME rank hex (Btn_Rank →
// MainMenuController.OpenLeagueTower). Driver: HUBLeagueTowerController (a HUBScreenController, so the
// lifecycle hook is the usual OnActivate(HUBGroupController)/OnDeactivate, like Profile/Shop).
//
// Sections, visual top-to-bottom (section-stack nav, same as HUBDeckSelectScreen):
//   • Top    — the HUB tab strip (HOME / DECKS / …) + ⋮.
//   • Header — back, season info (ⓘ → InfoPopup, handled by InfoPopupScreen), LADDER END REWARDS.
//              Entering the screen also reads a summary: season name, time left, and the rank status.
//   • Ladder — the rank tiers, grouped into leagues. Navigated as DATA computed from the season config
//              (leagueData → rankData), not the instantiated tower elements: each reads
//              "<league>, <rank>, <N> points, achieved/current/locked". Enter opens that league's
//              reward overlay via the controller's own OpenLeagueRewardOverlay (→ RewardOverlayScreen).
//
// Escape leaves to HOME (MainMenuController.OpenPlayScreen — what the back arrow does). The ladder is a
// horizontal row in landscape, so Left/Right move between tiers; default focus lands on the current rank.
internal sealed class LeagueTowerScreen : IScreenHandler
{
    private enum Section { Top, Header, Ladder }

    private sealed class Tier
    {
        public int LeagueIndex;
        public string League = "";
        public string Rank = "";
        public uint Threshold;
        public string Status = "";   // "current rank" / "achieved" / "locked"
    }

    private static LeagueTowerScreen? _instance;

    private readonly ScreenRouter _router;
    private HUBLeagueTowerController? _controller;
    private bool _active;

    private Section _section = Section.Ladder;
    private KeyboardNavigator? _topNav;
    private KeyboardNavigator? _headerNav;
    private readonly List<Tier> _tiers = new();
    private int _tierIndex;
    private int _subIndexAtPause = -1;

    public string Name => "League tower";

    public LeagueTowerScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new LeagueTowerScreen(router);

        var activate = AccessTools.Method(typeof(HUBLeagueTowerController), "OnActivate", new[] { typeof(HUBGroupController) });
        if (activate == null) { Log.Error("LeagueTowerScreen: OnActivate not found"); return; }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(LeagueTowerScreen), nameof(Activate_Postfix))));
        var deactivate = AccessTools.Method(typeof(HUBLeagueTowerController), "OnDeactivate", new[] { typeof(HUBGroupController) });
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(LeagueTowerScreen), nameof(Deactivate_Postfix))));

        Log.Info("LeagueTowerScreen installed");
    }

    private static void Activate_Postfix(HUBLeagueTowerController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
    }

    private static void Deactivate_Postfix(HUBLeagueTowerController __instance)
    {
        if (_instance == null || _instance._controller != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter()
    {
        Rebuild();
        _router.Speech.Say(SummaryOnEnter(), interrupt: true);
        EnterSection(DefaultSection(), announce: true, interruptPrior: false);
    }

    public void OnResume()
    {
        Rebuild();
        RestoreSubFocus();
        ReAnnounce(interrupt: true);
    }

    public void OnExit()
    {
        _subIndexAtPause = _section switch
        {
            Section.Top => _topNav?.Index ?? -1,
            Section.Header => _headerNav?.Index ?? -1,
            _ => -1,
        };
    }

    public void RespeakAll() { Rebuild(); ReAnnounce(interrupt: false); }

    public string StateSignature()
        => _controller == null ? "League|dead" : $"League|{_section}|{_tierIndex}|{_tiers.Count}";

    public void Tick()
    {
        if (_controller == null || !_controller.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _controller = null;
            return;
        }
        if (!Application.isFocused) return;

        // Escape / Backspace = back to HOME (the back arrow's own MainMenuController.OpenPlayScreen).
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            GoBack();
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            CycleSection(shift ? -1 : +1);
            return;
        }

        switch (_section)
        {
            case Section.Top: TickStrip(_topNav, null, Section.Header); break;
            case Section.Header: TickStrip(_headerNav, Section.Top, Section.Ladder); break;
            case Section.Ladder: TickLadder(); break;
        }
    }

    // ── Discovery ───────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        if (_controller == null) return;

        // Top strip (HOME / DECKS / … + ⋮) — same discovery as Home/Shop/Decks.
        var topItems = new List<Selectable>();
        var topController = UnityEngine.Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<System.Collections.IEnumerable>(topController, "persistentNavButtons");
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
        topItems = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();
        _topNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, AllowVertical = false, AllowHorizontal = true };
        _topNav.SetItems(topItems, UiLabels.TopNavLabel);
        _topNav.AllowVertical = false; _topNav.AllowHorizontal = true;

        // Header chrome buttons: every active Button under the controller that ISN'T a HUB-top-strip
        // button and ISN'T a per-tier reward button (those live inside a LeagueRankGroupParts and belong
        // to the ladder). Leaves the season-info ⓘ, LADDER END REWARDS, snap-to-current, and the back
        // arrow. Back also works via Escape, so a missed/odd label here is never a dead end.
        var topSet = new HashSet<Selectable>(topItems);
        var headerItems = _controller.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled
                        && !topSet.Contains(b)
                        && b.GetComponentInParent<LeagueRankGroupParts>() == null)
            .Cast<Selectable>()
            .OrderBy(s => ReadingOrder(s.transform)).ToList();
        _headerNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "League options" };
        _headerNav.SetItems(headerItems, HeaderLabel);
        // The header is a left-to-right strip (back / season info / ladder end rewards) whose buttons sit
        // at slightly different heights, so force a flat horizontal row — otherwise it auto-detects as a
        // 1-column list and Left/Right can't reach past the first item.
        _headerNav.ForceFlatHorizontal();
        Log.Info($"League tower header: {headerItems.Count} buttons [{string.Join(", ", headerItems.Select(s => s.gameObject.name))}]");

        RebuildLadder();
    }

    // The ladder tiers, computed from the season config (every league's every rank), in the same order
    // the game lays them out left-to-right. Status comes from the player's current rank: the exact
    // RankData instance GetRankByEXP returns is the "current rank" (reference match), ranks at/below it
    // are "achieved", the rest "locked" — mirroring PopulateLeagueTower's rankAchieved test.
    private void RebuildLadder()
    {
        _tiers.Clear();
        var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
        var leagues = cfg?.leagueData;
        if (cfg == null || leagues == null) return;

        uint exp = SeasonRank.exp;
        RankData? current = null;
        try { current = cfg.GetRankByEXP(exp); } catch { }

        int currentTier = -1;
        for (int li = 0; li < leagues.Count; li++)
        {
            var league = leagues[li];
            if (league?.rankData == null) continue;
            var leagueName = Loc(league.leagueName);
            foreach (var rank in league.rankData)
            {
                if (rank == null) continue;
                string status;
                if (current != null && ReferenceEquals(rank, current)) { status = "current rank"; currentTier = _tiers.Count; }
                else if (current != null && rank.expLimit <= current.expLimit) status = "achieved";
                else status = "locked";
                _tiers.Add(new Tier
                {
                    LeagueIndex = li,
                    League = leagueName,
                    Rank = Loc(rank.rankName),
                    Threshold = rank.expLimit,
                    Status = status,
                });
            }
        }
        if (_tierIndex < 0 || _tierIndex >= _tiers.Count) _tierIndex = currentTier >= 0 ? currentTier : 0;
    }

    // ── Sections ────────────────────────────────────────────────────────────────────────────────

    private Section DefaultSection() => _tiers.Count > 0 ? Section.Ladder : Section.Header;

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => _topNav != null && _topNav.Count > 0,
        Section.Header => _headerNav != null && _headerNav.Count > 0,
        Section.Ladder => _tiers.Count > 0,
        _ => false,
    };

    private void CycleSection(int dir)
    {
        var order = new[] { Section.Top, Section.Header, Section.Ladder };
        int cur = Array.IndexOf(order, _section);
        if (cur < 0) cur = 0;
        for (int step = 1; step <= order.Length; step++)
        {
            var cand = order[((cur + dir * step) % order.Length + order.Length) % order.Length];
            if (SectionHasContent(cand)) { EnterSection(cand, announce: true, interruptPrior: true); return; }
        }
    }

    private void EnterSection(Section s, bool announce, bool interruptPrior)
    {
        if (!SectionHasContent(s)) s = SectionHasContent(Section.Ladder) ? Section.Ladder : Section.Header;
        _section = s;
        switch (s)
        {
            case Section.Top: _topNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Header: _headerNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Ladder: TierFocus(_tierIndex, announce, interruptPrior); break;
        }
    }

    private void ReAnnounce(bool interrupt)
    {
        switch (_section)
        {
            case Section.Top: _topNav?.RefocusCurrent(announce: true); break;
            case Section.Header: _headerNav?.RefocusCurrent(announce: true); break;
            case Section.Ladder: AnnounceTier(interrupt); break;
        }
    }

    private void RestoreSubFocus()
    {
        if (_subIndexAtPause < 0) return;
        KeyboardNavigator? nav = _section switch
        {
            Section.Top => _topNav,
            Section.Header => _headerNav,
            _ => null,
        };
        if (nav != null && _subIndexAtPause < nav.Count)
            nav.FocusIndex(_subIndexAtPause, announce: false, interrupt: true);
    }

    // Horizontal strip (top nav / header): Left/Right within; Up/Down escape to the adjacent section.
    private void TickStrip(KeyboardNavigator? nav, Section? up, Section? down)
    {
        if (nav == null) return;
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (up.HasValue) EnterSection(up.Value, announce: true, interruptPrior: true); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (down.HasValue) EnterSection(down.Value, announce: true, interruptPrior: true); return; }
        nav.Tick();
    }

    // ── Ladder (data nav) ─────────────────────────────────────────────────────────────────────────

    private void TickLadder()
    {
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { MoveTier(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { MoveTier(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (SectionHasContent(Section.Header)) EnterSection(Section.Header, announce: true, interruptPrior: true);
            else if (SectionHasContent(Section.Top)) EnterSection(Section.Top, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
            OpenTierRewards();
    }

    private void MoveTier(int dir)
    {
        int next = _tierIndex + dir;
        if (next < 0 || next >= _tiers.Count) return;   // no wrap
        TierFocus(next, announce: true, interrupt: true);
    }

    private void TierFocus(int index, bool announce, bool interrupt)
    {
        if (index < 0 || index >= _tiers.Count) return;
        _tierIndex = index;
        if (announce) AnnounceTier(interrupt);
    }

    private void AnnounceTier(bool interrupt)
    {
        if (_tierIndex < 0 || _tierIndex >= _tiers.Count) return;
        var t = _tiers[_tierIndex];
        var label = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(t.League)) label.Add(t.League);
        if (!string.IsNullOrWhiteSpace(t.Rank)) label.Add(t.Rank);
        label.Add(t.Threshold == 1 ? "1 point" : $"{t.Threshold} points");
        label.Add(t.Status);
        _router.Speech.Say(Speech.Announce.Item(string.Join(", ", label), description: null, index: _tierIndex + 1, total: _tiers.Count), interrupt);
    }

    // Open the focused tier's league reward overlay via the controller's own method (→ RewardOverlayScreen
    // presents it). The game only wires this button on each league's first rank, but the call is per-
    // league, so it's meaningful from any tier in that league.
    private void OpenTierRewards()
    {
        if (_controller == null || _tierIndex < 0 || _tierIndex >= _tiers.Count) return;
        try { _controller.OpenLeagueRewardOverlay(_tiers[_tierIndex].LeagueIndex); }
        catch (Exception ex) { Log.Warn($"League tower: open rewards failed: {ex.Message}"); }
    }

    private void GoBack()
    {
        try { ManagerSingleton<RainierManager>.instance._mainMenuController.OpenPlayScreen(); }
        catch (Exception ex) { Log.Warn($"League tower: back failed: {ex.Message}"); }
    }

    // ── Labels / summary ──────────────────────────────────────────────────────────────────────────

    // Season name + time left + the rank status (reused from the HOME rank hex), spoken on entry.
    private string SummaryOnEnter()
    {
        var parts = new List<string> { Name };
        if (_controller != null)
        {
            var title = LocOf(ReadField<LocText>(_controller, "seasonTitleText"));
            if (!string.IsNullOrWhiteSpace(title)) parts.Add(title!);
            var timer = ReadField<Component>(_controller, "seasonTimer");
            var timeLeft = timer != null ? LocOf(ReadField<LocText>(timer, "timeLeftText")) : null;
            if (!string.IsNullOrWhiteSpace(timeLeft)) parts.Add(timeLeft!);
        }
        var rank = RankReader.Status();
        if (!string.IsNullOrWhiteSpace(rank)) parts.Add(rank!);
        return string.Join(". ", parts);
    }

    // Header buttons: worded ones (LADDER END REWARDS) keep their text; icon-only ones get a friendly
    // name — the back arrow and the season-info ⓘ have no text child.
    private static string HeaderLabel(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
            if (!string.IsNullOrEmpty(t.text) && t.text.Any(char.IsLetter)) return KeyboardNavigator.DefaultLabel(s);
        var n = s.gameObject.name.ToLowerInvariant();
        if (n.Contains("back") || n.Contains("return") || n.Contains("exit")) return "Back";
        if (n.Contains("info")) return "Season information";
        return KeyboardNavigator.DefaultLabel(s);
    }

    private static string Loc(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        try
        {
            var v = LocalizationManager.Instance?.GetString(id);
            if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v.Trim();
        }
        catch { }
        return "";
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
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

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
