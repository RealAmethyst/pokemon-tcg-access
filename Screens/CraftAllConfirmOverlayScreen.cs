using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.UI.Screens;
using UnityEngine;
using UnityEngine.UI;
using _Rainier.Scripts.UI.DeckEditor.Collection;

namespace PokemonTCGLiveAccessibility.Screens;

// "Exchange All" confirmation (NEW in 1.5.0) — driver: CraftAllConfirmOverlay (an OverlayScreenGroup)
// shown when the deck editor's "Exchange all unowned" button is pressed and the player can afford it
// (CraftUnownedCardsButtonController.ShowCraftingConfirmation -> Setup -> Activate). It shows a header
// ("Exchange N cards"), the total cost, a card gallery, and a confirm button (InvokePurchaseDelegate)
// + cancel/close. Silent without a handler.
//
// CraftAllConfirmOverlay does NOT override OnActivate, so — like ErrorOverlayScreen — we hook the
// shared base OverlayScreenGroup.OnActivate with a base-typed __instance + an `is` guard (typing it
// as the subclass would bad-cast for every other overlay; see [[feedback-overlay-hook-hazard]]).
// Dismissal is the Tick backstop.
internal sealed class CraftAllConfirmOverlayScreen : IScreenHandler
{
    private static CraftAllConfirmOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private CraftAllConfirmOverlay? _overlay;
    private bool _active;

    public string Name => "Exchange cards";

    public CraftAllConfirmOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new CraftAllConfirmOverlayScreen(router);

        var activate = AccessTools.Method(typeof(OverlayScreenGroup), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("CraftAllConfirmOverlayScreen: could not resolve OverlayScreenGroup.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftAllConfirmOverlayScreen), nameof(Activate_Postfix))));
        Log.Info("CraftAllConfirmOverlayScreen installed");
    }

    // Fires for every overlay activation (base method) — act only for CraftAllConfirmOverlay.
    private static void Activate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null || __instance is not CraftAllConfirmOverlay co) return;
        _instance._overlay = co;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "Exchange|dead" : $"Exchange|{_nav.Index}|{Message()}";

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

    // Escape = cancel (don't buy): the overlay's own teardown.
    private void Close() => _overlay?.Deactivate();

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
    }

    // "<header>. Cost <cost>." — header is the game's own "Exchange N cards" string, cost the total.
    private string Message()
    {
        if (_overlay == null) return Name;
        var header = TmpOf(ReadField<TMP_Text>(_overlay, "headerLabel"));
        var cost = TmpOf(ReadField<TMP_Text>(_overlay, "costLabel"));
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(header)) parts.Add(header!);
        if (!string.IsNullOrWhiteSpace(cost)) parts.Add($"cost {cost}");
        return parts.Count == 0 ? Name : string.Join(". ", parts);
    }

    private static string? TmpOf(TMP_Text? t)
    {
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static T? ReadField<T>(object inst, string name) where T : class
    {
        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = inst.GetType(); t != null; t = t.BaseType)
        {
            var fi = t.GetField(name, f);
            if (fi != null) return fi.GetValue(inst) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000 + p.x;
    }
}
