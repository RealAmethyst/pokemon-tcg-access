using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;
using TPCI.Rainier;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CardCategory = TPCI.Rainier.CardCategory;

namespace PokemonTCGLiveAccessibility.Screens;

// DECK EDITOR — driver: HUBEditDeckController (a HUBScreenController, like HOME/Shop), so the
// lifecycle hook is postfix OnActivate(HUBGroupController)/OnDeactivate.
//
// ── How adding/removing actually works (researched 2026-05-30) ────────────────────────────────
// The on-screen "drag a card into the deck" gesture is COSMETIC: CardEditorDrag just clones a
// picture of the card and moves it under the cursor. The real work happens at the drop, and
// DeckEditorCardDropTarget.OnDrop does nothing but call the controller's canonical
// HUBEditDeckController.AddCard / RemoveCard — the SAME methods the card-detail overlay's real
// +/- buttons (AddCardOverlayManager.AddClicked/RemoveClicked) call. AddCard already runs the
// legality check (CanAddCard → DeckValidationManager), mutates the deck, refreshes the UI and
// logs analytics. So this is the game's real endpoint, not a parallel op (unlike battle's
// MatchUserOperations.PlaceCard) — see [[feedback-drive-game-ui-not-ops]].
//
// Unlike battle, there is NO placement target to choose — a card goes into the one deck — so the
// battle pick-up/navigate/drop targeting (CardTargetingHandler/KbPlacementInitiator) does NOT
// apply. But the designer wants the drag done *properly* as a real pick-up → navigate → place
// flow (not a one-key shortcut): Enter picks a card up, Tab moves to the destination group, Enter
// there drops it through the game's real DeckEditorCardDropTarget.OnDrop. It's symmetric: hold a
// LIBRARY card → place on the DECK = add; hold a DECK card → place on the LIBRARY = remove.
//
// ── The FTUE catch (a battle-gotcha twin) ─────────────────────────────────────────────────────
// During the tutorial the deck step wires SetFtueDrop(...) so OnDrop — AFTER AddCard/RemoveCard —
// fires ftueDrop() → indicator.Unblock(), which advances the tutorial (in landscape the focused
// card has noClick=true, so that drop callback is the ONLY thing that advances it). So we MUST
// place through the real OnDrop (which carries ftueDrop), not a bare AddCard — else the deck edits
// but the tutorial freezes, exactly like a direct attack op froze the battle FTUE.
//
// ── Navigation ────────────────────────────────────────────────────────────────────────────────
// The card library is a VIRTUALIZED EnhancedScroller (only ~a screenful of cells exist), so we
// navigate the DATA, not realized cells: the library keeps the full filtered list (curCollection,
// reached via PopulateCollectionDrawer.GetNextCard/GetPreviousCard/GetCurrentCollectionAmount) and
// the deck keeps DataRowDeck.DeckStacks (filtered+sorted by CardSortHelper.SortDeck). A realized
// CardStubParts is only needed at pickup/inspect, and we scroll it into view the game's own way
// (GetCardButton). Sections cycle with Tab (the designer's "use Tab to go between groups"); within
// a card group Left/Right moves between cards and Down/Up reads the card's detail lines (battle
// convention). I opens the game's real card-detail overlay (AddCardOverlayScreen) — the second,
// quantity-precise add/remove method.
internal sealed class HUBEditDeckScreen : IScreenHandler
{
    private enum Section { Top, Tabs, Deck, Library }
    private enum Kind { Deck, Library }

    private static HUBEditDeckScreen? _instance;
    public static HUBEditDeckScreen? Instance => _instance;

    private readonly ScreenRouter _router;
    private HUBEditDeckController? _controller;
    private bool _active;
    private int _notCurrentTicks;   // consecutive ticks the editor hasn't been the current HUB screen

    // Section model. Top = back/options buttons; Tabs = category toggles; Deck/Library = card data.
    private Section _section = Section.Library;
    private KeyboardNavigator? _topNav;
    private KeyboardNavigator? _tabsNav;
    private GlobalCategorySelector? _categorySelector;
    private CardCategory _lastCategory = (CardCategory)(-1);

    // Section names sourced from the game's own headers (loc IDs deck_card_library / deck_deck —
    // the "CARD LIBRARY" banner and the deck panel). Announced when you Tab into a card group so
    // you always know whether you're in the Deck or the Card Library.
    private string _libGroupName = "Card Library";
    private string _deckGroupName = "Deck";

    // Deck group (data): the filtered+sorted deck stacks and a cursor.
    private ArchetypeDBCards[] _deckCards = Array.Empty<ArchetypeDBCards>();
    private int _deckIndex = -1;

    // Library group (data): cursor into curCollection, navigated incrementally (the full list can
    // be hundreds of cards — we never materialize it, we step with GetNext/GetPreviousCard).
    private int _libIndex = -1;
    private ArchetypeDBCards _libCurrent;
    private int _libCount;
    private bool _libAwaitingLoad;     // entered Library before its async filter query produced cards
    private bool _collectionCallbackRegistered;

    // Detail-line cursor for the focused card (Down/Up). -1 = on the name line.
    private readonly List<string> _lines = new();
    private int _lineIndex = -1;

