using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.CardDex;
using TPCI.Coroutines;
using TPCI.Localization;
using TPCI.Rainier.CardDex;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// CARD DEX (card collection) screen — driver HUBCardDexScreenController, spawned by CardDexFeatureDriver
// onto a HUBScreenFeaturePlatform whose OnActivate/OnDeactivate events call the controller's public
// parameterless OnActivate()/OnDeactivate() — those are the lifecycle hook (NOT the standard
// HUBScreenController.OnActivate(HUBGroupController), because this controller is a plain MonoBehaviour).
//
// Presented as a section stack (Up/Down escapes between sections, Left/Right within, Tab cycles,
// Escape → overflow menu), visually top-to-bottom:
//   1. HUB top strip + overflow "⋮".
//   2. Series selector — the "MEGA EVOLUTION" dropdown trigger. Enter opens the in-place series list
//      (a sub-mode handled here, since in landscape it's not an OverlayManager overlay).
//   3. Set carousel — the row of expansion sets. Left/Right load the adjacent set (the game's own
//      ArrowScroll); see CardDexCarouselSection.
//   4. View controls — the MASTER/MAIN SET VIEW toggle, the "(i)" key info button, and the SORT BY
//      button. Enter drives each one's real action (classification toggle / info popup / sort overlay).
//   5. Card grid — the collection. Up/Down/Left/Right move a card; Enter opens the card-detail overlay
//      (CardDexCardOverlayScreen); see CardDexGridSection.
//
// The series list and the landscape sort overlay are in-place expandable panels (not OverlayManager
// overlays), so they're handled as poll-detected sub-modes here rather than as separate router screens.
internal sealed class HUBCardDexScreen : IScreenHandler
{
    private enum Mode { Normal, SeriesPicker, SortPicker }

    private static HUBCardDexScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<INavSection> _groups = new();
    private int _activeGroup = -1;
    private HUBCardDexScreenController? _controller;
    private bool _active;

    private Mode _mode = Mode.Normal;

    // Reflected game refs (cached on activate).
    private CollectionSetCarousel? _carousel;
    private CardDexSeriesSelector? _seriesSelector;
    private HUBCollectionsScreenController? _collectionsController;
    private CardCollectionGalleryView? _galleryView;
    private LocText? _seriesTitle;
    private Button? _seriesBtn;
    private Button? _infoBtn;
    private Selectable? _masterSel;

    // The two control sections kept as concrete navs so Enter can dispatch each control's real action.
    private KeyboardNavigator? _topNav;
    private KeyboardNavigator? _seriesNav;
    private KeyboardNavigator? _viewNav;
    private int _seriesGroupIndex = -1;
    private int _viewGroupIndex = -1;
    private int _gridGroupIndex = -1;

    // Series-picker sub-mode state (data nav over the dropdown's series list).
    private List<CardDexSeriesObjectsData> _seriesData = new();
    private int _seriesPickIndex;
    // Sort-picker sub-mode (real Selectable toggles inside the sort overlay).
    private KeyboardNavigator? _sortNav;

    private bool _hasAnnouncedAtAll;
    private string _structureAtPause = string.Empty;
    private int _indexAtPause = -1;
    private string _lastContentSig = string.Empty;
    private Selectable? _lastFtueTarget;
    private bool _subscribedToFtue;
    private Coroutine? _ftueWaitCoroutine;

    public string Name => "Card Dex";

    public HUBCardDexScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBCardDexScreen(router);

