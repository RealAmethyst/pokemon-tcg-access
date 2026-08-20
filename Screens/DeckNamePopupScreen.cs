using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// RENAME DECK popup — driver: LandscapeEditDeckNamePopupGroup (a HUBGroupController).
//
// This popup had no handler at all, which is why renaming a deck never worked with the mod: the
// pencil opened it silently, the deck editor (or the DECKS screen) underneath kept owning the
// keyboard, and the text field was never activated — so arrows still moved cards in the editor,
// typing went nowhere, and the only way out was Escape.
//
// ── Who opens it (both routes end here) ───────────────────────────────────────────────────────
// In landscape — the only aspect this game runs in on desktop — there is exactly ONE entry point:
//   • deck editor: HUBEditDeckController.Settings_BeginNameChange() (the ✎ next to the deck name)
//   • DECKS screen: DeckPanel_L.OpenDeckRenameMenu() (the ✎ in the selected deck's panel)
// Both do `renameDeckPopup as LandscapeEditDeckNamePopupGroup` → ShowPopup(currentName, callback),
// so one handler on ShowPopup covers both. (The portrait path is a different object entirely —
// OverlayManager + RenameDeckOverlay — and never runs here.)
//
// ── Committing the rename ─────────────────────────────────────────────────────────────────────
// OnDone() / OnCancel() are the popup's own public methods, and they are what the prefab's DONE and
// CANCEL buttons invoke. OnDone sanitizes through HUBEditDeckController.GetValidDeckName, hands the
// name to the ShowPopup callback (which writes _deck.deckName, calls PlayerInventoryManager.UpdateDeck
// and refreshes the on-screen title) and saves to the service. OnCancel restores the cached name.
// So Enter on DONE routes through the game's real endpoint — see [[feedback-drive-game-ui-not-ops]].
//
// ── Nav model ─────────────────────────────────────────────────────────────────────────────────
// Same shape as the add-friend overlay, which is the other field-plus-buttons screen: the text FIELD
// is its own group (arrows are its caret, so they can't be nav) and the BUTTONS are a second group.
// TAB switches between the two. See [[feedback-arrows-over-tab]] and [[feedback-input-field-activation]].
//
// Enter inside the field: only routed to OnDone when the prefab left the field's own submit events
// unwired. If the prefab already wires onSubmit/onEndEdit, we keep our hands off it rather than
// risk submitting the rename twice.
internal sealed class DeckNamePopupScreen : IScreenHandler
{
    private static DeckNamePopupScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly List<KeyboardNavigator> _groups = new();
    private int _activeGroup;
    private LandscapeEditDeckNamePopupGroup? _popup;
    private TMP_InputField? _field;
    private bool _fieldOwnsSubmit;      // the prefab wired the field's own submit — don't double-fire
    private bool _active;
    private string _openedWith = "";    // the name the popup was opened on, to tell a real rename from a cancel
    private bool _committing;           // OnDone is running, so the close that follows is a commit

    public string Name => "Rename deck";

    public DeckNamePopupScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new DeckNamePopupScreen(router);

        var show = AccessTools.Method(typeof(LandscapeEditDeckNamePopupGroup), "ShowPopup",
                                      new[] { typeof(string), typeof(UnityAction<string>) })
                   ?? AccessTools.Method(typeof(LandscapeEditDeckNamePopupGroup), "ShowPopup");
        if (show == null)
        {
            Log.Error("DeckNamePopupScreen: could not resolve LandscapeEditDeckNamePopupGroup.ShowPopup — renaming a deck will be silent");
            return;
        }
        harmony.Patch(show, postfix: new HarmonyMethod(AccessTools.Method(typeof(DeckNamePopupScreen), nameof(Show_Postfix))));

