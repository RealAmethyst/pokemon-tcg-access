using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// HUB overflow "⋮" menu — FORUMS / SUPPORT / NEWS / INBOX / SETTINGS + QUIT GAME / TERMS OF USE /
// PRIVACY NOTICE / ABOUT (plus close + logout). Driver: HUBOverflowMenuController (base; _L in
// landscape). Its overlay (MyOverlayScreenGroup) opens OVER the HUB without pausing it, so without a
// handler the HUB keeps focus behind it. The controller registers OnOverlayGroupActivate/Deactivate
// as the overlay's activate/deactivate listeners — so those are clean, L/portrait-agnostic hooks.
//
// We present the menu's real buttons for keyboard nav; Escape dismisses (controller.Dismiss). Sub-
// screens it opens (Settings/About/TOS/Privacy) are separate overlays — their own handlers are a
// follow-up; the URL buttons (Forums/Support/News) and the Quit confirm (a handled modal) work now.
internal sealed class HUBOverflowMenuScreen : IScreenHandler
{
    private static HUBOverflowMenuScreen? _instance;

    private readonly ScreenRouter _router;
    private HUBOverflowMenuController? _controller;
    private bool _active;
    private bool _hasAnnounced;
    private readonly KeyboardNavigator _nav;

    public string Name => "Menu";

    public HUBOverflowMenuScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = true };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBOverflowMenuScreen(router);

        var act = AccessTools.Method(typeof(HUBOverflowMenuController), "OnOverlayGroupActivate");
        if (act == null)
        {
            Log.Error("HUBOverflowMenuScreen: could not resolve HUBOverflowMenuController.OnOverlayGroupActivate");
            return;
        }
        harmony.Patch(act, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBOverflowMenuScreen), nameof(Activate_Postfix))));

        var deact = AccessTools.Method(typeof(HUBOverflowMenuController), "OnOverlayGroupDeactivate");
        if (deact != null)
            harmony.Patch(deact, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBOverflowMenuScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBOverflowMenuScreen installed");
    }

    private static void Activate_Postfix(HUBOverflowMenuController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    private static void Deactivate_Postfix(HUBOverflowMenuController __instance)
    {
        if (_instance == null || _instance._controller != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter() { _hasAnnounced = false; Rebuild(announce: true); }
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _controller == null ? "Menu|dead" : $"Menu|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_controller == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;
        var ov = _controller.MyOverlayScreenGroup;
        if (ov == null || !ov.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _controller = null; return;
        }
        _nav.Tick();
    }

    private void Rebuild(bool announce)
    {
        if (_controller == null) return;
        var ov = _controller.MyOverlayScreenGroup;
        var items = ov != null
            ? ov.GetComponentsInChildren<Button>(includeInactive: false)
                .Where(b => b.isActiveAndEnabled)
                .Cast<Selectable>()
                .OrderBy(s => ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        _nav.SetItems(items);
        _nav.OnBack = () => _controller?.Dismiss();   // Escape / Backspace closes the menu

        if (!announce) return;
        if (!_hasAnnounced) { _router.Speech.Say(Name, interrupt: true); _hasAnnounced = true; }
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"Menu: {items.Count} buttons — {string.Join(", ", items.Take(16).Select(s => s.gameObject.name))}");
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
