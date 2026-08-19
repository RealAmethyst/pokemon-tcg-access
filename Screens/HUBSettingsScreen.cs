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

// The HUB settings overlay. The current client can show either the legacy flat panel or a tabbed
// Accessibility / General layout. All discovery is scoped to the hierarchy that is live now: the
// inactive legacy controller is never used as a source for the tabbed General panel.
internal sealed class HUBSettingsScreen : IScreenHandler
{
    private static HUBSettingsScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private readonly Dictionary<Toggle, PageDragTab> _tabs = new();
    private readonly Dictionary<HUBTabbedSettingsController.SettingsType, PageDragTab> _typedTabs = new();

    private int _activeGroup = -1;
    private int _tabGroupIndex = -1;
    private OverlayManager? _overlayManager;
    private OverlayScreenGroup? _group;
    private bool _active;

    // The live hierarchy identity. Tick compares this before and after input so a tab change is
    // rebuilt synchronously, while the controller's initial deferred panel activation is handled
    // by _pendingContent below.
    private HUBTabbedSettingsController? _tabbedController;
    private HUBTabbedSettingsController.SettingsType _settingsType = HUBTabbedSettingsController.SettingsType.None;
    private GameObject? _activePanel;
    private HUBOverflowSettingsController? _generalController;
    private MonoBehaviour? _accessibilityController;

    // General / legacy controls.
    private Button? _back, _migrate;
    private Slider? _music, _sfx;
    private TMP_Dropdown? _res;
    private Toggle? _windowed, _hover, _reduced;

    // Accessibility controls. Selector is not a Selectable, so one of its real arrow Buttons is
    // the focus proxy; Left / Right still invoke the corresponding real arrow Button.
    private Toggle? _boardAnims, _frontEndVfx, _battleVfx;
    private Selector? _foilSelector;
    private Button? _foilLeft, _foilRight, _foilFocus;
    private LocText? _boardDescription, _boardValueLabel;
    private LocText? _foilDescription, _foilValueLabel;
    private LocText? _frontEndDescription, _frontEndValueLabel;
    private LocText? _battleDescription, _battleValueLabel;

    // Resolution preview state: arrows preview without firing the confirmation modal; Enter commits.
    private int _resCommitted, _resPreview;
    private Selectable? _lastSel;

    // HUBTabbedSettingsController.Start deliberately has a no-live-panel interval. Rebuild from the
    // live hierarchy until panel controls exist, bounded by the overlay's own settled-state signal.
    private bool _pendingContent;

    public string Name => Loc("overflow_settings");

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

            var manager = overlayManager ?? ReadField<OverlayManager>(__instance, "overlayManager");
            var oldGroup = ReadField<OverlayScreenGroup>(__instance, "_oldSettingsController");
            var newGroup = ReadField<OverlayScreenGroup>(__instance, "_newSettingsController");
            var group = ResolveOpenGroup(manager, oldGroup, newGroup);
            if (group == null)
            {
                Log.Warn("HUBSettingsScreen: no live settings overlay was found");
                return;
            }

            _instance._overlayManager = manager;
            _instance._group = group;

