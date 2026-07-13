using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The season reset / "WELCOME TO <season> LADDER" overlay — the big popup with the new league hex, the
// league reward cards and COLLECT REWARDS. It appears right after the tutorial finishes, and on every
// season rollover.
//
// It is NOT a RewardOverlaysController overlay, which is why RewardOverlayScreen never saw it and the
// popup sat on screen unannounced with HOME still holding the keyboard behind it. Its driver is
// SeasonEndRewardsSetter, which calls OverlayManager.OpenOverlay(screenOverlays.seasonResetOverlay)
// directly from its private OpenSeasonResetOverlay(). Three flows reach it (SeasonEndRewardsSetter):
//   • season ended with rewards  → CONTINUE grants them (→ item-grant reveal)
//   • season ended, no rewards   → CONTINUE starts the new season
//   • new season / first login   → CheckGrantLadderRewards fills in the ladder reward and COLLECT REWARDS
// All three show the same overlay with one live button (`continueButton`), whose label the game sets
// from `buttonLoc`. So: announce what the overlay says, present that button, done.
//
// Close: the button's own handlers call OverlayManager.CloseOverlay(). There's no close hook to patch,
// so we let go the same way RewardOverlayScreen does — when OverlayManager.currentOverlay is no longer
// ours. (activeInHierarchy is unreliable for OverlayManager-closed overlays; see
// [[feedback-overlay-close-signal]].)
internal sealed class SeasonResetOverlayScreen : IScreenHandler
{
    private static SeasonResetOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private SeasonEndRewardsSetter? _setter;
    private bool _active;

    public string Name => "Season rewards";

    public SeasonResetOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new SeasonResetOverlayScreen(router);

        var open = AccessTools.Method(typeof(SeasonEndRewardsSetter), "OpenSeasonResetOverlay");
        if (open == null)
        {
            Log.Error("SeasonResetOverlayScreen: could not resolve SeasonEndRewardsSetter.OpenSeasonResetOverlay");
            return;
        }
        harmony.Patch(open, postfix: new HarmonyMethod(AccessTools.Method(typeof(SeasonResetOverlayScreen), nameof(Open_Postfix))));

        Log.Info("SeasonResetOverlayScreen installed");
    }

    private static void Open_Postfix(SeasonEndRewardsSetter __instance)
    {
        if (_instance == null) return;
        _instance._setter = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _setter == null ? "SeasonRewards|dead" : $"SeasonRewards|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_setter == null || !IsOverlayCurrent()) { _active = false; _router.Deactivate(this); _setter = null; return; }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    // OverlayManager is the authority: the button's own handler closes the overlay through it, and the
    // GameObject can stay activeInHierarchy afterwards.
    private bool IsOverlayCurrent()
    {
        try
        {
            var ov = _setter?.screenOverlays?.seasonResetOverlay;
            if (ov == null) return false;
            return ManagerSingleton<OverlayManager>.instance?.currentOverlay == ov;
        }
        catch { return true; }   // manager unreachable — don't yank focus prematurely
    }

    private void Rebuild(bool announce)
    {
        if (_setter == null) return;

        // The overlay's one live control. The game re-wires continueButton.onClick per flow (grant
        // rewards / start the new season), so Enter on it is exactly the sighted player's click.
        var button = Field<Button>("continueButton");
        var items = new List<Selectable>();
        if (button != null && button.gameObject.activeInHierarchy) items.Add(button);
        _nav.SetItems(items);
        if (!announce) return;

        var title = OverlayTitle();
        if (!string.IsNullOrWhiteSpace(title))
        {
            _router.Speech.Say(title!, interrupt: true);
            Log.Info($"SeasonResetOverlay: {title} ({items.Count} buttons)");
        }
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: string.IsNullOrWhiteSpace(title));
        else _router.Speech.Say(Name, interrupt: string.IsNullOrWhiteSpace(title));
    }

    // Everything the popup shows, in reading order: "WELCOME TO", the season name, your new trophy
    // count, the league reward heading, and the reward cards themselves. All read live from the game's
    // own LocTexts — the season name and league names are localized ids we must never bake in.
    private string? OverlayTitle()
    {
        var parts = new List<string>();
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(s!.Trim()); }

        Add(LocLine("otherLoc"));                    // "WELCOME TO"
        Add(LocLine("titleLoc"));                    // "JUNE 2026 LADDER"
        Add(RewardOverlayScreen.TmpText(Field<Component>("leagueNamePlate")));   // the new league's name
        Add(LocLine("seasonRankNewTrophyCount"));    // trophies carried into the new season
        Add(LocLine("ladderLoc"));                   // "<league> LEAGUE REWARD"
        Add(RewardOverlayScreen.SummarizeRewards(Field<Transform>("rewardHolder")));

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // LocText.CurrentText is the already-formatted display string (the season name is substituted in).
    // Skip it when the game has the containing group hidden — the same overlay serves the season-end
    // variant, where different groups are active.
    private string? LocLine(string field)
    {
        var loc = Field<LocText>(field);
        if (loc == null || !loc.gameObject.activeInHierarchy) return null;
        return loc.CurrentText;
    }

    private static readonly Dictionary<string, FieldInfo?> _fields = new();
    private T? Field<T>(string name) where T : class
    {
        if (_setter == null) return null;
        if (!_fields.TryGetValue(name, out var f))
        {
            f = AccessTools.Field(typeof(SeasonEndRewardsSetter), name);
            _fields[name] = f;
            if (f == null) Log.Warn($"SeasonResetOverlayScreen: field '{name}' not found");
        }
        return f?.GetValue(_setter) as T;
    }
}
