using System.Collections.Generic;
using System.Linq;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The in-battle settings / "pause" menu — the panel the gear icon (top-right of the board) opens:
// music & SFX volume sliders, resolution dropdown, fullscreen + card-hover toggles, and the
// CONCEDE button + back. Driver: the game's own `BattleSettings` MonoBehaviour (Show/Hide). The gear
// click is wired entirely in the prefab (no code path), so we find the object and call Show() ourselves.
//
// Opened from BattleScreen on Escape while in the field with no selection overlay up (those overlays
// push their own handler over BattleScreen, so Escape only reaches here when the board is clear).
// We present its real Selectables for keyboard nav; Enter activates buttons/toggles, Left/Right adjust
// the focused slider or dropdown, Escape/back closes (BattleSettings.Hide).
//
// Concede routes through the game's own confirmation (MatchManager.Confirm.ConfirmMatchAction → a
// ModalElementContainer with "I give up" / "never mind"), which the mod's ModalContainerScreen already
// speaks and navigates — so we don't special-case it. During the FTUE tutorial the game hides the
// concede button (DefaultBattleSettingsController), so it simply won't appear in the list there.
internal sealed class BattleSettingsScreen : IScreenHandler
{
    private static BattleSettingsScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private BattleSettings? _settings;
    private bool _active;

    public string Name => "Battle menu";

    public BattleSettingsScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false };
    }

    // No Harmony hook — the gear button is prefab-wired with no code path, so we open it ourselves.
    public static void Install(ScreenRouter router)
    {
        new BattleSettingsScreen(router);
        Log.Info("BattleSettingsScreen installed");
    }

    // Called by BattleScreen on Escape. Returns false (and stays silent-ish) if the menu can't be found.
    public static bool TryOpen() => _instance != null && _instance.OpenInternal();

    private bool OpenInternal()
    {
        var settings = FindSettings();
        if (settings == null) { _router.Speech.Say("Battle menu unavailable", interrupt: true); return false; }
        _settings = settings;
        try { settings.Show(); } catch (System.Exception ex) { Log.Error($"BattleSettings.Show threw: {ex.Message}"); }

        if (!_active) { _active = true; _router.Activate(this); }
        else Rebuild(announce: true);
        return true;
    }

    private static BattleSettings? FindSettings()
    {
        // Include inactive: the panel is disabled while closed.
        var all = UnityEngine.Object.FindObjectsByType<BattleSettings>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        return all.Length > 0 ? all[0] : null;
    }

    public void OnEnter() => Rebuild(announce: true);

    public void OnResume()
    {
        // A child (e.g. the concede confirmation modal) popped. If the panel is still open, re-present
        // it; if it was closed (concede confirmed/cancelled both hide it), let Tick pop us next frame.
        if (_settings == null || !_settings.gameObject.activeInHierarchy) return;
        Rebuild(announce: true);
    }

    public void OnExit() { /* keep _settings ref alive for a possible OnResume */ }

    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() =>
        _settings == null ? "BattleMenu|dead" : $"BattleMenu|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_settings == null || !_settings.gameObject.activeInHierarchy) { Close(); return; }
        if (!Application.isFocused) return;

        // Left/Right adjust the focused slider or dropdown in place (nav itself is vertical-only).
        bool left = Input.GetKeyDown(KeyCode.LeftArrow);
        bool right = Input.GetKeyDown(KeyCode.RightArrow);
        if (left || right)
        {
            var cur = _nav.Current;
            if (cur is Slider sl && sl.interactable)
            {
                float step = (sl.maxValue - sl.minValue) * 0.1f;
                if (step <= 0f) step = 0.1f;
                sl.value = Mathf.Clamp(sl.value + (right ? step : -step), sl.minValue, sl.maxValue);
                _nav.RefocusCurrent(announce: true);
                return;
            }
            if (cur is TMP_Dropdown dd && dd.interactable && dd.options != null && dd.options.Count > 0)
            {
                dd.value = (dd.value + (right ? 1 : -1) + dd.options.Count) % dd.options.Count;
                _nav.RefocusCurrent(announce: true);
                return;
            }
        }

        _nav.Tick();
    }

    private void Rebuild(bool announce)
    {
        if (_settings == null) return;

        var items = _settings.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s != null && s.gameObject.activeInHierarchy && !(s is Scrollbar))
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();

        _nav.SetItems(items, LabelFor);
        _nav.AllowVertical = true;            // a settings panel is a vertical list…
        _nav.AllowHorizontal = false;         // …and we own Left/Right for slider / dropdown values
        _nav.OnBack = Close;                  // Escape / Backspace closes the menu

        if (!announce) return;
        _router.SetCurrentTitle(this, Name);
        _router.Speech.Say(Name, interrupt: true);
        if (items.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        else _router.Speech.Say("No options", interrupt: false);
        Log.Info($"BattleSettings: {items.Count} items — {string.Join(", ", items.Take(12).Select(s => s.gameObject.name))}");
    }

    private void Close()
    {
        if (!_active) return;
        _active = false;
        try { _settings?.Hide(); } catch (System.Exception ex) { Log.Error($"BattleSettings.Hide threw: {ex.Message}"); }
        _settings = null;
        _router.Deactivate(this);
    }

    // Slider / dropdown read their live value; everything else uses the shared default label
    // (which already appends "checked/unchecked" for toggles and "unavailable" when disabled).
    private static string LabelFor(Selectable s)
    {
        if (s is Slider sl)
        {
            int pct = Mathf.RoundToInt(Mathf.InverseLerp(sl.minValue, sl.maxValue, sl.value) * 100f);
            return $"{KeyboardNavigator.DefaultLabel(s)}, {pct} percent";
        }
        if (s is TMP_Dropdown dd)
        {
            string v = dd.options != null && dd.value >= 0 && dd.value < dd.options.Count ? dd.options[dd.value].text : "";
            return string.IsNullOrEmpty(v) ? KeyboardNavigator.DefaultLabel(s) : $"{KeyboardNavigator.DefaultLabel(s)}, {v}";
        }
        return KeyboardNavigator.DefaultLabel(s);
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
