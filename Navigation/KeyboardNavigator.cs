using System;
using System.Collections.Generic;
using PokemonTCGLiveAccessibility.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Navigation;

// Reusable keyboard navigation for any uGUI screen.
//
// PTCGL has zero native keyboard nav (no InputModule reads arrows/Tab, no Selectable.navigation
// is configured anywhere — verified by reading the decompiled source). So every screen handler
// needs to read keys itself, but we *don't* synthesize parallel focus state: each focus change
// goes through Selectable.Select() (sets EventSystem.currentSelectedGameObject + fires the game's
// own OnSelect handlers), and each activation goes through Button.onClick.Invoke() — the same
// UnityEvent a mouse click would fire, with the locale args already wired in the prefab.
//
// Source-level safety nets baked in here so every screen handler benefits:
//   - Vertical / Horizontal / Tab axes are individually toggleable (per-screen).
//   - Items hidden behind another raycast target (modal overlay, blocker panel) are skipped:
//     uGUI's own raycast tells us when a button isn't actually clickable right now.
//   - Window-focus gate: we don't read keys when the game window isn't focused.
internal sealed class KeyboardNavigator
{
    public delegate string LabelResolver(Selectable s);

    private readonly SpeechEngine _speech;
    private readonly List<Selectable> _items = new();
    private LabelResolver _resolveLabel = DefaultLabel;
    private int _index = -1;

    private readonly PointerEventData _pointerData;
    private readonly List<RaycastResult> _raycastResults = new(8);

    // Axis config — default: vertical-only list nav. Set Horizontal / Tab true per-screen for grids / tab strips.
    public bool AllowVertical { get; set; } = true;
    public bool AllowHorizontal { get; set; } = false;
    public bool AllowTab { get; set; } = false;

    // For grid layouts. Up/Down moves by Columns items at a time; Left/Right by 1. Auto-
    // computed in SetItems from item positions (count of items in the first row). Defaults
    // to 1 so a flat list behaves identically to before. For single-row horizontal layouts
    // Columns equals the row length, which means Up/Down jumps out of bounds and is a no-op
    // (correct behaviour — vertical doesn't apply to a horizontal strip).
    public int Columns { get; private set; } = 1;

    // Escape / Backspace = back. Each popup-type handler wires this to its own dismiss path
    // (SimpleMessage: closeButton.onClick; ModalElementContainer: Close(); OverlayScreenGroup: Deactivate()).
    // Null = back is a no-op for this screen (e.g. first-launch screens with nowhere to go back to).
    public Action? OnBack { get; set; }

    // Group / section name. When a screen has multiple Tab-cycled groups, each group's
    // KeyboardNavigator gets a name (e.g. "Agreements", "Continue"). FocusFirst announces
    // the name interrupting, then queues the first item — so on a Tab transition the user
    // hears "Agreements. I agree to the Terms of Use, unchecked, 1 of 2". MoveBy / Activate
    // don't repeat the group name; the user knows their context once they're in it.
    public string? GroupName { get; set; }

    public KeyboardNavigator(SpeechEngine speech)
    {
        _speech = speech;
        _pointerData = new PointerEventData(EventSystem.current);
    }

    public int Index => _index;
    public int Count => _items.Count;
    public Selectable? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    // True when the current focus is in the visual top row of the group's grid. Used by
    // screen handlers to decide whether an Up arrow should escape to a section above this
    // one (rather than being a no-op when MoveBy(-Columns) hits the top).
    public bool IsAtTopRow => _index >= 0 && _index < Columns;
    // True when the current focus is in the visual bottom row. lastRowStart = (count-1)/cols
    // rounded down to a row boundary; any index ≥ that lives in the partial last row.
    public bool IsAtBottomRow
    {
        get
        {
            if (_items.Count == 0 || _index < 0) return false;
            var lastRowStart = ((_items.Count - 1) / Columns) * Columns;
            return _index >= lastRowStart;
        }
    }

