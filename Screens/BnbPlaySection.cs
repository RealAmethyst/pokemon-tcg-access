using System;
using System.Linq;
using System.Reflection;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// The BUILD & BATTLE tab's content, modeled as a SECTION of HOME (like the Learning Lab list). It's an
// event buy-in screen (HUBBnBBuyInScreenController → BnBBuyInScreenViewBindings): a captioned-image
// CAROUSEL of instruction slides, prev/next page buttons + page pips, the PLAY / buy-in button
// (EventBuyInButton, shows the entry cost or FREE), and an "ENDS IN …" countdown. The slide captions
// are info TEXT (CaptionedImagePagesUi.captionDisplay), not Selectables, so the generic sweep only
// surfaced the prev/next buttons — the player couldn't read what each slide says or hear the timer.
//
// Two rows: Instructions (Left/Right pages the carousel, reading each caption + "page X of N") and Play
// (Enter presses the buy-in button). Entry reads the event timer first. Refs are resolved live from the
// controller, so they always match the current event.
internal sealed class BnbPlaySection : INavSection
{
    private readonly SpeechEngine _speech;
    private readonly Action _escapeToParent;

    private TMP_Text? _caption;        // current slide caption (updates when the page changes)
    private Button? _prev;             // previous-page button
    private Button? _next;             // next-page button
    private Button? _play;             // the buy-in / PLAY button
    private GameObject? _buyInRoot;    // EventBuyInButton root (for the full PLAY + cost/FREE label)
    private Toggle[] _pips = Array.Empty<Toggle>();   // page dots — count + which is on = page X of N
    private TMP_Text? _timerTitle;     // "ENDS IN"
    private TMP_Text? _timerValue;     // "1W 4D"

    private int _index;                // 0 = instructions, 1 = play
    private int _rows;
    private string _lastCaption = "";  // last caption we spoke — so the poll only fires on a real change

    // The section is recreated on every HUBHomeScreen rebuild, and paging can itself trigger a rebuild
    // (prev/next may disable at the first/last slide → the content fingerprint changes). Remember the row
    // across rebuilds so focus doesn't jump back, and seed _lastCaption from the live caption so a rebuild
    // never re-announces the slide. Single B&B section at a time, so a static is safe.
    private static int s_rememberedRow;

    public BnbPlaySection(SpeechEngine speech, Action escapeToParent)
    {
        _speech = speech;
        _escapeToParent = escapeToParent;
        Resolve();
        _index = _rows > 0 ? Mathf.Clamp(s_rememberedRow, 0, _rows - 1) : 0;
        _lastCaption = _caption != null ? (_caption.text?.Trim() ?? "") : "";
    }

    private void Resolve()
    {
        var ctrl = UnityEngine.Object.FindAnyObjectByType<HUBBnBBuyInScreenController>();
        if (ctrl == null) { _rows = 0; return; }

        var view = ReadField<Component>(ctrl, "buyInScreen");
        var carousel = view != null ? ReadField<Component>(view, "instructionCarousel") : null;
        _caption = carousel != null ? ReadField<TMP_Text>(carousel, "captionDisplay") : null;

        var buyIn = view != null ? ReadField<Component>(view, "eventBuyInButton") : null;
        _buyInRoot = buyIn != null ? buyIn.gameObject : null;
        _play = buyIn != null ? ReadField<Button>(buyIn, "button") : null;

        var timer = ReadField<Component>(ctrl, "timerDisplay");
        _timerTitle = timer != null ? ReadField<TMP_Text>(timer, "titleText") : null;
        _timerValue = timer != null ? ReadField<TMP_Text>(timer, "timeLeftText") : null;

        if (view != null)
        {
            var buttons = view.GetComponentsInChildren<Button>(includeInactive: false);
            _prev = buttons.FirstOrDefault(b => b.gameObject.name.IndexOf("Prev", StringComparison.OrdinalIgnoreCase) >= 0);
            _next = buttons.FirstOrDefault(b => b.gameObject.name.IndexOf("Next", StringComparison.OrdinalIgnoreCase) >= 0);
            _pips = view.GetComponentsInChildren<Toggle>(includeInactive: false)
                        .Where(t => t.gameObject.name.IndexOf("Pip", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToArray();
        }

        // Instructions row exists if there's a caption to read; Play row if there's a buy-in button.
        _rows = (_caption != null ? 1 : 0) + (_play != null ? 1 : 0);
        Log.Info($"BnB section: caption={_caption != null}, play={_play != null}, prev={_prev != null}, next={_next != null}, pips={_pips.Length}");
    }

    // If there's no caption, the only row is Play (index 0). Map the logical row to its meaning.
    private bool HasInstructions => _caption != null;
    private bool IsInstructionsRow(int i) => HasInstructions && i == 0;
    private bool IsPlayRow(int i) => _play != null && i == _rows - 1 && (!HasInstructions || i == 1);

    public int Index => _index;
    public int Count => _rows;
    public bool IsAtTopRow => _index <= 0;
    public bool IsAtBottomRow => _index >= _rows - 1;
    public int CurrentColumn => 0;

    public void FocusFirst(bool announce = true, bool interruptPrior = true) { _index = 0; s_rememberedRow = 0; if (announce) AnnounceEntry(interruptPrior); }
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) { _index = 0; s_rememberedRow = 0; if (announce) AnnounceEntry(interruptPrior); }
    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) { _index = Math.Max(0, _rows - 1); s_rememberedRow = _index; if (announce) AnnounceRow(interruptPrior); }
    public void RefocusCurrent(bool announce = true, bool interrupt = true) { if (announce) AnnounceRow(interrupt); }

