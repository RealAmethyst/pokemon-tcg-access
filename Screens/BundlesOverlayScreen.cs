using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// "View all bundles" overlay — driver: BundlesOverlay (an OverlayScreenGroup, an EnhancedScroller
// of month-grouped bundle rows), opened from the BUNDLES tab's VIEW ALL button. Without a handler
// it pops over the HUB and traps a keyboard user (focus stays on the shop behind it).
//
// We navigate the bundle tiles it has spawned (same rich "name, price, N available" label as the
// main tab, via UiLabels.Describe → ShopLabels) and close on Escape. The scroller instantiates
// cells asynchronously and recycles them on scroll, so a content-signature poll rebuilds the nav
// as the spawned set changes. NOTE: revealing rows beyond the first screenful needs the scroller
// driven by keyboard — a follow-up; the curated RECOMMENDED / NEW ARRIVALS tabs and EXPANSIONS
// cover browsing in the meantime.
internal sealed class BundlesOverlayScreen : IScreenHandler
{
    private static BundlesOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private BundlesOverlay? _overlay;
    private bool _active;
    private string _sig = string.Empty;
    private bool _announced;

    public string Name => "All bundles";

    public BundlesOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new BundlesOverlayScreen(router);

        var activate = AccessTools.Method(typeof(BundlesOverlay), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("BundlesOverlayScreen: could not resolve BundlesOverlay.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(BundlesOverlayScreen), nameof(Activate_Postfix))));

        // No OnDeActivate hook (BundlesOverlay doesn't override it → patching the shared base
        // would mis-fire for every other overlay). Dismissal is the Tick backstop below.

        Log.Info("BundlesOverlayScreen installed");
    }

    private static void Activate_Postfix(BundlesOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
    }

    public void OnEnter() { _announced = false; Rebuild(announce: true); }
    public void OnResume() { _announced = false; Rebuild(announce: true); }
    public void OnExit() { }
    public void RespeakAll() { _announced = false; Rebuild(announce: true); }

    public string StateSignature() => _overlay == null ? "AllBundles|dead" : $"AllBundles|{_nav.Index}|{ProductSig()}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _overlay = null;
            return;
        }
        if (!Application.isFocused) return;

        // The scroller spawns / recycles tiles over time; rebuild the nav when the visible set changes.
        var sig = ProductSig();
        if (sig != _sig) { _sig = sig; Rebuild(announce: !_announced); return; }

        _nav.Tick();
    }

    private void Close()
    {
        if (_overlay != null) _overlay.Deactivate();
    }

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;
        var items = ProductButtons();
        _nav.SetItems(items);   // default resolver → UiLabels.Describe → "name, price, N available"
        _sig = ProductSig();

        if (!announce) return;
        if (items.Count == 0)
        {
            // Cells haven't spawned yet — name the overlay; the poll re-announces once tiles appear.
            _router.Speech.Say(Name, interrupt: true);
            return;
        }
        _announced = true;
        _router.Speech.Say(Name, interrupt: true);
        _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private List<Selectable> ProductButtons()
    {
        if (_overlay == null) return new List<Selectable>();
        var result = new List<Selectable>();
        foreach (var p in _overlay.GetComponentsInChildren<ShopProductController>(includeInactive: false))
        {
            var b = p.GetComponentInChildren<Button>(includeInactive: false);
            if (b != null && b.isActiveAndEnabled) result.Add(b);
        }
        return result.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    private string ProductSig()
    {
        if (_overlay == null) return "none";
        var ids = _overlay.GetComponentsInChildren<ShopProductController>(includeInactive: false)
            .Select(p => { try { return p.GetItemSetID(); } catch { return ""; } });
        return string.Join(",", ids);
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
