using System.Collections.Generic;
using System.Reflection;
using System.Text;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Updater.Components;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility;

// Accessibility for the standalone Updater app. The updater is a SEPARATE Unity process from the
// main game (see memory `project-updater-separate-process`); it auto-downloads and applies the
// patch, driven by UpdaterComponent — a status line, a progress meter, and PLAY / RETRY / SUPPORT /
// RELEASE NOTES buttons. None of it speaks or takes keyboard input natively.
//
// Because it's a single screen, there's no ScreenRouter here. We poll the live UI each frame:
//   - announce the game's own localized status text as it changes (status_checking/downloading/…),
//   - announce coarse progress milestones,
//   - surface the error popup when it appears,
//   - read release notes when that panel opens,
//   - and run KeyboardNavigator over the four buttons. Enter on PLAY routes through the button's
//     real onClick (relaunches the game + quits the updater) — we never reimplement the launch.
//
// Pure polling, no Harmony patches: nothing here needs to intercept the updater's logic, only read
// its visible state and drive its existing buttons. All game-facing text is read from the live TMP
// (no hardcoded status strings) per [[feedback-no-hardcoded-text]].
internal sealed class UpdaterScreen
{
    private const BindingFlags PrivInst = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly SpeechEngine _speech;
    private readonly KeyboardNavigator _nav;

    private UpdaterComponent? _comp;
    private bool _wired;

    // Reflected from UpdaterComponent / its sub-components (all private serialized fields).
    private TMP_Text? _statusText;
    private TMP_Text? _progressText;
    private Button? _play;
    private Button? _retry;
    private Button? _support;
    private Button? _releaseNotesButton;
    private Component? _releaseNotesView;
    private Transform? _notesContent;
    private TMP_Text? _notesVersion;
    private TMP_Text? _notesDate;
    private GameObject? _errorPopupGo;

    private string _lastStatus = "";
    private int _lastProgressMilestone = 0;   // 0..4 (×25%); 0 already "spoken" so we never announce 0%
    private bool _errorWasActive;
    private bool _playWasUsable;
    private bool _retryWasUsable;
    private bool _notesWereOpen;

    public UpdaterScreen(SpeechEngine speech)
    {
        _speech = speech;
        _nav = new KeyboardNavigator(speech);
    }

    public void Tick()
    {
        if (_comp == null)
        {
            _comp = Object.FindAnyObjectByType<UpdaterComponent>();
            if (_comp == null) return;
        }
        if (!_wired) Wire();

        PollStatus();
        PollProgress();
        PollError();
        PollNotes();
        PollButtonReadiness();
        _nav.Tick();
    }

    private void Wire()
    {
        _wired = true;

        var statusLoc = GetField<Component>(_comp!, "statusLocText");
        _statusText = statusLoc != null ? statusLoc.GetComponent<TMP_Text>() : null;

        var meter = GetField<Component>(_comp!, "meterController");
        if (meter != null)
        {
            var progLoc = GetField<Component>(meter, "progressLocText");
            _progressText = progLoc != null ? progLoc.GetComponent<TMP_Text>() : null;
        }

        _play = GetField<Button>(_comp!, "playButton");
        _retry = GetField<Button>(_comp!, "retryButton");
        _support = GetField<Button>(_comp!, "supportButton");

        var errorPopup = GetField<Component>(_comp!, "errorPopup");
        _errorPopupGo = errorPopup != null ? errorPopup.gameObject : null;

        _releaseNotesView = GetField<Component>(_comp!, "releaseNotesView");
        if (_releaseNotesView != null)
        {
            _releaseNotesButton = GetField<Button>(_releaseNotesView, "releaseNotesButton");
            _notesContent = GetField<Transform>(_releaseNotesView, "contentTransform");
            _notesVersion = GetField<TMP_Text>(_releaseNotesView, "versionText");
            _notesDate = GetField<TMP_Text>(_releaseNotesView, "dateText");
        }

        RebuildNav();

        // Don't auto-announce a focus on entry — the status line is empty at Start() and the first
        // real phase ("Checking…/Downloading…") lands via PollStatus next frame. Just seat a silent
        // focus so the arrow keys have somewhere to start.
        _nav.FocusFirst(announce: false);

        Log.Info($"Updater wired — status:{_statusText != null} progress:{_progressText != null} " +
                 $"play:{_play != null} retry:{_retry != null} support:{_support != null} " +
                 $"notes:{_releaseNotesButton != null} error:{_errorPopupGo != null}");
    }

    // Reading order, top-to-bottom: RELEASE NOTES (top-left) → SUPPORT → RETRY → PLAY (bottom-right).
    // play/retry start active-but-non-interactable (UpdaterComponent.Start sets interactable=false,
    // not SetActive(false)), so they're always in the list and read ", unavailable" until enabled —
    // the desktop convention KeyboardNavigator already implements.
    private void RebuildNav()
    {
        var items = new List<Selectable>();
        void Add(Button? b) { if (b != null && b.gameObject.activeInHierarchy) items.Add(b); }
        Add(_releaseNotesButton);
        Add(_support);
        Add(_retry);
        Add(_play);

        _nav.SetItems(items);
        _nav.ForceFlatVertical();   // a handful of scattered buttons — predictable Up/Down beats auto-detected axes
        _nav.AllowTab = true;

        // Escape closes the release-notes panel when it's open; otherwise no-op (the updater has no
        // cancel/quit affordance).
        _nav.OnBack = () =>
        {
            if (_notesWereOpen && _releaseNotesButton != null)
                _releaseNotesButton.onClick?.Invoke();
        };
    }