        // Every way this popup closes — DONE, CANCEL, the editor being torn down
        // (HUBEditDeckController.OnDeactivate) and DeckPanel_L.HideDeckRenamePopup — goes through
        // Deactivate(), so that's the close hook. Tick still backstops it. [[feedback-hook-every-hide-path]]
        var deactivate = AccessTools.Method(typeof(LandscapeEditDeckNamePopupGroup), "Deactivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(DeckNamePopupScreen), nameof(Deactivate_Postfix))));
        else
            Log.Warn("DeckNamePopupScreen: LandscapeEditDeckNamePopupGroup.Deactivate not found — closing falls back to the visibility check");

        // OnDone ends by calling Deactivate, so by itself a close tells us nothing about WHY. This
        // prefix marks the close that follows as a commit, so we only confirm a rename that really
        // happened — never one the player cancelled, and never one the editor tore down mid-edit.
        var done = AccessTools.Method(typeof(LandscapeEditDeckNamePopupGroup), "OnDone");
        if (done != null)
            harmony.Patch(done,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(DeckNamePopupScreen), nameof(Done_Prefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(DeckNamePopupScreen), nameof(Done_Finalizer))));
        else
            Log.Warn("DeckNamePopupScreen: LandscapeEditDeckNamePopupGroup.OnDone not found — a completed rename won't be confirmed aloud");