    // Held card (the "pick up" half of the drag). Null card = not holding. We hold the card's DATA
    // row (not a realized stub) so pickup is synchronous and immune to the scroller's virtualization
    // — the place step calls AddCard/RemoveCard directly, which is exactly what the drop does.
    private bool _holding;
    private ICardDataRow? _heldCard;
    private CardEditorDrag.CardDropAction _heldAction;
    private Section _heldFrom;
    private string _heldName = string.Empty;

    // Last deck-editor tutorial target we announced (so the guidance isn't repeated every frame).
    private string _lastFtueCardId = string.Empty;

    private bool _subscribedToFtue;
    private Selectable? _lastFtueTarget;

    // Non-card tutorial focuses (category tabs, Back, craft buttons) are reparented under a Blocker
    // and aren't in our normal sections, so we present the focused control directly for Enter — the
    // same control a sighted player clicks. (Card steps use JumpToCard + pickup/place instead.)
    private KeyboardNavigator? _ftueFocusNav;
    private bool _ftueFocusActive;
    private Selectable? _ftueFocusShown;

    // The sort / filter popups (AnimatedSetActive on the controller). When one is open we run a modal
    // sort mode: focus into its toggles, Tab is inert, Escape closes it.
    private AnimatedSetActive? _sortAnim;
    private AnimatedSetActive? _filterAnim;
    private KeyboardNavigator? _sortNav;
    private AnimatedSetActive? _sortPopup;   // the popup the modal mode is currently driving
    private bool _sortMode;

    // The tutorial arms a drop target with an ftueDrop callback (Unfocus + SaveInventory +
    // indicator.Unblock) that OnDrop fires after AddCard/RemoveCard to advance the FTUE. We call
    // AddCard/RemoveCard directly (robust, no synthesized drag), so we fire that same callback.
    private static readonly FieldInfo? FtueDropField = AccessTools.Field(typeof(DeckEditorCardDropTarget), "ftueDrop");
    private static readonly FieldInfo? HasFtueDropField = AccessTools.Field(typeof(DeckEditorCardDropTarget), "hasFtueDrop");
    private static readonly FieldInfo? OrigParentField = AccessTools.Field(typeof(DeckEditorCardDropTarget), "originalParent");

    public string Name => "Deck editor";

    public HUBEditDeckScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _ftueFocusNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Tutorial" };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBEditDeckScreen(router);