    private void PollStatus()
    {
        var s = _statusText != null ? _statusText.text : null;
        if (string.IsNullOrWhiteSpace(s) || s == _lastStatus) return;
        _lastStatus = s!;
        // The game's own localized phase text ("Downloading…", "Update complete!", …). Barge in:
        // a new phase supersedes the previous one.
        _speech.Say(s!, interrupt: true);
    }

    private void PollProgress()
    {
        var updater = TPCI.Updater.Updater.Instance;
        if (updater == null) return;
        // Coarse milestones at 25 / 50 / 75 so a long download isn't silent, without chattering every
        // frame. 100% is left to the "Update complete!" status line (PollStatus), which is clearer.
        int milestone = Mathf.Clamp(Mathf.FloorToInt(updater.Progress * 4f), 0, 4);
        if (milestone <= _lastProgressMilestone || milestone >= 4) { _lastProgressMilestone = Mathf.Max(_lastProgressMilestone, milestone); return; }
        _lastProgressMilestone = milestone;
        // Prefer the meter's own "%": it's the game's text and already localized.
        var pct = _progressText != null && !string.IsNullOrWhiteSpace(_progressText.text)
            ? _progressText.text.Trim()
            : (milestone * 25) + "%";
        _speech.Say(pct, interrupt: false);   // queue after the phase status, don't cut it off
    }

    private void PollError()
    {
        bool active = _errorPopupGo != null && _errorPopupGo.activeInHierarchy;
        if (active && !_errorWasActive)
        {
            var msg = ReadAllText(_errorPopupGo!);
            if (!string.IsNullOrWhiteSpace(msg)) _speech.Say(msg!, interrupt: true);
        }
        _errorWasActive = active;
    }

    private void PollNotes()
    {
        if (_releaseNotesView == null) return;
        // isReleasePanelOpen flips at the end of the open/close animation, so a false→true edge means
        // the panel has finished opening and its content (populated once at Initialize) is readable.
        bool open = GetValue<bool>(_releaseNotesView, "isReleasePanelOpen");
        if (open && !_notesWereOpen)
        {
            var notes = ReadNotes();
            if (!string.IsNullOrWhiteSpace(notes)) _speech.Say(notes!, interrupt: true);
        }
        _notesWereOpen = open;
    }

    // When PLAY or RETRY becomes usable (success / failure resolved), move focus onto it and announce
    // it — queued (interrupt:false) so it reads after the status line that just changed
    // ("Update complete!" → "Play, 4 of 4"). This is what lets the user land on the actionable button
    // the moment it's live without hunting for it.
    private void PollButtonReadiness()
    {
        bool playUsable = _play != null && _play.isActiveAndEnabled && _play.interactable;
        if (playUsable && !_playWasUsable) FocusButton(_play!);
        _playWasUsable = playUsable;

        bool retryUsable = _retry != null && _retry.isActiveAndEnabled && _retry.interactable;
        if (retryUsable && !_retryWasUsable) FocusButton(_retry!);
        _retryWasUsable = retryUsable;
    }

    private void FocusButton(Selectable s)
    {
        var items = _nav.Items;
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], s)) { _nav.FocusIndex(i, announce: true, interrupt: false); return; }
    }

    private string? ReadNotes()
    {
        var sb = new StringBuilder();
        Append(sb, _notesVersion?.text);
        Append(sb, _notesDate?.text);
        if (_notesContent != null) Append(sb, ReadAllText(_notesContent.gameObject));
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void Append(StringBuilder sb, string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (sb.Length > 0) sb.Append(". ");
        sb.Append(s!.Trim());
    }

    // Concatenate every non-empty visible TMP label under a GameObject, in hierarchy order.
    private static string? ReadAllText(GameObject go)
    {
        var sb = new StringBuilder();
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            Append(sb, t.text);
        return sb.Length == 0 ? null : sb.ToString();
    }

    // Reflect a reference-typed private field, walking up the type hierarchy (fields live on
    // UpdaterComponent / MeterController / ReleaseNotesView, which may have base types).
    private static T? GetField<T>(object obj, string name) where T : class
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            var fi = t.GetField(name, PrivInst);
            if (fi != null) return fi.GetValue(obj) as T;
        }
        return null;
    }

    private static T GetValue<T>(object obj, string name)
    {
        for (var t = obj.GetType(); t != null; t = t.BaseType)
        {
            var fi = t.GetField(name, PrivInst);
            if (fi != null) return (T)fi.GetValue(obj);
        }
        return default!;
    }
}
