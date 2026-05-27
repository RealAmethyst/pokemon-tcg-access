using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Confirmation popup used across the game's startup flows (SimpleMessage in TPCI.RainierClient).
//
// OpenNewMessage(title, message, buttonName, confirm, cancel?) pushes us on top of whatever
// screen invoked it (e.g. LanguageSelectScreen). Close() pops us off. While we're top of the
// stack the underlying screen is paused so its nav stops responding — and KeyboardNavigator's
// raycast-blocked check would have caught it anyway.
//
// Selectables exposed:
//   - acceptButton (the big colored CTA, e.g. "CONTINUE")
//   - closeButton (the small ✕, only present when a cancel callback was supplied)
internal sealed class SimpleMessageScreen : IScreenHandler
{
    private static SimpleMessageScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private SimpleMessage? _msg;

    public string Name => "Dialog";

    public SimpleMessageScreen(ScreenRouter router)
    {
        _router = router;
        _nav = new KeyboardNavigator(router.Speech);
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new SimpleMessageScreen(router);

        // Two public OpenNewMessage overloads exist (string-args and locId-args). LanguageSelect
        // uses the string-args one. Patch both for completeness.
        var openStr = AccessTools.Method(typeof(SimpleMessage), nameof(SimpleMessage.OpenNewMessage),
            new[] { typeof(string), typeof(string), typeof(string),
                    typeof(UnityEngine.Events.UnityAction), typeof(UnityEngine.Events.UnityAction), typeof(bool) });
        var openLoc = AccessTools.Method(typeof(SimpleMessage), nameof(SimpleMessage.OpenNewMessage),
            new[] { typeof(string), typeof(string), typeof(object[]), typeof(string),
                    typeof(UnityEngine.Events.UnityAction), typeof(UnityEngine.Events.UnityAction), typeof(bool) });
        var close = AccessTools.Method(typeof(SimpleMessage), nameof(SimpleMessage.Close));

        var openPostfix = new HarmonyMethod(AccessTools.Method(typeof(SimpleMessageScreen), nameof(Open_Postfix)));
        var closePostfix = new HarmonyMethod(AccessTools.Method(typeof(SimpleMessageScreen), nameof(Close_Postfix)));

        if (openStr != null) harmony.Patch(openStr, postfix: openPostfix);
        if (openLoc != null) harmony.Patch(openLoc, postfix: openPostfix);
        if (close != null) harmony.Patch(close, postfix: closePostfix);

        Log.Info("SimpleMessageScreen installed");
    }

    private static void Open_Postfix(SimpleMessage __instance)
    {
        if (_instance == null) return;
        _instance._msg = __instance;
        _instance._router.Activate(_instance);
    }

    private static void Close_Postfix(SimpleMessage __instance)
    {
        if (_instance == null) return;
        if (_instance._msg != __instance) return;
        _instance._router.Deactivate(_instance);
        _instance._msg = null;
    }

    public void OnEnter()
    {
        if (_msg == null) return;

        var (title, body, _) = ReadTexts(_msg);
        _router.SetCurrentTitle(this, title);

        var items = new List<Selectable>();
        var accept = ReadField<Button>(_msg, "acceptButton");
        var close = ReadField<Button>(_msg, "closeButton");
        if (accept != null && accept.gameObject.activeInHierarchy) items.Add(accept);
        if (close != null && close.gameObject.activeInHierarchy) items.Add(close);

        _nav.SetItems(items);

        // Escape / Backspace = invoke the X. SimpleMessage hides closeButton when the caller
        // didn't supply a closeAction (OK-only dialogs); in that case OnBack stays null so
        // back is a no-op, matching the game's mouse behavior.
        _nav.OnBack = (close != null && close.gameObject.activeInHierarchy)
            ? () => close.onClick?.Invoke()
            : (Action?)null;

        // Skip the popup title when it matches the parent screen's title — PTCGL frequently
        // reuses the same resourceID for both (e.g. the language picker's header AND its
        // confirmation modal both render "Select Language"), and the user already knows
        // which screen they're on. Empty titles are also skipped. Genuinely distinct titles
        // ("Error", "You have been logged out", etc.) still get spoken.
        var parentTitle = _router.PausedTitle;
        var titleRedundant = string.IsNullOrWhiteSpace(title)
            || string.Equals(title?.Trim(), parentTitle?.Trim(), System.StringComparison.OrdinalIgnoreCase);

        string? preamble;
        if (titleRedundant)
            preamble = string.IsNullOrWhiteSpace(body) ? title : body;
        else if (string.IsNullOrWhiteSpace(body))
            preamble = title;
        else
            preamble = $"{title}. {body}";

        if (!string.IsNullOrWhiteSpace(preamble))
            _router.Speech.Say(preamble!, interrupt: true);
        _nav.FocusFirst();
    }

    public void OnResume()
    {
        // Resume isn't part of SimpleMessage's normal lifecycle in PTCGL (modals are popped, not
        // re-shown), but if a future screen ever stacks something over this dialog, re-announce
        // the focused button without re-reading the body text.
        if (_msg == null) return;
        _nav.RefocusCurrent(announce: true);
    }

    public void RespeakAll()
    {
        // Game window regained focus — the popup may have opened (e.g. PTC login retry dialog)
        // while the user was on a browser tab. Speak the same preamble OnEnter would have, then
        // re-announce the focused button.
        if (_msg == null) return;

        var (title, body, _) = ReadTexts(_msg);
        var parentTitle = _router.PausedTitle;
        var titleRedundant = string.IsNullOrWhiteSpace(title)
            || string.Equals(title?.Trim(), parentTitle?.Trim(), StringComparison.OrdinalIgnoreCase);

        string? preamble;
        if (titleRedundant)
            preamble = string.IsNullOrWhiteSpace(body) ? title : body;
        else if (string.IsNullOrWhiteSpace(body))
            preamble = title;
        else
            preamble = $"{title}. {body}";

        if (!string.IsNullOrWhiteSpace(preamble))
            _router.Speech.Say(preamble!, interrupt: true);
        _nav.RefocusCurrent(announce: true);
    }

    public string StateSignature()
    {
        // Include title + body — the same SimpleMessage instance gets reused for different
        // messages (PTC retry, language confirm, errors), so detecting "is this a different
        // popup than before" requires looking at the actual text shown.
        if (_msg == null) return "Dialog|dead";
        var (title, body, _) = ReadTexts(_msg);
        return $"Dialog|{title}|{body}|{_nav.Index}";
    }

    public void OnExit()
    {
        // Keep _msg set across pause/resume; Close_Postfix is the only place that clears it.
    }

    public void Tick()
    {
        if (_msg == null)
        {
            _router.Deactivate(this);
            return;
        }
        _nav.Tick();
    }

    private static (string? title, string? body, string? buttonName) ReadTexts(SimpleMessage m)
    {
        return (
            ReadLocText(m, "messageTitleLoc"),
            ReadLocText(m, "messageTextLoc"),
            ReadLocText(m, "buttonTextLoc")
        );
    }

    private static string? ReadLocText(SimpleMessage m, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var fi = typeof(SimpleMessage).GetField(fieldName, flags);
        var loc = fi?.GetValue(m) as UnityEngine.Component;
        if (loc == null) return null;
        var tmp = loc.GetComponent<TMP_Text>();
        return tmp != null ? tmp.text : null;
    }

    private static T? ReadField<T>(SimpleMessage m, string fieldName) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return typeof(SimpleMessage).GetField(fieldName, flags)?.GetValue(m) as T;
    }
}
