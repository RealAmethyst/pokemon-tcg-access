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

// The HUB (front-of-house) Settings panel — opened from the overflow "⋮" menu's SETTINGS entry.
//
// Hookpoint: HUBOverflowMenuController.OnClickSettings(OverlayManager) — the exact native event for
// "the user opened HUB settings". It opens _newSettingsController (an OverlayScreenGroup) via
// OverlayManager.OpenOverlay; our postfix reads that same field and presents it.
//
// Unlike the in-battle gear menu (a flat panel handled by BattleSettingsScreen), this panel is
// VISUALLY SECTIONED — MIGRATE / SOUND / VIDEO OPTIONS / LANGUAGE — so we match the visual with the
// section-stack model used by HOME / Profile / Shop: each section is its own group with a spoken
// name, Tab cycles sections, and Up/Down at a group edge escapes into the adjacent section. Rather
// than blindly sweeping every Selectable (which read in scrambled order, surfaced the invisible
// close-blocker, and buried the resolution dropdown), we pull the real controls by reference off the
// HUBOverflowSettingsController and the LanguageSelectionButtons, so the order and grouping are
// deterministic and the only items present are real ones.
//
// All widgets keep their own prefab-wired onValueChanged / onClick — we set values / invoke clicks
// the same way a mouse would; the game applies everything. Resolution is the one exception worth
// care: the game gates a resolution change behind a confirmation modal, so Left/Right only PREVIEWS
// the dropdown (no apply) and Enter commits — otherwise every arrow press would flip the screen.
internal sealed class HUBSettingsScreen : IScreenHandler
{
    private static HUBSettingsScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup = -1;
    private OverlayManager? _overlayManager;
    private OverlayScreenGroup? _group;
    private bool _active;

    // Control references (for reference-based labelling — the GameObject names are useless:
    // "MusicSlider", "Dropdown", "en"/"es", etc.).
    private Button? _back, _migrate;
    private Slider? _music, _sfx;
    private TMP_Dropdown? _res;
    private Toggle? _windowed, _hover, _reduced;

    // Resolution preview state: the dropdown's value is shown without applying until Enter, so the
    // confirmation modal doesn't fire on every arrow press (and the screen doesn't flip per step).
    private int _resCommitted, _resPreview;
    private Selectable? _lastSel;

    public string Name => "Settings";

    public HUBSettingsScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBSettingsScreen(router);

