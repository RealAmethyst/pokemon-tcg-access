using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.Features.DeckManager;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// DECKS screen (the deck list / gateway to the editor). Driver: HUBDeckSelectController (a
// HUBScreenController, hook OnActivate/OnDeactivate). Sections, top-to-bottom:
//   • Top    — HUB tab strip (HOME / DECKS / …) + ⋮, same discovery as Home/Shop.
//   • Tabs   — deck grouping RECENTS / FAVORITES / ALL (DeckGroupingToggleGroup toggles).
//   • Decks  — CREATE A DECK + the deck tiles (DeckSelectEntry). Focusing a tile SELECTS it (the
//              game's own ReportDeckOptionClicked → updates the detail panel), and reads the deck
//              name + valid/invalid + active — navigate by data over deckList.ActiveDeckEntries.
//   • Detail — the selected deck's panel (DeckPanel_L): EDIT DECK / SET ACTIVE / TEST / DUPLICATE /
//              EXPORT / DELETE / rename / favorite. EDIT DECK opens the editor (HUBEditDeckScreen).
//
// Phase 1: the above (reaches the editor + the deck actions). Deferred: the format toggle
// (STANDARD/Expanded), SHOW VALID ONLY, and the search box — tracked follow-ups.
internal sealed class HUBDeckSelectScreen : IScreenHandler
{
    private enum Section { Top, Tabs, Decks, Detail }

    private static HUBDeckSelectScreen? _instance;

    private readonly ScreenRouter _router;
    private HUBDeckSelectController? _controller;
    private bool _active;

    private Section _section = Section.Decks;
    private KeyboardNavigator? _topNav;
    private KeyboardNavigator? _tabsNav;
    private KeyboardNavigator? _detailNav;
    private Button? _setActiveBtn;    // the live SET ACTIVE button (only for a non-active valid deck), matched by identity for labeling
    private Button? _renameBtn;       // the ✎ pencil (DeckPanel_L.editDeckNameButton); runtime-wired, so labelled by identity not by onClick
    private Button? _createButton;   // "CREATE A DECK", the deck grid's leftmost top item

    // Challenge-deck-selection mode: the deck screen reached from a friend battle request. The
    // controller shows returnToFriendJoinMatchButton (active only then), and LeaveDeckManager() routes
    // back to the challenge through it. We surface that button + make Escape return to the challenge
    // (instead of opening the ⋮ menu, which has no "back" otherwise).
    private Button? _returnButton;
    private bool _challengeMode;

    private readonly List<DeckSelectEntry> _entries = new();
    private int _deckIndex;   // -1 = the synthetic "Create a deck" item, 0..n = deck tiles
    private int _subIndexAtPause = -1;   // saved Top/Tabs/Detail position across a menu/popup pause

    // Tutorial: the game reparents the FTUE-focused element under a Blocker; since this handler owns
    // the screen, we present that element ourselves (the global fallback only fires with no handler).
    private KeyboardNavigator? _ftueNav;
    private Selectable? _ftueShown;

    public string Name => "Decks";

    public HUBDeckSelectScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _ftueNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Tutorial" };
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new HUBDeckSelectScreen(router);

