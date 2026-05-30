using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Gift-granting overlays — the launch / seasonal "ACCEPT GIFT" popups (e.g. the Mega Evolution Chaos
// Rising launch gift: floral card sleeves + Build-and-Battle event tokens). Driver: GiftGrantingOverlay
// (abstract OverlayScreenGroup; concrete e.g. ExpansionChallengeSeasonalStarterGiftOverlay).
// ActivateGiftPopup wires the super-title / title / body / gift-name LocTexts, the reward slots, and
// the `claimButton` ("ACCEPT GIFT", loc global_accept_gift) whose onClick claims the gift, closes this
// overlay, and chains into the ContentItemGrantingOverlay reveal (handled by ItemGrantScreen).
//
// Like the reward overlays, these pop OVER the HUB without pausing it, so without a handler the HUB
// keeps focus behind the modal and ACCEPT GIFT is unreachable. We hook ActivateGiftPopup (present) /
// OnDeActivate (dismiss), read the title + body + reward count, and claim on Enter.
internal sealed class GiftOverlayScreen : IScreenHandler
{
    private static GiftOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private GiftGrantingOverlay? _overlay;
    private bool _active;

    private readonly List<string> _lines = new();
    private int _lineIndex = -1;

    private static readonly FieldInfo? ClaimBtnF = AccessTools.Field(typeof(GiftGrantingOverlay), "claimButton");
    private static readonly FieldInfo? SuperTitleF = AccessTools.Field(typeof(GiftGrantingOverlay), "superTitleLoc");
    private static readonly FieldInfo? TitleF = AccessTools.Field(typeof(GiftGrantingOverlay), "titleLoc");
    private static readonly FieldInfo? BodyF = AccessTools.Field(typeof(GiftGrantingOverlay), "bodyLoc");
    private static readonly FieldInfo? GiftNameF = AccessTools.Field(typeof(GiftGrantingOverlay), "giftNameLoc");
    private static readonly FieldInfo? RewardHolderF = AccessTools.Field(typeof(GiftGrantingOverlay), "rewardHolder");

    public string Name => "Gift";

    public GiftOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new GiftOverlayScreen(router);

        var activate = AccessTools.Method(typeof(GiftGrantingOverlay), "ActivateGiftPopup");
        if (activate == null)
        {
            Log.Error("GiftOverlayScreen: could not resolve GiftGrantingOverlay.ActivateGiftPopup");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(GiftOverlayScreen), nameof(Activate_Postfix))));

        var deact = AccessTools.Method(typeof(GiftGrantingOverlay), "OnDeActivate");
        if (deact != null)
            harmony.Patch(deact, postfix: new HarmonyMethod(AccessTools.Method(typeof(GiftOverlayScreen), nameof(Deactivate_Postfix))));

        Log.Info("GiftOverlayScreen installed");
    }

    private static void Activate_Postfix(GiftGrantingOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Present(announce: true);
    }

    private static void Deactivate_Postfix(GiftGrantingOverlay __instance)
    {
        if (_instance == null || _instance._overlay != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._overlay = null;
    }

    public void OnEnter() => Present(announce: true);
    public void OnResume() => Present(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Present(announce: true);

    public string StateSignature() => _overlay == null ? "Gift|dead" : $"Gift|{_lineIndex}";

    public void Tick()
    {
        if (_overlay == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;
        if (!_overlay.gameObject.activeInHierarchy) { _active = false; _router.Deactivate(this); _overlay = null; return; }

        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { ReadLine(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { ReadLine(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            var btn = ClaimBtnF?.GetValue(_overlay) as Button;
            if (btn != null) btn.onClick.Invoke();
            else _router.Speech.Say("Can't claim the gift", interrupt: true);
        }
    }

    private void Present(bool announce)
    {
        BuildLines();
        _lineIndex = -1;
        if (!announce) return;
        // Read the whole gift on first arrival (title + body + reward count) — no control hint; the
        // design speaks for itself. Down/Up re-read line by line; Enter accepts.
        var full = string.Join(". ", _lines);
        _router.Speech.Say(string.IsNullOrWhiteSpace(full) ? Name : full, interrupt: true);
        Log.Info($"GiftOverlay: {(_lines.Count > 0 ? _lines[0] : Name)}");
    }

    private void BuildLines()
    {
        _lines.Clear();
        AddIf(Tmp(SuperTitleF));
        AddIf(Tmp(TitleF));
        AddIf(Tmp(BodyF));
        AddIf(Tmp(GiftNameF));
        int n = RewardCount();
        if (n > 0) _lines.Add(n == 1 ? "1 reward" : $"{n} rewards");
    }

    private void ReadLine(int dir)
    {
        if (_lines.Count == 0) return;
        int next = _lineIndex + dir;
        if (next < 0) { _lineIndex = -1; return; }
        if (next >= _lines.Count) return;
        _lineIndex = next;
        _router.Speech.Say(_lines[_lineIndex], interrupt: true);
    }

    private int RewardCount()
    {
        if (RewardHolderF?.GetValue(_overlay) is not Transform holder) return 0;
        int n = 0;
        foreach (Transform c in holder) if (c != null && c.gameObject.activeSelf) n++;
        return n;
    }

    private string? Tmp(FieldInfo? field)
    {
        if (field == null || _overlay == null) return null;
        if (field.GetValue(_overlay) is not Component c || !c.gameObject.activeInHierarchy) return null;
        var t = c.GetComponent<TMPro.TMP_Text>() ?? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : null;
    }

    private void AddIf(string? s) { if (!string.IsNullOrWhiteSpace(s)) _lines.Add(s!); }
}