        var activate = AccessTools.Method(typeof(HUBCardDexScreenController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBCardDexScreen: could not resolve HUBCardDexScreenController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBCardDexScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBCardDexScreenController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBCardDexScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBCardDexScreen installed");
    }

    private static void Activate_Postfix(HUBCardDexScreenController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        _instance.CacheRefs();
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBCardDexScreenController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    private void CacheRefs()
    {
        if (_controller == null) return;
        _carousel = ReadField<CollectionSetCarousel>(_controller, "_collectionSetCarousel");
        _seriesSelector = ReadField<CardDexSeriesSelector>(_controller, "_cardDexSeriesSelector");
        _seriesTitle = ReadField<LocText>(_controller, "_seriesTitle");
        _seriesBtn = ReadField<Button>(_controller, "_dexSeriesSelectBtn");
        _infoBtn = ReadField<Button>(_controller, "_cardDexInfoBtn");
        _collectionsController = ReadField<HUBCollectionsScreenController>(_controller, "collectionsScreenController");
        _galleryView = _collectionsController != null ? ReadField<CardCollectionGalleryView>(_collectionsController, "_cardCollectionGalleryView") : null;
        var masterComp = _collectionsController != null ? ReadField<Component>(_collectionsController, "classificationToggle") : null;
        _masterSel = masterComp != null ? masterComp.GetComponentInChildren<Selectable>(true) : null;
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    public void OnEnter()
    {
        SubscribeToFtue();

        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }
        Announce();
    }

    public void OnResume()
    {
        _mode = Mode.Normal;
        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        RebuildGroups();
        if (_groups.Count == 0) return;
        if (_activeGroup < 0 || _activeGroup >= _groups.Count) _activeGroup = PickDefaultGroup();
        _groups[_activeGroup].FocusFirst(announce: false);

        if (FTUEHook.FtueIsRunning) { StartFtueSettlementWait(); return; }

        // Returning from the card-detail overlay: put the grid cursor back on the card we opened
        // (FocusFirst above reset it to the first card).
        if (_indexAtPause >= 0 && _activeGroup >= 0 && _activeGroup < _groups.Count
            && _groups[_activeGroup] is CardDexGridSection grid)
            grid.RestoreIndex(_indexAtPause);

        if (StructureSignature() != _structureAtPause) Announce();
        else RestoreGroupFocus();
    }

    public void OnExit()
    {
        CancelFtueSettlementWait();
        _structureAtPause = StructureSignature();
        _indexAtPause = (_groups.Count > 0 && _activeGroup >= 0 && _activeGroup < _groups.Count)
            ? _groups[_activeGroup].Index : -1;
    }

    private void RestoreGroupFocus()
    {
        var g = _groups[_activeGroup];
        g.RefocusCurrent(announce: false);
    }

    public void RespeakAll()
    {
        if (_groups.Count == 0) return;
        _router.Speech.Say(ScreenSummary(), interrupt: true);
        _groups[_activeGroup].RefocusCurrent(announce: true, interrupt: false);
    }

    public string StateSignature()
    {
        if (_controller == null) return "CardDex|dead";
        var idx = _groups.Count > 0 && _activeGroup >= 0 ? _groups[_activeGroup].Index : -1;
        return $"CardDex|{_mode}|{_activeGroup}|{idx}|{ContentSig()}";
    }

    // ---- tick -----------------------------------------------------------------------------------

    public void Tick()
    {
        if (_controller == null)
        {
            _active = false; _router.Deactivate(this);
            return;
        }
        if (!Application.isFocused) return;

        // In-place dropdowns take over while open (poll their own open flags — they aren't
        // OverlayManager overlays in landscape, so there's no Activate hook to ride).
        if (SeriesOpen()) { TickSeriesPicker(); return; }
        if (SortOpen()) { TickSortPicker(); return; }
        if (_mode != Mode.Normal)
        {
            // A dropdown just closed — return to the section we came from.
            _mode = Mode.Normal;
            if (_groups.Count > 0 && _activeGroup >= 0 && _activeGroup < _groups.Count)
                _groups[_activeGroup].RefocusCurrent(announce: true);
        }

        if (!FTUEHook.FtueIsRunning)
        {
            var sig = ContentSig();
            if (sig != _lastContentSig) { _lastContentSig = sig; OnContentChanged(); return; }
        }

        if (_groups.Count == 0) return;
        var nav = _groups[_activeGroup];

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            OpenOverflowMenu();
            return;
        }

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (_groups.Count > 1)
            {
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) && _activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup--;
            _groups[_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) && _activeGroup < _groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            _activeGroup++;
            _groups[_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        // Enter / Space on a control section drives that control's real action (the dropdowns aren't
        // wired to onClick in landscape, so calling the public methods is the reliable game path).
        if ((_activeGroup == _seriesGroupIndex || _activeGroup == _viewGroupIndex)
            && (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) || UnityEngine.Input.GetKeyDown(KeyCode.Space)))
        {
            ActivateControl(nav);
            return;
        }

        nav.Tick();
    }

    private void ActivateControl(INavSection nav)
    {
        try
        {
            if (_activeGroup == _seriesGroupIndex) { _seriesSelector?.ToggleSelectionMenu(); return; }
            var cur = (nav as KeyboardNavigator)?.Current;
            if (cur == null) return;
            if (cur == _infoBtn) { _infoBtn!.onClick?.Invoke(); return; }
            if (_masterSel != null && cur == _masterSel) { _collectionsController?.ToggleClassificationAndRefreshDisplay(); return; }
            if (_masterSel != null) { _galleryView?.ToggleLandscapeSortingOverlay(); return; }   // not info/master ⇒ the SORT control
            // Master toggle couldn't be identified — drive the control's own (prefab-wired) action.
            if (cur is Toggle tg) tg.isOn = !tg.isOn;
            else if (cur is Button bt) bt.onClick?.Invoke();
        }
        catch (Exception e) { Log.Error("CardDex ActivateControl: " + e.Message); }
    }

    private void Announce()
    {
        if (_groups.Count == 0) return;
        if (!_hasAnnouncedAtAll)
        {
            _router.Speech.Say(ScreenSummary(), interrupt: true);
            _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            _hasAnnouncedAtAll = true;
        }
        else
        {
            _groups[_activeGroup].RefocusCurrent(announce: true);
        }
    }

    private string StructureSignature() => string.Join(",", _groups.Select(g => g.Count));

    // ---- group discovery ------------------------------------------------------------------------

    private void RebuildGroups()
    {
        _groups.Clear();
        _seriesGroupIndex = _viewGroupIndex = _gridGroupIndex = -1;
        _topNav = _seriesNav = _viewNav = null;
        if (_controller == null) { _lastContentSig = ContentSig(); return; }

        var known = new HashSet<Selectable>();

        // 1. HUB top strip + overflow "⋮".
        var topItems = new List<Selectable>();
        var topController = UnityEngine.Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
            if (navGroups != null)
                foreach (var pnb in navGroups)
                {
                    var btn = pnb != null ? ReadField<Button>(pnb, "navButton") : null;
                    if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) topItems.Add(btn);
                }
            var settingsT = ReadField<Transform>(topController, "settingsButton");
            var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
            if (overflow != null && overflow.isActiveAndEnabled && overflow.IsInteractable()) topItems.Add(overflow);
        }
        if (topItems.Count > 0)
        {
            var ordered = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();
            foreach (var s in ordered) known.Add(s);
            _topNav = AddGroup(ordered, resolver: UiLabels.TopNavLabel);
        }