        var activate = AccessTools.Method(typeof(HUBEditDeckController), "OnActivate");
        if (activate == null)
        {
            Log.Error("HUBEditDeckScreen: could not resolve HUBEditDeckController.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBEditDeckScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(HUBEditDeckController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBEditDeckScreen), nameof(Deactivate_Postfix))));

        AddCardOverlayScreen.Install(harmony, router);
        // 1.5.0 deck-editor overlays (open over the editor; silent without their own handlers).
        DeckStatsOverlayScreen.Install(harmony, router);
        CraftAllConfirmOverlayScreen.Install(harmony, router);
        DeckValidityOverlayScreen.Install(harmony, router);
        Log.Info("HUBEditDeckScreen installed");
    }

    private static void Activate_Postfix(HUBEditDeckController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(HUBEditDeckController __instance)
    {
        if (_instance == null || _instance._controller != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────

    public void OnEnter()
    {
        SubscribeToFtue();
        ClearHold();
        RebuildSections();

        if (FTUEHook.FtueIsRunning && FTUEHook.FocusedElement is { } t && t.isActiveAndEnabled)
        {
            // The FTUE highlighted a specific card/button — defer to the standard browse view but
            // make the player aware. Full per-card FTUE targeting is a follow-up; the critical bit
            // (placing through OnDrop so ftueDrop advances the tutorial) is handled by the drag.
            _lastFtueTarget = FTUEHook.FocusedElement;
        }

        AnnounceIntro();
        EnterSection(Section.Library, announce: true, interruptPrior: false);
    }

    public void OnResume()
    {
        ClearHold();
        RebuildSections();
        // A child overlay (AddCardOverlay) closed, or a modal popped. The deck may have changed.
        RefreshDeck();
        RefreshLibraryCount();
        // During the tutorial the FTUE guidance (next Tick) re-announces the target — don't repeat
        // the section here, or the rapid coaching dialogue push/pop would spam it.
        if (FTUEHook.FtueIsRunning) return;
        ReAnnounceCurrent(interrupt: true);
    }

    public void OnExit()
    {
        // Keep refs alive for OnResume — only drop the transient hold.
        if (_holding) CancelHold(announce: false);
    }

    public void RespeakAll()
    {
        if (_controller == null) return;
        AnnounceIntro();
        ReAnnounceCurrent(interrupt: false);
    }

    public string StateSignature()
    {
        if (_controller == null) return "DeckEditor|dead";
        int size = _controller.DataDeck?.WorkingDeck?.size ?? -1;
        int idx = _section == Section.Deck ? _deckIndex : _section == Section.Library ? _libIndex : -1;
        return $"DeckEditor|{_section}|{idx}|{size}|{_lastCategory}|hold={_holding}";
    }

    public void Tick()
    {
        if (_controller == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }

        // Lifecycle-desync guard: the FTUE (and some HUB nav paths) can leave the editor for another
        // screen WITHOUT firing HUBEditDeckController.OnDeactivate, which would leave us stale-active
        // — owning input on a screen we don't drive and blocking the FTUE fallback / next handler.
        // If the editor stops being the current HUB screen for a few frames, let go. (A few frames,
        // not one, so a 1-frame entry race where currentScreen lags OnActivate can't drop a live
        // editor.)
        if (!DeckEditorIsCurrentScreen())
        {
            if (++_notCurrentTicks >= 3)
            {
                Log.Info("DeckEditor: no longer the current HUB screen — self-deactivating (lifecycle desync)");
                _active = false;
                _router.Deactivate(this);
                _controller = null;
                _notCurrentTicks = 0;
                return;
            }
        }
        else _notCurrentTicks = 0;

        if (!Application.isFocused) return;

        // Category switched (toggle fired) → re-filter both card groups. The library re-queries
        // asynchronously, so flag it to re-focus card 0 when the new collection arrives (via
        // OnCollectionChanged); the deck is synchronous, so re-focus it now.
        if (_categorySelector != null && _categorySelector.CurrentVal != _lastCategory)
        {
            _lastCategory = _categorySelector.CurrentVal;
            RefreshDeck();
            _deckIndex = _deckCards.Length > 0 ? 0 : -1;
            _libIndex = -1;
            _libAwaitingLoad = true;
            if (_section == Section.Deck && _deckCards.Length > 0) DeckFocus(0, announce: true);
        }

        // Surface what the tutorial wants (the focused card is reparented under a Blocker, so we
        // read the target from the indicator and jump to it) — only when not mid-hold.
        if (!_holding) UpdateFtueGuidance();

        // A forced non-card tutorial focus (category tab, Back, …): drive just that control.
        if (_ftueFocusActive) { _ftueFocusNav?.Tick(); return; }

        // The sort / filter popup is its OWN modal: while it's open, drive only its options. Tab and
        // the section nav are inert (we return), Escape closes it.
        if (TickSortMode()) return;

        // Tab cycles sections (and, while holding, moves to the placement group).
        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            CycleSection(shift ? -1 : +1);
            return;
        }

        // Escape / Backspace: cancel a hold first; otherwise back out of the editor (to DECKS).
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) ||
            (UnityEngine.Input.GetKeyDown(KeyCode.Backspace) && !IsTextInputFocused()))
        {
            if (_holding) { CancelHold(announce: true); return; }
            DoBack();
            return;
        }

        switch (_section)
        {
            case Section.Top: _topNav?.Tick(); break;
            case Section.Tabs: _tabsNav?.Tick(); break;
            case Section.Deck: TickCardGroup(Kind.Deck); break;
            case Section.Library: TickCardGroup(Kind.Library); break;
        }
    }

    // ── Sections ──────────────────────────────────────────────────────────────────────────────

    private void RebuildSections()
    {
        if (_controller == null) return;

        _categorySelector = _controller.GetComponentInChildren<GlobalCategorySelector>(includeInactive: false);
        if (_categorySelector != null) _lastCategory = _categorySelector.CurrentVal;

        _libGroupName = Loc("deck_card_library", "Card Library");
        _deckGroupName = Loc("deck_deck", "Deck");

        // Category tabs (Pokémon / Trainer / Energy / All) — radio toggles.
        var toggles = _categorySelector != null
            ? _categorySelector.GetComponentsInChildren<Toggle>(includeInactive: false)
                .Where(t => t.isActiveAndEnabled).Cast<Selectable>()
                .OrderBy(s => ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        var toggleSet = new HashSet<Selectable>(toggles);
        _tabsNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, RadioToggleMode = true, GroupName = "Category" };
        _tabsNav.SetItems(toggles, CategoryLabel);

        // Top bar: header/options/back buttons — every Button that ISN'T a card stub's button or a
        // category toggle. (Card stub buttons live under CardStubParts; we drive those by data.)
        _sortAnim ??= ReflectAnim(_controller, "landscapeSortingOverlay");
        _filterAnim ??= ReflectAnim(_controller, "landscapeFilterOverlay");
        var topButtons = _controller.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Where(b => b.GetComponentInParent<CardStubParts>() == null)
            .Where(b => !toggleSet.Contains(b))
            .OrderBy(s => ReadingOrder(s.transform))
            .Cast<Selectable>().ToList();
        _topNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Deck options" };
        _topNav.SetItems(topButtons);

        // The library's filtered list loads asynchronously (and re-loads on category/search/filter
        // changes). Subscribe to its own change callback so we focus card 0 the moment it's ready,
        // rather than polling. AddCollectionChangedCallback dedups, and the drawer is a persistent
        // singleton, so registering once per handler lifetime is enough.
        if (!_collectionCallbackRegistered && _controller.CollectionDrawer != null)
        {
            _controller.CollectionDrawer.AddCollectionChangedCallback(OnCollectionChanged);
            _collectionCallbackRegistered = true;
        }

        RefreshDeck();
        RefreshLibraryCount();
    }

    // The drawer's filtered collection finished (re)loading — initial open, category switch, search
    // or filter. Refresh our count and, if we're parked in the Library awaiting it, focus card 0.
    private void OnCollectionChanged(int count)
    {
        _libCount = count;
        if (!_active || _controller == null) return;
        if (_section == Section.Library && _libAwaitingLoad)
        {
            _libAwaitingLoad = false;
            if (count > 0) LibFocus(0, announce: true, interrupt: false);
        }
    }

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => _topNav != null && _topNav.Count > 0,
        Section.Tabs => _tabsNav != null && _tabsNav.Count > 0,
        Section.Deck => _deckCards.Length > 0,
        Section.Library => _libCount > 0 || _libAwaitingLoad,
        _ => false,
    };

