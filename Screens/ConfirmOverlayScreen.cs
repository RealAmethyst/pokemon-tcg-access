using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Generic confirm dialog — driver: ConfirmOverlay (an OverlayScreenGroup). It carries a title, a
// message, and a user-action line (titleLocText / messageLocText / userActionLocText) plus a
// CONFIRM and a CANCEL button whose onClicks fire the overlay's OnConfirm / OnCancel UnityEvents
// (wired per-caller). The friends system uses it for "decline friend request?", "remove friend?",
// and "too many friends"; it's generic, so a handler here covers any caller that opens it.
//
// ConfirmOverlay overrides neither OnActivate nor OnDeActivate, so — like ErrorOverlayScreen — we
// hook the SHARED base OverlayScreenGroup.OnActivate with a base-typed __instance + an `is` guard
// (typing it as the subclass would bad-cast for every other overlay; see
// [[feedback-overlay-hook-hazard]]). Dismissal is the Tick backstop (the GameObject goes inactive).
internal sealed class ConfirmOverlayScreen : IScreenHandler
{
    private static ConfirmOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private ConfirmOverlay? _overlay;
    private bool _active;

    public string Name => "Confirm";

    public ConfirmOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Cancel;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ConfirmOverlayScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("ConfirmOverlayScreen: could not resolve OverlayScreenGroup.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ConfirmOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("ConfirmOverlayScreen installed");
    }

    // Fires for every overlay activation (base method) — act only for ConfirmOverlay.
    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not ConfirmOverlay co) return;
        _instance._overlay = co;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "Confirm|dead" : $"Confirm|{_nav.Index}|{Message()}";

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

    // Escape = cancel (the safe no-op): fire the overlay's own Cancel(), routing through whatever
    // OnCancel handler the caller wired (which closes it).
    private void Cancel() => _overlay?.Cancel();

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        var buttons = _overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        _nav.SetItems(buttons);
        _nav.AllowHorizontal = true;
        if (!announce) return;

        var msg = Message();
        _router.Speech.Say(string.IsNullOrWhiteSpace(msg) ? Name : msg!, interrupt: true);
        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"ConfirmOverlay: {msg} | {buttons.Count} buttons");
    }

    // "<title>. <message>." — read from the localized text components.
    private string Message()
    {
        if (_overlay == null) return Name;
        var parts = new List<string>(2);
        var title = LocOf(ReadField<LocText>(_overlay, "titleLocText"));
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title!);
        var body = LocOf(ReadField<LocText>(_overlay, "messageLocText"));
        if (!string.IsNullOrWhiteSpace(body)) parts.Add(body!);
        return parts.Count == 0 ? Name : string.Join(". ", parts);
    }

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
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
