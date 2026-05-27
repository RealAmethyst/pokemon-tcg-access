# UI Navigation — design questions

Survey of how PTCGL's UI is structured screen-by-screen, plus design questions about what kb-nav defaults we lock in **before** writing per-screen handlers. Once answers are filled in this file graduates to `../research/`.

Source: decompiled tree at `../research/decompiled/`, surveyed 2026-05-27. ~5,500 files, ~100 `InputField`/`TMP_InputField` instances, ~113 `LayoutGroup` instances, ~149 `ScrollRect` instances.

---

## 0. Quick glossary (so we don't talk past each other)

- **Group** = a discrete focusable region of a screen. E.g. on Deck Builder: filter tabs / search box / card grid / deck list = 4 groups.
- **Within-group movement** = arrow keys. Direction matches the visual layout (vertical list = ↑↓, horizontal strip = ←→, grid = all four).
- **Cross-group movement** = **Tab** / **Shift+Tab**.
- **Activate** = Enter / Space (already wired).
- **Back** = Escape / Backspace (already wired for SimpleMessage; per-handler).
- **PageDragTab** is the game's *own* term for the visual tab buttons (Shop has "Featured / Bundles / Expansions / Code"). That is a tab *strip*, navigated by ←→ as a horizontal group. It is **not** the keyboard Tab key. Different concept, similar word.

---

## 1. Global axis defaults

