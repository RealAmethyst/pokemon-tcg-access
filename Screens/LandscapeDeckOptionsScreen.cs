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

// Deck-options popup — driver: LandscapeDeckOptions (a HUBGroupController) opened via ShowPopup(deckName)
// from BOTH the DECKS screen (HUBDeckSelectController.OpenLandscapeDeckOptions) and the deck editor
// (HUBEditDeckController). It's the menu with a deck-name header + DECK CUSTOMIZATION / TEST DECK /
// DUPLICATE / VIEW DECK / DECK STATS / EXPORT / DELETE (+ a close X). Each option's onClick routes
// through LandscapeDeckOptions.Settings_* -> the IDeckOptionsHandler (DeckPanel_L on DECKS, or the
// editor) and then Deactivate. Without a handler the whole menu is silent — this is the one the user
// hit where DECK STATS / VIEW DECK / DELETE couldn't be reached.
//
// HUBGroupController.Activate/Deactivate just SetActive(true/false) the GameObject, so ShowPopup is the
// open hook and gameObject.activeInHierarchy is the close signal (Tick backstop). DECK STATS here opens
// DeckStatsOverlayController -> DeckStatsOverlayScreen; the others route to their real flows.
internal sealed class LandscapeDeckOptionsScreen : IScreenHandler
{
    private static LandscapeDeckOptionsScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private LandscapeDeckOptions? _popup;
    private bool _active;

    public string Name => "Deck options";

    public LandscapeDeckOptionsScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new LandscapeDeckOptionsScreen(router);

        var show = AccessTools.Method(typeof(LandscapeDeckOptions), "ShowPopup", new[] { typeof(string) });
        if (show == null)
        {
            Log.Error("LandscapeDeckOptionsScreen: could not resolve LandscapeDeckOptions.ShowPopup");
            return;
        }
        harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(LandscapeDeckOptionsScreen), nameof(Show_Postfix))));
        Log.Info("LandscapeDeckOptionsScreen installed");
    }

    private static void Show_Postfix(LandscapeDeckOptions __instance)
    {
        if (_instance == null) return;
        _instance._popup = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _popup == null ? "DeckOptions|dead" : $"DeckOptions|{_nav.Index}|{Title()}";

    public void Tick()
    {
        if (_popup == null || !_popup.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _popup = null;
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    // Escape = cancel: the popup's own teardown (HUBGroupController.Deactivate, public).
    private void Close() => _popup?.Deactivate();

    private void Rebuild(bool announce)
    {
        if (_popup == null || !_popup.gameObject.activeInHierarchy) return;

        var buttons = _popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        _nav.SetItems(buttons);
        _nav.ForceFlatVertical();   // a vertical menu of options (+ a close X) — predictable Up/Down
        if (!announce) return;

        var title = Title();
        _router.Speech.Say(string.IsNullOrWhiteSpace(title) ? Name : $"{Name}. {title}", interrupt: true);

        // Land on the first labelled option (CUSTOMIZATION/…), not the icon-only close X which sorts
        // first by reading order (it sits above the list). Close stays reachable (Up / Escape).
        int firstOption = buttons.FindIndex(HasLetterLabel);
        if (firstOption >= 0) _nav.FocusIndex(firstOption, announce: true, interrupt: false);
        else if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The popup's deck-name header, so it reads "Deck options. <deck name>".
    private string Title()
    {
        if (_popup == null) return "";
        var loc = ReadField<LocText>(_popup, "deckNameText");
        var s = loc != null ? loc.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? "" : s!.Trim();
    }

    private static bool HasLetterLabel(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (!string.IsNullOrEmpty(t.text) && t.text.Any(char.IsLetter)) return true;
        return false;
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
