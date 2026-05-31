using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
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

        // Build the spoken preamble (title + body), skipping a title that just repeats the parent
        // screen's. Speak it barging in, then QUEUE the focused button after it as its own announce —
        // two separate utterances, so the button reads as a distinct, independently-navigable item.
        // FocusFirst's interruptPrior:false is what queues the button instead of cutting the body off;
        // the bug was the parameterless FocusFirst(), which defaults to interruptPrior:true.
        var preamble = BuildPreamble(title, body, _router.PausedTitle);
        if (!string.IsNullOrWhiteSpace(preamble))
            _router.Speech.Say(preamble!, interrupt: true);
        _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Title + body into one spoken preamble, skipping a title that just repeats the parent screen's.
    private static string? BuildPreamble(string? title, string? body, string? parentTitle)
    {
        var titleRedundant = string.IsNullOrWhiteSpace(title)
            || string.Equals(title?.Trim(), parentTitle?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (titleRedundant) return string.IsNullOrWhiteSpace(body) ? title : body;
        if (string.IsNullOrWhiteSpace(body)) return title;
        return $"{title}. {body}";
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
        // Game window regained focus — the popup may have opened (e.g. PTC login retry) while the
        // user was on a browser tab. Speak the body barging in, then queue the focused button after it.
        if (_msg == null) return;

        var (title, body, _) = ReadTexts(_msg);
        var preamble = BuildPreamble(title, body, _router.PausedTitle);
        if (!string.IsNullOrWhiteSpace(preamble))
            _router.Speech.Say(preamble!, interrupt: true);
        _nav.RefocusCurrent(announce: true, interrupt: false);
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

        // Prefer the live TMP text, but the loc-args overload (SetTextByIDFormat — used by the logout
        // confirm) may not have pushed to the TMP yet when our Open postfix runs. Fall back to the
        // LocText's own CurrentText, then resolve its resourceId through the loc table, so the body is
        // never silently empty. (Without this the logout body read blank because only the title — set
        // via SetTextByID — had rendered.)
        var tmp = loc.GetComponent<TMP_Text>();
        if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text)) return tmp.text;

        if (loc is LocText lt)
        {
            if (!string.IsNullOrWhiteSpace(lt.CurrentText)) return lt.CurrentText;
            if (!string.IsNullOrEmpty(lt.resourceId))
            {
                var resolved = LocalizationHook.Get(lt.resourceId);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
        }
        return tmp != null ? tmp.text : null;
    }

    private static T? ReadField<T>(SimpleMessage m, string fieldName) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return typeof(SimpleMessage).GetField(fieldName, flags)?.GetValue(m) as T;
    }
}
