using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        if (_controller == null) { _active = false; _router.Deactivate(this); return; }
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
        // The persistent top tab strip (HOME / DECKS / PROFILE / SHOP / BATTLE PASS / CARD DEX) and the
        // ⋮ button live in the same canvas hierarchy the overlay sweep reaches, so they'd otherwise show
        // up inside the menu — redundant, since they're always reachable on the top bar itself. Exclude
        // them by identity (robust regardless of how the prefab nests them).
        var topBar = TopBarButtons();
        var items = ov != null
            ? ov.GetComponentsInChildren<Button>(includeInactive: false)
                .Where(b => b.isActiveAndEnabled)
                .Where(b => !IsCloseChrome(b.gameObject.name))
                .Where(b => !topBar.Contains(b))
                .Cast<Selectable>()
                .OrderBy(s => ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        _nav.SetItems(items);
        // The menu is a vertical list — force it (auto-axis detection was reading horizontal because
        // the full-screen tap-outside blocker skewed the bounding box, so Up/Down did nothing).
        _nav.ForceFlatVertical();
        _nav.OnBack = () => _controller?.Dismiss();   // Escape / Backspace closes the menu

        if (!announce) return;
        if (!_hasAnnounced) { _router.Speech.Say(Name, interrupt: true); _hasAnnounced = true; }
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"Menu: {items.Count} buttons — {string.Join(", ", items.Take(16).Select(s => s.gameObject.name))}");
    }

    // Open the overflow menu from anywhere (e.g. Escape on a HUB screen) by invoking the top bar's
    // real ⋮ button — the same click a sighted player makes. Gated on the button being active and
    // interactable, so we never open it when the game wouldn't let a mouse user. If the menu is
    // already open, this closes it instead (so the same key toggles). Returns true when it acted.
    // The overlay's own activate/deactivate listeners route through Activate_Postfix /
    // Deactivate_Postfix exactly like a mouse click, so the handler wires up automatically.
    public static bool TryOpen()
    {
        if (_instance != null && _instance._active)
        {
            _instance._controller?.Dismiss();
            return true;
        }
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        if (top == null) return false;
        var t = ReadField<Transform>(top, "settingsButton");
        var btn = t != null ? t.GetComponent<Button>() : null;
        if (btn == null || !btn.isActiveAndEnabled || !btn.IsInteractable()) return false;
        btn.onClick?.Invoke();
        return true;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var ty = instance.GetType(); ty != null; ty = ty.BaseType)
        {
            var f = ty.GetField(name, flags);
            if (f != null) return f.GetValue(instance) as T;
        }
        return null;
    }

    // The window-chrome close affordances aren't real menu entries: "Close" is the X (redundant with
    // Escape, which we wire to Dismiss) and "CloseTransparent" is the invisible tap-outside blocker
    // (a full-screen button behind the menu). Both route to the same Dismiss, so listing them just
    // clutters the top of the menu and — being off to the side / full-screen — skews axis detection.
    private static bool IsCloseChrome(string name) => name == "Close" || name == "CloseTransparent";

    // The persistent top-bar buttons (the HOME/DECKS/… nav strip + the ⋮ overflow button), gathered by
    // identity from HUBTopController_L so they can be excluded from the menu's own list — they're always
    // available on the top bar, so repeating them inside the menu is just clutter.
    private static HashSet<Selectable> TopBarButtons()
    {
        var set = new HashSet<Selectable>();
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        if (top == null) return set;
        var navGroups = ReadField<List<PersistentNavButtonGroup>>(top, "persistentNavButtons");
        if (navGroups != null)
            foreach (var pnb in navGroups)
            {
                var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                if (btn != null) set.Add(btn);
            }
        var settingsT = ReadField<Transform>(top, "settingsButton");
        var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
        if (overflow != null) set.Add(overflow);
        return set;
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
