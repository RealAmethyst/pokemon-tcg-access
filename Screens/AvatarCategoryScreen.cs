using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes;
using SharedSDKUtils;
using TPCI.Localization;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Sub-screen for the avatar customization items panel — the list of FACE / HAIR / GLASSES /
// etc. items that appears when the user activates a category from the Profile grid.
//
// Driver: HUBProfileController (TPCI.RainierClient/HUBProfileController.cs). Items live in the
// private List<AvatarCustomizationData> `_currSortedItemData`, populated by RefreshItemMenu
// when a category opens. The visible UI uses an EnhancedScroller (virtualised) plus a phrase
// ScrollRect — neither of which are friendly to traverse via Selectable scans. We bypass that
// view layer and navigate the data list directly; activation goes through
// HUBProfileController.SelectItemByIndex which is what CustomizationItem buttons themselves
// call, so the avatar visually updates exactly as it would from a mouse click.
//
// Activation flow:
//   • User on outer Profile screen (HUBProfileScreen), focuses a grid item (FACE / HAIR / etc.)
//   • Presses Enter — AvatarCustomizationGridItem.OnCustomizationTypeSelected fires
//   • Calls HUBProfileController.SelectCategory(type) — our postfix activates this handler
//   • Profile screen pauses, we own the keyboard
//
// Close path:
//   • User presses Escape / Backspace — we call HUBProfileController.SetState(None) which
//     closes the items panel, then deactivate; outer Profile resumes
//   • OR Profile screen deactivates (user navigated away) — HUBProfileController.ResetFromClose
//     fires (called from InternalDeactivation), which our postfix uses to deactivate cleanly
internal sealed class AvatarCategoryScreen : IScreenHandler
{
    private static AvatarCategoryScreen? _instance;

    private readonly ScreenRouter _router;
    private HUBProfileController? _controller;
    private AvatarCustomizationType _category = AvatarCustomizationType.None;
    private int _index;
    private bool _active;

    // Section pointer. The category panel is a stack of vertically-adjacent groups,
    // bottom-to-top:
    //   Items   — the main item grid (always present)
    //   Slider  — single-value slider (Hair hue today; Glasses sliders pending)
    //   Colors  — color swatch strip (Face skin/eyes; Hair color presets)
    //   Tabs    — sub-tab strip (Intro/Victory for Catchphrase + Anim; Face/Eyes for Face)
    //   Back    — the close-list button at the top-left of every panel (always present)
    // Visual order is encoded in `_sectionStack` from bottom to top — Up moves to the
    // next entry, Down moves to the previous entry, sections absent for the category
    // are skipped. See GetSectionStack.
    private enum Section { Items, Slider, Colors, Tabs, Back }
    private Section _section = Section.Items;
    private int _tabIndex;
    // Two-toggle pair backing whichever sub-tab strip the current category has.
    // Intro/Victory (phrase/anim panels): _tab0=Intro, _tab1=Victory; activation routes
    // through SelectCategory (game wires onValueChanged → SelectCategory). Face/Eyes
    // (face panel's color-strip switcher): _tab0=Face, _tab1=Eyes; activation calls the
    // game's OnFaceSelected/OnEyesSelected listeners which toggle the color group's
    // visibility WITHOUT changing _selectedCategory, so we re-announce here.
    private UnityEngine.UI.Selectable? _tab0Toggle;
    private UnityEngine.UI.Selectable? _tab1Toggle;

    // Color swatches in whichever group is currently visible (SkinToneF/M when Face tab
    // active, EyesF/M when Eyes tab active — both inside AvatarGroupObjectsController's
    // _colorGroupObjectDict). Rediscovered every time the user enters Colors because the
    // active group can change between visits (e.g. user swaps tab, comes back).
    private readonly List<UnityEngine.UI.Toggle> _colorSwatches = new();
    private int _colorIndex;
    // Cached display name for the currently-discovered color group, used as the section
    // header on entry and shown in announcements. Derived from the group's ColorGroupID
    // ("Skin tone" / "Eye color" / "Hair color") — see GetColorSectionName.
    private string _colorSectionName = string.Empty;

    // The customization HUD's close button — the back arrow at the top-left of every
    // category panel. Lives on AvatarGroupObjectsController._closeListButton (game-native);
    // its onClick runs CloseList which saves customizations and tears down the panel.
    private UnityEngine.UI.Button? _backButton;

    // Slider section state. Hair surfaces a single slider (hue). Glasses surfaces 1 (frames
    // colour) or 2 (lens colour + transparency) depending on which Frames/Lenses sub-tab is
    // active. We store parallel name + slider lists plus a focus index, and within the
    // section Up/Down moves between sliders while Left/Right adjusts the focused one's
    // value. The game's wired onValueChanged on each slider applies the corresponding
    // colour/alpha — setting `.value` is enough; no extra apply call needed.
    private readonly List<UnityEngine.UI.Slider> _sliders = new();
    private readonly List<string> _sliderNames = new();
    private int _sliderIndex;

    private static FieldInfo? _currSortedItemDataField;
    private static FieldInfo? _selectedItemIndexField;
    private static FieldInfo? _itemsPerRowField;
    private static FieldInfo? _spawnedPhraseCustomizationsField;
    private static MethodInfo? _getEquippedIndexMethod;
    private static MethodInfo? _isOwnedMethod;

    public string Name => "AvatarCategory";

    public AvatarCategoryScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new AvatarCategoryScreen(router);

        _currSortedItemDataField = AccessTools.Field(typeof(HUBProfileController), "_currSortedItemData");
        _selectedItemIndexField = AccessTools.Field(typeof(HUBProfileController), "_selectedItemIndex");
        _itemsPerRowField = AccessTools.Field(typeof(HUBProfileController), "_itemsPerRow");
        _spawnedPhraseCustomizationsField = AccessTools.Field(typeof(HUBProfileController), "_spawnedPhraseCustomizations");
        _getEquippedIndexMethod = AccessTools.Method(typeof(HUBProfileController), "GetIndexForEquippedCustOfCurrentType");
        _isOwnedMethod = AccessTools.Method(typeof(HUBProfileController), "IsOwned", new[] { typeof(AvatarCustomizationData) });