        var activate = AccessTools.Method(typeof(HUBDeckSelectController), "OnActivate");
        if (activate == null) { Log.Error("HUBDeckSelectScreen: OnActivate not found"); return; }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBDeckSelectScreen), nameof(Activate_Postfix))));
        var deactivate = AccessTools.Method(typeof(HUBDeckSelectController), "OnDeactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(HUBDeckSelectScreen), nameof(Deactivate_Postfix))));

        Log.Info("HUBDeckSelectScreen installed");
    }

    private static void Activate_Postfix(HUBDeckSelectController __instance)
    {
        if (_instance == null) return;
        _instance._controller = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
    }

    private static void Deactivate_Postfix(HUBDeckSelectController __instance)
    {
        if (_instance == null || _instance._controller != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._controller = null;
    }

    public void OnEnter()
    {
        Rebuild();
        if (PresentFtueIfActive(interrupt: true)) return;
        _router.Speech.Say(Name, interrupt: true);
        EnterSection(Section.Decks, announce: true, interruptPrior: false);
    }

    public void OnResume()
    {
        Rebuild();
        if (PresentFtueIfActive(interrupt: true)) return;
        RestoreSubFocus();                 // returning from the ⋮ menu / a popup → exact prior position
        ReAnnounce(interrupt: true);
    }

    public void OnExit()
    {
        // Remember the within-section position so a menu/popup round-trip lands back on it. The deck
        // grid already persists via _deckIndex; this covers the Top strip / grouping Tabs / Detail panel.
        _subIndexAtPause = _section switch
        {
            Section.Top => _topNav?.Index ?? -1,
            Section.Tabs => _tabsNav?.Index ?? -1,
            Section.Detail => _detailNav?.Index ?? -1,
            _ => -1,
        };
    }

    // Restore the saved sub-section index into the freshly-rebuilt navigator (Rebuild creates new
    // KeyboardNavigators with no memory of the prior index). ReAnnounce then re-selects + reads it.
    private void RestoreSubFocus()
    {
        if (_subIndexAtPause < 0) return;
        KeyboardNavigator? nav = _section switch
        {
            Section.Top => _topNav,
            Section.Tabs => _tabsNav,
            Section.Detail => _detailNav,
            _ => null,
        };
        if (nav != null && _subIndexAtPause < nav.Count)
            nav.FocusIndex(_subIndexAtPause, announce: false, interrupt: true);
    }

    public void RespeakAll() { Rebuild(); ReAnnounce(interrupt: false); }

    public string StateSignature()
    {
        if (_controller == null) return "Decks|dead";
        return $"Decks|{_section}|{_deckIndex}|{_entries.Count}";
    }

    public void Tick()
    {
        if (_controller == null) { _active = false; _router.Deactivate(this); return; }
        if (!Application.isFocused) return;

        // Forced tutorial focus (reparented element) — present + drive it; nothing else this tick.
        if (PresentFtueIfActive(interrupt: false)) { _ftueNav?.Tick(); return; }

        // Escape: in challenge-deck-selection it returns to the battle request (the game's own
        // LeaveDeckManager → returnToFriendJoinMatchButton); otherwise it opens the ⋮ menu (the deck
        // list has no other "back" — the editor is a separate screen).
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
        {
            if (_challengeMode && _controller != null) _controller.LeaveDeckManager();
            else HUBOverflowMenuScreen.TryOpen();
            return;
        }

        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            CycleSection(shift ? -1 : +1);
            return;
        }

        switch (_section)
        {
            case Section.Top: TickStrip(_topNav, null, Section.Tabs); break;
            case Section.Tabs: TickStrip(_tabsNav, Section.Top, Section.Decks); break;
            case Section.Detail: TickDetail(); break;
            case Section.Decks: TickDecks(); break;
        }
    }

    // ── Sections ──────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        if (_controller == null) return;

        // Top strip (HOME / DECKS / … + ⋮) — same discovery as Home/Shop.
        var topItems = new List<Selectable>();
        var topController = UnityEngine.Object.FindAnyObjectByType<HUBTopController_L>();
        if (topController != null)
        {
            var navGroups = ReadField<System.Collections.IEnumerable>(topController, "persistentNavButtons");
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
        topItems = topItems.OrderBy(s => ReadingOrder(s.transform)).ToList();

        // Challenge mode: surface the "return to the battle request" button (active only in this mode)
        // at the front of the top strip so it's reachable, and remember we're in challenge mode so
        // Escape goes back to the challenge.
        _returnButton = ReadField<Button>(_controller, "returnToFriendJoinMatchButton");
        _challengeMode = _returnButton != null && _returnButton.isActiveAndEnabled;
        if (_challengeMode) topItems.Insert(0, _returnButton!);

        _topNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, AllowVertical = false, AllowHorizontal = true };
        _topNav.SetItems(topItems, TopLabel);
        _topNav.AllowVertical = false; _topNav.AllowHorizontal = true;   // strip: Left/Right within, Up/Down escapes sections

        // Grouping tabs (RECENTS / FAVORITES / ALL).
        var grouping = ReadField<Component>(_controller, "deckGroupingToggleGroup");
        var groupToggles = grouping != null
            ? grouping.GetComponentsInChildren<Toggle>(includeInactive: false)
                .Where(t => t.isActiveAndEnabled).Cast<Selectable>()
                .OrderBy(s => ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        _tabsNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, RadioToggleMode = true, GroupName = "Deck grouping" };
        _tabsNav.SetItems(groupToggles, ToggleLabel);
        _tabsNav.AllowVertical = false; _tabsNav.AllowHorizontal = true;

        RebuildDetail();

        // CREATE A DECK — read the game's own create-deck prefab button (DeckSelectList.createDeckPrefab
        // → GetComponentInChildren<Button>, the exact button the game wires to CreateNewDeckButtonActions).
        // A game update moved/restructured that prefab so the old "scan deckSelectGrid for a button not
        // inside a DeckSelectEntry" stopped finding it, and the button vanished from nav. (Fallback: the
        // old grid scan, in case createDeckPrefab is ever unreadable.)
        _createButton = null;
        var deckListObj = CallMethod<object>(_controller, "GetDeckList");
        if (deckListObj != null)
        {
            var createPrefab = ReadField<GameObject>(deckListObj, "createDeckPrefab");
            if (createPrefab != null) _createButton = createPrefab.GetComponentInChildren<Button>(includeInactive: true);
        }
        if (_createButton == null)
        {
            var grid = ReadField<Component>(_controller, "deckSelectGrid");
            if (grid != null)
                _createButton = grid.GetComponentsInChildren<Button>(includeInactive: false)
                    .FirstOrDefault(b => b.GetComponentInParent<DeckSelectEntry>() == null);
        }
        Log.Info($"Decks: create button = {(_createButton != null ? $"{_createButton.gameObject.name} (active={_createButton.isActiveAndEnabled})" : "NOT FOUND")}");

        RefreshEntries();
    }

    // Detail panel = a flat vertical LIST of deck actions, in reading order:
    //   favorite ★ · rename ✎ · options ⋮ · SET ACTIVE · CUSTOMIZE · EDIT DECK · TEST DECK.
    // (The cards below are content — CardStubParts, excluded.) It's a menu, so we navigate it as a
    // list (Up/Down through every option); the old 2-D spatial nav made Left/Right jump to unexpected
    // options on this irregular header-row + full-width-button + action-row layout.
    //
    // Re-scanned on every entry to Section.Detail, NOT once at Rebuild(): the panel reflects the
    // currently-selected deck, and SET ACTIVE is only a live (active) button for a non-active, valid
    // deck. The active deck — which GetDefaultDeckEntry() picks as the default selection on entering
    // DECKS — hides it (SetActiveDeckButtonParts.SetState(DISABLED) → activeState.SetActive(false)),
    // so a one-shot scan would capture the active deck's panel and permanently miss SET ACTIVE for
    // every other deck. The selection→panel update is synchronous, so the live hierarchy is correct
    // by the time the user Tabs into the section.
    private void RebuildDetail()
    {
        if (_controller == null) return;
        var panel = CallMethod<Component>(_controller, "GetDeckPanel");
        var items = new List<Selectable>();
        if (panel != null)
        {
            items = panel.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled
                            && s.GetComponentInParent<CardStubParts>() == null)
                .ToList();

            // Guarantee SET ACTIVE is present by pulling the game's own setActiveButton reference from
            // SetActiveDeckButtonParts, rather than trusting the generic descendant scan to reach it
            // (it lives in a nested state-swapper that toggles activeState on/off per deck). Only added
            // when it's the live, clickable button — i.e. a non-active valid deck.
            var sadbp = ReadField<Component>(panel, "setActiveDeckButtonParts");
            var setBtn = sadbp != null ? ReadField<Button>(sadbp, "setActiveButton") : null;
            _setActiveBtn = setBtn != null && setBtn.isActiveAndEnabled ? setBtn : null;
            if (_setActiveBtn != null && !items.Contains(_setActiveBtn)) items.Add(_setActiveBtn);

            // The ✎ rename pencil (DeckPanel_L.editDeckNameButton). It's an icon with its onClick added
            // at runtime (OnEnable → AddListener(OpenDeckRenameMenu)), so UiLabels.ByAction — which reads
            // only prefab-persistent onClick entries — can't see it. Grab it by identity to give it the
            // same localized "Rename deck" label the editor's pencil gets. The generic scan already
            // includes it when active; this just records the reference for DetailLabel.
            _renameBtn = ReadField<Button>(panel, "editDeckNameButton");
        }
        items = items.OrderBy(s => ReadingOrder(s.transform)).ToList();
        _detailNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Deck options" };
        _detailNav.SetItems(items, DetailLabel);
        _detailNav.ForceFlatVertical();   // predictable Up/Down list, not a confusing spatial grid
        Log.Info($"Decks detail: {items.Count} options [{string.Join(", ", items.Select(s => s.gameObject.name))}]");
    }

    // The SET ACTIVE button is matched by identity (the game's own setActiveButton reference), not by
    // name/text. Its clickable target can carry no worded label of its own (the "SET ACTIVE" text sits
    // on a sibling dynamic-button state object), so if the text-scrape finds no real word, name it
    // explicitly. Every other option keeps the default text-scrape label.
    private string DetailLabel(Selectable s)
    {
        if (_setActiveBtn != null && s == _setActiveBtn)
        {
            var baseLabel = KeyboardNavigator.ResolveBaseLabel(s);
            if (!baseLabel.Any(char.IsLetter) || baseLabel.Equals("button", StringComparison.OrdinalIgnoreCase))
                return "Set active";
        }
        // The runtime-wired rename pencil — name it with the game's own rename label (English fallback).
        if (_renameBtn != null && s == _renameBtn)
            return Util.UiLabels.Localize("ui_deck_options_rename", "Rename deck");
        return KeyboardNavigator.DefaultLabel(s);
    }

    private void RefreshEntries()
    {
        _entries.Clear();
        var list = CallMethod<object>(_controller!, "GetDeckList");
        var active = list != null ? ReadMember<System.Collections.IEnumerable>(list, "ActiveDeckEntries") : null;
        if (active != null)
            foreach (var e in active)
                if (e is DeckSelectEntry entry) _entries.Add(entry);
        if (_deckIndex >= _entries.Count) _deckIndex = _entries.Count - 1;
    }

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => _topNav != null && _topNav.Count > 0,
        Section.Tabs => _tabsNav != null && _tabsNav.Count > 0,
        Section.Decks => true,                       // always has at least "Create a deck"
        Section.Detail => _detailNav != null && _detailNav.Count > 0,
        _ => false,
    };

    private void CycleSection(int dir)
    {
        var order = new[] { Section.Top, Section.Tabs, Section.Decks, Section.Detail };
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
        // Re-scan the detail panel against the live deck selection before entering it, so SET ACTIVE
        // appears for a non-active valid deck (it's hidden for the active deck, the default selection).
        if (s == Section.Detail) RebuildDetail();
        if (!SectionHasContent(s)) s = Section.Decks;
        _section = s;
        switch (s)
        {
            case Section.Top: _topNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Tabs: _tabsNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Detail: _detailNav?.FocusFirst(announce, interruptPrior); break;
            case Section.Decks: DeckFocus(_deckIndex < -1 || _deckIndex >= _entries.Count ? (_entries.Count > 0 ? 0 : -1) : _deckIndex, announce, interruptPrior); break;
        }
    }

    private void ReAnnounce(bool interrupt)
    {
        switch (_section)
        {
            case Section.Top: _topNav?.RefocusCurrent(announce: true); break;
            case Section.Tabs: _tabsNav?.RefocusCurrent(announce: true); break;
            case Section.Detail: _detailNav?.RefocusCurrent(announce: true); break;
            case Section.Decks: AnnounceDeck(interrupt); break;
        }
    }

    // ── Deck grid (data nav; focus selects the deck → updates the detail panel) ──────────────────

    private void TickDecks()
    {
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { MoveDeckSpatial(1, 0); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { MoveDeckSpatial(-1, 0); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            // Up at the top row escapes to the grouping tabs (→ then the top strip), like HOME.
            if (!MoveDeckSpatial(0, 1)) EnterSection(Section.Tabs, announce: true, interruptPrior: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { MoveDeckSpatial(0, -1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (_deckIndex < 0) CallMethod<object>(_controller!, "CreateNewDeckButtonActions");        // CREATE A DECK
            else if (_deckIndex < _entries.Count) OpenEditor(_entries[_deckIndex].storedDeck);          // open the deck in the editor
        }
    }

    // Move within the deck grid spatially (4-wide layout + the CREATE tile). Returns false when
    // there's nothing that way, so Up at the top row escapes upward.
    private bool MoveDeckSpatial(int dx, int dy)
    {
        bool hasCreate = _createButton != null && _createButton.isActiveAndEnabled;
        var pos = new List<Vector2>();
        if (hasCreate) pos.Add(KeyboardNavigator.WorldCenter(_createButton!));
        foreach (var e in _entries) pos.Add(Center(e.transform));
        if (pos.Count == 0) return false;
        int cur = (hasCreate ? 1 : 0) + _deckIndex;
        if (cur < 0 || cur >= pos.Count) cur = 0;
        int best = SpatialPick(pos, cur, dx, dy);
        if (best < 0) return false;
        DeckFocus(best - (hasCreate ? 1 : 0), announce: true, interrupt: true);
        return true;
    }

    // Open the editor for a deck — the controller's own OpenDeckEditorForDeck (private) does the full
    // SetupForDeck/Activate; driving it directly is more reliable than the panel's EDIT DECK button,
    // which depends on the panel having committed the selection.
    private void OpenEditor(object deck)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var m = typeof(HUBDeckSelectController).GetMethod("OpenDeckEditorForDeck", flags);
            if (m != null) m.Invoke(_controller, new[] { deck });
            else Log.Warn("Decks: OpenDeckEditorForDeck not found");
        }
        catch (Exception ex) { Log.Warn($"Decks: open editor failed: {ex.Message}"); }
    }

    // The deck-options panel is a flat vertical list of deck actions (see RebuildDetail): Up/Down
    // step through every option, Enter/Space activates. Arrows STAY within the options (no edge-
    // escape — it's a self-contained menu); Tab / Shift+Tab moves between sections and Escape opens
    // the ⋮ menu, both consumed in Tick() before we get here.
    private void TickDetail() => _detailNav?.Tick();

    // Horizontal strip (top nav / grouping tabs): Left/Right within; Up/Down escape to the section
    // above/below — the HOME-style arrow escape so the top strip is reachable without Tab.
    private void TickStrip(KeyboardNavigator? nav, Section? up, Section? down)
    {
        if (nav == null) return;
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (up.HasValue) EnterSection(up.Value, announce: true, interruptPrior: true); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (down.HasValue) EnterSection(down.Value, announce: true, interruptPrior: true); return; }
        nav.Tick();
    }

    private static Vector2 Center(Transform t)
    {
        if (t is RectTransform rt) { var c = new Vector3[4]; rt.GetWorldCorners(c); return (c[0] + c[2]) * 0.5f; }
        var p = t.position; return new Vector2(p.x, p.y);
    }

    private static int SpatialPick(IReadOnlyList<Vector2> pos, int cur, float dx, float dy)
    {
        var from = pos[cur];
        int best = -1; float bestScore = float.MaxValue;
        for (int i = 0; i < pos.Count; i++)
        {
            if (i == cur) continue;
            float ox = pos[i].x - from.x, oy = pos[i].y - from.y;
            float along = ox * dx + oy * dy;
            if (along <= 2f) continue;
            float perp = Mathf.Abs(ox * dy - oy * dx);
            float score = along + perp * 2.5f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private void DeckFocus(int index, bool announce, bool interrupt)
    {
        _deckIndex = index;
        if (index >= 0 && index < _entries.Count)
        {
            // Select the deck the game's own way so the detail panel reflects it.
            try { _entries[index].ReportDeckOptionClicked(); } catch (Exception ex) { Log.Warn($"Decks: select failed: {ex.Message}"); }
        }
        if (announce) AnnounceDeck(interrupt);
    }

    private void AnnounceDeck(bool interrupt)
    {
        if (_deckIndex < 0)
        {
            _router.Speech.Say(Loc("deck_manager_create_a_deck", "Create a deck"), interrupt);
            return;
        }
        if (_deckIndex >= _entries.Count) return;
        var entry = _entries[_deckIndex];
        var deck = entry.storedDeck;
        var label = entry.DeckName;
        bool valid = false;
        try { valid = deck.IsValid(HUBGameModeController.gameMode); } catch { }
        var status = valid ? "valid" : "invalid";

        // A deck can be the active deck for more than one format at once — the game shows one coloured
        // check per mode on the tile (e.g. one deck active for Standard, another for Expanded). Say
        // exactly which format(s): "active for Standard", "active for Standard and Expanded" — so two
        // decks each active for a different format don't both read a bare, ambiguous "active". Mode
        // names come from the game's own GetGameModeLocString; a mode with no player-facing name (NONE,
        // Speed, …) falls back to a plain "active".
        var modeNames = new List<string>();
        bool anyActive = false;
        try
        {
            foreach (var mode in HUBGameModeController.GetOrderedGameModes())
            {
                if (!HUBDeckSelectController.IsActiveDeckForMode(mode, deck)) continue;
                anyActive = true;
                var name = Loc(HUBGameModeController.GetGameModeLocString(mode), "");
                if (!string.IsNullOrWhiteSpace(name)) modeNames.Add(name.Trim());
            }
        }
        catch { }
        if (modeNames.Count > 0) status += ", active for " + Conjoin(modeNames);
        else if (anyActive) status += ", active";

        _router.Speech.Say(Speech.Announce.Item($"{label}, {status}", description: null, index: _deckIndex + 1, total: _entries.Count), interrupt);
    }

    // Join names for speech: "A", "A and B", "A, B and C".
    private static string Conjoin(List<string> parts)
    {
        if (parts.Count <= 1) return parts.Count == 1 ? parts[0] : "";
        if (parts.Count == 2) return parts[0] + " and " + parts[1];
        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
    }

    // ── FTUE ──────────────────────────────────────────────────────────────────────────────────

    // Present the FTUE-focused element (reparented under a Blocker) so the tutorial's DECKS steps
    // (btn_DeckEntry, EditDeck) stay reachable now that this handler owns the screen; returns true
    // while one is being driven.
    private bool PresentFtueIfActive(bool interrupt)
    {
        if (FTUEHook.FtueIsRunning && !_ftueLoggedRunning)
        {
            _ftueLoggedRunning = true;
            Log.Info($"Decks: FTUE running, focus={(FTUEHook.FocusedElement != null ? FTUEHook.FocusedElement.gameObject.name : "none")}");
        }
        if (!FTUEHook.FtueIsRunning) _ftueLoggedRunning = false;

        var focus = FTUEHook.FtueIsRunning ? FTUEHook.FocusedElement : null;
        if (focus == null || !focus.isActiveAndEnabled) { _ftueShown = null; return false; }
        if (focus != _ftueShown)
        {
            _ftueShown = focus;
            _ftueNav!.RadioToggleMode = focus is Toggle;
            _ftueNav.SetItems(new List<Selectable> { focus });
            _ftueNav.FocusFirst(announce: true, interruptPrior: interrupt);
            Log.Info($"Decks: presenting FTUE focus '{focus.gameObject.name}'");
        }
        return true;
    }
    private bool _ftueLoggedRunning;

    // ── Labels / reflection helpers ─────────────────────────────────────────────────────────────

    // Top strip: HUB tabs use the persistent-nav label; the challenge "return" button reads as a clear
    // action (its own text is a bare back-arrow / generic style name).
    private string TopLabel(Selectable s)
        => s == _returnButton ? "Return to battle request" : UiLabels.TopNavLabel(s);

    private static string ToggleLabel(Selectable s)
    {
        var tmp = s.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        var label = tmp != null && !string.IsNullOrWhiteSpace(tmp.text) ? tmp.text : s.gameObject.name;
        var state = (s is Toggle t && t.isOn) ? "selected" : "not selected";
        return $"{label}, {state}";
    }

    private static string Loc(string id, string fallback)
    {
        try
        {
            var v = TPCI.Localization.LocalizationManager.Instance?.GetString(id);
            if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v;
        }
        catch { }
        return fallback;
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

    private static T? ReadMember<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, flags);
            if (p != null) return p.GetValue(instance) as T;
            var f = t.GetField(name, flags);
            if (f != null) return f.GetValue(instance) as T;
        }
        return null;
    }

    private static T? CallMethod<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var m = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
            if (m != null) return m.Invoke(instance, null) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