        Log.Info("DeckNamePopupScreen installed");
    }

    private static void Show_Postfix(LandscapeEditDeckNamePopupGroup __instance)
    {
        if (_instance == null) return;
        _instance._popup = __instance;
        _instance._committing = false;
        // ShowPopup has already written the current deck name into the box, so the box IS the name we
        // were opened on — no need to depend on the argument's metadata name to learn it.
        var field = ReadField<TMP_InputField>(__instance, "deckTitleInputField");
        _instance._openedWith = field != null ? (field.text ?? "").Trim() : "";

        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    private static void Done_Prefix(LandscapeEditDeckNamePopupGroup __instance)
    {
        if (_instance != null && _instance._popup == __instance) _instance._committing = true;
    }

    private static void Done_Finalizer(LandscapeEditDeckNamePopupGroup __instance)
    {
        // On the normal path OnDone reaches Deactivate(), whose postfix reads _committing, announces,
        // clears it and nulls _popup — so by the time this finalizer runs _popup != __instance and this
        // is a no-op. Only if OnDone THREW before Deactivate does _popup still point here, leaving
        // _committing stuck true; clearing it stops a later unrelated teardown from falsely announcing a
        // rename. We never touch __exception, so a real error still propagates.
        if (_instance != null && _instance._popup == __instance) _instance._committing = false;
    }

    private static void Deactivate_Postfix(LandscapeEditDeckNamePopupGroup __instance)
    {
        if (_instance == null || _instance._popup != __instance || !_instance._active) return;

        // OnDone has already run GetValidDeckName + the ShowPopup callback by now, so the field holds
        // the name the deck actually took. An empty box falls back to the old name, which is no rename.
        var committed = _instance._committing;
        var newName = _instance._field != null ? (_instance._field.text ?? "").Trim() : "";
        _instance._committing = false;

        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._popup = null;

        // Spoken last, after the screen underneath re-announces where the player is standing, so the
        // confirmation is the thing they're left with.
        if (committed && newName.Length > 0 && newName != _instance._openedWith)
            _instance._router.Speech.Say($"Deck renamed to {newName}", interrupt: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() => ClearGroups();   // drop the field's echo hook when paused/popped
    public void RespeakAll() => Rebuild(announce: true);

    // Detach each navigator before dropping it, so the deck-name field's character-echo listener
    // doesn't outlive the navigator and double-fire on the next rebuild. See KeyboardNavigator.Detach.
    private void ClearGroups()
    {
        foreach (var g in _groups) g.Detach();
        _groups.Clear();
    }

    public string StateSignature()
        => _popup == null ? "RenameDeck|dead" : $"RenameDeck|{_activeGroup}|{CurrentNav?.Index ?? -1}|{_field?.text}";

    private KeyboardNavigator? CurrentNav =>
        _activeGroup >= 0 && _activeGroup < _groups.Count ? _groups[_activeGroup] : null;

    public void Tick()
    {
        if (!IsVisible())
        {
            _active = false; _router.Deactivate(this); _popup = null;
            return;
        }
        if (!Application.isFocused) return;

        // Escape FIRST, before any content check can return early — a popup that can't be closed
        // seals a blind player in. [[feedback-screen-must-always-be-closable]]
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }

        if (_groups.Count == 0) return;

        // TAB switches GROUPS (field ↔ buttons); it never steps control by control. Arrows belong to
        // whichever group is live — inside the field they're the caret.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Tab))
        {
            if (_groups.Count > 1)
            {
                var shift = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                _activeGroup = (_activeGroup + (shift ? -1 : 1) + _groups.Count) % _groups.Count;
                _groups[_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Enter while typing = commit the rename, but only when the prefab left the field's own
        // submit events unwired. If it wired them, TMP already fires them on this same keypress and
        // acting too would submit twice (a redundant SaveDecksToServiceAsync).
        if (!_fieldOwnsSubmit && CurrentNav is { } nav && nav.CurrentIsTextField
            && (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)))
        {
            Submit();
            return;
        }

        CurrentNav?.Tick();
    }

    // Escape = the popup's own cancel: restores the name it was opened with, then closes.
    private void Cancel() => _popup?.OnCancel();

    // The game's own commit path — identical to pressing DONE.
    private void Submit() => _popup?.OnDone();

    private void Rebuild(bool announce)
    {
        ClearGroups();
        _field = null;
        _fieldOwnsSubmit = false;
        if (_popup == null) return;

        // Group 1: the deck-name field. ShowPopup has already pre-filled it with the current name,
        // and the prefab's own onFocusSelectAll decides whether typing replaces it.
        _field = ReadField<TMP_InputField>(_popup, "deckTitleInputField");
        if (_field != null && _field.isActiveAndEnabled)
        {
            // If we can't tell whether the field wires its own submit, fail CLOSED — assume it does, so
            // Enter-in-field defers to the field / DONE rather than risking a second OnDone on the same
            // keypress. DONE stays reachable either way, so the cost of guessing "owned" is nil.
            try { _fieldOwnsSubmit = HasPersistent(_field.onEndEdit) || HasPersistent(_field.onSubmit); }
            catch { _fieldOwnsSubmit = true; Log.Warn("Rename deck: couldn't inspect the field's submit wiring — deferring Enter to the field/DONE"); }
            AddGroup(new List<Selectable> { _field }, FieldLabel);
        }
        else
        {
            Log.Warn($"Rename deck: deckTitleInputField {(_field == null ? "not found on the popup" : "is inactive")} — buttons only, no typing");
        }

        // Group 2: DONE / CANCEL (and any close ✕), by reading order. Their own text is the game's
        // localized label, so they need no help from us.
        var buttons = _popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Where(IsRealButton)
            .OrderBy(b => ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        if (buttons.Count > 0)
        {
            var g = AddGroup(buttons, KeyboardNavigator.DefaultLabel);
            // DONE and CANCEL sit side by side, so Left/Right is the natural axis; keep Up/Down live
            // too in case the prefab stacks them in a taller aspect.
            g.AllowHorizontal = true;
            g.AllowVertical = true;
        }

        Log.Info($"Rename deck popup: field={(_field != null ? _field.gameObject.name : "none")} " +
                 $"fieldOwnsSubmit={_fieldOwnsSubmit} buttons=[{string.Join(", ", ButtonReport())}]");

        if (_groups.Count == 0) return;
        _activeGroup = 0;   // land in the field when there is one
        if (!announce) return;

        // Title first (interrupting whatever the editor was saying), then the focused item QUEUES
        // behind it as a second utterance. [[feedback-speech-queue-single-utterance]]
        _router.Speech.Say(Title(), interrupt: true);
        _groups[0].FocusFirst(announce: true, interruptPrior: false);
    }

    private KeyboardNavigator AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver label)
    {
        // AllowTab=false: this handler owns Tab (group switching), not the navigator.
        var g = new KeyboardNavigator(_router.Speech) { AllowTab = false };
        g.SetItems(items, label);
        g.OnBack = Cancel;
        _groups.Add(g);
        return g;
    }

    // The popup's own header, so the screen announces itself in the player's language. Falls back to
    // mod English only when the prefab has no header text to read. [[feedback-no-hardcoded-text]]
    private string Title()
    {
        var header = HeaderText();
        return string.IsNullOrWhiteSpace(header) ? Name : header!;
    }

    private string? HeaderText()
    {
        if (_popup == null) return null;
        foreach (var t in _popup.GetComponentsInChildren<TMP_Text>(includeInactive: false)
                                .OrderBy(t => ReadingOrder(t.transform)))
        {
            if (!HasLetter(t.text)) continue;
            if (IsPartOfField(t)) continue;                          // the field's own text / placeholder
            if (t.GetComponentInParent<Button>() != null) continue;  // a DONE / CANCEL label
            return t.text.Trim();
        }
        return null;
    }

    private bool IsPartOfField(TMP_Text t)
    {
        if (_field == null) return false;
        if (ReferenceEquals(t, _field.textComponent) || ReferenceEquals(t, _field.placeholder)) return true;
        return t.transform.IsChildOf(_field.transform);
    }

    // "<what it's for>, <current name>, edit field". DefaultLabel already builds exactly that from the
    // placeholder, but a rename field arrives pre-filled — so when the prefab has no placeholder text
    // we'd otherwise announce a raw GameObject name. Name it from the popup's own header instead.
    private string FieldLabel(Selectable s)
    {
        var label = KeyboardNavigator.DefaultLabel(s);
        if (s is not TMP_InputField f) return label;
        if (f.placeholder is TMP_Text ph && HasLetter(ph.text)) return label;   // placeholder said it already
        var content = string.IsNullOrWhiteSpace(f.text) ? "blank" : f.text.Trim();
        var suffix = f.interactable ? "" : ", unavailable";
        return $"{Title()}, {content}, edit field{suffix}";
    }

    // A real control, not a decorative child: PTCGL prefabs often put a clickable icon or background
    // inside the button that already handles the click.
    private bool IsRealButton(Button b)
    {
        for (var t = b.transform.parent; t != null && _popup != null && t != _popup.transform; t = t.parent)
            if (t.GetComponent<Button>() != null) return false;
        return true;
    }

    // Icon-only DONE/CANCEL would read as a prefab name, so log what each button is actually wired to
    // — that's the ground truth for naming them if the next in-game pass shows a bare label.
    private IEnumerable<string> ButtonReport()
    {
        if (_popup == null) return System.Array.Empty<string>();
        return _popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .Select(b =>
            {
                var handlers = UiLabels.Handlers(b);
                var dropped = IsRealButton(b) ? "" : "(dropped)";
                return string.IsNullOrEmpty(handlers) ? $"{b.gameObject.name}{dropped}" : $"{b.gameObject.name}→{handlers}{dropped}";
            });
    }

    private bool IsVisible()
    {
        if (_popup == null || !_popup.gameObject.activeInHierarchy) return false;
        // Deactivate() runs Anim.Hide() before the GameObject goes inactive, and Hide() leaves the
        // object active while the close animation plays. currentlyVisible flips immediately, so it's
        // the earlier, truer signal of "this popup is on its way out".
        var anim = _popup.GetComponent<AnimatedSetActive>();
        return anim == null || anim.currentlyVisible;
    }

    // No internal catch: an inspection failure must reach Rebuild's try so it can fail CLOSED
    // (assume the field owns submit), not be swallowed into a false "field doesn't submit".
    private static bool HasPersistent(UnityEventBase? e) => e != null && e.GetPersistentEventCount() > 0;

    private static bool HasLetter(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s!) if (char.IsLetter(c)) return true;
        return false;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }

    private static float ReadingOrder(Transform t)
    {
        var pos = t.position;
        return -pos.y * 10000 + pos.x;
    }
}