Proposed defaults (matching what you've said):

- A handler with a **single vertical list** → `AllowVertical=true`, others false. No Tab.
- A handler with a **single horizontal strip** → `AllowHorizontal=true`, others false. No Tab.
- A handler with a **grid** → `AllowVertical=true`, `AllowHorizontal=true`, no Tab (within the grid).
- A handler with **multiple groups** → each group has its own axis(es); Tab moves between groups.
- Inside an **edit field** → typing flows to the field; Tab exits.

**What I need:** confirm or override. Specifically:
- Should Tab also cycle when there's only one group (acting as "Enter next on the list")? My recommendation: **no**, Tab is reserved for group-switching; in a single-group screen it does nothing.

Answer: Agreed

---

## 2. Wrap behavior at list ends

When ↓ is pressed on the last item of a vertical list:
- **(a)** Wrap to the first item (current behavior in `KeyboardNavigator`).
- **(b)** Stop and announce something ("end of list").
- **(c)** Stop silently.

My recommendation: (a) wrap, for short lists (≤10 items). For long lists (card grids with 100+ items), wrap can be disorienting. So maybe (a) wrap by default, opt out per-screen for big lists.

**What I need:** which feels right?

Answer: C

---

## 3. Cross-group order (when Tab cycles)

Order of groups when Tab cycles. Two natural orderings:

- **Visual reading order**: top-left → top-right → bottom-left → bottom-right (English reading order).
- **Logical priority**: most-important first (e.g. on Deck Builder, the card grid is the main focus area, even though it's in the middle).

My recommendation: visual reading order is the predictable default; override sparingly.

**What I need:** confirm reading-order default, per-screen override allowed.

Answer: I agree, we try to match visually wherever possible, but we can override per handler

---

## 4. Edit fields — Tab to enter / Tab to exit

You said: *"edit fields uses tab to move out and inside, so that's something you can mark as something to look into how those work as well"*.

Survey findings — 23 files contain InputField / TMP_InputField. The big ones:

| Field | Location | Activation in-game (mouse) | Submission |
|---|---|---|---|
| Deck name | `DeckTitleEditBar` (top of Deck Builder) | Click the deck name | Enter or click outside |
| Card search | `ExpandableSearchBar` (Deck Builder & Collections) | Click search icon (auto-expands + focuses) | Enter or click outside |
| Add Friend name | `AddFriendOverlayScreenGroup` | Modal opens, field is already focused | Enter when send button enables |
| Code Redemption | `ShopPageCodeCardScreen._scanResultInputField` | Manual entry on Code Redemption tab | Redeem button |
| Series filter | `SearchableDropdown` inside `CardDexSeriesSelector` | Click dropdown, type to filter | Click result |

The desktop convention I think you're describing:

- Tab lands focus **on** the edit field (Tab from the previous group); pressing Enter **or** Tab again "enters" the field (text editing mode).
- While editing: Tab exits, Shift+Tab also exits, Enter submits, Esc cancels.
- Outside editing mode but on the field: Tab moves to the next group as normal.

Open questions:

**4a.** When focus lands on an edit field (e.g. via Tab from previous group), do we **auto-enter editing mode** (cursor active, typing flows in) — like the game does when you click search → it auto-focuses — or do we **stop at the field** and require Enter to begin typing?

Answer: auto edit mode

**4b.** Inside editing mode, what does ↑/↓ do? Options: (i) ignore (text cursor doesn't move vertically — single-line fields), (ii) move out of the field (treat as "tab exit"), (iii) navigate within the field if multi-line. PTCGL fields are all single-line as far as I can see. (i) seems safest.

Answer: ignore up down in edit, left right goes through characters so you can edit

**4c.** Backspace inside an edit field is text-delete, not back-out. `KeyboardNavigator` already suppresses Backspace=back when an `InputField`/`TMP_InputField` is focused. Confirm that's right? yes

Answer: yes. Also for the edit field, my main concern was that arrow keys aren't used to back out or exit the field, that is why I suggested the tab to enter them, however that might not line up visually or make sense in some screens, so maybe only note down how arrows work inside edit fields

**4d.** Should the field's current text contents be spoken on focus (e.g. "Deck name: My Charizard Deck, edit field")? Or just announce as "edit field, empty" / "edit field, <value>"?

Answer: it should speak what's already there, if the edit field has a label it speaks that first, for example decname then what's in the field, if the edit field doesn't have a label we only speak what's in it

---

## 5. Scrolling lists — scroll-to-focused

149 `ScrollRect` instances in the game. Most "lists" we navigate (friend list, card grid, deck list, expansion carousel) are scrollable — only some visible at a time.

Question: when keyboard focus moves to an item that's off-screen, do we:
- **(a)** Automatically scroll the ScrollRect so the focused item is visible.
- **(b)** Don't scroll; let the user lose visual context (they're blind anyway, so visual context doesn't matter directly — but `Selectable.Select()` calls Unity's own `OnSelect` chain which sometimes scrolls the parent ScrollRect already; need to test).

**What I need:** (a) almost certainly, but worth confirming. There's also implementation work — we'd need to compute the focused item's RectTransform position vs ScrollRect viewport and adjust `normalizedPosition`.

Answer: Definitley A and we need to see if there is a game native way we can implement it, maybe there is a function that does it for us or something, but yes, definitely A

---

## 6. Per-screen structural summaries

For each screen below: groups, axes, edit fields, gotchas.

### 6.1 Title screen (post-language-select)
Not surveyed in detail — driver class TBD. Likely a single "Tap anywhere to continue" or a small set of buttons. **Open: research before implementing.**

### 6.2 Login screen
`LoginManager` has multiple paths (PTC login web-view, debug skip, disable-PTCS). The actual login UI uses a web view (`loginWebFlow`) — meaning the standard PTC sign-in is outside Unity. That's a screen-reader concern (browser handles it) but not ours to handle. **The interesting fields are the debug buttons (already excluded), not normal-user inputs.**

Note: login *errors* are likely SimpleMessage popups — those should work via our existing modal handler.

### 6.3 Main Menu / HUB (`HUBPlayScreenController`)
**Layout (landscape):**
- Top: horizontal `PageDragTab` strip — Casual / Ranked / Tournament (game modes)
- Center-left: deck selector (vertical list, scrollable)
- Center: game mode info (read-only)
- Center-right: avatar render (decorative)
- Top-right: Friends notification button

**Groups (proposed):**
1. Game mode tab strip — horizontal, ←→
2. Deck selector — vertical, ↑↓
3. Friends button — single button, Enter

Tab order: 1 → 2 → 3.

**Open: is there a "Play" / "Battle" CTA button somewhere that should be the prominent first focus instead of the tab strip?**

Answer: I think the tab strip is fine, however if we click on that, does it take us further then? Like lets say we have a tab strip, we focus it, then click on t, does it then take us to the tab contents or is it work in a different way? That is something we need to research, because if we can click on the tab strip thing then have curser switch to the contents, I think that would be a nice touch, then having escape or backspace back out to that tab strip.

### 6.4 Settings (`HUBSettingsController`)
**Layout:** single vertical list of 2 sliders (sound + music) + a close button.

**Groups:** 1. The list.

Trivial — first concrete handler after LanguageSelect probably. **Question: how do sliders work for kb nav?** Options:
- Land focus on slider → ←→ changes value, ↑↓ moves to next group element.
- Land focus → Enter enters slider edit mode → ←→ changes → Enter or Tab exits.

PTCGL has 2 sliders here, both single sliders in a vertical list. I'd default to: focus on slider, ←→ changes by 5% steps, ↑↓ moves up/down the list. Confirm?

Answer: A, focus on it, then left right to change

### 6.5 Deck Builder (`HUBEditDeckController`)
**The most complex non-battle screen.** Layout (landscape):
- Top bar: deck name editor (`TMP_InputField` inside `DeckTitleEditBar`)
- Left column: category selector (horizontal tabs `CategorySelector`) + filter drawer (vertical list of expandable toggles)
- Center: search bar (`ExpandableSearchBar` with `TMP_InputField`) + card grid (`EnhancedScroller` via `PopulateCollectionDrawer`)
- Right column: deck list (vertical) + deck stats overlay

**Groups (proposed, 6 in reading order):**
1. Deck name editor (edit field)
2. Category selector (horizontal)
3. Filter drawer (vertical, nested expandable)
4. Search bar (edit field)
5. Card grid (grid, both axes; ScrollRect)
6. Deck list (vertical, ScrollRect)

**Gotchas:**
- Card grid is the main work area — possibly the right *default* focus on enter, even though it's group 5 in reading order.
- Filter drawer has nested expandable toggles. Expanding a group reveals child toggles. We'd need to flatten or treat each expansion as a sub-group.
- `DragSelectionScrollRect` (line 105) — drag-to-multi-select in the card grid. Kb equivalent: shift+arrow to extend selection? — defer until we get there.

**Open: what's the default first-focus on entering the deck builder?** Card grid (the main thing the user came here to do) or deck name (literally first in reading order)? I'd lean card grid.

Answer: card grid is fine, I also feel for most of these I'd need to actually playtest

### 6.6 Shop (`HUBShopScreenController` + `ShopBuilder`)
**Layout:**
- Top: horizontal `PageDragTab` strip — Featured / Bundles / Expansions / Code Redemption
- Below: content varies by tab. Code Redemption is the only tab with an edit field.

**Groups:**
1. Tab strip — horizontal
2. Page content — varies (grid of products / banner carousel + grid / input field)

Tab order: 1 → 2. (When the Code Redemption tab is active, group 2 is the input field.)

**Note:** prices/currency values mean speech format here uses the "shops are exceptions to the standard item-description-index format" rule from your memory.

### 6.7 Friends (`FriendsHUBScreenController`)
**Layout:**
- Top: horizontal tab strip (Pending / Friends / Customization) or slide-out drawer (landscape)
- Main: `EnhancedScroller` friends list (vertical, sorted into status buckets)
- Top-right: "Add Friend" button → opens modal with `TMP_InputField`

**Groups:**
1. Tab strip — horizontal
2. Friends list — vertical, large (scrollable)
3. Add Friend button — single (modal entry point)

Modal sub-handler (later): Add Friend overlay has an input field + Send button — 2 groups inside the modal. Modal handlers may need own SimpleMessage-style sub-handler.

### 6.8 Collections / Card Dex (`HUBCollectionsScreenController`)
**Layout:**
- Top: series dropdown (`SearchableDropdown`) + set carousel (horizontal scroll)
- Below: card gallery (grid)
- Right: MainSet/MasterSet toggle

**Groups:**
1. Series dropdown — edit field + dropdown list when active
2. Set carousel — horizontal
3. Card gallery — grid
4. Classification toggle — single toggle

`SearchableDropdown` is the only place in the game where the game itself reads keyboard input (`KeyCode` checks in `SearchableDropdown.cs:61-73`). We may be able to lean on this for free in this one spot.

### 6.9 Profile (`HUBProfileScreenController`)
**Layout:** avatar render + male/female `ToggleGroup` + optional sub-panels (`_avatarScreenGroup`, `_statScreenGroup`).

**Groups:** 1 (the toggles) or 2 if sub-panel opens (treat as modal).

Trivial.

### 6.10 Battle / Match
Out of scope for this doc. Deferred per your instruction (*"battles and stuff will be needed to look more into"*).

---

## 7. Modals / overlays

We already have `SimpleMessageScreen`. Other modal-like classes found:

- `ModalElementContainer` family (`ModalDrawer`, `ModalPopup`) — used via `RainierManager.genericModalDrawer` / `genericModalPopup`. Has `BlockerState` (`CLOSE` = tap-outside dismisses, `BLOCK` = no dismiss, `CLICKTHROUGH` = transparent).
- `OverlayScreenGroup` subclasses — `InfoPopup`, `ProfessorTooltip`, `BuildAndBattleInfoPopup`, etc. Open via `Activate*`, close via `Deactivate`.
- `AddFriendOverlayScreenGroup`, `FriendOptionsOverlayScreenGroup`, etc. — screen-specific overlays.

**Conventions to bake in (proposed):**

- Each modal is a `IScreenHandler` pushed on top of the parent (already the architecture).
- Escape/Backspace → back action wired to the modal's specific dismiss path (`closeButton.onClick.Invoke()` / `Close()` / `Deactivate()`). For BLOCK-mode modals where the game itself doesn't allow tap-outside dismiss, we honor that — `OnBack` stays null and Esc does nothing.
- Tab cycles groups *within* the modal; doesn't escape to the parent.
- Modal title is suppressed when it matches parent title (already done).

**What I need:** confirm the BLOCK-mode honoring rule — should Esc work on every modal regardless, or should we mirror the game's design intent (BLOCK = locked, only buttons dismiss)?

Answer: we mirror the games intent, always

---

## 8. Side menu / persistent nav

You mentioned a *"sideways menu"* (left-hand persistent menu / sidebar) in your reply. I didn't yet find that in the survey. Likely lives as a child of `_mainMenuController` (`MainMenuNavigation`). **Open: identify it and survey separately — this is its own design question.**

---

## 9. Screen-by-screen summary table

| Screen | Groups | Edit fields | Tab cycles? | First-focus default |
|---|---|---|---|---|
| Title | 1 (TBD) | 0 | No | TBD |
| Login | n/a (web view) | n/a | n/a | n/a |
| Main Menu (HUB Play) | 3 | 0 | Yes | Tab strip |
| Settings | 1 | 0 | No | First slider |
| Deck Builder | 6 | 2 | Yes | TBD — card grid or deck name |
| Shop | 2 | 1 (Code Redemption) | Yes | Tab strip |
| Friends | 3 | 1 (Add Friend modal) | Yes | Tab strip |
| Collections | 4 | 1 (series dropdown) | Yes | TBD — series or grid |
| Profile | 1 | 0 | No | First toggle |
| Battle | TBD | n/a | TBD | TBD |

---

## 10. Implementation order

Once §1–§7 are answered, suggested order for building per-screen handlers:

1. **Settings** — simplest, validates the single-group + slider patterns.
2. **Title / first post-language screen** — research what it actually is.
3. **Side menu / persistent nav** — research first, then implement (you flagged this).
4. **Main Menu (HUB Play)** — tab strip + list + button, validates Tab cycling.
5. **Profile** — easy, builds confidence.
6. **Friends** — adds modal sub-handler practice (Add Friend overlay).
7. **Shop** — multi-tab with one edit-field tab.
8. **Collections** — searchable dropdown + grid.
9. **Deck Builder** — the big one. Likely needs a dedicated questions doc.
10. **Battle** — its own multi-doc phase.

**What I need:** confirm or override the order.

Answer: we go in order of what I can access, so the thing I land on after I've confirmed my language is obvuously the next one to work on, then we just go from there.

---

## End — please answer inline above

Once each `Answer:` line has a response, this file graduates to `../research/QUESTIONS-002-ui-navigation.md` and we start on §10.1 (Settings).
