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
    private Button? _createButton;   // "CREATE A DECK", the deck grid's leftmost top item

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
        if (_controller == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;

        // Forced tutorial focus (reparented element) — present + drive it; nothing else this tick.
        if (PresentFtueIfActive(interrupt: false)) { _ftueNav?.Tick(); return; }

        // Escape opens the overflow (⋮) menu — the same affordance as the top-bar button. There's no
        // other "back" on the deck list (the editor is a separate screen), so Escape is free here.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { HUBOverflowMenuScreen.TryOpen(); return; }

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
        _topNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, AllowVertical = false, AllowHorizontal = true };
        _topNav.SetItems(topItems, UiLabels.TopNavLabel);
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

        // Detail panel buttons (EDIT DECK / SET ACTIVE / TEST / …).
        var panel = CallMethod<Component>(_controller, "GetDeckPanel");
        var detailItems = panel != null
            ? panel.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled)
                .OrderBy(s => ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        _detailNav = new KeyboardNavigator(_router.Speech) { AllowTab = false, GroupName = "Deck options" };
        _detailNav.SetItems(detailItems);

        // CREATE A DECK — the deck grid's own button (a child of the grid that isn't a deck tile).
        var grid = ReadField<Component>(_controller, "deckSelectGrid");
        _createButton = grid != null
            ? grid.GetComponentsInChildren<Button>(includeInactive: false).FirstOrDefault(b => b.GetComponentInParent<DeckSelectEntry>() == null)
            : null;

        RefreshEntries();
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

    // The deck-options panel is an irregular grid (ACTIVE DECK + EDIT DECK on one row, then
    // TEST/DUPLICATE/EXPORT/DELETE), so navigate it spatially — Right and Down differ correctly, and
    // every option is reachable. Up at the top escapes back to the deck grid.
    private void TickDetail()
    {
        var nav = _detailNav;
        if (nav == null || nav.Count == 0) return;
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) nav.TrySpatialMove(1, 0);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) nav.TrySpatialMove(-1, 0);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) nav.TrySpatialMove(0, -1);
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (!nav.TrySpatialMove(0, 1)) EnterSection(Section.Decks, announce: true, interruptPrior: true); }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) nav.Activate();
    }

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
        bool valid = false, active = false;
        try { valid = deck.IsValid(HUBGameModeController.gameMode); } catch { }
        try { active = _controller!.IsActiveDeck(deck); } catch { }
        var status = valid ? "valid" : "invalid";
        if (active) status += ", active";
        _router.Speech.Say(Speech.Announce.Item($"{label}, {status}", description: null, index: _deckIndex + 1, total: _entries.Count), interrupt);
    }

    // ── FTUE ──────────────────────────────────────────────────────────────────────────────────

    // Present the FTUE-focused element (reparented under a Blocker) so the tutorial's DECKS steps
    // (btn_DeckEntry, EditDeck) stay reachable now that this handler owns the screen; returns true
    // while one is being driven.
    private bool PresentFtueIfActive(bool interrupt)
    {
        var focus = FTUEHook.FtueIsRunning ? FTUEHook.FocusedElement : null;
        if (focus == null || !focus.isActiveAndEnabled) { _ftueShown = null; return false; }
        if (focus != _ftueShown)
        {
            _ftueShown = focus;
            _ftueNav!.RadioToggleMode = focus is Toggle;
            _ftueNav.SetItems(new List<Selectable> { focus });
            _ftueNav.FocusFirst(announce: true, interruptPrior: interrupt);
        }
        return true;
    }

    // ── Labels / reflection helpers ─────────────────────────────────────────────────────────────

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
