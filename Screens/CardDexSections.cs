using System;
using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.CardDex;
using TPCI.Localization;
using TPCI.Rainier.CardDex;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// The two non-Selectable nav sections of the CARD DEX screen. Both navigate the game's own DATA
// (not the virtualized EnhancedScroller cells, which only spawn the on-screen handful) and drive the
// game's real endpoints — see [[feedback-keyboard-complete-no-mouse]], [[project-deck-editor]].
//
//   • CardDexCarouselSection — the horizontal strip of expansion SETS. Left/Right call the carousel's
//     own ArrowScroll, which loads that set's cards into the grid below (exactly what the < > arrows
//     do). One logical row, so Up/Down always escape to the adjacent section.
//   • CardDexGridSection — the card grid. Navigates the gallery's curCollection list as a grid
//     (Left/Right step one card in sort order, Up/Down jump a row); Enter opens the card-detail
//     overlay for that stack — the same overlay a tile click opens.

// ---- set carousel ----------------------------------------------------------------------------

internal sealed class CardDexCarouselSection : INavSection
{
    private readonly SpeechEngine _speech;
    private readonly CollectionSetCarousel _carousel;
    private readonly Func<CardDexClassification> _classification;

    private static readonly FieldInfo? DataField =
        AccessTools.Field(typeof(CollectionSetCarousel), "_carouselObjectsData");

    public CardDexCarouselSection(SpeechEngine speech, CollectionSetCarousel carousel, Func<CardDexClassification> classification)
    {
        _speech = speech;
        _carousel = carousel;
        _classification = classification;
    }

    private List<CollectionCarouselObjectsData> Data()
        => (DataField?.GetValue(_carousel) as List<CollectionCarouselObjectsData>) ?? new List<CollectionCarouselObjectsData>();

    private int CurrentIndex()
    {
        var data = Data();
        var id = HUBCardDexScreenController.LoadedSetId;
        for (int i = 0; i < data.Count; i++)
            if (data[i] != null && data[i].setId == id) return i;
        return data.Count > 0 ? 0 : -1;
    }

    public int Index => CurrentIndex();
    public int Count => Data().Count;
    public bool IsAtTopRow => true;      // single horizontal row → Up always escapes upward
    public bool IsAtBottomRow => true;   // …and Down always escapes downward
    public int CurrentColumn => 0;

    public void FocusFirst(bool announce = true, bool interruptPrior = true) { if (announce) Announce(interruptPrior, withSection: true); }
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) { if (announce) Announce(interruptPrior, withSection: true); }
    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) { if (announce) Announce(interruptPrior, withSection: true); }
    public void RefocusCurrent(bool announce = true, bool interrupt = true) { if (announce) Announce(interrupt, withSection: true); }

    public void Tick()
    {
        if (!Application.isFocused) return;
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { _carousel.ArrowScroll(-1); Announce(true, withSection: false); }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { _carousel.ArrowScroll(1); Announce(true, withSection: false); }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) Announce(true, withSection: false);
    }

    private void Announce(bool interrupt, bool withSection)
    {
        var data = Data();
        int idx = CurrentIndex();
        if (idx < 0 || idx >= data.Count) { _speech.Say("Sets", interrupt); return; }
        var label = SetLabel(data[idx], idx, data.Count);
        if (withSection) { _speech.Say("Sets", interrupt); _speech.Say(label, interrupt: false); }
        else _speech.Say(label, interrupt);
    }

    private string SetLabel(CollectionCarouselObjectsData d, int idx, int total)
    {
        var name = CardDexText.Loc("tcg_" + (d.setId ?? string.Empty).ToLowerInvariant()) ?? d.setId ?? "Set";
        var parts = new List<string> { name };
        try
        {
            var prog = d.setProgress != null ? d.setProgress.SetProgress : null;
            if (prog != null && prog.TryGetValue(_classification(), out var p) && p != null)
                parts.Add($"{p.CardProgress} of {p.CardTotal} collected");
        }
        catch { }
        if (d.setProgress != null && d.setProgress.HasNewCards) parts.Add("new cards");
        parts.Add($"set {idx + 1} of {total}");
        return string.Join(", ", parts);
    }
}

// ---- card grid -------------------------------------------------------------------------------

internal sealed class CardDexGridSection : INavSection
{
    private readonly SpeechEngine _speech;
    private readonly CardCollectionGalleryView _gallery;
    private readonly Action<ArchetypeDBCards> _openDetail;

