using System.Collections.Generic;
using System.Reflection;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// Number-key tab shortcuts for the persistent HUB top strip (HOME / DECKS / PROFILE / SHOP /
// BATTLE PASS / CARD DEX). Pressing 1-6 — on the number row or the numpad — jumps straight to that
// screen: the accelerator a sighted player gets by clicking a tab, without arrowing across the strip.
//
// The strip is HUBTopController_L.persistentNavButtons, the same global element every HUB screen
// handler already navigates, so this is a single global reader (like CurrencyReader / PlayerNameReader)
// rather than per-handler code — every HUB screen, present and future, gets it for free. Each digit maps
// to the Nth *visible* nav tab in reading order, matching what the user hears when arrowing the strip.
//
// It fires ONLY when that tab is genuinely usable right now: the top controller is present, the tab
// button is active, and — crucially — raycast-reachable. When a modal / overlay covers the strip its
// blocker fails that raycast exactly as it blocks a mouse click, so a digit typed under a popup (or in
// battle, where there's no strip at all) is left alone. That native "is it actually clickable" check is
// what scopes this to "screens that let you switch tabs" without hardcoding a screen list. Skipped while
// a text field is focused (the digit is being typed — a redeem code, deck name, search box).
//
// Switching goes through the button's own onClick (→ HUBTopController_L.UpdatePersistentNav), so every
// native guard — CanNavigate, CanEnter, navBusy, unsaved-edit CanExit prompts — runs untouched, and the
// destination screen's handler announces itself on activation (identical to arrowing to the tab and
// pressing Enter). Pressing the current tab's own number confirms with "<tab>, selected"; a visible but
// feature-gated tab reads "<tab>, unavailable" instead of a silent nothing.
internal static class HubTabShortcuts
{
    private static SpeechEngine? _speech;

    public static void Install(SpeechEngine speech) => _speech = speech;

    public static void Tick()
    {
        if (_speech == null || !Application.isFocused) return;

        // Detect a digit press (1-9) — number row OR numpad — before touching the scene, so the
        // lookups below only run on an actual digit press, never every frame.
        int digit = -1;
        for (int d = 1; d <= 9; d++)
            if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha1 + (d - 1))
             || UnityEngine.Input.GetKeyDown(KeyCode.Keypad1 + (d - 1))) { digit = d; break; }
        if (digit < 0) return;

        // A focused text field owns the digit (deck name, redeem code, search box).
        if (IsTextInputFocused()) return;

        try { HandleDigit(digit); }
        catch (System.Exception ex) { Log.Error($"HubTabShortcuts: {ex}"); }
    }

    private static void HandleDigit(int digit)
    {
        var topController = UnityEngine.Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController == null) return;   // not on a HUB screen → the strip isn't here

        var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
        if (navGroups == null) return;

        // Number the VISIBLE tabs in reading order — the same order the strip announces when arrowed,
        // so "3" is the third tab the user would hear. The overflow "⋮" isn't in persistentNavButtons,
        // so it's naturally excluded from the numbering.
        var tabs = new List<(PersistentNavButtonGroup group, Button btn)>();
        foreach (var pnb in navGroups)
        {
            var btn = pnb != null ? pnb.navButton : null;
            if (btn != null && btn.isActiveAndEnabled) tabs.Add((pnb!, btn));
        }
        tabs.Sort((a, b) => ReadingOrder(a.btn.transform).CompareTo(ReadingOrder(b.btn.transform)));

        int i = digit - 1;
        if (i >= tabs.Count) return;   // no tab at this number (e.g. 7 pressed with 6 tabs)
        var (group, target) = tabs[i];

        // The strip must actually be clickable right now. A modal / overlay blocker over it fails this
        // raycast just as it blocks a mouse click, so a digit pressed under a popup is left alone.
        if (!KeyboardNavigator.IsRaycastReachable(target)) return;

        // Already on this tab → confirm where you are rather than re-navigate (the game no-ops a
        // re-press anyway, silently — so without this the key would seem dead).
        if (group.IsOn()) { _speech!.Say(UiLabels.TopNavLabel(target), interrupt: true); return; }

        // Visible but gated (a feature-locked tab) → say why instead of nothing.
        if (!target.IsInteractable()) { _speech!.Say(UiLabels.TopNavLabel(target), interrupt: true); return; }

        // Real switch: the button's own click, with every native navigation guard intact. The
        // destination screen's handler announces itself on activation.
        Log.Info($"HubTabShortcuts: {digit} → {KeyboardNavigator.ResolveBaseLabel(target)}");
        target.onClick?.Invoke();
    }

    private static bool IsTextInputFocused()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
    }

    // Top-to-bottom, then left-to-right — identical to every HUB handler's strip ordering, so the
    // number order matches the arrow order.
    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000f + pos.x;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }
}