        // 2 & 4. The screen chrome controls (series trigger, view toggle, info, sort), discovered by
        //        exclusion: every interactable control under the controller that isn't the top strip,
        //        a carousel cell, a grid cell, or a dropdown item. Bucketed by whether they sit above
        //        the set carousel (the series selector) or below it (the view controls).
        float carouselY = _carousel != null ? _carousel.transform.position.y : float.NaN;
        var above = new List<Selectable>();
        var below = new List<Selectable>();
        foreach (var s in ChromeControls(known))
        {
            if (!float.IsNaN(carouselY) && s.transform.position.y > carouselY) above.Add(s);
            else below.Add(s);
        }

        // 2. Series selector (above the carousel).
        if (above.Count > 0)
        {
            var ordered = above.OrderBy(s => ReadingOrder(s.transform)).ToList();
            _seriesNav = AddGroup(ordered, resolver: ControlLabel, groupName: "Series");
            _seriesGroupIndex = _groups.Count - 1;
        }

        // 3. Set carousel.
        if (_carousel != null)
        {
            _groups.Add(new CardDexCarouselSection(_router.Speech, _carousel, CurrentClassification));
            Log.Info($"Card Dex group {_groups.Count} [Sets]: carousel");
        }

        // 4. View controls (below the carousel).
        if (below.Count > 0)
        {
            var ordered = below.OrderBy(s => ReadingOrder(s.transform)).ToList();
            _viewNav = AddGroup(ordered, resolver: ControlLabel, groupName: "View");
            _viewGroupIndex = _groups.Count - 1;
        }

        // 5. Card grid.
        if (_galleryView != null)
        {
            _groups.Add(new CardDexGridSection(_router.Speech, _galleryView, OpenCardDetail));
            _gridGroupIndex = _groups.Count - 1;
            Log.Info($"Card Dex group {_groups.Count} [Card collection]: grid");
        }