    // Current column (0..Columns-1) of the focused item, or -1 if no focus. Lets the screen
    // handler preserve the user's column when escaping vertically into a different group —
    // e.g. user on FACE (col 0 of avatar grid) presses Up, escape lands them on FRIENDS,
    // press Up again, escape lands on the HOME tab (col 0 of the top strip).
    public int CurrentColumn => _index >= 0 ? _index % Columns : -1;

    public void SetItems(IEnumerable<Selectable> items, LabelResolver? resolver = null)
    {
        _items.Clear();
        foreach (var s in items)
            if (s != null) _items.Add(s);
        _resolveLabel = resolver ?? DefaultLabel;
        _index = -1;

        // Auto-detect orientation from the items' bounding box. Handlers can still override
        // AllowVertical / AllowHorizontal after SetItems if the visual layout doesn't match
        // how we want to navigate (e.g. a 2-column grid that the user prefers as a flat list).
        if (_items.Count > 1)
        {
            var (vertical, horizontal) = DetectAxes(_items);
            AllowVertical = vertical;
            AllowHorizontal = horizontal;
            Columns = DetectColumns(_items);
        }
        else
        {
            Columns = 1;
        }
    }

    // Count of items in the first visual row — used by Up/Down to jump rows instead of items.
    // Items are expected ordered top-to-bottom, left-to-right (HUBProfileScreen.RebuildGroups
    // does this via ReadingOrder). We count consecutive items whose y matches the first
    // item's y within a small tolerance, then stop at the first y-drop (= start of row 2).
    public static int DetectColumns(IReadOnlyList<Selectable> items)
    {
        if (items.Count <= 1) return 1;
        var firstY = items[0].transform.position.y;
        // Tolerance scales with the height of the first item so it works at any UI scale.
        var tolerance = 1f;
        if (items[0].transform is RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            tolerance = Mathf.Max(1f, Mathf.Abs(corners[1].y - corners[0].y) * 0.5f);
        }
        int columns = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] == null) continue;
            var y = items[i].transform.position.y;
            if (Mathf.Abs(y - firstY) <= tolerance) columns++;
            else break;
        }
        return Mathf.Max(1, columns);
    }

    // Decide which arrow keys make sense for a focus group by measuring how its items spread
    // on screen. If the bounding box is much wider than tall, only horizontal arrows. Much
    // taller than wide → only vertical. Roughly equal → both (grid). Single-item groups
    // default to vertical (doesn't matter; arrows are no-ops with one item anyway).
    public static (bool vertical, bool horizontal) DetectAxes(IReadOnlyList<Selectable> items)
    {
        if (items.Count <= 1) return (true, false);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in items)
        {
            if (s == null) continue;
            var p = s.transform.position;
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y;
            if (p.y > maxY) maxY = p.y;
        }

        var widthRange = maxX - minX;
        var heightRange = maxY - minY;

        const float ratio = 1.5f;
        if (widthRange > heightRange * ratio) return (false, true);
        if (heightRange > widthRange * ratio) return (true, false);
        return (true, true);
    }

    public void FocusFirst(bool announce = true, bool interruptPrior = true)
    {
        if (_items.Count == 0) return;
        // Allow disabled items here — desktop accessibility convention is "you can navigate
        // to a dimmed item, you just can't activate it". DefaultLabel appends "unavailable"
        // when the Selectable is not interactable. Also skip the raycast-blocked check since
        // OnEnter runs synchronously off a Harmony postfix and layout may not be ready yet.
        //
        // interruptPrior=true (default, Tab transition): group name interrupts current speech
        // then the item queues after. interruptPrior=false (initial OnEnter after the screen
        // description was just spoken): group name and item both queue, so the description
        // plays in full before the focus content.
        for (int i = 0; i < _items.Count; i++)
        {
            if (IsActiveInHierarchy(_items[i]))
            {
                SetIndex(i, announce, interrupt: interruptPrior, announceGroup: true);
                return;
            }
        }
    }

    // Focus the first row of the grid, preserving column where possible. Used by screen
    // handlers on a Down-escape into this group from the section above — landing in the top
    // row matches the visual flow (the new focus is the row closest to where the user came
    // from). preferredColumn < 0 means "any" → falls back to leftmost.
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        if (_items.Count == 0) return;
        var rowEnd = Mathf.Min(Columns - 1, _items.Count - 1);
        var target = (preferredColumn < 0) ? 0 : Mathf.Min(preferredColumn, rowEnd);
        if (IsActiveInHierarchy(_items[target]))
        {
            SetIndex(target, announce, interrupt: interruptPrior, announceGroup: true);
            return;
        }
        // Fallback: any navigable item in the first row.
        for (int i = 0; i <= rowEnd; i++)
            if (IsActiveInHierarchy(_items[i]))
            {
                SetIndex(i, announce, interrupt: interruptPrior, announceGroup: true);
                return;
            }
    }

    // Focus the last (visual) row, preserving column where possible. Used by screen handlers
    // on an Up-escape into this group from the section below — landing in the bottom row
    // matches the visual flow (closest to where the user came from), instead of jumping to
    // the very top of the group.
    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        if (_items.Count == 0) return;
        var lastRowStart = ((_items.Count - 1) / Columns) * Columns;
        var rowEnd = Mathf.Min(lastRowStart + Columns - 1, _items.Count - 1);
        var target = (preferredColumn < 0) ? lastRowStart : Mathf.Min(lastRowStart + preferredColumn, rowEnd);
        if (IsActiveInHierarchy(_items[target]))
        {
            SetIndex(target, announce, interrupt: interruptPrior, announceGroup: true);
            return;
        }
        for (int i = lastRowStart; i <= rowEnd; i++)
            if (IsActiveInHierarchy(_items[i]))
            {
                SetIndex(i, announce, interrupt: interruptPrior, announceGroup: true);
                return;
            }
    }

    // Re-apply Selectable.Select + re-announce on the currently-focused item. Used by screen
    // handlers on OnResume to restore the player's position after a modal closes. Falls back
    // to FocusFirst if the saved index is no longer valid (item destroyed / removed).
    public void RefocusCurrent(bool announce = true)
    {
        if (_index >= 0 && _index < _items.Count && IsActiveInHierarchy(_items[_index]))
        {
            _items[_index].Select();
            if (announce) Speak(interrupt: true);
            return;
        }
        FocusFirst(announce);
    }

    public void MoveNext() => MoveBy(+1);
    public void MovePrev() => MoveBy(-1);

    public bool Activate(bool isSpaceKey = false)
    {
        var sel = Current;
        if (!IsUsable(sel)) return false;
        if (sel is Toggle toggle)
        {
            // Space toggles, Enter is a no-op on checkboxes (web/desktop convention).
            if (!isSpaceKey) return false;
            toggle.isOn = !toggle.isOn;
            // Just speak the new state — user already knows which checkbox they pressed.
            _speech.Say(toggle.isOn ? "checked" : "unchecked", interrupt: true);
            return true;
        }
        if (sel is Button btn)
        {
            btn.onClick?.Invoke();
            return true;
        }
        ExecuteEvents.Execute(sel!.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        return true;
    }

    public void Tick()
    {
        if (!Application.isFocused) return;

        // Back input runs even when there are no items in the list — a "OK only" popup with no
        // navigable buttons should still be dismissable by Escape if the handler wired OnBack.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || (UnityEngine.Input.GetKeyDown(KeyCode.Backspace) && !IsTextInputFocused()))
        {
            OnBack?.Invoke();
            return;
        }

        if (_items.Count == 0) return;

        if (AllowTab && UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            if (shift) MovePrev(); else MoveNext();
        }
        else if (AllowVertical && UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            // Grid layouts: jump by Columns so Down lands in the column below. For partial
            // last rows (e.g. 6 items in a 5-column grid) MoveVerticalInGrid clamps the
            // target column to whatever the target row actually has, so Down from Face 2
            // through Face 5 still reaches Face 6 instead of being a no-op.
            MoveVerticalInGrid(+1);
        }
        else if (AllowVertical && UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            MoveVerticalInGrid(-1);
        }
        else if (AllowHorizontal && UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            // sameRowOnly: in a grid, Right from the last item of a row must NOT wrap to
            // the first item of the next row. For flat horizontal strips Columns equals
            // the row length, so "same row" includes every item — wrapping was never
            // possible there anyway.
            MoveBy(1, sameRowOnly: true);
        }
        else if (AllowHorizontal && UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            MoveBy(-1, sameRowOnly: true);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
              || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Activate(isSpaceKey: false);
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            Activate(isSpaceKey: true);
        }
    }

    // Grid-aware vertical move: stays in the same column when possible, but if the target
    // row is partial and doesn't have that column, clamps to the row's last item. Mirrors
    // how Windows ListView and most native grid widgets handle partial-row navigation —
    // Down should never be a silent no-op as long as the target row has *anything* in it.
    private void MoveVerticalInGrid(int direction)
    {
        if (_items.Count == 0 || Columns <= 0) return;
        if (_index < 0)
        {
            // No selection yet — just focus first navigable.
            FocusFirst(announce: true, interruptPrior: true);
            return;
        }
        int currentRow = _index / Columns;
        int currentCol = _index % Columns;
        int targetRow = currentRow + direction;
        if (targetRow < 0) return;
        int rowStart = targetRow * Columns;
        if (rowStart >= _items.Count) return;
        int rowLastIndex = Mathf.Min(rowStart + Columns - 1, _items.Count - 1);
        int targetIndex = Mathf.Min(rowStart + currentCol, rowLastIndex);
        if (targetIndex == _index) return;
        // Honour the same overlay-blocking filter as linear MoveBy — fall back to walking
        // toward the target if the precise cell is blocked.
        int probe = targetIndex;
        while (probe >= rowStart && probe <= rowLastIndex)
        {
            if (IsNavigable(_items[probe]))
            {
                SetIndex(probe, announce: true, interrupt: true, announceGroup: false);
                return;
            }
            // Try the next column in the row (toward the start) so we still land *somewhere*
            // in the target row.
            probe--;
        }
    }

    private void MoveBy(int delta, bool sameRowOnly = false)
    {
        if (_items.Count == 0) return;
        // No wrap: stop silently at the ends of the list. Only Tab between groups wraps; that's
        // a screen-handler concern, not the per-group navigator. Across-the-board rule — menus,
        // dialogs, anywhere — pressing past the last item just does nothing.
        int startIndex = _index < 0 ? (delta > 0 ? 0 : _items.Count - 1) : _index;
        int currentRow = (_index < 0) ? -1 : _index / Columns;
        int next = (_index < 0) ? startIndex : _index + delta;
        while (next >= 0 && next < _items.Count)
        {
            // For horizontal moves in a grid, stop at the row boundary — Right from the last
            // column shouldn't wrap to the first column of the next row.
            if (sameRowOnly && currentRow >= 0 && next / Columns != currentRow) return;
            // Use IsNavigable: skip dead and overlay-blocked items, but allow disabled (non-
            // interactable) ones so the user can still find a dimmed button and hear why.
            if (IsNavigable(_items[next])) { SetIndex(next, announce: true, interrupt: true, announceGroup: false); return; }
            next += delta;
        }
    }

    private void SetIndex(int i, bool announce, bool interrupt, bool announceGroup)
    {
        _index = i;
        var sel = _items[_index];
        if (sel != null) sel.Select();
        if (!announce) return;
        if (announceGroup && !string.IsNullOrEmpty(GroupName))
        {
            _speech.Say(GroupName!, interrupt: interrupt);  // interrupt prior or queue, per caller
            Speak(interrupt: false);                         // item label always queues after the group name
        }
        else
        {
            Speak(interrupt);
        }
    }

    // Most permissive: GameObject is in active hierarchy. Used by FocusFirst — initial focus
    // shouldn't be denied due to layout-timing races or temporary disabled state.
    private static bool IsActiveInHierarchy(Selectable? s)
    {
        return s != null && s.isActiveAndEnabled;
    }

    // Active item — caller decides whether the raycast/overlay check matters. Modal screens
    // pause the underlying screen via the router so the overlay-blocking check isn't needed
    // here; including it bites accessibility cases like the FTUE top blocker, where the
    // game reparents HOME above an invisible blocker that covers all other tabs — raycast
    // at DECKS hits the blocker, MoveBy refuses to advance, and the user can't navigate the
    // top strip even though every tab's Selectable is still interactable.
    private bool IsNavigable(Selectable? s)
    {
        return IsActiveInHierarchy(s);
    }

    // Fully usable: active, interactable, not blocked. Used by Activate — pressing Enter on
    // a disabled item is a no-op (just like clicking a dimmed button).
    private bool IsUsable(Selectable? s)
    {
        if (!IsNavigable(s)) return false;
        return s!.interactable;
    }

    // Don't eat Backspace if the user is typing in a text field (deck name editor, search box, etc.).
    // Escape is still fine in those — desktop convention is "escape cancels the field/dialog".
    private static bool IsTextInputFocused()
    {
        var es = EventSystem.current;
        if (es == null) return false;
        var sel = es.currentSelectedGameObject;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
    }

    // Returns true when something else is on top of this Selectable in screen space (modal blur,
    // popup blocker, in-game overlay). Uses uGUI's own raycast so it matches what the game's
    // click handlers see — a button you can't click with the mouse is one we shouldn't navigate to.
    private bool IsBlockedByOverlay(Selectable s) => !IsRaycastReachable(s, _pointerData, _raycastResults);

    // Same algorithm exposed as a static utility so screen handlers can filter their groups
    // with the same "would Unity's click router actually reach this item" check at RebuildGroups
    // time. Caller-owned scratch lists so we don't allocate per call.
    public static bool IsRaycastReachable(Selectable s)
    {
        var es = EventSystem.current;
        if (es == null) return true;
        return IsRaycastReachable(s, new PointerEventData(es), new List<RaycastResult>(8));
    }

    private static bool IsRaycastReachable(Selectable s, PointerEventData pointer, List<RaycastResult> results)
    {
        if (s == null) return false;
        var es = EventSystem.current;
        if (es == null) return true;
        if (s.transform is not RectTransform rect) return true;

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var worldCenter = (corners[0] + corners[2]) * 0.5f;

        Camera? cam = null;
        var canvas = s.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            cam = canvas.worldCamera;

        var screenPoint = RectTransformUtility.WorldToScreenPoint(cam!, worldCenter);

        pointer.position = screenPoint;
        results.Clear();
        es.RaycastAll(pointer, results);

        // No hits typically means layout hasn't computed yet; lenient pass so first-frame
        // RebuildGroups doesn't strip everything before layout settles.
        if (results.Count == 0) return true;

        var top = results[0].gameObject;
        for (var t = top != null ? top.transform : null; t != null; t = t.parent)
        {
            if (t.gameObject == s.gameObject) return true;
        }
        return false;
    }

    private void Speak(bool interrupt)
    {
        var sel = Current;
        if (sel == null) return;
        var label = _resolveLabel(sel);
        _speech.Say(Announce.Item(label, description: null, index: _index + 1, total: _items.Count), interrupt);
    }

    public static string DefaultLabel(Selectable s)
    {
        var label = ResolveBaseLabel(s);
        if (s is Toggle toggle)
            label = $"{label}, {(toggle.isOn ? "checked" : "unchecked")}";
        if (!s.interactable)
            label = $"{label}, unavailable";
        return label;
    }

    private static string ResolveBaseLabel(Selectable s)
    {
        var tmp = s.GetComponentInChildren<TMP_Text>(includeInactive: false);
        if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
        var txt = s.GetComponentInChildren<Text>(includeInactive: false);
        if (txt != null && !string.IsNullOrEmpty(txt.text)) return txt.text;
        return StripPrefabSuffix(s.gameObject.name);
    }

    // Real game text wins (it's already authored). This only cleans the GameObject-name fallback
    // we hit for icon-only buttons (X close, back arrow, etc.) — Unity prefab convention is
    // "CloseButton" / "BackBtn", which is noise to read aloud.
    private static string StripPrefabSuffix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        foreach (var suffix in PrefabSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - suffix.Length).TrimEnd('_', ' ');
        }
        return name;
    }

    private static readonly string[] PrefabSuffixes = { "_Button", " Button", "Button", "_Btn", " Btn", "Btn" };
}
