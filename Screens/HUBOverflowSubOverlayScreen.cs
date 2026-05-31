using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The overflow "⋮" menu's text/link sub-panels: ABOUT, TERMS OF USE, PRIVACY NOTICE. Each is opened
// by its own HUBOverflowMenuController.OnClick* method, which OverlayManager.OpenOverlay's a dedicated
// OverlayScreenGroup field (_aboutController / _tosController / _privacyController). Without a handler,
// focus stays stranded on the menu behind the panel.
//
// One handler covers all three (same shape: an OverlayScreenGroup of readable text + a few buttons):
//   • ABOUT (HUBOverflowAboutController) — the app version line plus DELETE ACCOUNT and the
//     Privacy Notice / EULA / Credits / Attributions link buttons. (Delete Account especially must be
//     reachable + readable, so this isn't optional polish.)
//   • TERMS OF USE / PRIVACY NOTICE — a legal-text panel with a back/close affordance.
//
// Presentation mirrors HUBSettingsScreen / HUBOverflowMenuScreen: read the panel name + its body text
// as a preamble, present the real buttons as a flat vertical nav (close-chrome filtered, since Escape
// already closes), and lifecycle off the game's overlay system (IsOpenOverlay to detect an external
// close, CloseOverlay on Escape → returns to the still-open ⋮ menu beneath). Buttons keep their own
// onClick, so links/Delete Account fire exactly as a mouse would.
internal sealed class HUBOverflowSubOverlayScreen : IScreenHandler
{
    private static HUBOverflowSubOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private OverlayManager? _overlayManager;
    private OverlayScreenGroup? _group;
    private string _panelName = "About";
    private bool _active;

    public string Name => _panelName;

    public HUBOverflowSubOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBOverflowSubOverlayScreen(router);

        Hook(harmony, "OnClickAbout", nameof(About_Postfix));
        Hook(harmony, "OnClickTOS", nameof(TOS_Postfix));
        Hook(harmony, "OnClickPrivacyPolicy", nameof(Privacy_Postfix));

