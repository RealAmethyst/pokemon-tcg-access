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

    // Section pointer. The category panel can be navigated as a stack of vertically-
    // adjacent groups; right now we recognise the items list (Items) and, on Catchphrase
    // panels, the INTRO/VICTORY toggle group (Tabs). Up from the top row of Items escapes
    // to Tabs; Down from Tabs returns to Items. Other categories don't have a Tabs section
    // wired yet — they stay in Items.
    private enum Section { Items, Tabs }
    private Section _section = Section.Items;
    private int _tabIndex;
    private UnityEngine.UI.Selectable? _introToggle;
    private UnityEngine.UI.Selectable? _victoryToggle;

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
        DiscoverPhraseTabs();
        Announce(includeHeader: true);
    }
    public void OnResume()
    {
        DiscoverPhraseTabs();
        Announce(includeHeader: false);
    }
    public void OnExit() { /* preserve _controller for resume */ }
    public void RespeakAll() => Announce(includeHeader: true);

    // Categories that share a panel with Intro/Victory sub-tabs.
    //   • CatchphraseIntro / CatchphraseVictory — phrase panel, items in _phraseScrollRect
    //   • AnimIntro / AnimVictory — pose/animation panel, items in _avatarEnhancedScroller
    // Both prefabs name their toggles "Intro" / "Victory" and use ColoredEventTriggerToggle.
    private static bool HasIntroVictoryTabs(AvatarCustomizationType type) =>
        type == AvatarCustomizationType.CatchphraseIntro
     || type == AvatarCustomizationType.CatchphraseVictory
     || type == AvatarCustomizationType.AnimIntro
     || type == AvatarCustomizationType.AnimVictory;

    // The Intro/Victory sub-tabs are plain ColoredEventTriggerToggle instances (the
    // CatchphraseTypeToggleGroup class exists in source but isn't instantiated here). Each
    // panel — phrase, animation — has its own pair of named toggles. Filtering by
    // activeInHierarchy is unreliable at OnEnter time, so we anchor on the panel's own
    // ScrollRect (an unambiguous game-native reference): phrase categories use
    // _phraseScrollRect, anim categories use _avatarEnhancedScroller's ScrollRect. Walk up
    // the parent chain until we hit an ancestor whose subtree contains BOTH an "Intro" and
    // "Victory" toggle — that ancestor is the panel container, and those are our toggles.
    private void DiscoverPhraseTabs()
    {
        _introToggle = null;
        _victoryToggle = null;
        if (!HasIntroVictoryTabs(_category) || _controller == null) return;

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
                _introToggle = intro.MyToggle;
                _victoryToggle = victory.MyToggle;
                Log.Info($"AvatarCategory tabs: discovered at ancestor '{t.name}' for {_category}");
                return;
            }
        }
        Log.Info($"AvatarCategory tabs: walked up from anchor, no Intro/Victory pair found for {_category}");
    }

    private bool TabsAvailable => _introToggle != null && _victoryToggle != null;

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

        if (_section == Section.Tabs)
        {
            TickTabs();
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
            Log.Info($"AvatarCategory items Up: _index={_index} perRow={perRow} moved={moved} tabsAvailable={TabsAvailable}");
            if (!moved && TabsAvailable)
                EnterTabsSection();
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
        // The toggle group is always 2 items — INTRO at index 0, VICTORY at index 1 —
        // mirrored from CatchphraseTypeToggleGroup's two SerializeFields.
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
            // Drop back into items section — re-announce with section name so the user
            // hears which tab's items they're looking at (Intro vs Victory).
            _section = Section.Items;
            Announce(includeHeader: true);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            // No section above tabs wired yet (back arrow is a future addition).
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            ActivateTab();
        }
    }

    private void EnterTabsSection()
    {
        _section = Section.Tabs;
        _tabIndex = GetActiveTabIndex();
        Log.Info($"AvatarCategory: entering tabs section, tabIndex={_tabIndex}");
        AnnounceTab();
    }

    // Tab index for the currently active category. CatchphraseIntro / AnimIntro = 0,
    // CatchphraseVictory / AnimVictory = 1.
    private int GetActiveTabIndex() => _category switch
    {
        AvatarCustomizationType.CatchphraseVictory => 1,
        AvatarCustomizationType.AnimVictory => 1,
        _ => 0,
    };

    private void AnnounceTab()
    {
        var sel = _tabIndex == 0 ? _introToggle : _victoryToggle;
        if (sel == null) return;
        sel.Select();
        var name = _tabIndex == 0 ? "Intro" : "Victory";
        _router.Speech.Say($"{name}, {_tabIndex + 1} of 2", interrupt: true);
    }

    private void ActivateTab()
    {
        var sel = _tabIndex == 0 ? _introToggle : _victoryToggle;
        if (sel == null) return;
        // Setting Toggle.isOn=true triggers CatchphraseTypeToggleGroup.OnFilterStatusChanged,
        // which calls HUBProfileController.SelectCategory(CatchphraseIntro|Victory). Our own
        // SelectCategory_Postfix then updates _category / _index and Announce respects the
        // current section (stays in Tabs and re-announces the active tab).
        if (sel is UnityEngine.UI.Toggle t)
        {
            if (!t.isOn) t.isOn = true;
        }
        else if (sel is UnityEngine.UI.Button b)
        {
            b.onClick?.Invoke();
        }
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
        var items = GetItems();
        if (items == null || items.Count == 0)
        {
            Log.Info($"AvatarCategory: {_category} has 0 items");
            return;
        }
        if (_index < 0 || _index >= items.Count) _index = 0;

        var label = BuildItemLabel(items[_index] as AvatarCustomizationData, _index, items.Count);
        if (string.IsNullOrEmpty(label)) return;

        // Section name = the active sub-tab label when the panel has tabs (Intro/Victory
        // for catchphrases, AnimIntro/AnimVictory for animations), or the category name
        // otherwise. We don't invent a separate "Items" section name — the tab IS the
        // section, as per the user's spec: "We don't need dedicated section names when
        // the tabs themselves are what we need."
        var sectionName = GetItemsSectionName(_category);

        if (_section == Section.Tabs)
        {
            // User is on the tabs strip. Announce the active tab + position.
            _tabIndex = GetActiveTabIndex();
            var tabName = _tabIndex == 0 ? "Intro" : "Victory";
            _router.Speech.Say($"{tabName}, {_tabIndex + 1} of 2", interrupt: true);
            return;
        }

        // Items section. On entry (includeHeader=true), prefix with the section name so the
        // user knows where they are. On in-section moves, just the item label.
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
