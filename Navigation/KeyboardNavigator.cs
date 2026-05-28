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
        }
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
            MoveNext();
        }
        else if (AllowVertical && UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            MovePrev();
        }
        else if (AllowHorizontal && UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            MoveNext();
        }
        else if (AllowHorizontal && UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            MovePrev();
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

    private void MoveBy(int delta)
    {
        if (_items.Count == 0) return;
        // No wrap: stop silently at the ends of the list. Only Tab between groups wraps; that's
        // a screen-handler concern, not the per-group navigator. Across-the-board rule — menus,
        // dialogs, anywhere — pressing past the last item just does nothing.
        int next = _index < 0 ? (delta > 0 ? 0 : _items.Count - 1) : _index + delta;
        while (next >= 0 && next < _items.Count)
        {
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

    // Active + not occluded by a higher overlay. Used by MoveBy — we *can* land focus on a
    // disabled (non-interactable) item, but we don't navigate to something hiding behind a modal.
    private bool IsNavigable(Selectable? s)
    {
        if (!IsActiveInHierarchy(s)) return false;
        return !IsBlockedByOverlay(s!);
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
    private bool IsBlockedByOverlay(Selectable s)
    {
        var es = EventSystem.current;
        if (es == null) return false;
        if (s.transform is not RectTransform rect) return false;

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var worldCenter = (corners[0] + corners[2]) * 0.5f;

        Camera? cam = null;
        var canvas = s.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            cam = canvas.worldCamera;

        var screenPoint = RectTransformUtility.WorldToScreenPoint(cam!, worldCenter);

        _pointerData.position = screenPoint;
        _raycastResults.Clear();
        es.RaycastAll(_pointerData, _raycastResults);

        // Lenient: a no-hit raycast usually means layout hasn't computed yet (canvas isn't sized,
        // RectTransform world position not finalized). Treat as not-blocked and let the next
        // move retry once layout is alive.
        if (_raycastResults.Count == 0) return false;

        var top = _raycastResults[0].gameObject;
        for (var t = top != null ? top.transform : null; t != null; t = t.parent)
        {
            if (t.gameObject == s.gameObject) return false;
        }
        return true;
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