    public void Tick()
    {
        if (!Application.isFocused) return;
        // The carousel may update the caption a frame after the prev/next click (or via a slide
        // animation), so detect the change here and read the new slide — rather than reading it
        // immediately in ChangePage and catching the stale text.
        PollCaption();
        // Up/Down at the section edges are consumed by HUBHomeScreen for section escape; mid-section
        // they move between the two rows.
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { Move(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { Move(-1); return; }
        if (IsInstructionsRow(_index))
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { ChangePage(_next); return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { ChangePage(_prev); return; }
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (IsPlayRow(_index)) Press(_play);
            else AnnounceRow(interrupt: true);   // re-read the instructions slide
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
            _escapeToParent();
    }

    private void Move(int dir)
    {
        int next = _index + dir;
        if (next < 0 || next >= _rows) return;
        _index = next;
        s_rememberedRow = next;
        AnnounceRow(interrupt: true);
    }

    private void ChangePage(Button? btn)
    {
        // Turn the page via the game's own prev/next button; PollCaption reads the new slide once the
        // caption actually updates (sync or after the slide animation).
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) btn.onClick.Invoke();
    }

    // Read the new slide whenever the caption changes on its own (the result of a prev/next press).
    private void PollCaption()
    {
        if (!IsInstructionsRow(_index) || _caption == null) return;
        var cur = _caption.text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(cur) && cur != _lastCaption)
            AnnounceInstructions(interrupt: true, withRole: false);
    }

    private void Press(Button? btn)
    {
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) btn.onClick.Invoke();
    }

    private void AnnounceEntry(bool interrupt)
    {
        var timer = TimerText();
        if (!string.IsNullOrWhiteSpace(timer)) { _speech.Say(timer!, interrupt); AnnounceRow(interrupt: false); }
        else AnnounceRow(interrupt);
    }

    private void AnnounceRow(bool interrupt)
    {
        if (IsInstructionsRow(_index)) AnnounceInstructions(interrupt, withRole: true);
        else AnnouncePlay(interrupt);
    }

    private void AnnounceInstructions(bool interrupt, bool withRole)
    {
        var caption = _caption != null ? _caption.text?.Trim() : null;
        _lastCaption = caption ?? "";   // mark as spoken so PollCaption only fires on the next real change
        var page = PageText();
        var parts = new System.Collections.Generic.List<string>();
        if (withRole) parts.Add("Instructions");
        if (!string.IsNullOrWhiteSpace(page)) parts.Add(page!);
        if (!string.IsNullOrWhiteSpace(caption)) parts.Add(caption!);
        _speech.Say(parts.Count > 0 ? string.Join(", ", parts) : "Instructions", interrupt);
    }

    private void AnnouncePlay(bool interrupt)
    {
        // Read the whole buy-in button (PLAY + the FREE / ticket cost shown on it), not just "PLAY".
        string? label = null;
        if (_buyInRoot != null)
        {
            var words = _buyInRoot.GetComponentsInChildren<TMP_Text>(includeInactive: false)
                .Where(t => t != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text) && t.text.Any(char.IsLetterOrDigit))
                .Select(t => t.text!.Trim());
            label = string.Join(", ", words);
        }
        if (string.IsNullOrWhiteSpace(label)) label = _play != null ? KeyboardNavigator.ResolveBaseLabel(_play) : "Play";
        _speech.Say(label!, interrupt);
    }

    private string? PageText()
    {
        if (_pips.Length == 0) return null;
        int cur = Array.FindIndex(_pips, t => t != null && t.isOn);
        if (cur < 0) cur = 0;
        return $"page {cur + 1} of {_pips.Length}";
    }

    private string? TimerText()
    {
        var t = _timerTitle != null && _timerTitle.gameObject.activeInHierarchy ? _timerTitle.text?.Trim() : null;
        var v = _timerValue != null && _timerValue.gameObject.activeInHierarchy ? _timerValue.text?.Trim() : null;
        var parts = new[] { t, v }.Where(x => !string.IsNullOrWhiteSpace(x));
        return parts.Any() ? string.Join(" ", parts) : null;
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
}
