using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using TPCI.UI.News;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// News overlay — the post-login "what's new / expansion launch" carousel (driver: NewsOverlay, an
// OverlayScreenGroup in TPCI.UI.News). It's a sequence of news_pages: PreviousClicked/NextClicked flip
// pages, the prev/next buttons auto-show, and the CONTINUE button (`closeButton`) appears ONLY on the
// last page (→ CloseClicked marks news read + closes). The gift overlays literally wait on
// NewsDataManager.IsRead, so this is the first post-tutorial popup in the chain.
//
// Like the other overlays it pops OVER the HUB without pausing it, so without a handler the HUB keeps
// focus behind it. Model (matches the game): Left/Right = prev/next page, each page speaks (header +
// body) as you land on it; Enter pages forward too and, on the last page, presses CONTINUE to finish.
internal sealed class NewsOverlayScreen : IScreenHandler
{
    private static NewsOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private NewsOverlay? _overlay;
    private bool _active;
    private int _lastReadPage = -1;

    private static readonly FieldInfo? HeaderF = AccessTools.Field(typeof(NewsOverlay), "headerLoc");
    private static readonly FieldInfo? NewsPageF = AccessTools.Field(typeof(NewsOverlay), "newsPage");
    private static readonly FieldInfo? CloseBtnF = AccessTools.Field(typeof(NewsOverlay), "closeButton");
    private static readonly FieldInfo? PageIdxF = AccessTools.Field(typeof(NewsOverlay), "_currentPageIndex");
    private static readonly MethodInfo? TotalPagesG = AccessTools.PropertyGetter(typeof(NewsOverlay), "TotalPageCount");

    public string Name => "News";

    public NewsOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new NewsOverlayScreen(router);

        var activate = AccessTools.Method(typeof(NewsOverlay), "Activate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("NewsOverlayScreen: could not resolve NewsOverlay.Activate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(NewsOverlayScreen), nameof(Activate_Postfix))));

        var deact = AccessTools.Method(typeof(NewsOverlay), "OnDeActivate");
        if (deact != null)
            harmony.Patch(deact, postfix: new HarmonyMethod(AccessTools.Method(typeof(NewsOverlayScreen), nameof(Deactivate_Postfix))));

        Log.Info("NewsOverlayScreen installed");
    }

    private static void Activate_Postfix(NewsOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        _instance._lastReadPage = -1;   // force a read of page 0 on the next tick
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
    }

    private static void Deactivate_Postfix(NewsOverlay __instance)
    {
        if (_instance == null || _instance._overlay != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._overlay = null;
    }

    public void OnEnter() { _lastReadPage = -1; }
    public void OnResume() { _lastReadPage = -1; }
    public void OnExit() { }
    public void RespeakAll() { _lastReadPage = -1; }

    public string StateSignature() => _overlay == null ? "News|dead" : $"News|{CurrentPage()}";

    public void Tick()
    {
        if (_overlay == null) { _active = false; _router.Deactivate(this); return; }
        if (!_overlay.gameObject.activeInHierarchy) { _active = false; _router.Deactivate(this); _overlay = null; return; }
        if (!Application.isFocused) return;

        // Read the page whenever it changes (the page-change methods repopulate header + entries; we
        // read on the following tick so the content has settled).
        int page = CurrentPage();
        if (page != _lastReadPage) { _lastReadPage = page; ReadPage(); }

        bool last = OnLastPage();
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { _overlay.NextClicked(); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)
         || (UnityEngine.Input.GetKeyDown(KeyCode.Backspace))) { _overlay.PreviousClicked(); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (last) _overlay.CloseClicked();   // finish (marks news read + closes)
            else _overlay.NextClicked();
        }
    }

    private void ReadPage()
    {
        if (_overlay == null) return;
        var parts = new List<string>();

        int idx = CurrentPage();
        int total = TotalPages();
        if (total > 1) parts.Add($"Page {idx + 1} of {total}");

        var header = TmpOf(HeaderF?.GetValue(_overlay) as Component);
        if (!string.IsNullOrWhiteSpace(header)) parts.Add(header!);

        if (NewsPageF?.GetValue(_overlay) is Component np)
            foreach (var t in np.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
                if (!string.IsNullOrWhiteSpace(t.text)) parts.Add(t.text);

        // On the last page the CONTINUE button is shown — read it by its label (the affordance), so
        // the player knows the carousel ends here and Enter finishes.
        if (CloseBtnF?.GetValue(_overlay) is Button close && close.gameObject.activeInHierarchy)
        {
            var label = LabelOf(close);
            if (!string.IsNullOrWhiteSpace(label)) parts.Add(label!);
        }

        _router.Speech.Say(parts.Count > 0 ? string.Join(". ", parts) : Name, interrupt: true);
        Log.Info($"News: page {idx + 1}/{total}");
    }

    private bool OnLastPage()
        => _overlay != null && CloseBtnF?.GetValue(_overlay) is Button close && close.gameObject.activeInHierarchy;

    private int CurrentPage()
    {
        if (_overlay == null || PageIdxF == null) return 0;
        return PageIdxF.GetValue(_overlay) is int i ? i : 0;
    }

    private int TotalPages()
    {
        try { return _overlay != null && TotalPagesG != null && TotalPagesG.Invoke(_overlay, null) is int n ? n : 0; }
        catch { return 0; }
    }

    private static string? LabelOf(Component? c)
    {
        var t = c != null ? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false) : null;
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : null;
    }

    private static string? TmpOf(Component? c)
    {
        if (c == null || !c.gameObject.activeInHierarchy) return null;
        var t = c.GetComponent<TMPro.TMP_Text>() ?? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : null;
    }
}
