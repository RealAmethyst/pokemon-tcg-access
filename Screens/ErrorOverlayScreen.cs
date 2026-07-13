using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using TPCI.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Generic error overlay — driver: ErrorOverlay (an OverlayScreenGroup) shown for purchase failures
// ("insufficient funds"), network errors, etc. It carries an error line + a user-action line
// (errorText/errorLocText, userActionText/userActionLocText) and one CLOSE button whose onClick is
// HandleButtonAction (close, or restart on a fatal error). Without a handler it pops over the HUB
// and traps a keyboard user.
//
// ErrorOverlay overrides neither OnActivate nor OnDeActivate, so we hook the SHARED base
// OverlayScreenGroup.OnActivate with a base-typed __instance + an `is ErrorOverlay` guard (typing
// __instance as the subclass would throw a bad cast for every other overlay). Dismissal is the Tick
// backstop — the overlay GameObject goes inactive on close.
internal sealed class ErrorOverlayScreen : IScreenHandler
{
    private static ErrorOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private ErrorOverlay? _overlay;
    private bool _active;

    public string Name => "Error";

    public ErrorOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ErrorOverlayScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("ErrorOverlayScreen: could not resolve OverlayScreenGroup.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ErrorOverlayScreen), nameof(Activate_Postfix))));

        Log.Info("ErrorOverlayScreen installed");
    }

    // Fires for every overlay activation (base method) — act only for ErrorOverlay.
    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not ErrorOverlay eo) return;
        _instance._overlay = eo;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "Error|dead" : $"Error|{_nav.Index}|{Message()}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _overlay = null;
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    private void Close()
    {
        // Route through the real CLOSE button so a fatal-error "restart" still happens; fall back
        // to closing the overlay directly.
        var btn = _nav.Items.OfType<Button>().FirstOrDefault();
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) { btn.onClick?.Invoke(); return; }
        if (_overlay != null) _overlay.Deactivate();
    }

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>()
            .ToList();
        _nav.SetItems(buttons);
        _nav.AllowHorizontal = true;
        if (!announce) return;

        var msg = Message();
        _router.Speech.Say(string.IsNullOrWhiteSpace(msg) ? Name : msg!, interrupt: true);
        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"ErrorOverlay: {msg} | {buttons.Count} buttons");
    }

    // The error line + the user-action line, preferring the localized component then the raw TMP.
    private string Message()
    {
        if (_overlay == null) return "";
        var parts = new List<string>(2);
        var err = LocOf(ReadField<LocText>(_overlay, "errorLocText")) ?? TmpOf(ReadField<TMP_Text>(_overlay, "errorText"));
        if (!string.IsNullOrWhiteSpace(err)) parts.Add(err!);
        var act = LocOf(ReadField<LocText>(_overlay, "userActionLocText")) ?? TmpOf(ReadField<TMP_Text>(_overlay, "userActionText"));
        if (!string.IsNullOrWhiteSpace(act)) parts.Add(act!);
        return string.Join(". ", parts);
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? TmpOf(TMP_Text? t)
    {
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? null : s;
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

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