    private static readonly FieldInfo? CollectionField =
        AccessTools.Field(typeof(CardCollectionGalleryView), "curCollection");
    private static readonly FieldInfo? ContentWindowField =
        AccessTools.Field(typeof(CardCollectionGalleryView), "contentWindow");
    private static readonly FieldInfo? NumColumnsField =
        AccessTools.Field(typeof(CardDexScrollerController), "numColumns");

    private int _index;

    public CardDexGridSection(SpeechEngine speech, CardCollectionGalleryView gallery, Action<ArchetypeDBCards> openDetail)
    {
        _speech = speech;
        _gallery = gallery;
        _openDetail = openDetail;
    }

    private List<ArchetypeDBCards> Cards()
        => (CollectionField?.GetValue(_gallery) as List<ArchetypeDBCards>) ?? new List<ArchetypeDBCards>();

    private int Columns()
    {
        try
        {
            var cw = ContentWindowField?.GetValue(_gallery);
            if (cw != null && NumColumnsField?.GetValue(cw) is int n && n > 0) return n;
        }
        catch { }
        return 4;
    }

    public int Index => _index;
    public int Count => Cards().Count;
    public int CurrentColumn { get { var c = Columns(); return c > 0 && _index >= 0 ? _index % c : 0; } }

    public bool IsAtTopRow => _index >= 0 && _index < Columns();
    public bool IsAtBottomRow
    {
        get
        {
            int count = Cards().Count, cols = Columns();
            if (count == 0 || _index < 0 || cols <= 0) return true;
            int lastRowStart = ((count - 1) / cols) * cols;
            return _index >= lastRowStart;
        }
    }

    public void FocusFirst(bool announce = true, bool interruptPrior = true)
    {
        _index = Cards().Count > 0 ? 0 : -1;
        if (announce) Speak(interruptPrior, withSection: true);
    }

    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        var count = Cards().Count; var cols = Columns();
        if (count == 0) { _index = -1; return; }
        _index = preferredColumn < 0 ? 0 : Mathf.Min(preferredColumn, Mathf.Min(cols - 1, count - 1));
        if (announce) Speak(interruptPrior, withSection: true);
    }

    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        var count = Cards().Count; var cols = Columns();
        if (count == 0) { _index = -1; return; }
        int lastRowStart = ((count - 1) / cols) * cols;
        _index = preferredColumn < 0 ? lastRowStart : Mathf.Min(lastRowStart + preferredColumn, count - 1);
        if (announce) Speak(interruptPrior, withSection: true);
    }

    public void RefocusCurrent(bool announce = true, bool interrupt = true)
    {
        var count = Cards().Count;
        if (count == 0) { _index = -1; return; }
        if (_index < 0 || _index >= count) _index = 0;
        if (announce) Speak(interrupt, withSection: true);
    }

    // Put the cursor back on a specific card (clamped) — used to restore position after the
    // card-detail overlay closes.
    public void RestoreIndex(int i)
    {
        var count = Cards().Count;
        _index = count == 0 ? -1 : Mathf.Clamp(i, 0, count - 1);
    }

    public void Tick()
    {
        if (!Application.isFocused) return;
        var count = Cards().Count;
        if (count == 0) return;
        var cols = Columns();

        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { if (_index > 0) { _index--; Speak(true, false); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { if (_index < count - 1) { _index++; Speak(true, false); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (_index - cols >= 0) { _index -= cols; Speak(true, false); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (_index + cols < count) { _index += cols; Speak(true, false); } else if (_index < count - 1) { _index = count - 1; Speak(true, false); } }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            var cards = Cards();
            if (_index >= 0 && _index < cards.Count && !cards[_index].IsDefault) _openDetail(cards[_index]);
        }
    }

    private void Speak(bool interrupt, bool withSection)
    {
        var cards = Cards();
        if (_index < 0 || _index >= cards.Count) { _speech.Say("Card collection, no cards", interrupt); return; }
        var label = CardDexText.CardStackLabel(cards[_index]);
        if (withSection) { _speech.Say("Card collection", interrupt); _speech.Say(Announce.Item(label, null, _index + 1, cards.Count), interrupt: false); }
        else _speech.Say(Announce.Item(label, null, _index + 1, cards.Count), interrupt);
    }
}