        Log.Info("HUBOverflowSubOverlayScreen installed");
    }

    private static void Hook(HarmonyLib.Harmony harmony, string method, string postfix)
    {
        var m = AccessTools.Method(typeof(HUBOverflowMenuController), method);
        if (m == null) { Log.Error($"HUBOverflowSubOverlayScreen: could not resolve {method}"); return; }
        harmony.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBOverflowSubOverlayScreen), postfix)));
    }

    private static void About_Postfix(HUBOverflowMenuController __instance)
        => Open(__instance, "_aboutController", LocOr("overflow_about", "About"));
    private static void TOS_Postfix(HUBOverflowMenuController __instance)
        => Open(__instance, "_tosController", LocOr("overflow_termsofuse", "Terms of Use"));
    private static void Privacy_Postfix(HUBOverflowMenuController __instance)
        => Open(__instance, "_privacyController", LocOr("overflow_privacynotice", "Privacy Notice"));

    private static void Open(HUBOverflowMenuController ctrl, string field, string name)
    {
        try
        {
            if (_instance == null || ctrl == null) return;
            var group = ReadField<OverlayScreenGroup>(ctrl, field);
            if (group == null) { Log.Warn($"HUBOverflowSubOverlayScreen: {field} was null"); return; }

            _instance._overlayManager = ReadField<OverlayManager>(ctrl, "overlayManager");
            _instance._group = group;
            _instance._panelName = name;

            if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
            else _instance.Rebuild(announce: true);
        }
        catch (System.Exception ex) { Log.Error($"HUBOverflowSubOverlayScreen.Open threw: {ex.Message}"); }
    }

    public void OnEnter() => Rebuild(announce: true);

    public void OnResume()
    {
        // A child (e.g. the Delete Account confirm modal) closed. Re-present if we're still open;
        // otherwise Tick pops us next frame.
        if (!IsStillOpen()) return;
        Rebuild(announce: true);
    }

    public void OnExit() { }

    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => !IsStillOpen() ? "SubOverlay|dead" : $"SubOverlay|{_panelName}|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (!IsStillOpen()) { Close(); return; }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    private void Rebuild(bool announce)
    {
        if (_group == null) return;

        // Force layout before reading positions — overlays open animated, so transforms are at origin
        // on the first frame and reading-order sorts would scramble.
        if (_group.transform is RectTransform rt)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        // Buttons: real entries only (drop the X / invisible tap-outside blocker; Escape closes).
        var items = _group.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Where(b => !IsCloseChrome(b.gameObject.name))
            .Cast<Selectable>()
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        _nav.SetItems(items, DefaultLabelOrText);
        _nav.ForceFlatVertical();              // a vertical list of links — predictable Up/Down
        _nav.OnBack = Close;

        if (!announce) return;

        _router.SetCurrentTitle(this, _panelName);
        var body = BuildBodyText(items);
        var preamble = string.IsNullOrWhiteSpace(body) ? _panelName : $"{_panelName}. {body}";
        _router.Speech.Say(preamble, interrupt: true);
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"SubOverlay '{_panelName}': {items.Count} buttons — {string.Join(", ", items.Take(12).Select(s => s.gameObject.name))}");
    }

    // The panel's readable text (version line, legal copy, descriptions): every TMP NOT under a
    // button, in reading order, deduped. Long legal docs read as one interruptible utterance for now;
    // if paragraph navigation is wanted, that's a follow-up.
    private string BuildBodyText(List<Selectable> buttons)
    {
        if (_group == null) return "";
        var btnSet = new HashSet<GameObject>(buttons.Select(b => b.gameObject));
        var seen = new HashSet<int>();
        var parts = new List<(float order, string text)>();

        foreach (var t in _group.GetComponentsInChildren<TMP_Text>(includeInactive: false))
        {
            if (t == null) continue;
            if (t.GetComponentInParent<Button>() != null) continue;   // skip button labels
            if (!seen.Add(t.GetInstanceID())) continue;
            string txt = t.text;
            if (string.IsNullOrWhiteSpace(txt))
            {
                var loc = t.GetComponent<LocText>();
                if (loc != null)
                {
                    txt = loc.CurrentText;
                    if (string.IsNullOrWhiteSpace(txt) && !string.IsNullOrEmpty(loc.resourceId))
                        txt = LocalizationHook.Get(loc.resourceId) ?? "";
                }
            }
            if (!string.IsNullOrWhiteSpace(txt)) parts.Add((ReadingOrder(t.transform), txt.Trim()));
        }

        return string.Join(". ", parts.OrderBy(p => p.order).Select(p => p.text));
    }

    private void Close()
    {
        if (!_active) return;
        _active = false;
        try
        {
            if (_overlayManager != null && _group != null && _overlayManager.IsOpenOverlay(_group))
                _overlayManager.CloseOverlay(_group);
        }
        catch (System.Exception ex) { Log.Error($"HUBOverflowSubOverlayScreen.Close threw: {ex.Message}"); }
        _group = null;
        _router.Deactivate(this);
    }

    private bool IsStillOpen()
    {
        if (_group == null) return false;
        try { if (_overlayManager != null) return _overlayManager.IsOpenOverlay(_group); } catch { }
        return _group != null && _group.gameObject.activeInHierarchy;
    }

    // Button label: its own text child if it has one, else the humanized name (DefaultLabel handles
    // both, plus disabled state).
    private static string DefaultLabelOrText(Selectable s) => KeyboardNavigator.DefaultLabel(s);

    private static bool IsCloseChrome(string name) => name == "Close" || name == "CloseTransparent";

    private static string LocOr(string id, string fallback)
    {
        try { var v = LocalizationManager.Instance?.GetString(id); if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v!; } catch { }
        return fallback;
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