        var selectCategory = AccessTools.Method(typeof(HUBProfileController), nameof(HUBProfileController.SelectCategory));
        if (selectCategory == null)
        {
            Log.Error("AvatarCategoryScreen: could not resolve HUBProfileController.SelectCategory");
            return;
        }
        harmony.Patch(selectCategory, postfix: new HarmonyMethod(AccessTools.Method(typeof(AvatarCategoryScreen), nameof(SelectCategory_Postfix))));

        var resetFromClose = AccessTools.Method(typeof(HUBProfileController), nameof(HUBProfileController.ResetFromClose));
        if (resetFromClose != null)
            harmony.Patch(resetFromClose, postfix: new HarmonyMethod(AccessTools.Method(typeof(AvatarCategoryScreen), nameof(ResetFromClose_Postfix))));

        Log.Info("AvatarCategoryScreen installed");
    }

    private static void SelectCategory_Postfix(HUBProfileController __instance, AvatarCustomizationType category)
    {
        if (_instance == null) return;
        if (category == AvatarCustomizationType.None)
        {
            // Game closed the panel by setting None — mirror via our deactivation path
            _instance.DeactivateIfActive();
            return;
        }

        _instance._controller = __instance;
        var wasInTabs = _instance._section == Section.Tabs;
        _instance._category = category;
        // When the user activated a sub-tab (still in Tabs section), reset items focus to
        // the top of the new category — landing on the equipped item after a tab switch is
        // confusing because the position isn't predictable. Initial panel-open keeps the
        // equipped-item focus so the user immediately hears their current selection.
        _instance._index = wasInTabs ? 0 : _instance.GetEquippedIndex();

        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
        else
        {
            // Category switched while we were already active (game's own logic, not via our nav).
            // Re-announce the new category from its equipped item.
            _instance.Announce(includeHeader: true);
        }
    }

    private static void ResetFromClose_Postfix(HUBProfileController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        _instance.DeactivateIfActive();
    }

    private void DeactivateIfActive()
    {
        if (!_active) return;
        _active = false;
        _router.Deactivate(this);
        _controller = null;
    }

    public void OnEnter()
    {
        _section = Section.Items;  // always land on items first
        DiscoverSubTabs();
        DiscoverColorSwatches();
        DiscoverSlider();
        DiscoverBackButton();
        Announce(includeHeader: true);
    }
    public void OnResume()
    {
        DiscoverSubTabs();
        DiscoverColorSwatches();
        DiscoverSlider();
        DiscoverBackButton();
        Announce(includeHeader: false);
    }
    public void OnExit() { /* preserve _controller for resume */ }
    public void RespeakAll() => Announce(includeHeader: true);

    // Two flavours of sub-tab strip currently wired:
    //
    //   • Intro/Victory pair — Catchphrase + Anim categories. Each tab switches
    //     _selectedCategory (via SelectCategory) and rebuilds the items panel.
    //   • Face/Eyes pair — Face category only. Tabs don't switch category; they sit
    //     above the color-swatch strip (inside AvatarGroupObjectsController) and toggle
    //     between skin-tone and eyes-color swatches. Items panel stays as Face items.
    //
    // Both pairs go through the same Section.Tabs section abstraction; the only places
    // that branch are: discovery anchor, tab labels, GetActiveTabIndex source-of-truth,
    // and post-activation re-announce.
    private static bool HasIntroVictorySubTabs(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.CatchphraseIntro
     || type == AvatarCustomizationType.CatchphraseVictory
     || type == AvatarCustomizationType.AnimIntro
     || type == AvatarCustomizationType.AnimVictory;

    private static bool HasFaceEyesSubTabs(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.Face;

    private static bool HasFramesLensesSubTabs(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.Glasses;

    private static bool HasSubTabs(AvatarCustomizationType type) =>
        HasIntroVictorySubTabs(type) || HasFaceEyesSubTabs(type) || HasFramesLensesSubTabs(type);

    // Pairs are wired the same way internally (two toggles, index 0 and 1) but speak
    // different labels and resolve their active state from different sources. Activation
    // of the Face/Eyes and Frames/Lenses pairs doesn't route through SelectCategory, so
    // those branches re-announce in ActivateTab; Intro/Victory pairs do route through
    // SelectCategory and rely on SelectCategory_Postfix to re-announce.
    private (string a, string b) GetTabPair()
    {
        if (HasFaceEyesSubTabs(_category)) return ("Face", "Eyes");
        if (HasFramesLensesSubTabs(_category)) return ("Frames", "Lenses");
        return ("Intro", "Victory");
    }

    private string GetTabLabel(int index)
    {
        var (a, b) = GetTabPair();
        return index == 0 ? a : b;
    }

    // Pick the right discovery strategy for the current category's tab pair.
    //
    //   • Intro/Victory: tabs are ColoredEventTriggerToggle instances named "Intro" /
    //     "Victory", sitting as siblings of the items scroller (phrase panel uses
    //     _phraseScrollRect, anim panel uses _avatarEnhancedScroller). Walking up the
    //     parent chain from the scroller and scanning the ancestor's subtree finds the
    //     pair reliably even when multiple instances exist in the scene
    //     (landscape/portrait × phrase/anim = 4 each).
    //   • Face/Eyes: tabs live inside AvatarGroupObjectsController._faceSelectorGameObject
    //     and are plain UnityEngine.UI.Toggle (ImprovedToggle's RequireComponent ensures
    //     each ColoredEventTriggerToggle GameObject also has a Toggle, so the toggle
    //     component path is the same regardless of styling wrapper). Anchor via the
    //     game's own private SerializeField — that's the single unambiguous reference.
    private void DiscoverSubTabs()
    {
        _tab0Toggle = null;
        _tab1Toggle = null;
        if (!HasSubTabs(_category) || _controller == null) return;

        if (HasFaceEyesSubTabs(_category))
        {
            DiscoverPlainTogglePair("_faceSelectorGameObject", "Face", "Eyes");
            return;
        }
        if (HasFramesLensesSubTabs(_category))
        {
            DiscoverPlainTogglePair("_glassesSelectorGameObject", "Frames", "Lenses");
            return;
        }
        DiscoverIntroVictoryTabs();
    }

    // Generic discovery for tab pairs that live as named Toggle children inside a private
    // GameObject SerializeField on AvatarGroupObjectsController. Face/Eyes lives in
    // _faceSelectorGameObject; Frames/Lenses lives in _glassesSelectorGameObject. Both
    // prefabs use plain UnityEngine.UI.Toggle children (an ImprovedToggle wrapper may sit
    // on the same GameObject, but RequireComponent guarantees the Toggle path works).
    private void DiscoverPlainTogglePair(string selectorFieldName, string firstName, string secondName)
    {
        var agoField = AccessTools.Field(typeof(HUBProfileController), "_avatarGroupObjectsController");
        var ago = agoField?.GetValue(_controller) as TPCI.Avatar.Customizations.AvatarGroupObjectsController;
        if (ago == null)
        {
            Log.Info($"AvatarCategory tabs: AvatarGroupObjectsController null for {_category}");
            return;
        }
        var selectorField = AccessTools.Field(typeof(TPCI.Avatar.Customizations.AvatarGroupObjectsController), selectorFieldName);
        var selector = selectorField?.GetValue(ago) as GameObject;
        if (selector == null)
        {
            Log.Info($"AvatarCategory tabs: AvatarGroupObjectsController.{selectorFieldName} is null");
            return;
        }
        var first = FindToggleByName(selector.transform, firstName);
        var second = FindToggleByName(selector.transform, secondName);
        if (first != null && second != null)
        {
            _tab0Toggle = first;
            _tab1Toggle = second;
            Log.Info($"AvatarCategory tabs: discovered {firstName}/{secondName} under '{selector.name}'");
            return;
        }
        Log.Info($"AvatarCategory tabs: {firstName}/{secondName} pair not found under '{selector.name}' (first={first != null} second={second != null})");
    }

    private void DiscoverIntroVictoryTabs()
    {
        Transform? anchor = null;
        if (_category == AvatarCustomizationType.CatchphraseIntro || _category == AvatarCustomizationType.CatchphraseVictory)
        {
            var fld = AccessTools.Field(typeof(HUBProfileController), "_phraseScrollRect");
            anchor = (fld?.GetValue(_controller) as UnityEngine.UI.ScrollRect)?.transform;
        }
        else // AnimIntro / AnimVictory
        {
            var fld = AccessTools.Field(typeof(HUBProfileController), "_avatarEnhancedScroller");
            var scroller = fld?.GetValue(_controller) as EnhancedUI.EnhancedScroller.EnhancedScroller;
            anchor = scroller != null ? scroller.transform : null;
        }
        if (anchor == null)
        {
            Log.Info($"AvatarCategory tabs: no scroll anchor for {_category}");
            return;
        }

        for (var t = anchor.parent; t != null; t = t.parent)
        {
            var toggles = t.GetComponentsInChildren<ColoredEventTriggerToggle>(includeInactive: true);
            ColoredEventTriggerToggle? intro = null;
            ColoredEventTriggerToggle? victory = null;
            foreach (var tog in toggles)
            {
                if (tog == null) continue;
                var n = tog.gameObject.name;
                if (intro == null && string.Equals(n, "Intro", System.StringComparison.OrdinalIgnoreCase)) intro = tog;
                else if (victory == null && string.Equals(n, "Victory", System.StringComparison.OrdinalIgnoreCase)) victory = tog;
            }
            if (intro != null && victory != null)
            {
                _tab0Toggle = intro.MyToggle;
                _tab1Toggle = victory.MyToggle;
                Log.Info($"AvatarCategory tabs: discovered Intro/Victory at ancestor '{t.name}' for {_category}");
                return;
            }
        }
        Log.Info($"AvatarCategory tabs: walked up from anchor, no Intro/Victory pair found for {_category}");
    }

    private static UnityEngine.UI.Toggle? FindToggleByName(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (t == null) continue;
            if (!string.Equals(t.gameObject.name, name, System.StringComparison.OrdinalIgnoreCase)) continue;
            var tog = t.GetComponent<UnityEngine.UI.Toggle>();
            if (tog != null) return tog;
        }
        return null;
    }

    private bool TabsAvailable => _tab0Toggle != null && _tab1Toggle != null;
    private bool ColorsAvailable => _colorSwatches.Count > 0;

    // Categories whose panel includes a color-swatch strip we navigate. Face surfaces
    // skin tone (Face tab active) or eye color (Eyes tab active). Hair surfaces a single
    // hair-color preset list (paired with the hue slider in Section.Slider). Glasses
    // is structurally different (frames/lenses sub-tabs + color & alpha sliders) and is
    // still pending — task #5.
    private static bool HasColorStrip(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.Face || type == AvatarCustomizationType.Hair;

    // Find every Toggle inside the currently-visible color group and cache them as the
    // Colors-section item list. Source of truth is the game's own
    // AvatarGroupObjectsController._colorGroupObjectDict — whichever ColorGroupObjects'
    // _localGameObject has activeInHierarchy=true is the strip the user sees, and its
    // _localScrollRect.content carries the swatch toggles (spawned via
    // CreateColorToggleSwatch + AssignAndRegisterUIToggleElement; both make them children
    // of the scroll rect's content). Toggle ordering follows sibling index, which matches
    // visual left-to-right.
    private void DiscoverColorSwatches()
    {
        _colorSwatches.Clear();
        _colorSectionName = string.Empty;
        if (!HasColorStrip(_category) || _controller == null) return;

        var agoField = AccessTools.Field(typeof(HUBProfileController), "_avatarGroupObjectsController");
        var ago = agoField?.GetValue(_controller) as TPCI.Avatar.Customizations.AvatarGroupObjectsController;
        if (ago == null) return;

        var dictField = AccessTools.Field(typeof(TPCI.Avatar.Customizations.AvatarGroupObjectsController), "_colorGroupObjectDict");
        if (dictField?.GetValue(ago) is not IDictionary dict) return;

        TPCI.Avatar.Customizations.ColorGroupID activeId = TPCI.Avatar.Customizations.ColorGroupID.None;
        UnityEngine.UI.ScrollRect? activeScroll = null;
        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Value is not TPCI.Avatar.Customizations.ColorGroupObjects groupObj) continue;
            if (groupObj._localGameObject == null) continue;
            if (!groupObj._localGameObject.activeInHierarchy) continue;
            activeId = (TPCI.Avatar.Customizations.ColorGroupID)entry.Key;
            activeScroll = groupObj._localScrollRect;
            break;
        }
        if (activeScroll == null || activeScroll.content == null)
        {
            Log.Info($"AvatarCategory colors: no active color group visible for {_category}");
            return;
        }

        // Sibling order across the scroll content matches the visual left-to-right order;
        // GetComponentsInChildren is documented to follow Transform.GetChild order.
        var toggles = activeScroll.content.GetComponentsInChildren<UnityEngine.UI.Toggle>(includeInactive: false);
        foreach (var tog in toggles)
        {
            if (tog != null) _colorSwatches.Add(tog);
        }
        _colorSectionName = GetColorSectionName(activeId);
        Log.Info($"AvatarCategory colors: {activeId} → {_colorSwatches.Count} swatches");
    }

    // Human-readable header for whichever color group is currently visible. Used as the
    // section name announced on entry to Colors, the same way the Items section uses
    // GetItemsSectionName.
    private static string GetColorSectionName(TPCI.Avatar.Customizations.ColorGroupID id) => id switch
    {
        TPCI.Avatar.Customizations.ColorGroupID.SkinToneF => "Skin tone",
        TPCI.Avatar.Customizations.ColorGroupID.SkinToneM => "Skin tone",
        TPCI.Avatar.Customizations.ColorGroupID.EyesF => "Eye color",
        TPCI.Avatar.Customizations.ColorGroupID.EyesM => "Eye color",
        TPCI.Avatar.Customizations.ColorGroupID.Hair => "Hair color",
        _ => "Color",
    };

    // The close-list button often lives on a GameObject that animates between active
    // and inactive (the customization HUD's top area), but the Button reference itself
    // is the same one AvatarGroupObjectsController.Initialize wired CloseList onto.
    // onClick.Invoke routes through the listener list regardless of the GameObject's
    // active state, and the user's audio cue is "Back" being announced — they don't
    // care about a visible focus ring on a possibly-animating button. So a non-null
    // reference is enough to let the section into the nav stack.
    private bool BackAvailable => _backButton != null;

    // Resolve the close-list button from the game's own private SerializeField on
    // AvatarGroupObjectsController. Initialised in the controller's Initialize() path
    // (one of two branches depending on _isLandscape — both end up populating
    // _closeListButton with the customization HUD's Button). Re-discovery on every
    // OnEnter/OnResume keeps the reference fresh across panel close/reopen.
    private void DiscoverBackButton()
    {
        _backButton = null;
        if (_controller == null) return;

        var agoField = AccessTools.Field(typeof(HUBProfileController), "_avatarGroupObjectsController");
        var ago = agoField?.GetValue(_controller) as TPCI.Avatar.Customizations.AvatarGroupObjectsController;
        if (ago == null)
        {
            Log.Info("AvatarCategory back: HUBProfileController._avatarGroupObjectsController is null");
            return;
        }

        var btnField = AccessTools.Field(typeof(TPCI.Avatar.Customizations.AvatarGroupObjectsController), "_closeListButton");
        _backButton = btnField?.GetValue(ago) as UnityEngine.UI.Button;
        if (_backButton == null)
            Log.Info("AvatarCategory back: AvatarGroupObjectsController._closeListButton is null (HUD not yet wired?)");
        // The discovered Button (named 'CloseButton' in landscape, lives under 'TabButtonGroup')
        // is normally inactive — it's a click-handler endpoint wired by Initialize, not a
        // visible focused control. The visible back arrow is rendered by a different GameObject
        // in the customization HUD. UnityEvent.onClick.Invoke routes through regardless of
        // activeInHierarchy, so the click works either way (CloseList fires, panel closes).
    }

    private bool SliderAvailable => _sliders.Count > 0;
    private UnityEngine.UI.Slider? CurrentSlider =>
        _sliderIndex >= 0 && _sliderIndex < _sliders.Count ? _sliders[_sliderIndex] : null;

    // Categories whose panel includes a value slider section. Hair → 1 slider (hue).
    // Glasses → 1 slider (frames color) when Frames tab is active, 2 sliders (lens color
    // + lens transparency) when Lenses tab is active.
    private static bool HasSlider(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.Hair || type == AvatarCustomizationType.Glasses;

    // Read whichever sliders are active for the current category + tab state. Source of
    // truth is the game's own private fields on AvatarGroupObjectsController — they're
    // populated during Initialize and the slider's onValueChanged is already wired to
    // the corresponding apply call, so setting `.value` is enough to update state.
    private void DiscoverSlider()
    {
        _sliders.Clear();
        _sliderNames.Clear();
        if (!HasSlider(_category) || _controller == null) return;

        var agoField = AccessTools.Field(typeof(HUBProfileController), "_avatarGroupObjectsController");
        var ago = agoField?.GetValue(_controller) as TPCI.Avatar.Customizations.AvatarGroupObjectsController;
        if (ago == null) return;

        if (_category == AvatarCustomizationType.Hair)
        {
            AddSliderIfNotNull(ago, "_colorHairSlider", "Hair tint");
        }
        else if (_category == AvatarCustomizationType.Glasses)
        {
            // Frames tab active → frames color slider only. Lenses tab active → lens color
            // and lens transparency. Read which toggle is on (DiscoverSubTabs has already
            // resolved _tab0Toggle/_tab1Toggle for Glasses by this point).
            var lensesActive = _tab1Toggle is UnityEngine.UI.Toggle t1 && t1.isOn;
            if (lensesActive)
            {
                AddSliderIfNotNull(ago, "_lensesColorSlider", "Lens color");
                AddSliderIfNotNull(ago, "_lensesAlphaSlider", "Lens transparency");
            }
            else
            {
                AddSliderIfNotNull(ago, "_framesColorSlider", "Frames color");
            }
        }

        if (_sliders.Count > 0)
            Log.Info($"AvatarCategory slider: {_category} → {_sliders.Count} sliders ({string.Join(", ", _sliderNames)})");
        else
            Log.Info($"AvatarCategory slider: {_category} → no sliders resolved");
    }

    private void AddSliderIfNotNull(TPCI.Avatar.Customizations.AvatarGroupObjectsController ago, string fieldName, string label)
    {
        var fld = AccessTools.Field(typeof(TPCI.Avatar.Customizations.AvatarGroupObjectsController), fieldName);
        if (fld?.GetValue(ago) is UnityEngine.UI.Slider slider)
        {
            _sliders.Add(slider);
            _sliderNames.Add(label);
        }
    }

    // Index of the equipped (isOn=true) swatch in the current strip, or 0 if none. The
    // game initialises one toggle to isOn=true on group load based on the player's
    // currently equipped color (IE_LoadToggleLists' setToggleOn arg), and keeps that
    // single-selection invariant through the ToggleGroup component. Default focus lands
    // on the equipped swatch so the user immediately hears their current pick.
    private int GetEquippedColorIndex()
    {
        for (int i = 0; i < _colorSwatches.Count; i++)
            if (_colorSwatches[i] != null && _colorSwatches[i].isOn) return i;
        return 0;
    }

    public string StateSignature() => $"AvatarCategory|{_category}|{_index}";

    public void Tick()
    {
        if (_controller == null)
        {
            DeactivateIfActive();
            return;
        }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            CloseCategoryPanel();
            return;
        }

        if (_section == Section.Back)
        {
            TickBack();
            return;
        }
        if (_section == Section.Tabs)
        {
            TickTabs();
            return;
        }
        if (_section == Section.Colors)
        {
            TickColors();
            return;
        }
        if (_section == Section.Slider)
        {
            TickSlider();
            return;
        }

        TickItems();
    }

    private void TickItems()
    {
        var items = GetItems();
        if (items == null || items.Count == 0) return;

        // Per-row count detection differs by category. Phrase categories (CatchphraseIntro /
        // Victory) spawn their items into _phraseContent under a LayoutGroup driven by the
        // prefab — observed in landscape to be a vertical list (perRow=1). For non-phrase
        // categories the EnhancedScroller's `_itemsPerRow` gives us the column count.
        var perRow = System.Math.Max(1, IsPhraseCategory(_category) ? DetectPhraseColumns() : GetItemsPerRow());

        // Unified grid-aware moves. The grid helpers degenerate gracefully when perRow=1
        // or when items fit in one row; see their bodies for the exact behaviour. Up at
        // the top row escapes to the tabs section (if the panel actually has tabs above —
        // currently Catchphrases is the wired case). Other arrow keys stop at edges.
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
            MoveHorizontalInGrid(+1, items.Count, perRow);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
            MoveHorizontalInGrid(-1, items.Count, perRow);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
            MoveVerticalInGrid(+1, items.Count, perRow);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            var moved = MoveVerticalInGrid(-1, items.Count, perRow);
            Log.Info($"AvatarCategory items Up: _index={_index} perRow={perRow} moved={moved}");
            if (!moved) EscapeUp(Section.Items);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            ActivateCurrent();
        }
    }

    private void TickTabs()
    {
        // Both supported pairs are always 2 items — _tab0 at index 0, _tab1 at index 1
        // (Intro/Victory for phrase+anim panels; Face/Eyes for the Face panel's color-
        // strip switcher).
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (_tabIndex < 1) { _tabIndex++; AnnounceTab(); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (_tabIndex > 0) { _tabIndex--; AnnounceTab(); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            EscapeDown(Section.Tabs);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            EscapeUp(Section.Tabs);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            ActivateTab();
        }
    }

    private void TickColors()
    {
        if (_colorSwatches.Count == 0)
        {
            // Lost the swatches between Tick frames (group hidden by animation, etc.) —
            // fall back into Items rather than freezing the user in an empty section.
            _section = Section.Items;
            Announce(includeHeader: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (_colorIndex < _colorSwatches.Count - 1) { _colorIndex++; AnnounceColor(includeHeader: false); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (_colorIndex > 0) { _colorIndex--; AnnounceColor(includeHeader: false); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            EscapeDown(Section.Colors);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            EscapeUp(Section.Colors);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            ActivateColor();
        }
    }

    private void EnterTabsSection()
    {
        _section = Section.Tabs;
        _tabIndex = GetActiveTabIndex();
        Log.Info($"AvatarCategory: entering tabs section, tabIndex={_tabIndex}");
        AnnounceTab();
    }

    private void EnterColorsSection()
    {
        _section = Section.Colors;
        _colorIndex = GetEquippedColorIndex();
        Log.Info($"AvatarCategory: entering colors section, group='{_colorSectionName}' colorIndex={_colorIndex}/{_colorSwatches.Count}");
        AnnounceColor(includeHeader: true);
    }

    private void AnnounceColor(bool includeHeader)
    {
        if (_colorSwatches.Count == 0) return;
        if (_colorIndex < 0 || _colorIndex >= _colorSwatches.Count) _colorIndex = 0;
        var swatch = _colorSwatches[_colorIndex];
        if (swatch != null) swatch.Select();
        var label = BuildColorLabel(_colorIndex, _colorSwatches.Count);
        if (includeHeader && !string.IsNullOrEmpty(_colorSectionName))
        {
            _router.Speech.Say(_colorSectionName, interrupt: true);
            _router.Speech.Say(label, interrupt: false);
        }
        else
        {
            _router.Speech.Say(label, interrupt: true);
        }
    }

    // Swatches don't have human-readable names — they're keyed by IDs like
    // "skintone_f_001". Best we can do without a loc table for individual colors is the
    // index. "equipped" status comes from the toggle's own isOn state (game keeps a
    // single-selected invariant via ToggleGroup) so the user always knows which swatch
    // is their current pick.
    private string BuildColorLabel(int index, int total)
    {
        var swatch = _colorSwatches[index];
        var status = swatch != null && swatch.isOn ? "equipped" : "available";
        return $"Color {index + 1}, {status}, {index + 1} of {total}";
    }

    private void ActivateColor()
    {
        if (_colorIndex < 0 || _colorIndex >= _colorSwatches.Count) return;
        var swatch = _colorSwatches[_colorIndex];
        if (swatch == null) return;
        // Setting isOn=true routes through the toggle's onValueChanged → game's
        // OnFaceColorToggle / OnEyesColorToggle / OnHairColorToggle, which apply the
        // color via AvatarManager. The ToggleGroup component makes the previously-
        // selected swatch flip to isOn=false automatically. Re-announce to confirm.
        if (!swatch.isOn) swatch.isOn = true;
        AnnounceColor(includeHeader: false);
    }

    // Visual section stack from bottom to top, with only the sections actually present
    // for the current category. Up = walk forward in this list, Down = walk backward.
    // Empty positions (e.g. Tabs not applicable, Slider not applicable) are skipped.
    //
    // Order rationale: Items is at the bottom of the panel. Slider sits directly above
    // Items because it's the "fine-tune" sub-control of the color picker, which lives
    // above it as Colors. Tabs go above Colors when present (Face's tabs span the panel
    // top), and Back is the top-most close arrow.
    private static readonly Section[] s_orderedSections =
    {
        Section.Items,
        Section.Slider,
        Section.Colors,
        Section.Tabs,
        Section.Back,
    };

    private void EscapeUp(Section from)
    {
        int idx = System.Array.IndexOf(s_orderedSections, from);
        for (int i = idx + 1; i < s_orderedSections.Length; i++)
            if (TryEnterSection(s_orderedSections[i])) return;
    }

    private void EscapeDown(Section from)
    {
        int idx = System.Array.IndexOf(s_orderedSections, from);
        for (int i = idx - 1; i >= 0; i--)
            if (TryEnterSection(s_orderedSections[i])) return;
    }

    private bool TryEnterSection(Section target)
    {
        switch (target)
        {
            case Section.Back:
                if (BackAvailable) { EnterBackSection(); return true; }
                return false;
            case Section.Tabs:
                if (TabsAvailable) { EnterTabsSection(); return true; }
                return false;
            case Section.Colors:
                if (HasColorStrip(_category))
                {
                    DiscoverColorSwatches();
                    if (ColorsAvailable) { EnterColorsSection(); return true; }
                }
                return false;
            case Section.Slider:
                if (HasSlider(_category))
                {
                    DiscoverSlider();
                    if (SliderAvailable) { EnterSliderSection(); return true; }
                }
                return false;
            case Section.Items:
                _section = Section.Items;
                Announce(includeHeader: true);
                return true;
            default:
                return false;
        }
    }

    private void TickBack()
    {
        if (!BackAvailable)
        {
            // Back button got torn down between frames — fall back into the next section.
            EscapeDown(Section.Back);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            EscapeDown(Section.Back);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            ActivateBack();
        }
    }

    private void EnterBackSection()
    {
        _section = Section.Back;
        Log.Info($"AvatarCategory: entering back section");
        AnnounceBack();
    }

    private void AnnounceBack()
    {
        if (_backButton != null) _backButton.Select();
        _router.Speech.Say("Back", interrupt: true);
    }

    private void ActivateBack()
    {
        if (_backButton == null) return;
        // Invoking the close button's onClick routes through AvatarGroupObjectsController.
        // CloseList — which calls SaveHairAndGlassesColorIfChanged, ResetFromClose,
        // ResetProfileItemsForClose, SetState(None), and DeActivateAll in the right order.
        // ResetFromClose triggers our ResetFromClose_Postfix → DeactivateIfActive, so the
        // handler's lifecycle is handled by the game's own close flow.
        _backButton.onClick?.Invoke();
    }

    private void TickSlider()
    {
        if (!SliderAvailable)
        {
            // Sliders torn down (panel state change) — drop back into the next section.
            EscapeDown(Section.Slider);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            AdjustSlider(+1);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            AdjustSlider(-1);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            // Walk up the stacked sliders within the section first; only escape to the
            // section above once we're already at the top slider.
            if (_sliderIndex > 0) { _sliderIndex--; AnnounceSlider(includeHeader: true); }
            else EscapeUp(Section.Slider);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            if (_sliderIndex < _sliders.Count - 1) { _sliderIndex++; AnnounceSlider(includeHeader: true); }
            else EscapeDown(Section.Slider);
        }
        // No Enter activation — sliders commit via Left/Right value changes (the game's
        // onValueChanged listener applies the value on each step).
    }

    private void EnterSliderSection()
    {
        _section = Section.Slider;
        // Land on the first slider in the stack. Most panels only have one anyway; for
        // Glasses Lenses tab (2 sliders) starting at the top is the predictable default.
        _sliderIndex = 0;
        Log.Info($"AvatarCategory: entering slider section, count={_sliders.Count}");
        AnnounceSlider(includeHeader: true);
    }

    private void AnnounceSlider(bool includeHeader)
    {
        var slider = CurrentSlider;
        if (slider == null) return;
        // Focus the slider's underlying Selectable so screen readers / Unity's selection
        // chain stay coherent with our nav model.
        slider.Select();
        var pct = Mathf.RoundToInt(slider.value * 100f);
        var name = _sliderIndex < _sliderNames.Count ? _sliderNames[_sliderIndex] : string.Empty;
        if (includeHeader && !string.IsNullOrEmpty(name))
            _router.Speech.Say($"{name}, {pct} percent", interrupt: true);
        else
            _router.Speech.Say($"{pct} percent", interrupt: true);
    }

    // Step size for arrow-key adjustments. 5% gives 20 discrete positions over the
    // slider's 0..1 range — fine enough for the gradient sliders (smooth color ramps)
    // without forcing the user to hold the key down forever. May want a per-slider
    // override down the line (e.g. lens alpha might benefit from 10% to feel snappier).
    private const float SliderStep = 0.05f;

    private void AdjustSlider(int direction)
    {
        var slider = CurrentSlider;
        if (slider == null) return;
        var newVal = Mathf.Clamp01(slider.value + direction * SliderStep);
        if (Mathf.Approximately(newVal, slider.value)) return;
        // Setting .value fires onValueChanged → game's apply call for that slider:
        //   _colorHairSlider     → OnProcessHairColorSlider     → ApplyHairColor
        //   _framesColorSlider   → OnProcessFrameColorSlider    → ApplyGlassesFrameColor
        //   _lensesColorSlider   → OnProcessLensesColorSlider   → ApplyGlassesLenseColor
        //   _lensesAlphaSlider   → OnProcessLensesAlphaSlider   → ApplyGlassesLenseColor (alpha-only)
        // The avatar updates synchronously.
        slider.value = newVal;
        AnnounceSlider(includeHeader: false);
    }

    // Tab index for whichever sub-tab pair the current category has. For Intro/Victory
    // pairs the source of truth is _selectedCategory (the game's own state). For Face/
    // Eyes and Frames/Lenses the category never changes when the user picks the second
    // tab, so read which toggle is isOn=true.
    private int GetActiveTabIndex()
    {
        if (HasFaceEyesSubTabs(_category) || HasFramesLensesSubTabs(_category))
        {
            if (_tab1Toggle is UnityEngine.UI.Toggle t1 && t1.isOn) return 1;
            return 0;
        }
        return _category switch
        {
            AvatarCustomizationType.CatchphraseVictory => 1,
            AvatarCustomizationType.AnimVictory => 1,
            _ => 0,
        };
    }

    private void AnnounceTab()
    {
        var sel = _tabIndex == 0 ? _tab0Toggle : _tab1Toggle;
        if (sel == null) return;
        sel.Select();
        _router.Speech.Say($"{GetTabLabel(_tabIndex)}, {_tabIndex + 1} of 2", interrupt: true);
    }

    private void ActivateTab()
    {
        var sel = _tabIndex == 0 ? _tab0Toggle : _tab1Toggle;
        if (sel == null) return;
        // For Intro/Victory: setting Toggle.isOn=true routes through the toggle's
        // onValueChanged → game's OnCatchPhraseIntroSelected/OnVersusSelected/etc →
        // HUBProfileController.SelectCategory(CatchphraseIntro|Victory|AnimIntro|AnimVictory).
        // Our SelectCategory_Postfix then re-announces the active tab.
        //
        // For Face/Eyes: setting Toggle.isOn=true routes through OnFaceSelected/
        // OnEyesSelected which only toggles the color group's visibility — no
        // SelectCategory call, so SelectCategory_Postfix won't fire. We re-announce
        // explicitly below to match the Intro/Victory confirmation pattern.
        if (sel is UnityEngine.UI.Toggle t)
        {
            if (!t.isOn) t.isOn = true;
        }
        else if (sel is UnityEngine.UI.Button b)
        {
            b.onClick?.Invoke();
        }
        if (HasFaceEyesSubTabs(_category) || HasFramesLensesSubTabs(_category)) AnnounceTab();
    }

    // Stays inside the current row. For perRow=1 (vertical list) the row contains only one
    // item, so any horizontal move falls outside [rowStart, rowEnd] and is a no-op — which
    // is exactly the desired behaviour for catchphrases. Returns true if focus actually
    // moved so callers can detect "at the edge" and choose to escape to a different section.
    private bool MoveHorizontalInGrid(int direction, int total, int perRow)
    {
        var currentRow = _index / perRow;
        var rowStart = currentRow * perRow;
        var rowEnd = System.Math.Min(rowStart + perRow - 1, total - 1);
        var next = _index + direction;
        if (next < rowStart || next > rowEnd) return false;
        _index = next;
        Announce(includeHeader: false);
        return true;
    }

    // Jumps to the target row, clamping the column to whatever the target row actually has.
    // Returns true if focus moved (same rationale as the horizontal variant).
    private bool MoveVerticalInGrid(int direction, int total, int perRow)
    {
        var currentRow = _index / perRow;
        var currentCol = _index % perRow;
        var targetRow = currentRow + direction;
        if (targetRow < 0) return false;
        var rowStart = targetRow * perRow;
        if (rowStart >= total) return false;
        var rowLastIndex = System.Math.Min(rowStart + perRow - 1, total - 1);
        var targetIndex = System.Math.Min(rowStart + currentCol, rowLastIndex);
        if (targetIndex == _index) return false;
        _index = targetIndex;
        Announce(includeHeader: false);
        return true;
    }

    private int GetItemsPerRow()
    {
        if (_controller == null || _itemsPerRowField == null) return 1;
        try { return System.Math.Max(1, (int)(_itemsPerRowField.GetValue(_controller) ?? 1)); }
        catch { return 1; }
    }

    private static bool IsPhraseCategory(AvatarCustomizationType c)
        => c == AvatarCustomizationType.CatchphraseIntro || c == AvatarCustomizationType.CatchphraseVictory;

    // For CatchphraseIntro / Victory the items are pre-spawned CustomizationItem instances
    // inside _phraseContent, laid out by a LayoutGroup we don't know the shape of in advance
    // (the landscape prefab could be a grid while portrait is a vertical list). We read the
    // spawned items' world Y positions and count how many sit in the first row — same
    // algorithm KeyboardNavigator uses for outer-Profile selectables.
    private int DetectPhraseColumns()
    {
        if (_controller == null || _spawnedPhraseCustomizationsField == null) return 1;
        try
        {
            if (_spawnedPhraseCustomizationsField.GetValue(_controller) is not IList list || list.Count == 0)
                return 1;
            var firstY = float.NaN;
            int columns = 0;
            foreach (var obj in list)
            {
                if (obj is not Component comp || comp == null) continue;
                if (!comp.gameObject.activeInHierarchy) continue;
                var y = comp.transform.position.y;
                if (float.IsNaN(firstY))
                {
                    firstY = y;
                    columns = 1;
                    continue;
                }
                if (System.Math.Abs(y - firstY) <= 1f) columns++;
                else break;
            }
            return System.Math.Max(1, columns);
        }
        catch { return 1; }
    }

    private void ActivateCurrent()
    {
        if (_controller == null) return;
        _controller.SelectItemByIndex(_index);
        // Selection may have changed equipped state — re-announce. The controller has updated
        // its _selectedItemIndex; we follow it so subsequent moves are relative to the new pick.
        if (_selectedItemIndexField?.GetValue(_controller) is int selectedIndex && selectedIndex >= 0)
            _index = selectedIndex;
        Announce(includeHeader: false);
    }

    private void CloseCategoryPanel()
    {
        if (_controller == null) return;
        var controller = _controller;
        // Close the panel FIRST. SetState(None) → SetCurrentAvatarStateAndItemsMenu(Closed)
        // synchronously sets _currentUIState back to Closed and toggles _zoomOutIcon.SetActive
        // off; only the items-menu animator transition is async. Doing this before deactivate
        // means Profile.OnResume's RebuildGroups sees the (mostly) correct outer-profile
        // state instead of the still-open one — without this order swap ZoomOutIcon leaks
        // into the catch-all because it's still active when the rebuild runs.
        controller.SetState(AvatarCustomizationType.None);
        DeactivateIfActive();
    }

    private IList? GetItems()
    {
        if (_controller == null || _currSortedItemDataField == null) return null;
        return _currSortedItemDataField.GetValue(_controller) as IList;
    }

    private int GetEquippedIndex()
    {
        if (_controller == null || _getEquippedIndexMethod == null) return 0;
        try { return (int)(_getEquippedIndexMethod.Invoke(_controller, null) ?? 0); }
        catch { return 0; }
    }

    private bool IsOwned(AvatarCustomizationData data)
    {
        if (_controller == null || _isOwnedMethod == null) return false;
        try { return (bool)(_isOwnedMethod.Invoke(_controller, new object[] { data }) ?? false); }
        catch { return false; }
    }

    private void Announce(bool includeHeader)
    {
        if (_controller == null) return;

        // Tabs / Colors / Back are independent of the items list — handle them first so a
        // momentarily-empty _currSortedItemData (e.g. mid-transition) doesn't suppress the
        // section announcement.
        if (_section == Section.Back)
        {
            AnnounceBack();
            return;
        }
        if (_section == Section.Tabs)
        {
            _tabIndex = GetActiveTabIndex();
            _router.Speech.Say($"{GetTabLabel(_tabIndex)}, {_tabIndex + 1} of 2", interrupt: true);
            return;
        }
        if (_section == Section.Colors)
        {
            AnnounceColor(includeHeader: includeHeader);
            return;
        }
        if (_section == Section.Slider)
        {
            AnnounceSlider(includeHeader: includeHeader);
            return;
        }

        // Items section. Needs the underlying list.
        var items = GetItems();
        if (items == null || items.Count == 0)
        {
            Log.Info($"AvatarCategory: {_category} has 0 items");
            return;
        }
        if (_index < 0 || _index >= items.Count) _index = 0;

        var label = BuildItemLabel(items[_index] as AvatarCustomizationData, _index, items.Count);
        if (string.IsNullOrEmpty(label)) return;

        var sectionName = GetItemsSectionName(_category);
        if (includeHeader)
        {
            Log.Info($"AvatarCategory enter: category={_category} section='{sectionName}' items={items.Count}");
            _router.Speech.Say(sectionName, interrupt: true);
            _router.Speech.Say(label!, interrupt: false);
        }
        else
        {
            _router.Speech.Say(label!, interrupt: true);
        }
    }

    // Section name for the items list, used as the announcement prefix on entry / re-entry.
    // For categories that share a panel with a sub-tab partner (Catchphrase Intro/Victory,
    // Anim Intro/Victory), the tab name IS the section name — the panel-level category
    // ("Catchphrases" / "Poses") is implicit context. For categories without sub-tabs the
    // category name is the section name.
    private static string GetItemsSectionName(AvatarCustomizationType type) => type switch
    {
        AvatarCustomizationType.CatchphraseIntro => "Intro",
        AvatarCustomizationType.CatchphraseVictory => "Victory",
        AvatarCustomizationType.AnimIntro => "Intro",
        AvatarCustomizationType.AnimVictory => "Victory",
        AvatarCustomizationType.Face => "Face",
        AvatarCustomizationType.Eyes => "Eyes",
        AvatarCustomizationType.Hair => "Hair",
        AvatarCustomizationType.Glasses => "Glasses",
        AvatarCustomizationType.Hats => "Hats",
        AvatarCustomizationType.Tops => "Tops",
        AvatarCustomizationType.Bottoms => "Bottoms",
        AvatarCustomizationType.Shoes => "Shoes",
        AvatarCustomizationType.Pose => "Pose",
        _ => type.ToString()
    };

    private string? BuildItemLabel(AvatarCustomizationData? data, int index, int total)
    {
        if (data == null) return null;
        var id = data.ID ?? string.Empty;
        string name;
        if (id.Contains("none"))
        {
            name = "none";
        }
        else if (_category == AvatarCustomizationType.Face || _category == AvatarCustomizationType.Hair)
        {
            // FACE and HAIR item IDs aren't loc keys — the game itself hides item names for
            // these categories (HUBProfileController.IsValidItemNameCategory returns false).
            // Fall back to a position-based identifier so the user has a stable handle.
            name = $"{_category} {index + 1}";
        }
        else
        {
            var loc = ResolveLoc(id);
            name = string.IsNullOrEmpty(loc) ? id : loc!;
        }
        // Shop-like format: item, status/price, position. Status is "equipped" for the currently
        // worn item (matches GetIndexForEquippedCustOfCurrentType) or "owned" for unworn-owned,
        // or the price for unowned. Per the speech-announce convention we keep this one call.
        var equippedIndex = GetEquippedIndex();
        string status;
        if (index == equippedIndex) status = "equipped";
        else if (IsOwned(data)) status = "owned";
        else if (data.Cost > 0) status = $"{data.Cost} {CurrencyName(data.currencyType)}";
        else status = "unavailable";
        return $"{name}, {status}, {index + 1} of {total}";
    }

    // LocalizationManager returns "(Missing)<key>" when a resource isn't in the loaded table.
    // TextProcessor strips that marker, but we want to detect "missing" up here so we can
    // fall back to a sensible default before passing anything to speech.
    private static string? ResolveLoc(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var loc = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(loc)) return null;
        if (loc!.Contains("(Missing)")) return null;
        return loc;
    }

    private static string CurrencyName(CurrencyType c) => c switch
    {
        CurrencyType.HardCurrency => "crystals",
        CurrencyType.SoftCurrency => "coins",
        _ => "currency",
    };

    private static string GetCategoryLocId(AvatarCustomizationType type) => type switch
    {
        AvatarCustomizationType.Face => "avatar_face",
        AvatarCustomizationType.Eyes => "avatar_eyes",
        AvatarCustomizationType.Hair => "avatar_hair",
        AvatarCustomizationType.Hats => "avatar_hats",
        AvatarCustomizationType.Tops => "avatar_tops",
        AvatarCustomizationType.Bottoms => "avatar_bottoms",
        AvatarCustomizationType.Shoes => "avatar_shoes",
        AvatarCustomizationType.Pose => "avatar_poses",
        AvatarCustomizationType.CatchphraseIntro => "avatar_catchphrase",
        AvatarCustomizationType.CatchphraseVictory => "avatar_catchphrase",
        AvatarCustomizationType.AnimIntro => "avatar_poses",
        AvatarCustomizationType.AnimVictory => "avatar_poses",
        AvatarCustomizationType.Glasses => "avatar_glasses",
        _ => "avatar_face",
    };
}
