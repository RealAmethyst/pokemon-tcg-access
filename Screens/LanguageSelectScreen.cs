using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// First-launch Select Language screen. Driver: LanguageSelect (TPCI.RainierClient).
//
// LoginManager.InitializeLanguageSelect() instantiates a prefab, calls LanguageSelect.Initialize(),
// which only opens the screen when PlayerPrefs key "LoginManager.hasPlayerSelectedLanguage" is 0.
// The 7 language buttons are children of that prefab — plain uGUI Buttons whose onClick
// UnityEvents are wired in the prefab to call LanguageSelect.SelectLanguage("en"|"es"|...).
// We discover them, focus them via Selectable.Select(), and activate via onClick.Invoke().
//
// Note: the title text comes from a separate "_startupLoc" table that LocalizationManager.Instance
// doesn't see — asking the main manager for "startup_select_language" yields "(Missing)<…>".
// So we read selectLanguageTitle.text directly (set by Initialize via _startupLoc).
internal sealed class LanguageSelectScreen : IScreenHandler
{
    private static LanguageSelectScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private LanguageSelect? _ls;

    public string Name => "Select Language";

    public LanguageSelectScreen(ScreenRouter router)
    {
        _router = router;
        _nav = new KeyboardNavigator(router.Speech);
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new LanguageSelectScreen(router);

        var target = AccessTools.Method(typeof(LanguageSelect), nameof(LanguageSelect.Initialize));
        if (target == null)
        {
            Log.Error("LanguageSelectScreen: could not resolve LanguageSelect.Initialize");
            return;
        }
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(LanguageSelectScreen), nameof(Initialize_Postfix)));
        harmony.Patch(target, postfix: postfix);
        Log.Info("LanguageSelectScreen installed (patched LanguageSelect.Initialize)");
    }

    private static void Initialize_Postfix(LanguageSelect __instance)
    {
        if (_instance == null) return;
        // Initialize runs every launch; the screen is only actually shown on first launch.
        if (__instance.hasPlayerSelectedLanguage) return;
        _instance._ls = __instance;
        _instance._router.Activate(_instance);
    }

    public void OnEnter()
    {
        // Fresh activation: rebuild item list (resets nav index), speak the screen title, focus first.
        if (_ls == null || _ls.hasPlayerSelectedLanguage) return;

        var debug = CollectDebugButtons(_ls);
        var langButtons = _ls
            .GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => !debug.Contains(b))
            .Cast<UnityEngine.UI.Selectable>()
            .OrderBy(s => ReadingOrder(s.transform))   // row-major: grid Up/Down/Left/Right must line up with the visual
            .ToList();

        if (langButtons.Count == 0)
        {
            Log.Warn("LanguageSelectScreen: no language buttons discovered under LanguageSelect");
            return;
        }

        _nav.SetItems(langButtons);
        // Navigate as the true 2-column grid it visually is: Up/Down between rows, Left/Right
        // between columns (SetItems auto-detected Columns = 2 from the layout). Tab off — single group.
        // Earlier this was forced to a flat vertical list, but with Columns = 2 the column-preserving
        // vertical move only ever walked the left column, leaving the right column (Español, Français,
        // Português) completely unreachable. A grid matches what a sighted player sees.
        _nav.AllowVertical = true;
        _nav.AllowHorizontal = true;
        _nav.AllowTab = false;

        // selectLanguageTitle is populated by Initialize from the startup-only loc table; just read what's rendered.
        var title = ReadTitleText(_ls) ?? Name;
        _router.SetCurrentTitle(this, title);
        _router.Speech.Say(title, interrupt: true);
        _nav.FocusFirst();
    }

    public void OnResume()
    {
        // Returning from a popup (e.g. confirmation modal was cancelled). Keep the same item
        // list and focus index — just re-announce where the user is, no title repeat.
        if (_ls == null || _ls.hasPlayerSelectedLanguage) return;
        _nav.RefocusCurrent(announce: true);
    }

    public void RespeakAll()
    {
        // Game window regained focus — user may have missed announcements while we were silent.
        // Speak title + current focused item, but don't rebuild state or reset focus.
        if (_ls == null || _ls.hasPlayerSelectedLanguage) return;
        var title = ReadTitleText(_ls) ?? Name;
        _router.Speech.Say(title, interrupt: true);
        _nav.RefocusCurrent(announce: true);
    }

    public string StateSignature()
    {
        // Screen identity + focused-index. Don't include the title text — it doesn't change for
        // this screen. If the prefab got destroyed, _ls reads null and the sig flips → re-announce
        // covers "you came back to a different screen entirely".
        return $"LangSelect|{(_ls == null ? "dead" : "live")}|{_nav.Index}";
    }

    public void OnExit()
    {
        // Keep _ls and _nav state (items + focus index) intact for a potential OnResume.
        // Tick's null check handles real destruction.
    }

    public void Tick()
    {
        // Deactivate only on real exit conditions:
        //   - the LanguageSelect GameObject has been destroyed
        //   - the player just confirmed a language (PlayerPrefs flag flips inside ConfirmLanguageSelection)
        // Do NOT check languageAnimation.isOpen — it's false during the opening animation,
        // which would cause us to self-deactivate on the very next frame after activation.
        if (_ls == null || _ls.hasPlayerSelectedLanguage)
        {
            _router.Deactivate(this);
            return;
        }
        _nav.Tick();
    }

    // skipDebugButton and disablePTCSButton are serialized fields on LanguageSelect — debug-only
    // entries we don't want in the language list.
    private static HashSet<Button> CollectDebugButtons(LanguageSelect ls)
    {
        var set = new HashSet<Button>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var t = typeof(LanguageSelect);
        if (t.GetField("skipDebugButton", flags)?.GetValue(ls) is Button a) set.Add(a);
        if (t.GetField("disablePTCSButton", flags)?.GetValue(ls) is Button b) set.Add(b);
        return set;
    }

    private static string? ReadTitleText(LanguageSelect ls)
    {
        var field = typeof(LanguageSelect).GetField("selectLanguageTitle", BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(ls) as TextMeshProUGUI)?.text;
    }

    // Top-to-bottom, then left-to-right. Same formula the other screen handlers use so the
    // 2-column grid sorts row-major (English, Español, Italiano, … ) — KeyboardNavigator's
    // grid math assumes that ordering.
    private static float ReadingOrder(UnityEngine.Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