    private void CycleSection(int dir)
    {
        // While holding a card, only cycle the groups you can place it into (Deck / Library) — Tab
        // shouldn't wander to the Top bar or Category tabs mid-drag.
        var order = _holding
            ? new[] { Section.Deck, Section.Library }
            : new[] { Section.Top, Section.Tabs, Section.Deck, Section.Library };
        int cur = Array.IndexOf(order, _section);
        if (cur < 0) cur = 0;
        for (int step = 1; step <= order.Length; step++)
        {
            var cand = order[((cur + dir * step) % order.Length + order.Length) % order.Length];
            if (SectionHasContent(cand)) { EnterSection(cand, announce: true, interruptPrior: true); return; }
        }
    }

    private void EnterSection(Section s, bool announce, bool interruptPrior)
    {
        // Fall through to a section that actually has content (e.g. empty deck → Library).
        if (!SectionHasContent(s))
        {
            if (SectionHasContent(Section.Library)) s = Section.Library;
            else if (SectionHasContent(Section.Deck)) s = Section.Deck;
            else if (SectionHasContent(Section.Tabs)) s = Section.Tabs;
            else if (SectionHasContent(Section.Top)) s = Section.Top;
            else return;
        }
        _section = s;
        _lineIndex = -1;
        switch (s)
        {
            case Section.Top: _topNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Tabs: _tabsNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Deck:
                if (announce) Say(_deckGroupName, interruptPrior);   // section name, then the card queues
                DeckFocus(0, announce, interrupt: false);
                break;
            case Section.Library:
                RefreshLibraryCount();
                if (_libCount > 0)
                {
                    _libAwaitingLoad = false;
                    if (announce) Say(_libGroupName, interruptPrior);
                    LibFocus(0, announce, interrupt: false);
                }
                else { _libAwaitingLoad = true; if (announce) Say($"{_libGroupName}, loading", interruptPrior); }
                break;
        }
    }

    private void ReAnnounceCurrent(bool interrupt)
    {
        switch (_section)
        {
            case Section.Top: _topNav?.RefocusCurrent(announce: true); break;
            case Section.Tabs: _tabsNav?.RefocusCurrent(announce: true); break;
            case Section.Deck: if (_deckIndex >= 0) ReadCard(Kind.Deck, interrupt); else EnterSection(Section.Deck, true, interrupt); break;
            case Section.Library: if (_libIndex >= 0) ReadCard(Kind.Library, interrupt); else EnterSection(Section.Library, true, interrupt); break;
        }
    }

    // ── Card groups (data nav) ──────────────────────────────────────────────────────────────────