            if (!_instance._active)
            {
                _instance._active = true;
                _instance._router.Activate(_instance);
            }
            else
            {
                _instance.Rebuild(announce: true);
            }
        }
        catch (System.Exception ex)
        {
            // A Harmony postfix must never unwind into the game.
            Log.Error($"HUBSettingsScreen.OnClickSettings_Postfix threw: {ex.Message}");
        }
    }

    private static OverlayScreenGroup? ResolveOpenGroup(
        OverlayManager? manager, OverlayScreenGroup? oldGroup, OverlayScreenGroup? newGroup)
    {
        if (manager != null)
        {
            try
            {
                if (oldGroup != null && manager.IsOverlayOpen(oldGroup)) return oldGroup;
                if (newGroup != null && manager.IsOverlayOpen(newGroup)) return newGroup;
            }
            catch { }
        }

        if (oldGroup != null && oldGroup.gameObject.activeInHierarchy) return oldGroup;
        if (newGroup != null && newGroup.gameObject.activeInHierarchy) return newGroup;
        return newGroup ?? oldGroup;
    }

    public void OnEnter() => Rebuild(announce: true);

    public void OnResume()
    {
        if (IsStillOpen()) Rebuild(announce: true);
    }

    public void OnExit() { }

    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature()
    {
        if (!IsStillOpen()) return "Settings|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        int panelId = _activePanel != null ? _activePanel.GetInstanceID() : 0;
        return $"Settings|{_settingsType}|{panelId}|{_activeGroup}|{idx}";
    }

    public void Tick()
    {
        if (!IsStillOpen()) { Close(); return; }
        if (!Application.isFocused) return;

        // Back is handled HERE, before any content check, so the panel is always closable. Every
        // path below can legitimately return early (content not up yet, live hierarchy changed, no
        // groups built) and each of those used to swallow Escape, leaving the player sealed inside
        // a screen with no way out — the worst failure this screen can have.
        // Escape only: Backspace still goes through the navigator, which suppresses it while a text
        // field has focus so it can delete characters instead of closing the screen.
        if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }

        if (_pendingContent) { Rebuild(announce: true); return; }
        if (LiveHierarchyChanged()) { Rebuild(announce: true); return; }
        if (_groups.Count == 0 || _activeGroup < 0 || _activeGroup >= _groups.Count) return;

        var nav = _groups[_activeGroup];
        var cur = nav.Current;

        // Restore an uncommitted resolution preview when focus leaves the dropdown.
        if (_lastSel == (Selectable?)_res && cur != (Selectable?)_res)
            RestoreResolutionPreview();
        if (cur == (Selectable?)_res && _lastSel != (Selectable?)_res && _res != null)
            _resCommitted = _resPreview = _res.value;
        _lastSel = cur;

        bool left = Input.GetKeyDown(KeyCode.LeftArrow);
        bool right = Input.GetKeyDown(KeyCode.RightArrow);
        if (left || right)
        {
            if (cur is Slider slider && slider.interactable)
            {
                float step = (slider.maxValue - slider.minValue) * 0.1f;
                if (step <= 0f) step = 0.1f;
                slider.value = Mathf.Clamp(slider.value + (right ? step : -step), slider.minValue, slider.maxValue);
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
            // A two-state setting reads like every other value on this screen, so it is driven like
            // one: Right turns it on, Left turns it off. Deterministic rather than a flip, so the
            // same key always produces the same result. Assigning isOn fires the game's own
            // onValueChanged, which is what applies and saves the setting.
            if (IsSettingToggle(cur) && cur is Toggle sideToggle)
            {
                if (sideToggle.isOn != right)
                {
                    sideToggle.isOn = right;
                    nav.RefocusCurrent(announce: true);
                }
                return;
            }
        }

        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        bool space = Input.GetKeyDown(KeyCode.Space);
        if (enter && cur == (Selectable?)_res && _res != null)
        {
            // Restore the committed value without notification, then assign the preview so exactly
            // one onValueChanged fires and the game's confirmation modal opens once.
            if (_resPreview != _resCommitted)
            {
                _res.SetValueWithoutNotify(_resCommitted);
                _res.value = _resPreview;
                _resCommitted = _resPreview;
            }
            return;
        }
        // The foil row is read-only (see FoilAnimationsLabel): swallow Enter / Space so its focus
        // proxy can never fire the arrow Button behind it.
        if ((enter || space) && cur == (Selectable?)_foilFocus)
            return;

        // Enter flips a setting. KeyboardNavigator deliberately reserves checkbox toggling for Space
        // (the desktop convention for a form checkbox), but these rows are settings rather than form
        // fields: Enter is what a player reaches for, and there is no way to hint otherwise without
        // speaking control instructions. Space still works through the navigator as before.
        if (enter && IsSettingToggle(cur) && cur is Toggle enterToggle)
        {
            enterToggle.isOn = !enterToggle.isOn;
            nav.RefocusCurrent(announce: true);
            return;
        }

        // Tab cycles sections. The per-section navigators never consume Tab themselves.
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: Up at a group's top row enters the previous section from below; Down at
        // the bottom row enters the next section from above.
        if (Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            int column = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(column, announce: true, interruptPrior: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            int column = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(column, announce: true, interruptPrior: true);
            return;
        }

        nav.Tick();

        // RadioToggleMode just drove the PageDragTab's real Toggle event. The controller swaps the
        // mapped panel synchronously, so re-present the new live panel immediately.
        if (LiveHierarchyChanged()) Rebuild(announce: true);
    }



    private void Rebuild(bool announce)
    {
        try { RebuildCore(announce); }
        catch (System.Exception ex)
        {
            _pendingContent = false;
            Log.Error($"HUBSettingsScreen.Rebuild threw: {ex.Message}");
        }
    }

    private void RebuildCore(bool announce)
    {
        if (_group == null) return;

        RestoreResolutionPreview();

        if (_group.transform is RectTransform rect)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        ClearDiscoveredState();

        // Fixed stack position 1: Back. Escape is also wired to the same close path.
        _back = _group.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b != null && b.isActiveAndEnabled)
            .FirstOrDefault(b => b.gameObject.name.IndexOf("Back", System.StringComparison.OrdinalIgnoreCase) >= 0);
        if (_back != null)
            AddGroup(new List<Selectable> { _back }, groupName: null);

        // Fixed stack position 2: the tab strip, if the tabbed controller is actually live.
        _tabbedController = FindLiveTabbedController();
        if (_tabbedController != null)
        {
            var tabItems = DiscoverTabs(_tabbedController);
            if (tabItems.Count > 0)
            {
                _tabGroupIndex = _groups.Count;
                // No group name: the HUB top tab strip announces only the tab itself, and these tabs
                // are the same kind of control, so they read the same way (HUBHomeScreen's top strip
                // is added with no groupName either).
                var tabNav = AddGroup(tabItems.Cast<Selectable>().ToList(), groupName: null,
                    horizontal: true, radioMode: true);
                int selected = tabItems.FindIndex(t => t.isOn);
                if (selected >= 0) tabNav.FocusIndex(selected, announce: false, interrupt: false);
            }

            _activePanel = FindActiveTabbedPanel(_tabbedController, out _settingsType);
        }

        int panelStart = _groups.Count;
        if (_tabbedController != null)
        {
            if (_activePanel != null)
            {
                if (_settingsType == HUBTabbedSettingsController.SettingsType.General)
                {
                    _generalController = FindLiveGeneralController(_activePanel);
                    if (_generalController != null) BuildGeneralSections(_generalController, _activePanel);
                }
                else if (_settingsType == HUBTabbedSettingsController.SettingsType.Accessibility)
                {
                    _accessibilityController = FindLiveAccessibilityController(_activePanel);
                    if (_accessibilityController != null) BuildAccessibilitySection(_accessibilityController);
                }
            }
        }
        else
        {
            // Legacy route: only an active-and-enabled controller is eligible. This excludes the
            // dead _origSettings controller when the switcher chose the tabbed layout.
            _generalController = FindLiveGeneralController(_group.gameObject);
            if (_generalController != null)
                BuildGeneralSections(_generalController, FindLegacyPanelRoot(_generalController));
        }

        int panelSectionCount = _groups.Count - panelStart;
        bool contentExpected = _tabbedController != null || _generalController != null;
        _pendingContent = contentExpected && panelSectionCount == 0 && !ShowSettled();
        if (_pendingContent) return;

        // Preserve the old screen's entry behavior: start at the first live settings section, while
        // Back and the tab strip remain one Up-arrow away. If content is unavailable, fall back to
        // the tab strip and then Back rather than leaving the screen unfocused.
        if (panelSectionCount > 0)
            _activeGroup = panelStart;
        else if (_tabGroupIndex >= 0)
            _activeGroup = _tabGroupIndex;
        else
            _activeGroup = _groups.Count > 0 ? 0 : -1;

        if (!announce || _activeGroup < 0) return;
        _router.SetCurrentTitle(this, Name);
        _router.Speech.Say(Name, interrupt: true);
        _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"Settings: {_groups.Count} sections - " +
                 string.Join(" | ", _groups.Select(g => $"{g.GroupName ?? Loc("global_back")}({g.Count})")));
    }

    private void ClearDiscoveredState()
    {
        _groups.Clear();
        _tabs.Clear();
        _typedTabs.Clear();
        _activeGroup = -1;
        _tabGroupIndex = -1;
        _tabbedController = null;
        _settingsType = HUBTabbedSettingsController.SettingsType.None;
        _activePanel = null;
        _generalController = null;
        _accessibilityController = null;

        _back = _migrate = null;
        _music = _sfx = null;
        _res = null;
        _windowed = _hover = _reduced = null;

        _boardAnims = _frontEndVfx = _battleVfx = null;
        _foilSelector = null;
        _foilLeft = _foilRight = _foilFocus = null;
        _boardDescription = _boardValueLabel = null;
        _foilDescription = _foilValueLabel = null;
        _frontEndDescription = _frontEndValueLabel = null;
        _battleDescription = _battleValueLabel = null;
        _lastSel = null;
    }

    private HUBTabbedSettingsController? FindLiveTabbedController()
    {
        if (_group == null) return null;
        return _group.GetComponentsInChildren<HUBTabbedSettingsController>(includeInactive: false)
            .FirstOrDefault(c => c != null && c.isActiveAndEnabled);
    }

    private static HUBOverflowSettingsController? FindLiveGeneralController(GameObject root)
    {
        return root.GetComponentsInChildren<HUBOverflowSettingsController>(includeInactive: false)
            .FirstOrDefault(c => c != null && c.isActiveAndEnabled);
    }

    private static MonoBehaviour? FindLiveAccessibilityController(GameObject root)
    {
        var battle = root.GetComponentsInChildren<BattleAccessibilitySettingsController>(includeInactive: false)
            .FirstOrDefault(c => c != null && c.isActiveAndEnabled);
        if (battle != null) return battle;

        // Installed static Aug-2026 assets contain this controller on the same panel shape. Supporting
        // both costs no guessed fields: both classes expose the exact serialized field names below.
        return root.GetComponentsInChildren<HUBOverflowAccessibilitySettingsController>(includeInactive: false)
            .FirstOrDefault(c => c != null && c.isActiveAndEnabled);
    }

    private List<Toggle> DiscoverTabs(HUBTabbedSettingsController controller)
    {
        var result = new List<Toggle>();
        var options = ReadMap<PageDragTab>(controller, "_options");
        if (options == null) return result;

        foreach (var pair in options)
        {
            var type = pair.Key;
            var tab = pair.Value;
            if (tab == null || !tab.gameObject.activeInHierarchy) continue;
            var toggle = tab.toggle;
            if (toggle == null || !toggle.isActiveAndEnabled) continue;
            _tabs[toggle] = tab;
            _typedTabs[type] = tab;
            result.Add(toggle);
        }
        result.Sort((a, b) => ReadingOrder(a.transform).CompareTo(ReadingOrder(b.transform)));
        return result;
    }

    private static GameObject? FindActiveTabbedPanel(
        HUBTabbedSettingsController controller,
        out HUBTabbedSettingsController.SettingsType type)
    {
        type = ReadValueField(controller, "_currentSettingsType", HUBTabbedSettingsController.SettingsType.None);
        var panels = ReadMap<GameObject>(controller, "_panels");
        if (panels == null) return null;

        foreach (var pair in panels)
            if (pair.Key == type && pair.Value != null && pair.Value.activeInHierarchy)
                return pair.Value;

        // Fail closed on a stale _currentSettingsType: only accept a panel that is actually live.
        foreach (var pair in panels)
            if (pair.Value != null && pair.Value.activeInHierarchy)
            {
                type = pair.Key;
                return pair.Value;
            }
        return null;
    }

    // The tab controller's _options / _panels are TPCI `ReorderableDictionary`s. They implement the
    // NON-generic IDictionary too, but their explicit `IDictionary.GetEnumerator()` returns the
    // inner `Dictionary<K,V>`'s own struct enumerator — whose IEnumerator.Current yields a boxed
    // KeyValuePair, not a DictionaryEntry. So `foreach (DictionaryEntry e in map)` throws
    // InvalidCastException ("Specified cast is not valid") on the first element, every frame.
    // Enumerate the GENERIC interface instead, which is what the object actually yields. This needs
    // no reference to TPCICollections: the generic interface and both type arguments are types we
    // already have.
    private static IEnumerable<KeyValuePair<HUBTabbedSettingsController.SettingsType, TValue>>? ReadMap<TValue>(
        object instance, string name)
        => ReadField<object>(instance, name)
           as IEnumerable<KeyValuePair<HUBTabbedSettingsController.SettingsType, TValue>>;

    // The toggles on this screen that represent a SETTING. Deliberately by reference: the tab strip
    // is made of Toggles too, and it must keep its radio behaviour rather than being flipped.
    private bool IsSettingToggle(Selectable? s)
        => s != null && (ReferenceEquals(s, _boardAnims) || ReferenceEquals(s, _frontEndVfx)
                      || ReferenceEquals(s, _battleVfx)  || ReferenceEquals(s, _hover)
                      || ReferenceEquals(s, _reduced)    || ReferenceEquals(s, _windowed));

    private bool LiveHierarchyChanged()
    {
        if (_group == null) return false;
        try
        {
            var tabbed = FindLiveTabbedController();
            if (tabbed != _tabbedController) return true;
            if (tabbed != null)
            {
                var panel = FindActiveTabbedPanel(tabbed, out var type);
                return panel != _activePanel || type != _settingsType;
            }

            return FindLiveGeneralController(_group.gameObject) != _generalController;
        }
        catch (System.Exception ex)
        {
            Log.Error($"HUBSettingsScreen: live-state check failed: {ex.Message}");
            return false;
        }
    }

    private GameObject FindLegacyPanelRoot(HUBOverflowSettingsController controller)
    {
        if (_group != null)
        {
            foreach (var switcher in _group.GetComponentsInChildren<HUBOverflowSettingsSwitcher>(includeInactive: true))
            {
                var root = ReadField<GameObject>(switcher, "_origSettings");
                if (root != null && root.activeInHierarchy &&
                    (controller.transform == root.transform || controller.transform.IsChildOf(root.transform)))
                    return root;
            }
        }
        return _group != null ? _group.gameObject : controller.gameObject;
    }

    private void BuildGeneralSections(HUBOverflowSettingsController controller, GameObject panelRoot)
    {
        var sections = new List<(string? name, List<Selectable> items)>();

        _migrate = ReadField<Button>(controller, "_migrateButton");
        var migrateGroup = ReadField<GameObject>(controller, "_migrateGroup");
        if (Active(_migrate) && (migrateGroup == null || migrateGroup.activeInHierarchy))
        {
            string account = TextOf(migrateGroup != null
                ? migrateGroup.GetComponentInChildren<LocText>(includeInactive: false)
                : null);
            if (string.IsNullOrWhiteSpace(account)) account = Loc("startup_tcgomigrate_title");
            sections.Add((account, new List<Selectable> { _migrate! }));
        }

        _music = ReadField<Slider>(controller, "_musicSlider");
        _sfx = ReadField<Slider>(controller, "_sfxSlider");
        var sound = LiveItems(_music, _sfx);
        if (sound.Count > 0) sections.Add((Loc("settings_sound"), sound));

        _res = ReadField<TMP_Dropdown>(controller, "_resolutionsSelector");
        _windowed = ReadField<Toggle>(controller, "_fullScreenToggle");
        var video = LiveItems(_res, _windowed);
        if (video.Count > 0) sections.Add((Loc("overflow_video_options"), video));

        _hover = ReadField<Toggle>(controller, "_cardHoverEnabledToggle");
        _reduced = ReadField<Toggle>(controller, "_useReducedBoardFXToggle");
        var battle = LiveItems(_hover, _reduced);
        if (battle.Count > 0) sections.Add((Loc("settings_battle_options"), battle));

        var languages = panelRoot.GetComponentsInChildren<LanguageSelectionButton>(includeInactive: false)
            .Select(lb => (Selectable?)(ReadField<Button>(lb, "_myButton") ?? lb.GetComponentInChildren<Button>(true)))
            .Where(s => s != null && s!.isActiveAndEnabled)
            .Select(s => s!)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        if (languages.Count > 0) sections.Add((Loc("settings_language"), languages));

        sections.Sort((a, b) => SectionKey(a.items).CompareTo(SectionKey(b.items)));
        foreach (var section in sections) AddGroup(section.items, section.name);
    }

    private void BuildAccessibilitySection(MonoBehaviour controller)
    {
        _boardAnims = ReadField<Toggle>(controller, "_gameBoardAnimsVFXToggle");
        _boardDescription = ReadField<LocText>(controller, "_gameBoardAnimsDescription");
        _boardValueLabel = ReadField<LocText>(controller, "_gameBoardAnimsVFXLabel");

        _foilSelector = ReadField<Selector>(controller, "_cardFoilAnimsSelector");
        _foilDescription = ReadField<LocText>(controller, "_cardFoilAnimsDescription");
        if (_foilSelector != null)
        {
            _foilLeft = ReadField<Button>(_foilSelector, "leftButton");
            _foilRight = ReadField<Button>(_foilSelector, "rightButton");
            _foilValueLabel = ReadField<LocText>(_foilSelector, "locText");
            _foilFocus = Active(_foilRight) ? _foilRight : Active(_foilLeft) ? _foilLeft : null;
        }

        _frontEndVfx = ReadField<Toggle>(controller, "_useAmbientFrontEndVFXToggle");
        _frontEndDescription = ReadField<LocText>(controller, "_useAmbientFrontEndVFXDescription");
        _frontEndValueLabel = ReadField<LocText>(controller, "_useAmbientFrontEndVFXLabel");

        _battleVfx = ReadField<Toggle>(controller, "_useCardBattleVFXToggle");
        _battleDescription = ReadField<LocText>(controller, "_useCardBattleVFXDescription");
        _battleValueLabel = ReadField<LocText>(controller, "_useCardBattleVFXLabel");

        var items = LiveItems(_boardAnims, _foilFocus, _frontEndVfx, _battleVfx);
        if (items.Count > 0) AddGroup(items, PanelName(_settingsType));
    }

    private string? PanelName(HUBTabbedSettingsController.SettingsType type)
    {
        if (!_typedTabs.TryGetValue(type, out var tab) || tab == null) return null;
        string id = tab.GetLocTextID();
        string label = Loc(id);
        if (!string.IsNullOrWhiteSpace(label) && label != id) return label;
        return TextOf(ReadField<LocText>(tab, "locTextObj"));
    }

    private static List<Selectable> LiveItems(params Selectable?[] items)
    {
        var result = new List<Selectable>();
        foreach (var item in items) if (Active(item)) result.Add(item!);
        return result;
    }

    private KeyboardNavigator AddGroup(
        List<Selectable> items, string? groupName, bool horizontal = false, bool radioMode = false)
    {
        var nav = new KeyboardNavigator(_router.Speech)
        {
            AllowTab = false,
            AllowVertical = !horizontal,
            AllowHorizontal = horizontal,
            RadioToggleMode = radioMode,
            GroupName = groupName,
            OnBack = Close
        };
        nav.SetItems(items, LabelFor);
        _groups.Add(nav);
        return nav;
    }

    private static float SectionKey(List<Selectable> items)
    {
        float min = float.MaxValue;
        foreach (var item in items)
        {
            if (item == null) continue;
            float key = ReadingOrder(item.transform);
            if (key < min) min = key;
        }
        return min;
    }

    private void RestoreResolutionPreview()
    {
        if (_res == null || _resPreview == _resCommitted) return;
        _res.SetValueWithoutNotify(_resCommitted);
        _res.RefreshShownValue();
        _resPreview = _resCommitted;
    }

    private void Close()
    {
        if (!_active) return;
        _active = false;
        _pendingContent = false;
        RestoreResolutionPreview();
        try
        {
            if (_overlayManager != null && _group != null && _overlayManager.IsOverlayOpen(_group))
                _overlayManager.CloseOverlay(_group);
        }
        catch (System.Exception ex)
        {
            Log.Error($"HUBSettingsScreen.Close threw: {ex.Message}");
        }
        _group = null;
        _router.Deactivate(this);
    }

    private bool IsStillOpen()
    {
        if (_group == null) return false;
        try { if (_overlayManager != null) return _overlayManager.IsOverlayOpen(_group); }
        catch { }
        return _group.gameObject.activeInHierarchy;
    }

    private bool ShowSettled()
    {
        try
        {
            var animated = _group != null ? _group.GetComponent<AnimatedSetActive>() : null;
            return animated == null || animated.IsAnimationVisibleAndIdle();
        }
        catch { return true; }
    }

    private string LabelFor(Selectable selectable)
    {
        if (selectable == (Selectable?)_back) return Loc("global_back");

        if (selectable is Toggle tabToggle && _tabs.TryGetValue(tabToggle, out var tab))
            return TabLabel(tabToggle, tab);

        if (selectable == (Selectable?)_migrate)
        {
            var text = _migrate != null ? _migrate.GetComponentInChildren<TMP_Text>(includeInactive: false) : null;
            return text != null && !string.IsNullOrWhiteSpace(text.text)
                ? text.text
                : Loc("startup_tcgomigrate_title");
        }
        if (selectable == (Selectable?)_music) return $"{Loc("settings_music")}, {Pct(_music!)} percent";
        if (selectable == (Selectable?)_sfx) return $"{Loc("settings_sfx")}, {Pct(_sfx!)} percent";
        if (selectable == (Selectable?)_res && _res != null)
        {
            string value = _res.options != null && _res.value >= 0 && _res.value < _res.options.Count
                ? _res.options[_res.value].text
                : "";
            return JoinLabel(Loc("overflow_resolution"), value);
        }
        if (selectable == (Selectable?)_windowed) return $"{Loc("overflow_windowed")}, {State(_windowed!)}";
        if (selectable == (Selectable?)_hover) return $"{Loc("settings_hover_over")}, {State(_hover!)}";
        if (selectable == (Selectable?)_reduced) return $"{Loc("settings_reducefx")}, {State(_reduced!)}";

        if (selectable == (Selectable?)_boardAnims)
            return GameBoardAnimationsLabel();
        if (selectable == (Selectable?)_foilFocus)
            return FoilAnimationsLabel();
        if (selectable == (Selectable?)_frontEndVfx)
            return FrontEndVfxLabel();
        if (selectable == (Selectable?)_battleVfx)
            return BattleVfxLabel();

        var language = selectable.GetComponent<LanguageSelectionButton>();
        if (language != null)
        {
            string name = Loc(language.localeLabel);
            if (string.IsNullOrWhiteSpace(name) || name == language.localeLabel)
            {
                var text = selectable.GetComponentInChildren<TMP_Text>(includeInactive: false);
                name = text != null && !string.IsNullOrWhiteSpace(text.text) ? text.text : language.localeTag;
            }
            bool current = false;
            try { current = language.localeTag == LocalizationManager.Instance?.GetCurrentLanguage(); }
            catch { }
            return current ? $"{name}, current" : name;
        }

        return KeyboardNavigator.DefaultLabel(selectable);
    }

    // The accessibility controller exposes only each row's DESCRIPTION and its value label — the
    // row's NAME is a separate LocText it never references. Read it off the row itself: start at the
    // description's own container and take the first LocText there that isn't one of the LocTexts we
    // already know about, climbing at most two levels so we can never wander into a neighbouring
    // row. Every known description and value label is excluded, so a stray match can't be another
    // row's text. This is the game's own text; nothing is invented, and an empty result just means
    // the row reads as it did before.
    private string RowName(LocText? description)
    {
        if (description == null) return "";
        try
        {
            var known = new HashSet<LocText>();
            foreach (var t in new[] { _boardDescription, _boardValueLabel, _foilDescription, _foilValueLabel,
                                      _frontEndDescription, _frontEndValueLabel, _battleDescription, _battleValueLabel })
                if (t != null) known.Add(t);

            var scope = description.transform.parent;
            for (int level = 0; level < 2 && scope != null; level++, scope = scope.parent)
            {
                foreach (var candidate in scope.GetComponentsInChildren<LocText>(includeInactive: false))
                {
                    if (candidate == null || known.Contains(candidate)) continue;
                    var text = TextOf(candidate);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch { }
        return "";
    }

    private string GameBoardAnimationsLabel()
    {
        try
        {
            var accessibility = ManagerSingleton<GameManager>.instance?.Accessibility;
            if (accessibility != null)
            {
                bool value = accessibility.GameBoardAnim.Value;
                return JoinLabel(RowName(_boardDescription),
                    JoinLabel(Loc(BoolSettingBase.GetToggleLabel(value)),
                              Loc(GameBoardAnimSetting.GetDescription(value))));
            }
        }
        catch { }
        return JoinLabel(RowName(_boardDescription), JoinLabel(TextOf(_boardValueLabel), TextOf(_boardDescription)));
    }

    private string FrontEndVfxLabel()
    {
        try
        {
            var accessibility = ManagerSingleton<GameManager>.instance?.Accessibility;
            if (accessibility != null)
            {
                bool value = accessibility.UseFrontEndVFX.Value;
                return JoinLabel(RowName(_frontEndDescription),
                    JoinLabel(Loc(BoolSettingBase.GetToggleLabel(value)),
                              Loc(UseFrontEndVFXBoolSetting.GetDescriptionForFrontEndVFX(value))));
            }
        }
        catch { }
        return JoinLabel(RowName(_frontEndDescription), JoinLabel(TextOf(_frontEndValueLabel), TextOf(_frontEndDescription)));
    }

    private string BattleVfxLabel()
    {
        try
        {
            var accessibility = ManagerSingleton<GameManager>.instance?.Accessibility;
            if (accessibility != null)
            {
                bool value = accessibility.UseCardBattleVFX.Value;
                return JoinLabel(RowName(_battleDescription),
                    JoinLabel(Loc(BoolSettingBase.GetToggleLabel(value)),
                              Loc(UseCardBattleVFXBoolSetting.GetDescriptionForCardBattleVFX(value))));
            }
        }
        catch { }
        return JoinLabel(RowName(_battleDescription), JoinLabel(TextOf(_battleValueLabel), TextOf(_battleDescription)));
    }

    // Read-only. Selector.Next / Previous in this client only accept SelectorModel<GameBoardAnimOption>
    // while this row binds SelectorModel<CardFoilAnimOption>, so the arrows either side of it do
    // nothing for anyone — the setting is genuinely unchangeable in the shipped game. We report that
    // rather than repairing it: the mod's job is to give the same access a sighted player has, not
    // more. If a later client fixes the widget, delete this note and the trailing state below.
    private string FoilAnimationsLabel()
    {
        CardFoilAnimOption value;
        try
        {
            if (_foilSelector != null)
                value = _foilSelector.GetValue<CardFoilAnimOption>();
            else
                value = ManagerSingleton<GameManager>.instance.Accessibility.CardFoilAnim.Value;

            return Unchangeable(JoinLabel(RowName(_foilDescription),
                             JoinLabel(Loc(value.ToString()), Loc(CardFoilAnimSetting.GetDescription(value)))));
        }
        catch { }
        return Unchangeable(JoinLabel(RowName(_foilDescription), JoinLabel(FoilValueText(), TextOf(_foilDescription))));
    }

    private static string TabLabel(Toggle toggle, PageDragTab tab)
    {
        string id = tab.GetLocTextID();
        string label = Loc(id);
        if (string.IsNullOrWhiteSpace(label) || label == id)
            label = TextOf(ReadField<LocText>(tab, "locTextObj"));
        if (string.IsNullOrWhiteSpace(label)) label = id;

        string state = toggle.isOn ? "selected" : "not selected";
        return toggle.interactable || toggle.isOn
            ? $"{label}, {state}"
            : $"{label}, {state}, unavailable";
    }

    private string FoilValueText()
    {
        if (_foilSelector != null)
        {
            try
            {
                var value = _foilSelector.GetValue<CardFoilAnimOption>();
                string id = value.ToString();
                string localized = Loc(id);
                if (!string.IsNullOrWhiteSpace(localized) && localized != id) return localized;
            }
            catch { }
        }
        return TextOf(_foilValueLabel);
    }

    private static string JoinLabel(string? first, string? second)
    {
        first = first?.Trim() ?? "";
        second = second?.Trim() ?? "";
        if (first.Length == 0) return second;
        if (second.Length == 0 || string.Equals(first, second, System.StringComparison.OrdinalIgnoreCase)) return first;
        return $"{first}, {second}";
    }

    private static string TextOf(LocText? text)
    {
        if (text == null) return "";
        try
        {
            string current = text.CurrentText;
            if (!string.IsNullOrWhiteSpace(current)) return current;
            return Loc(text.resourceId);
        }
        catch { return ""; }
    }

    private static int Pct(Slider slider)
        => Mathf.RoundToInt(Mathf.InverseLerp(slider.minValue, slider.maxValue, slider.value) * 100f);

    // The game's own words for a two-state setting (common_on / common_off), so these rows read the
    // same as the Accessibility ones and localize with the client. Deliberately not "checked" /
    // "unchecked": these are settings switches rather than form checkboxes, and the game already
    // names their states — no reason to speak mod English over the client's own text.
    private static string Unchangeable(string label)
        => string.IsNullOrWhiteSpace(label) ? label : $"{label}, unavailable";

    private static string State(Toggle toggle) => Loc(BoolSettingBase.GetToggleLabel(toggle.isOn));
    private static bool Active(Selectable? selectable) => selectable != null && selectable.isActiveAndEnabled;

    private static string Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        try
        {
            string? value = LocalizationManager.Instance?.GetString(id);
            if (!string.IsNullOrWhiteSpace(value) && !value!.Contains("(Missing)")) return value!;
        }
        catch { }
        return id!;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }

    private static T ReadValueField<T>(object instance, string name, T fallback) where T : struct
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, flags);
            if (field?.GetValue(instance) is T value) return value;
        }
        return fallback;
    }

    private static float ReadingOrder(Transform transform)
    {
        var position = transform.position;
        return -position.y * 10000 + position.x;
    }
}