        _lastContentSig = ContentSig();
    }

    // Interactable Buttons / Toggles that belong to the screen chrome (series, view toggle, info,
    // sort) — everything under the controller that isn't already known (top strip), a carousel cell,
    // a grid cell, or a dropdown item.
    private List<Selectable> ChromeControls(HashSet<Selectable> known)
    {
        var result = new List<Selectable>();
        var seen = new HashSet<Selectable>(known);
        var carouselT = _carousel != null ? _carousel.transform : null;
        var gridT = GridScrollerTransform();
        var seriesListT = _seriesSelector != null ? _seriesSelector.transform : null;
        var sortT = _galleryView != null && _galleryView.SortingOverlay != null ? _galleryView.SortingOverlay.transform : null;

        void TryAdd(Selectable? s)
        {
            if (s == null || seen.Contains(s) || !s.isActiveAndEnabled) return;
            if (!(s is Button || s is Toggle)) return;
            if (carouselT != null && s.transform.IsChildOf(carouselT)) return;
            if (gridT != null && s.transform.IsChildOf(gridT)) return;
            if (seriesListT != null && s.transform.IsChildOf(seriesListT)) return;
            if (sortT != null && s.transform.IsChildOf(sortT)) return;
            // The master toggle is a wrapper with an inner child Selectable — keep only the inner one.
            if (s is Toggle && _masterSel != null && s != _masterSel && _masterSel.transform.IsChildOf(s.transform)) return;
            seen.Add(s);
            result.Add(s);
        }

        // Seed with the controls we hold direct refs to (the scan may not reach them if they sit
        // outside the controller's transform), then sweep both the controller and the collections
        // subpage (which owns the master-set toggle + sort button) for anything else.
        TryAdd(_seriesBtn);
        TryAdd(_infoBtn);
        TryAdd(_masterSel);
        foreach (var root in new Component?[] { _controller, _collectionsController })
        {
            if (root == null) continue;
            foreach (var s in root.GetComponentsInChildren<Selectable>(includeInactive: false)) TryAdd(s);
        }
        return result;
    }

    private Transform? GridScrollerTransform()
    {
        if (_galleryView == null) return null;
        var cw = ReadField<Component>(_galleryView, "contentWindow");
        return cw != null ? cw.transform : null;
    }

    private int PickDefaultGroup()
    {
        if (_gridGroupIndex >= 0 && _gridGroupIndex < _groups.Count) return _gridGroupIndex;
        return _groups.Count > 0 ? _groups.Count - 1 : 0;
    }

    private CardDexClassification CurrentClassification()
        => _collectionsController != null ? _collectionsController.GetCardDexClassification() : CardDexClassification.MasterSet;

    // ---- content-change integration -------------------------------------------------------------

    private string ContentSig()
    {
        if (_controller == null) return "none";
        string set = HUBCardDexScreenController.LoadedSetId ?? "";
        string series = HUBCardDexScreenController.LoadedSeriesId ?? "";
        string cls = CurrentClassification().ToString();
        int cards = 0;
        try { var list = _galleryView != null ? ReadField<object>(_galleryView, "curCollection") : null; if (list is System.Collections.ICollection c) cards = c.Count; } catch { }
        return $"{series}|{set}|{cls}|{cards}";
    }

    private void OnContentChanged()
    {
        int prev = _activeGroup;
        RebuildGroups();
        if (_groups.Count == 0) { _activeGroup = -1; return; }
        _activeGroup = Mathf.Clamp(prev < 0 ? PickDefaultGroup() : prev, 0, _groups.Count - 1);
        _groups[_activeGroup].RefocusCurrent(announce: false);
    }

    // ---- card-detail overlay --------------------------------------------------------------------

    private void OpenCardDetail(ArchetypeDBCards stack)
    {
        try
        {
            var om = ManagerSingleton<OverlayManager>.instance;
            var ov = om != null ? om.cardDexPurchaseCardOverlay : null;
            if (ov != null && _galleryView != null) { ov.SetAdjacentProvider(_galleryView); ov.Setup(stack); }
        }
        catch (Exception e) { Log.Error("CardDex OpenCardDetail: " + e.Message); }
    }

    // ---- series picker sub-mode -----------------------------------------------------------------

    private bool SeriesOpen()
    {
        try { return _seriesSelector != null && ReadField<object>(_seriesSelector, "isOpen") is bool b && b; }
        catch { return false; }
    }

    private void TickSeriesPicker()
    {
        if (_mode != Mode.SeriesPicker)
        {
            _mode = Mode.SeriesPicker;
            _seriesData = SeriesEntries();
            _seriesPickIndex = SelectedSeriesIndex();
            _router.Speech.Say("Series", interrupt: true);
            AnnounceSeriesPick(interrupt: false);
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            _seriesSelector?.CloseSeriesOverlay();
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (_seriesPickIndex < _seriesData.Count - 1) { _seriesPickIndex++; AnnounceSeriesPick(true); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (_seriesPickIndex > 0) { _seriesPickIndex--; AnnounceSeriesPick(true); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (_seriesPickIndex >= 0 && _seriesPickIndex < _seriesData.Count)
            {
                var e = _seriesData[_seriesPickIndex];
                try { e.onSelectSeries?.Invoke(e.seriesProgress, e.seriesId); } catch (Exception ex) { Log.Error("CardDex series select: " + ex.Message); }
            }
        }
    }

    private List<CardDexSeriesObjectsData> SeriesEntries()
    {
        var dict = _seriesSelector != null ? ReadField<object>(_seriesSelector, "_cardDexSeriesObjectsData") : null;
        var list = new List<CardDexSeriesObjectsData>();
        if (dict is IEnumerable en)
            foreach (var kv in en)
            {
                var val = kv?.GetType().GetProperty("Value")?.GetValue(kv) as CardDexSeriesObjectsData;
                if (val != null) list.Add(val);
            }
        return list;
    }

    private int SelectedSeriesIndex()
    {
        var id = HUBCardDexScreenController.LoadedSeriesId;
        for (int i = 0; i < _seriesData.Count; i++) if (_seriesData[i].seriesId == id) return i;
        return 0;
    }

    private void AnnounceSeriesPick(bool interrupt)
    {
        if (_seriesPickIndex < 0 || _seriesPickIndex >= _seriesData.Count) return;
        var e = _seriesData[_seriesPickIndex];
        var name = CardDexText.Loc("tcg_" + (e.seriesId ?? "").ToLowerInvariant()) ?? e.seriesId ?? "Series";
        var parts = new List<string> { name };
        try
        {
            if (e.seriesProgress != null && e.seriesProgress.SeriesProgress != null
                && e.seriesProgress.SeriesProgress.TryGetValue(CurrentClassification(), out var p) && p != null)
                parts.Add($"{p.CardProgress} of {p.CardTotal} collected");
        }
        catch { }
        if (e.seriesId == HUBCardDexScreenController.LoadedSeriesId) parts.Add("selected");
        parts.Add($"{_seriesPickIndex + 1} of {_seriesData.Count}");
        _router.Speech.Say(string.Join(", ", parts), interrupt);
    }

    // ---- sort picker sub-mode -------------------------------------------------------------------

    private bool SortOpen()
    {
        try
        {
            var blocker = _galleryView != null ? ReadField<GameObject>(_galleryView, "sortUIBlocker") : null;
            return blocker != null && blocker.activeSelf;
        }
        catch { return false; }
    }

    private void TickSortPicker()
    {
        if (_mode != Mode.SortPicker || _sortNav == null)
        {
            _mode = Mode.SortPicker;
            var overlay = _galleryView != null ? _galleryView.SortingOverlay : null;
            var items = overlay != null
                ? overlay.GetComponentsInChildren<Selectable>(includeInactive: false)
                    .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled)
                    .OrderBy(s => ReadingOrder(s.transform)).ToList()
                : new List<Selectable>();
            _sortNav = new KeyboardNavigator(_router.Speech) { AllowTab = true };
            _sortNav.SetItems(items);
            _sortNav.ForceFlatVertical();
            _sortNav.OnBack = () => _galleryView?.CloseSortingOverlay();
            _router.Speech.Say("Sort by", interrupt: true);
            _sortNav.FocusFirst(announce: true, interruptPrior: false);
        }
        _sortNav.Tick();
    }

    // ---- labels ---------------------------------------------------------------------------------

    private string ScreenSummary()
    {
        var parts = new List<string> { "Card Dex" };
        var series = CardDexText.Loc("tcg_" + (HUBCardDexScreenController.LoadedSeriesId ?? "").ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(series) && _seriesTitle != null) series = Clean(_seriesTitle);
        if (!string.IsNullOrWhiteSpace(series)) parts.Add(series!);
        var set = CardDexText.Loc("tcg_" + (HUBCardDexScreenController.LoadedSetId ?? "").ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(set)) parts.Add(set!);
        return string.Join(", ", parts);
    }

    private string ControlLabel(Selectable s)
    {
        if (s == _infoBtn) return "Card dex key, information";
        if (_seriesNav != null && _seriesNav.Items.Contains(s))
        {
            var name = _seriesTitle != null ? Clean(_seriesTitle) : null;
            if (string.IsNullOrWhiteSpace(name)) name = CardDexText.Loc("tcg_" + (HUBCardDexScreenController.LoadedSeriesId ?? "").ToLowerInvariant());
            return string.IsNullOrWhiteSpace(name) ? "Series" : $"Series, {name}";
        }
        if (s == _masterSel)
        {
            var text = FirstLetterText(s);
            var label = string.IsNullOrWhiteSpace(text) ? "Set view" : text!;
            if (!s.interactable) label += ", unavailable";
            return label;
        }
        // Sort and anything else: read the control's own text (it's self-describing, e.g. "SORT BY: …").
        return KeyboardNavigator.DefaultLabel(s);
    }

    private static string? FirstLetterText(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
            if (!string.IsNullOrWhiteSpace(t.text) && t.text.Any(char.IsLetter)) return t.text.Trim();
        return null;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private KeyboardNavigator AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null, string? groupName = null)
    {
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = groupName };
        g.SetItems(items, resolver);
        g.OnBack = OpenOverflowMenu;
        _groups.Add(g);
        var names = string.Join(", ", items.Take(20).Select(s => s != null ? s.gameObject.name : "<null>"));
        Log.Info($"Card Dex group {_groups.Count} [{groupName ?? "-"}]: {items.Count} items — {names}");
        return g;
    }

    private static string Clean(LocText? t)
    {
        if (t == null) return "";
        var s = t.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? "" : s!.Trim();
    }

    // ---- FTUE coordination (same model as the other HUB handlers) -------------------------------

    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        _groups.Clear();
        _activeGroup = -1;
        _seriesGroupIndex = _viewGroupIndex = _gridGroupIndex = -1;
        _lastContentSig = ContentSig();

        var target = FTUEHook.FocusedElement;
        _lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled) return;

        // A coached Toggle is a tab, so Enter must SELECT it — see FTUEHook.FocusedElement.
        AddGroup(new List<Selectable> { target }).RadioToggleMode = target is Toggle;
        _activeGroup = 0;
        _groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private void SubscribeToFtue()
    {
        if (_subscribedToFtue) return;
        FTUEHook.FocusedElementChanged += OnFtueFocusChanged;
        _subscribedToFtue = true;
    }

    private void OnFtueFocusChanged()
    {
        if (!_active || _controller == null) return;
        if (FTUEHook.FocusedElement == _lastFtueTarget) return;
        if (FTUEHook.FtueIsRunning) RebuildForFtue();
    }

    private void StartFtueSettlementWait()
    {
        CancelFtueSettlementWait();
        _ftueWaitCoroutine = AnywhereCoroutine.Instance.unmanaged.StartCoroutine(WaitForFtueToSettle());
    }

    private void CancelFtueSettlementWait()
    {
        if (_ftueWaitCoroutine != null)
        {
            AnywhereCoroutine.Instance.unmanaged.StopCoroutine(_ftueWaitCoroutine);
            _ftueWaitCoroutine = null;
        }
    }

    private IEnumerator WaitForFtueToSettle()
    {
        var mgr = FTUEManager.instance;
        var animator = mgr != null ? mgr.GetComponent<Animator>() : null;
        while (FTUEHook.FtueIsRunning && animator != null && !animator.enabled) yield return null;
        yield return null;
        if (FTUEHook.FtueSpeechClaimingStateActive) { _ftueWaitCoroutine = null; yield break; }
        _ftueWaitCoroutine = null;
        if (_active && _groups.Count > 0) Announce();
    }

    private static T? ReadField<T>(object? instance, string name) where T : class
    {
        if (instance == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }

    private static void OpenOverflowMenu() => HUBOverflowMenuScreen.TryOpen();

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