        var click = AccessTools.Method(typeof(HUBOverflowMenuController), "OnClickSettings");
        if (click == null)
        {
            Log.Error("HUBSettingsScreen: could not resolve HUBOverflowMenuController.OnClickSettings");
            return;
        }
        harmony.Patch(click, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBSettingsScreen), nameof(OnClickSettings_Postfix))));
        Log.Info("HUBSettingsScreen installed");
    }

    private static void OnClickSettings_Postfix(HUBOverflowMenuController __instance, OverlayManager overlayManager)
    {
        try
        {
            if (_instance == null || __instance == null) return;

            bool legacy = false;
            try { legacy = HUBOverflowMenuController.ShowLegacySettings; } catch { }
            var group = ReadField<OverlayScreenGroup>(__instance, legacy ? "_oldSettingsController" : "_newSettingsController")
                        ?? ReadField<OverlayScreenGroup>(__instance, "_newSettingsController");
            if (group == null) { Log.Warn("HUBSettingsScreen: settings overlay field was null"); return; }

            _instance._overlayManager = overlayManager ?? ReadField<OverlayManager>(__instance, "overlayManager");
            _instance._group = group;

            if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
            else _instance.Rebuild(announce: true);
        }
        catch (System.Exception ex) { Log.Error($"HUBSettingsScreen.OnClickSettings_Postfix threw: {ex.Message}"); }
    }

    public void OnEnter() => Rebuild(announce: true);

    public void OnResume()
    {
        // A child overlay (e.g. the resolution / language confirmation) closed. If we're still open,
        // re-present; otherwise Tick pops us next frame.
        if (!IsStillOpen()) return;
        Rebuild(announce: true);
    }

    public void OnExit() { }

    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature()
    {
        if (!IsStillOpen()) return "Settings|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"Settings|{_activeGroup}|{idx}";
    }

    public void Tick()
    {
        if (!IsStillOpen()) { Close(); return; }
        if (!Application.isFocused) return;
        if (_groups.Count == 0) return;

        var nav = _groups[_activeGroup];
        var cur = nav.Current;

        // Restore an un-applied resolution preview when focus leaves the dropdown.
        if (_lastSel == (Selectable?)_res && cur != (Selectable?)_res && _res != null && _resPreview != _resCommitted)
        {
            _res.SetValueWithoutNotify(_resCommitted);
            _res.RefreshShownValue();
            _resPreview = _resCommitted;
        }
        // Snapshot the committed value when focus arrives on the dropdown.
        if (cur == (Selectable?)_res && _lastSel != (Selectable?)_res && _res != null)
            _resCommitted = _resPreview = _res.value;
        _lastSel = cur;

        // Left/Right: adjust the focused slider, or preview the resolution dropdown, in place.
        bool left = Input.GetKeyDown(KeyCode.LeftArrow);
        bool right = Input.GetKeyDown(KeyCode.RightArrow);
        if (left || right)
        {
            if (cur is Slider sl && sl.interactable)
            {
                float step = (sl.maxValue - sl.minValue) * 0.1f;
                if (step <= 0f) step = 0.1f;
                sl.value = Mathf.Clamp(sl.value + (right ? step : -step), sl.minValue, sl.maxValue);
                nav.RefocusCurrent(announce: true);
                return;
            }
            if (cur == (Selectable?)_res && _res != null && _res.options != null && _res.options.Count > 0)
            {
                _resPreview = (_resPreview + (right ? 1 : -1) + _res.options.Count) % _res.options.Count;
                _res.SetValueWithoutNotify(_resPreview);
                _res.RefreshShownValue();
                _router.Speech.Say(LabelFor(_res), interrupt: true);
                return;
            }
        }

        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        if (enter && cur == (Selectable?)_res && _res != null)
        {
            // Commit the previewed resolution — fires the game's onValueChanged → its confirm modal
            // (handled by ModalContainerScreen). No-op if nothing changed.
            if (_resPreview != _resCommitted)
            {
                _res.SetValueWithoutNotify(_resCommitted);
                _res.value = _resPreview;
                _resCommitted = _resPreview;
            }
            return;
        }

        // Tab cycles sections.
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: Up at a group's top row jumps to the section above (its bottom row); Down
        // at the bottom row jumps to the section below (its top row), preserving column.
        if (Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        nav.Tick();
    }

    private void Rebuild(bool announce)
    {
        if (_group == null) return;

        // Force a layout pass before reading anything positional — the overlay opens with an
        // animation, so on the first frame the RectTransforms are still at origin; without this the
        // reading-order sort scrambles and column detection (the language grid) is wrong.
        if (_group.transform is RectTransform rt)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        _groups.Clear();
        _back = _migrate = null;
        _music = _sfx = null;
        _res = null;
        _windowed = _hover = _reduced = null;

        var ctrl = _group.GetComponentInChildren<HUBOverflowSettingsController>(includeInactive: true);

        // Collect sections, then order them by their real on-screen position (topmost item first) so
        // the section sequence matches the visual no matter how the prefab hierarchy is ordered —
        // this is what fixes the earlier scrambled reading (resolution buried after the languages).
        // Section + row names use the game's own loc strings (so they read exactly like the visible
        // headers, and localize), with English fallbacks.
        var sections = new List<(string? name, List<Selectable> items, bool vertical)>();

        // Header back arrow (top-left). The X close and the invisible CloseTransparent blocker are
        // deliberately NOT listed — Escape and this Back both close, so one affordance is enough.
        _back = _group.GetComponentsInChildren<Button>(includeInactive: false)
            .FirstOrDefault(b => b.gameObject.name.IndexOf("Back", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (_back != null) sections.Add((null, new List<Selectable> { _back }, false));

        if (ctrl != null)
        {
            // Account — migrate (only when the migrate group is actually shown).
            _migrate = ReadField<Button>(ctrl, "_migrateButton");
            var migrateGroup = ReadField<GameObject>(ctrl, "_migrateGroup");
            if (Active(_migrate) && (migrateGroup == null || migrateGroup.activeInHierarchy))
                sections.Add((LocOr("startup_tcgomigrate_title", "Account"), new List<Selectable> { _migrate! }, false));

            // Sound — music + SFX sliders.
            _music = ReadField<Slider>(ctrl, "_musicSlider");
            _sfx = ReadField<Slider>(ctrl, "_sfxSlider");
            var sound = new List<Selectable>();
            if (Active(_music)) sound.Add(_music!);
            if (Active(_sfx)) sound.Add(_sfx!);
            if (sound.Count > 0) sections.Add((LocOr("settings_sound", "Sound"), sound, true));

            // Video options — resolution dropdown + windowed toggle.
            _res = ReadField<TMP_Dropdown>(ctrl, "_resolutionsSelector");
            _windowed = ReadField<Toggle>(ctrl, "_fullScreenToggle");
            var video = new List<Selectable>();
            if (Active(_res)) video.Add(_res!);
            if (Active(_windowed)) video.Add(_windowed!);
            if (video.Count > 0) sections.Add((LocOr("overflow_video_options", "Video options"), video, true));

            // Board display options — card-hover + reduced-board-FX toggles (each only when shown).
            // (These aren't in the visible top of the panel; grouped together and position-sorted so
            // they land wherever they actually sit. Confirm the spoken section name in testing.)
            _hover = ReadField<Toggle>(ctrl, "_cardHoverEnabledToggle");
            _reduced = ReadField<Toggle>(ctrl, "_useReducedBoardFXToggle");
            var board = new List<Selectable>();
            if (Active(_hover)) board.Add(_hover!);
            if (Active(_reduced)) board.Add(_reduced!);
            if (board.Count > 0) sections.Add((LocOr("settings_battle_options", "Game options"), board, true));
        }

        // Language — every LanguageSelectionButton's Button, in reading order (a grid).
        var langButtons = _group.GetComponentsInChildren<LanguageSelectionButton>(includeInactive: false)
            .Select(lb => (Selectable?)(ReadField<Button>(lb, "_myButton") ?? lb.GetComponentInChildren<Button>(true)))
            .Where(s => s != null && s!.isActiveAndEnabled)
            .Select(s => s!)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (langButtons.Count > 0)
            sections.Add((LocOr("settings_language", "Language"), langButtons, false));

        // Order sections by the topmost item in each, then build the nav groups.
        sections.Sort((a, b) => SectionKey(a.items).CompareTo(SectionKey(b.items)));
        int backIndex = -1;
        foreach (var sec in sections)
        {
            if (sec.name == null && _back != null && sec.items.Count == 1 && sec.items[0] == (Selectable)_back)
                backIndex = _groups.Count;
            AddGroup(sec.items, sec.name, sec.vertical);
        }

        // Default focus: the first content section (skip the lone Back button at the top).
        _activeGroup = (backIndex == 0 && _groups.Count > 1) ? 1 : 0;

        if (!announce || _groups.Count == 0) return;
        _router.SetCurrentTitle(this, Name);
        _router.Speech.Say(Name, interrupt: true);
        _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"Settings: {_groups.Count} sections — " +
                 string.Join(" | ", _groups.Select(g => $"{g.GroupName ?? "Back"}({g.Count})")));
    }

    // Sort key for a section = the reading order of its topmost-leftmost item.
    private static float SectionKey(List<Selectable> items)
    {
        float min = float.MaxValue;
        foreach (var s in items) if (s != null) { var k = ReadingOrder(s.transform); if (k < min) min = k; }
        return min;
    }

    private void AddGroup(List<Selectable> items, string? groupName, bool vertical = false)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = groupName };
        g.SetItems(items, LabelFor);
        if (vertical)
        {
            // Sliders / dropdown / toggles are a vertical list; we own Left/Right for their values.
            g.AllowVertical = true;
            g.AllowHorizontal = false;
        }
        g.OnBack = Close;
        _groups.Add(g);
    }

    private void Close()
    {
        if (!_active) return;
        _active = false;
        try
        {
            if (_overlayManager != null && _group != null && _overlayManager.IsOverlayOpen(_group))
                _overlayManager.CloseOverlay(_group);
        }
        catch (System.Exception ex) { Log.Error($"HUBSettingsScreen.Close threw: {ex.Message}"); }
        _group = null;
        _router.Deactivate(this);
    }

    private bool IsStillOpen()
    {
        if (_group == null) return false;
        try { if (_overlayManager != null) return _overlayManager.IsOverlayOpen(_group); } catch { }
        return _group != null && _group.gameObject.activeInHierarchy;
    }

    // Reference-based labels — the GameObject names ("MusicSlider", "Dropdown", "en") aren't
    // speakable, and the visible row labels live on sibling LocTexts, so we name the known controls
    // directly and read their live value. (Section/row English is mod scaffolding pending the
    // broader mod-speech localization pass; the language names themselves come from the game.)
    private string LabelFor(Selectable s)
    {
        if (s == (Selectable?)_back) return LocOr("global_back", "Back");
        if (s == (Selectable?)_migrate)
        {
            // The button's own visible (localized) text — "MIGRATE" — is the truest label.
            var tmp = _migrate != null ? _migrate.GetComponentInChildren<TMP_Text>(includeInactive: false) : null;
            return tmp != null && !string.IsNullOrWhiteSpace(tmp.text) ? tmp.text : "Migrate account";
        }
        if (s == (Selectable?)_music) return $"{LocOr("settings_music", "Music")}, {Pct(_music!)} percent";
        if (s == (Selectable?)_sfx) return $"{LocOr("settings_sfx", "Sound effects")}, {Pct(_sfx!)} percent";
        if (s == (Selectable?)_res && _res != null)
        {
            var label = LocOr("overflow_resolution", "Screen resolution");
            string v = _res.options != null && _res.value >= 0 && _res.value < _res.options.Count ? _res.options[_res.value].text : "";
            return string.IsNullOrEmpty(v) ? label : $"{label}, {v}";
        }
        if (s == (Selectable?)_windowed) return $"{LocOr("overflow_windowed", "Windowed")}, {State(_windowed!)}";
        if (s == (Selectable?)_hover) return $"{LocOr("settings_hover_over", "Card hover")}, {State(_hover!)}";
        if (s == (Selectable?)_reduced) return $"{LocOr("settings_reducefx", "Reduced board effects")}, {State(_reduced!)}";

        var lb = s.GetComponent<LanguageSelectionButton>();
        if (lb != null)
        {
            string name = Loc(lb.localeLabel);
            if (string.IsNullOrWhiteSpace(name) || name == lb.localeLabel)
            {
                var tmp = s.GetComponentInChildren<TMP_Text>(includeInactive: false);
                name = tmp != null && !string.IsNullOrWhiteSpace(tmp.text) ? tmp.text : lb.localeTag;
            }
            bool current = false;
            try { current = lb.localeTag == LocalizationManager.Instance?.GetCurrentLanguage(); } catch { }
            return current ? $"{name}, current" : name;
        }

        return KeyboardNavigator.DefaultLabel(s);
    }

    private static int Pct(Slider s) => Mathf.RoundToInt(Mathf.InverseLerp(s.minValue, s.maxValue, s.value) * 100f);
    private static string State(Toggle t) => t.isOn ? "checked" : "unchecked";
    private static bool Active(Selectable? s) => s != null && s.isActiveAndEnabled;

    private static string Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        try { var v = LocalizationManager.Instance?.GetString(id); if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v!; } catch { }
        return id!;
    }

    // Game's localized string for the id, or the English fallback if it's missing/unresolved.
    private static string LocOr(string id, string fallback)
    {
        var v = Loc(id);
        return (string.IsNullOrEmpty(v) || v == id) ? fallback : v;
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