    private void TickCardGroup(Kind kind)
    {
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { MoveCard(kind, +1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { MoveCard(kind, -1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { ReadLine(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { ReadLine(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.I)) { Inspect(kind); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (_holding) TryPlace(kind);
            else PickUp(kind);
        }
    }

    private void MoveCard(Kind kind, int dir)
    {
        _lineIndex = -1;
        if (kind == Kind.Deck)
        {
            if (_deckCards.Length == 0) return;
            int next = _deckIndex + dir;
            if (next < 0 || next >= _deckCards.Length) return;
            DeckFocus(next, announce: true);
        }
        else
        {
            if (_libCount == 0) return;
            int next = _libIndex + dir;
            if (next < 0 || next >= _libCount) return;
            var kv = dir > 0
                ? _controller!.CollectionDrawer.GetNextCard(_libCurrent, _libIndex)
                : _controller!.CollectionDrawer.GetPreviousCard(_libCurrent, _libIndex);
            _libCurrent = kv.Key;
            _libIndex = kv.Value;
            ReadCard(Kind.Library, interrupt: true);
        }
    }

    private void DeckFocus(int index, bool announce, bool interrupt = true)
    {
        if (index < 0 || index >= _deckCards.Length) { _deckIndex = -1; return; }
        _deckIndex = index;
        _lineIndex = -1;
        if (announce) ReadCard(Kind.Deck, interrupt);
    }

    private void LibFocus(int index, bool announce, bool interrupt = true)
    {
        if (_libCount == 0) { _libIndex = -1; return; }
        // Step to the requested index from index 0 (only used for the first focus; movement uses
        // GetNext/PreviousCard incrementally). GetNextCard(default, -1) yields curCollection[0].
        var kv = _controller!.CollectionDrawer.GetNextCard(default, -1);
        _libCurrent = kv.Key;
        _libIndex = kv.Value;
        for (int i = 0; i < index && _libIndex < _libCount - 1; i++)
        {
            kv = _controller.CollectionDrawer.GetNextCard(_libCurrent, _libIndex);
            _libCurrent = kv.Key; _libIndex = kv.Value;
        }
        _lineIndex = -1;
        if (announce) ReadCard(Kind.Library, interrupt);
    }

    private void RefreshDeck()
    {
        if (_controller?.DataDeck == null) { _deckCards = Array.Empty<ArchetypeDBCards>(); return; }
        var cat = _categorySelector != null ? _categorySelector.CurrentVal : CardCategory.All;
        try { _deckCards = CardSortHelper.SortDeck(_controller.DataDeck, cat); }
        catch { _deckCards = Array.Empty<ArchetypeDBCards>(); }
        if (_deckIndex >= _deckCards.Length) _deckIndex = _deckCards.Length - 1;
    }

    private void RefreshLibraryCount()
    {
        _libCount = _controller?.CollectionDrawer != null ? _controller.CollectionDrawer.GetCurrentCollectionAmount() : 0;
        if (_libIndex >= _libCount) _libIndex = _libCount - 1;
    }

    private ArchetypeDBCards? CurrentStack(Kind kind)
    {
        if (kind == Kind.Deck)
            return _deckIndex >= 0 && _deckIndex < _deckCards.Length ? _deckCards[_deckIndex] : (ArchetypeDBCards?)null;
        return _libIndex >= 0 && _libCount > 0 ? _libCurrent : (ArchetypeDBCards?)null;
    }

    private void ReadCard(Kind kind, bool interrupt)
    {
        var st = CurrentStack(kind);
        if (st is not { } stack || stack.IsDefault) return;
        _lines.Clear();
        _lineIndex = -1;
        int idx = kind == Kind.Deck ? _deckIndex : _libIndex;
        int total = kind == Kind.Deck ? _deckCards.Length : _libCount;
        _router.Speech.Say(Speech.Announce.Item(CardLabel(stack), description: null, index: idx + 1, total: total), interrupt);
    }

    // "Charizard ex, Stage 2 Pokémon, 1 in deck, 4 owned" — name+type from the printed card, plus the
    // counts a sighted player reads off the badges: how many are in the deck and how many you OWN
    // (TotalOwnedQuantity counts every printing of the card). A card you don't own at all reads "not
    // owned"; a deck that uses more copies than you own (legal to build, but you can't play it ranked)
    // reads "you don't own enough" — the missing flag the player couldn't see.
    private string CardLabel(ArchetypeDBCards stack)
    {
        var card = PreferredCard(stack);
        var row = card != null ? ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(card.CardID) : null;
        var name = row != null ? CardText.NameWithType(row) : stack.LocalizedCardName();

        int inDeck = stack.TotalDeckQuantity();
        int owned = SafeOwned(stack);

        var parts = new List<string> { name };
        if (inDeck > 0) parts.Add($"{inDeck} in deck");
        if (owned <= 0) parts.Add("not owned");
        else
        {
            parts.Add($"{owned} owned");
            if (inDeck > owned) parts.Add("you don't own enough");
        }
        return string.Join(", ", parts);
    }

    private static int SafeOwned(ArchetypeDBCards stack)
    {
        try { return stack.TotalOwnedQuantity(); } catch { return 0; }
    }

    private static ICardDataRow? PreferredCard(ArchetypeDBCards stack)
    {
        try { return stack.GetPreferredDisplayCard(); } catch { return null; }
    }

    private void ReadLine(int dir)
    {
        var st = CurrentStack(_section == Section.Deck ? Kind.Deck : Kind.Library);
        if (st is not { } stack || stack.IsDefault) return;
        if (_lines.Count == 0)
        {
            var card = PreferredCard(stack);
            var row = card != null ? ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(card.CardID) : null;
            if (row != null) _lines.AddRange(CardText.DetailLines(row));
        }
        int next = _lineIndex + dir;
        if (next < -1) return;
        if (next == -1) { _lineIndex = -1; _router.Speech.Say(CardLabel(stack), interrupt: true); return; }
        if (next >= _lines.Count) return;
        _lineIndex = next;
        _router.Speech.Say(_lines[_lineIndex], interrupt: true);
    }

    // ── Pick up → navigate → place (the proper drag) ────────────────────────────────────────────
    // Pickup records the card's DATA row (synchronous — no scroller realization, which could hang the
    // handler). Place calls AddCard/RemoveCard, exactly what DeckEditorCardDropTarget.OnDrop does, and
    // during the tutorial also fires the ftueDrop the drop would have, so the FTUE advances.

    private void PickUp(Kind kind)
    {
        var st = CurrentStack(kind);
        if (st is not { } stack || stack.IsDefault) return;

        var card = kind == Kind.Deck
            ? (TryGet(() => stack.GetPreferredDisplayCardForDeck()) ?? PreferredCard(stack))
            : PreferredCard(stack);
        if (card == null) { Say($"Can't pick up {stack.LocalizedCardName()}", interrupt: true); return; }

        _heldCard = card;
        _heldAction = kind == Kind.Deck ? CardEditorDrag.CardDropAction.Remove : CardEditorDrag.CardDropAction.Add;
        _heldFrom = _section;
        _heldName = stack.LocalizedCardName();
        _holding = true;
        HUBEditDeckController.IsEditorDragging = true;   // match the game's drag state
        Say($"Holding {_heldName}", interrupt: true);
    }

    private void TryPlace(Kind destination)
    {
        // A library card (Add) goes onto the Deck; a deck card (Remove) goes onto the Library.
        // Placing it back where it came from cancels.
        bool addToDeck = _heldAction == CardEditorDrag.CardDropAction.Add && destination == Kind.Deck;
        bool removeFromDeck = _heldAction == CardEditorDrag.CardDropAction.Remove && destination == Kind.Library;
        if (!addToDeck && !removeFromDeck)
        {
            if (destination == _ToKind(_heldFrom)) { CancelHold(announce: true); return; }
            var d = _heldAction == CardEditorDrag.CardDropAction.Add ? "the deck" : "the collection";
            Say($"Tab to {d} to place {_heldName}.", interrupt: true);
            return;
        }

        var card = _heldCard;
        var name = _heldName;
        var action = _heldAction;
        var from = _heldFrom;
        bool ftue = FTUEHook.FtueIsRunning;
        if (card == null) { CancelHold(announce: true); return; }

        if (action == CardEditorDrag.CardDropAction.Add &&
            !_controller!.CanAddCard(card, DeckEditorGameMode.CurrentGameMode))
        {
            // The game's own gate said no (deck full / copy limit / not owned). Report and stop.
            ClearHold();
            _section = from;
            Say($"Can't add {name} right now{FullDeckHint()}", interrupt: true);
            ReAnnounceCurrent(interrupt: false);
            return;
        }

        // The drop's real action — DeckEditorCardDropTarget.OnDrop calls exactly this.
        if (action == CardEditorDrag.CardDropAction.Add) _controller!.AddCard(card, focusOnAddedCard: true);
        else _controller!.RemoveCard(card);

        // During the tutorial, fire the same ftueDrop the drop would have fired, so the FTUE advances.
        if (ftue) CompleteFtueDrop();

        ClearHold();
        RefreshDeck();
        RefreshLibraryCount();

        _section = from;
        var verb = action == CardEditorDrag.CardDropAction.Add ? "Added" : "Removed";
        Say($"{verb} {name}{NewCountMessage(name)}", interrupt: true);
        ReAnnounceCurrent(interrupt: false);
    }

    private string FullDeckHint()
    {
        try
        {
            var deck = _controller?.DataDeck?.WorkingDeck;
            int max = ManagerSingleton<DeckValidationManager>.instance.GetDeckSizeForGameMode(DeckEditorGameMode.CurrentGameMode);
            if (deck != null && deck.size >= max) return ", the deck is full";
        }
        catch { }
        return string.Empty;
    }

    // The tutorial arms a drop target with an ftueDrop callback (Unfocus + SaveInventory +
    // indicator.Unblock) that OnDrop fires AFTER AddCard/RemoveCard to advance the FTUE. We call
    // AddCard/RemoveCard directly, so replicate that tail on whichever target the FTUE armed — else
    // the deck edits but the tutorial freezes (a battle-gotcha twin; see [[project-battle-architecture]]).
    private void CompleteFtueDrop()
    {
        try
        {
            foreach (var dt in Resources.FindObjectsOfTypeAll<DeckEditorCardDropTarget>())
            {
                if (HasFtueDropField?.GetValue(dt) is not bool armed || !armed) continue;
                var ftueDrop = FtueDropField?.GetValue(dt) as UnityEngine.Events.UnityAction;
                ftueDrop?.Invoke();                          // Unfocus + SaveInventory + indicator.Unblock
                HasFtueDropField?.SetValue(dt, false);
                FtueDropField?.SetValue(dt, null);
                if (OrigParentField?.GetValue(dt) is Transform orig && orig != null) dt.transform.SetParent(orig);
                Log.Info("DeckEditor: fired FTUE drop advance");
                return;
            }
            Log.Info("DeckEditor: place during FTUE but no armed drop target found");
        }
        catch (Exception ex) { Log.Warn($"DeckEditor CompleteFtueDrop failed: {ex.Message}"); }
    }

    private string NewCountMessage(string name)
    {
        foreach (var s in _deckCards)
            if (string.CompareOrdinal(s.LocalizedCardName(), name) == 0)
                return $", {s.TotalDeckQuantity()} in deck";
        return ", 0 in deck";
    }

    private void CancelHold(bool announce)
    {
        var name = _heldName;
        ClearHold();
        if (announce) Say($"Cancelled holding {name}", interrupt: true);
    }

    private void ClearHold()
    {
        _holding = false;
        _heldCard = null;
        _heldName = string.Empty;
        HUBEditDeckController.IsEditorDragging = false;
    }

    private static Kind _ToKind(Section s) => s == Section.Deck ? Kind.Deck : Kind.Library;

    // ── Tutorial guidance ───────────────────────────────────────────────────────────────────────
    // The deck step highlights a specific card (FTUEDeckEditorIndicator) — its focused element is
    // reparented under a Blocker so we can't walk to its CardStubParts, but the indicator carries the
    // card id + element type. Announce what to do and jump the cursor onto that card so the user can
    // just press Enter to pick it up; completing the action advances the FTUE via CompleteFtueDrop.
    private void UpdateFtueGuidance()
    {
        if (!FTUEHook.FtueIsRunning) { _lastFtueCardId = string.Empty; ClearFtueFocus(); return; }

        var cardId = FTUEHook.DeckEditorFtueCardId;
        if (!string.IsNullOrEmpty(cardId))
        {
            // Card step: jump to the target card; the user completes it via pickup/place (or I).
            ClearFtueFocus();
            if (cardId == _lastFtueCardId) return;
            _lastFtueCardId = cardId!;
            var row = ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(cardId);
            var name = row != null ? row.LocalizedCardName : cardId;
            var element = FTUEHook.DeckEditorFtueElement ?? "";
            var verb = FtueVerb(element);
            Log.Info($"DeckEditor FTUE target: element={element} card={cardId} ({name})");
            bool jumped = JumpToCard(cardId!);
            Say(jumped ? $"Tutorial: {verb} this card" : $"Tutorial: {verb} {name}", interrupt: false);
            return;
        }

        // Non-card step (category tab, Back, craft button, …): the focused control is reparented
        // under a Blocker and isn't in our sections, so present it directly for Enter — the same
        // real control a sighted player clicks. Mirrors HUBShopScreen.RebuildForFtue.
        _lastFtueCardId = string.Empty;
        var focus = FTUEHook.FocusedElement;
        if (focus == null || !focus.isActiveAndEnabled) { ClearFtueFocus(); return; }
        if (focus == _ftueFocusShown) return;
        _ftueFocusShown = focus;
        _ftueFocusActive = true;
        var el = FTUEHook.DeckEditorFtueElement ?? "";
        _ftueFocusNav!.RadioToggleMode = focus is Toggle;
        _ftueFocusNav.SetItems(new List<Selectable> { focus });
        Log.Info($"DeckEditor FTUE focus (non-card): element={el} name={focus.gameObject.name}");
        Say(FtueFocusInstruction(el, focus), interrupt: true);
        _ftueFocusNav.FocusFirst(announce: false, interruptPrior: false);   // select; the line above is the speech
    }

    private void ClearFtueFocus()
    {
        _ftueFocusActive = false;
        _ftueFocusShown = null;
    }

    private static string FtueFocusInstruction(string element, Selectable focus) => element switch
    {
        "PokemonTab" => "Tutorial: select the Pokémon category",
        "TrainerTab" => "Tutorial: select the Trainer category",
        "EnergyTab" => "Tutorial: select the Energy category",
        "DeckCountTab" => "Tutorial: open the deck count",
        "Home" => "Tutorial: go to Home",
        "Back" => "Tutorial: go back",
        _ => $"Tutorial: use {KeyboardNavigator.DefaultLabel(focus)}",
    };

    private static string FtueVerb(string element) => element switch
    {
        "LibraryCard" or "AddCard" => "add",
        "DeckCard" or "RemoveCard" => "remove",
        "OpenableLibraryCard" => "open",
        _ => "select",
    };

    // Move the cursor onto the tutorial's target card (announcing its section + the card). Returns
    // true if it found and focused it. Deck first (small, by archetype membership), then the library
    // (by archetype index in the current filter).
    private bool JumpToCard(string cardId)
    {
        var row = ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(cardId);
        if (row == null) return false;
        for (int i = 0; i < _deckCards.Length; i++)
        {
            bool has;
            try { has = _deckCards[i].HasCard(row); } catch { has = false; }
            if (has)
            {
                _section = Section.Deck;
                Say(_deckGroupName, interrupt: false);
                DeckFocus(i, announce: true, interrupt: false);
                return true;
            }
        }
        if (_controller?.CollectionDrawer != null)
        {
            int idx = _controller.CollectionDrawer.GetCardIndex(row.ArchetypeID);
            if (idx >= 0)
            {
                RefreshLibraryCount();
                if (idx < _libCount)
                {
                    _section = Section.Library;
                    Say(_libGroupName, interrupt: false);
                    LibFocus(idx, announce: true, interrupt: false);
                    return true;
                }
            }
        }
        return false;
    }

    // ── Inspect (the second method: the game's real card-detail overlay) ────────────────────────

    private void Inspect(Kind kind)
    {
        if (_holding) { Say("Place or cancel the held card first.", interrupt: true); return; }
        var st = CurrentStack(kind);
        if (st is not { } stack || stack.IsDefault) return;
        var card = PreferredCard(stack);
        if (card == null) return;

        // Open the overlay exactly the way a click does — invoke the realized stub's cardButton.
        // (AddCardOverlayScreen activates itself off the overlay's Setup hook.)
        if (kind == Kind.Deck)
        {
            var stub = _controller!.DeckPanel.GetCardButton(card.CardID);
            if (stub != null && stub.CardButton != null) stub.CardButton.onClick.Invoke();
            else Say("Can't open card details", interrupt: true);
        }
        else
        {
            // 1.5.0 removed PopulateCollectionDrawer.GetCardButton(id, cb); ScrollToCard(id, cb)
            // is the replacement — scrolls the (virtualized) collection to the card and hands back
            // its realized CardStubParts, whose CardButton we click to open the detail overlay.
            _controller!.CollectionDrawer.ScrollToCard(card.CardID, stub =>
            {
                if (stub != null && stub.CardButton != null) stub.CardButton.onClick.Invoke();
                else Say("Can't open card details", interrupt: true);
            });
        }
    }

    // ── Intro / helpers ─────────────────────────────────────────────────────────────────────────

    private void AnnounceIntro()
    {
        if (_controller?.DataDeck?.WorkingDeck == null) { Say(Name, interrupt: true); return; }
        var deck = _controller.DataDeck.WorkingDeck;
        int max = 60;
        try { max = ManagerSingleton<DeckValidationManager>.instance.GetDeckSizeForGameMode(DeckEditorGameMode.CurrentGameMode); }
        catch { }
        bool valid = false;
        try { valid = _controller.DataDeck.IsValidQuick(DeckEditorGameMode.CurrentGameMode); } catch { }
        var name = string.IsNullOrEmpty(deck.deckName) ? "deck" : deck.deckName;
        _router.Speech.Say($"Deck editor. {name}. {deck.size} of {max} cards, {(valid ? "valid" : "invalid")}.", interrupt: true);
        _router.SetCurrentTitle(this, Name);
    }

    // Back out of the editor to DECKS. Drives the controller's own exit (HideScreen →
    // navigation.GoToPreviousScreen), gated by CanExit — an invalid deck shows the game's own
    // "leave anyway?" validation popup (which runs the exit on confirm); a valid deck leaves at once.
    private void DoBack()
    {
        if (_controller == null) return;
        try
        {
            var hide = AccessTools.Method(typeof(HUBEditDeckController), "HideScreen");
            if (hide == null) { Log.Warn("DeckEditor: HideScreen not found"); return; }
            Action nav = () => hide.Invoke(_controller, null);
            if (_controller.CanExit(nav)) nav();   // valid → leave now; invalid → CanExit shows the popup
        }
        catch (Exception ex) { Log.Warn($"DeckEditor back failed: {ex.Message}"); }
    }

    private void Say(string text, bool interrupt) => _router.Speech.Say(text, interrupt);

    // Is the deck editor still the HUB's current screen? Authoritative source is the HUB navigation's
    // currentScreen; a hidden GameObject is a secondary signal. Defaults to true when unreadable so we
    // never falsely deactivate a live editor.
    private bool DeckEditorIsCurrentScreen()
    {
        if (_controller == null) return false;
        try
        {
            var nav = ManagerSingleton<RainierManager>.instance?._mainMenuController?.navigation;
            if (nav != null && nav.currentScreen != null && !ReferenceEquals(nav.currentScreen, _controller))
                return false;
        }
        catch { }
        try { if (!_controller.gameObject.activeInHierarchy) return false; } catch { }
        return true;
    }

    // Resolve a game loc ID to its localized text (so section names match what's on screen in any
    // language); fall back to a plain label if the table doesn't have it.
    private static string Loc(string id, string fallback)
    {
        try
        {
            var s = LocalizationManager.Instance?.GetString(id);
            if (!string.IsNullOrWhiteSpace(s) && !s!.Contains("(Missing)")) return s;
        }
        catch { }
        return fallback;
    }

    // The category toggle's only visible text is its COUNT badge (e.g. "13"), so reading the text gave
    // just a number. Speak the category NAME from the filter tab's loc id, with the count as a
    // qualifier: "Pokémon, 13 cards, selected".
    private string CategoryLabel(Selectable s)
    {
        var name = CategoryName(s);
        var tmp = s.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        var count = tmp != null && !string.IsNullOrWhiteSpace(tmp.text) ? tmp.text!.Trim() : null;
        var state = (s is Toggle t && t.isOn) ? "selected" : "not selected";

        var parts = new List<string> { !string.IsNullOrWhiteSpace(name) ? name! : (count ?? s.gameObject.name) };
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(count)) parts.Add($"{count} cards");
        parts.Add(state);
        return string.Join(", ", parts);
    }

    // The localized name of the category whose tab toggle this is (the game's filter-tab FilterName).
    private string? CategoryName(Selectable s)
    {
        try
        {
            if (_categorySelector == null) return null;
            foreach (var tab in _categorySelector.AllTabs)
                if (tab?.Toggle != null && ReferenceEquals(tab.Toggle.MyToggle, s)) return LocName(tab.FilterName);
        }
        catch { }
        return null;
    }

    private static string? LocName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var v = TPCI.Localization.LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(v) || v!.Contains("(Missing)")) return null;
        return v.Trim();
    }

    // ── Sort / filter popup (modal) ───────────────────────────────────────────────────────────

    // Drive the modal sort/filter mode while a popup is open; returns true to consume the frame so the
    // section nav and Tab stay inert. Escape closes the popup via its own Hide().
    private bool TickSortMode()
    {
        var popup = ActiveSortPopup();
        if (popup == null)
        {
            if (_sortMode) { _sortMode = false; _sortPopup = null; ReAnnounceCurrent(interrupt: true); }
            return false;
        }
        if (!_sortMode || !ReferenceEquals(_sortPopup, popup)) EnterSortMode(popup);

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)
            || (UnityEngine.Input.GetKeyDown(KeyCode.Backspace) && !IsTextInputFocused()))
        {
            try { popup.Hide(); } catch { }   // ExitSortMode runs next tick once the popup is gone
            return true;
        }
        _sortNav?.Tick();
        return true;
    }

    // The open sort or filter popup (mutually exclusive), or null when both are closed.
    private AnimatedSetActive? ActiveSortPopup()
    {
        if (_sortAnim != null && _sortAnim.gameObject.activeInHierarchy) return _sortAnim;
        if (_filterAnim != null && _filterAnim.gameObject.activeInHierarchy) return _filterAnim;
        return null;
    }

    private void EnterSortMode(AnimatedSetActive popup)
    {
        _sortMode = true;
        _sortPopup = popup;
        _sortNav ??= new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Sort" };
        var items = popup.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => (s is Toggle || s is Button) && s.isActiveAndEnabled && s.IsInteractable())
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();
        _sortNav.SetItems(items);
        Say(ReferenceEquals(popup, _filterAnim) ? "Filter" : "Sort by", interrupt: true);
        _sortNav.FocusFirst(announce: true, interruptPrior: false);
    }

    private static AnimatedSetActive? ReflectAnim(object? instance, string name)
    {
        if (instance == null) return null;
        try { return AccessTools.Field(instance.GetType(), name)?.GetValue(instance) as AnimatedSetActive; }
        catch { return null; }
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
        _lastFtueTarget = FTUEHook.FocusedElement;
    }

    private static bool IsTextInputFocused()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
    }

    private static T? TryGet<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
