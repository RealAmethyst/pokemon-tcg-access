using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CardDatabase.DataAccess;
using HarmonyLib;
using MatchLogic;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Card-detail overlay — driver: AddCardOverlayManager : BigCardOverlayController : OverlayScreenGroup,
// the big single-card view the deck editor's I key opens (and the library opens). It shows the card,
// its count in the deck, owned/craftable info, the rarity-VERSION drawer, and real + / − buttons.
//
// CRUCIAL: ownership and deck legality are keyed by the FULL CardID, which ENCODES the rarity — e.g.
// "budew" and "budew" reverse-holo are different CardIDs with separate owned counts, and the game
// validates per exact CardID. So every count here is PER VERSION (TotalOwnedQuantityForSpecificCard /
// TotalDeckQuantityForSpecificCard), never the archetype-wide TotalOwnedQuantity(). Reading the latter
// was the "says I own 2 but the deck is still invalid" bug: you owned 2 across two rarities but 0 of
// the one actually in the deck. (The game's own rarity bar uses the per-card numbers too —
// AddCardOverlayManager.SetupRarityInfoBar → RelativeDeckQuantityForSpecificCard.)
//
// Three sections, Up/Down to move between them (only the ones that apply are present):
//   • Card     — the selected version: "name, type, rarity, N in deck, owned / not owned".
//                Left/Right browse other cards; +/− add/remove THIS version.
//   • Versions — one entry per rarity printing, read straight off the game's RarityDrawerParts
//                buttons: "rarity, N owned, M in deck, [not owned], [selected]". Enter picks it
//                (switches what +/− adds), exactly like clicking the rarity stack. Present only
//                when there is more than one version.
//   • Details  — HP / type / attacks / rules (CardText.DetailLines). Up/Down read line by line, the
//                same as normal card reading; Up at the first line steps back up to the section above.
//
// We drive the game's OWN widgets: +/− → AddClicked/RemoveClicked (= the deck editor's AddCard/
// RemoveCard the drag's drop also calls); a version pick → the ClickableCardStack button's onClick
// (= SelectedAlternateRarity). Activates off the overlay's Setup, pops on its OnDeActivate.
internal sealed class AddCardOverlayScreen : IScreenHandler
{
    private enum Section { Card, Versions, Details }

    private static AddCardOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private AddCardOverlayManager? _overlay;
    private bool _active;

    private Section _section = Section.Card;
    private int _verIndex;
    private int _lineIndex;
    private readonly List<string> _lines = new();
    private string? _linesCardId;

    // 1.5.0 renamed these protected properties to PascalCase (selectedCard -> SelectedCard); the
    // getters are case-sensitive, so the old lowercase lookup returned null and the overlay couldn't
    // read which card/stack was selected.
    private static readonly MethodInfo? CardGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedCard");
    private static readonly MethodInfo? StackGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedStack");
    // The game's rarity-version drawer + the per-version stack buttons it builds. Private, so reflected.
    private static readonly FieldInfo? RarityDrawerField = AccessTools.Field(typeof(AddCardOverlayManager), "rarityDrawerParts");
    private static readonly FieldInfo? RarityInstancesField = AccessTools.Field(typeof(RarityDrawerParts), "_cardRarityInstances");
    private static readonly FieldInfo? RarityNumActiveField = AccessTools.Field(typeof(RarityDrawerParts), "_numActiveInstances");

    public string Name => "Card details";

    public AddCardOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new AddCardOverlayScreen(router);

        // 1.5.0 expanded Setup to 5 params (added preferredDisplayCard + archetypeChangedAction).
        // BigCardOverlayController also declares a 2-param Setup, so name-only resolution is ambiguous —
        // we must match the full signature exactly.
        var setup = AccessTools.Method(typeof(AddCardOverlayManager), "Setup",
            new[] { typeof(ArchetypeDBCards), typeof(ICardDataRow), typeof(AddCardOverlayManager.CardLocation), typeof(GameMode), typeof(AddCardOverlayManager.ArchetypeChangedDelegate) });
        if (setup == null)
        {
            Log.Error("AddCardOverlayScreen: could not resolve AddCardOverlayManager.Setup");
            return;
        }
        harmony.Patch(setup, postfix: new HarmonyMethod(AccessTools.Method(typeof(AddCardOverlayScreen), nameof(Setup_Postfix))));

        var deact = AccessTools.Method(typeof(AddCardOverlayManager), "OnDeActivate");
        if (deact != null)
            harmony.Patch(deact, postfix: new HarmonyMethod(AccessTools.Method(typeof(AddCardOverlayScreen), nameof(Deactivate_Postfix))));

        Log.Info("AddCardOverlayScreen installed");
    }

    private static void Setup_Postfix(AddCardOverlayManager __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
        else
        {
            _instance.ResetToCard();
            _instance.AnnounceCard(interrupt: true);   // re-Setup while open (rare) — just re-read
        }
    }

    private static void Deactivate_Postfix(AddCardOverlayManager __instance)
    {
        if (_instance == null || _instance._overlay != __instance || !_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._overlay = null;
    }

    public void OnEnter()
    {
        ResetToCard();
        _router.Speech.Say(Name, interrupt: true);
        AnnounceCard(interrupt: false);
    }

    public void OnResume() { ResetToCard(); AnnounceCard(interrupt: true); }
    public void OnExit() { }
    public void RespeakAll() => AnnounceCard(interrupt: false);

    public string StateSignature()
    {
        if (_overlay == null) return "CardDetail|dead";
        var card = SelectedCard();
        return $"CardDetail|{_section}|{card?.CardID}|{DeckQty(card)}";
    }

    public void Tick()
    {
        if (_overlay == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            _overlay.CloseAddCardOverlay();
            return;
        }

        // +/− always act on the currently-selected version, from any section.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Equals) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadPlus)
            || UnityEngine.Input.GetKeyDown(KeyCode.Plus)) { Adjust(add: true); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Minus) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadMinus)) { Adjust(add: false); return; }

        // Up/Down move between sections — EXCEPT inside Details, where they read the card's lines (like
        // normal card reading), stepping back up to the section above only at the first line. Left/Right
        // move within the horizontal sections: other cards (Card) and other printings (Versions).
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (_section == Section.Details) StepLine(+1); else MoveSection(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (_section == Section.Details) DetailUp(); else MoveSection(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { if (_section != Section.Details) MoveWithin(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { if (_section != Section.Details) MoveWithin(-1); return; }

        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) { Activate(); return; }
    }

    private void ResetToCard()
    {
        HideVersions();
        _section = Section.Card;
        _verIndex = 0;
        _lineIndex = 0;
        _linesCardId = null;
    }

    // ───────────────────────── section movement ─────────────────────────

    // The sections present for the current card, top-to-bottom. Versions only when there's a choice
    // to make; Details only when the card has detail lines.
    private List<Section> Sections()
    {
        var list = new List<Section> { Section.Card };
        if (VersionStacks().Count > 1) list.Add(Section.Versions);
        EnsureLines();
        if (_lines.Count > 0) list.Add(Section.Details);
        return list;
    }

    private void MoveSection(int dir)
    {
        var secs = Sections();
        int cur = secs.IndexOf(_section);
        if (cur < 0) cur = 0;
        int next = cur + dir;
        if (next < 0 || next >= secs.Count) return;   // stop at the ends
        if (_section == Section.Versions) HideVersions();
        _section = secs[next];
        EnterSection();
    }

    private void EnterSection()
    {
        switch (_section)
        {
            case Section.Card:
                AnnounceCard(interrupt: true);
                break;
            case Section.Versions:
                ShowVersions();
                _verIndex = CurrentVersionIndex();
                _router.Speech.Say("Versions", interrupt: true);
                AnnounceVersion(interrupt: false);
                break;
            case Section.Details:
                EnsureLines();
                _lineIndex = 0;
                _router.Speech.Say("Details", interrupt: true);
                if (_lines.Count > 0) _router.Speech.Say(_lines[0], interrupt: false);
                break;
        }
    }

    // Left/Right within a horizontal section: browse cards (Card) or printings (Versions). Details is a
    // vertical line list navigated with Up/Down (StepLine / DetailUp), not Left/Right.
    private void MoveWithin(int dir)
    {
        switch (_section)
        {
            case Section.Card: Flip(dir); break;
            case Section.Versions: StepVersion(dir); break;
        }
    }

    private void Activate()
    {
        if (_section == Section.Versions) { ChooseVersion(); return; }
        _overlay?.CloseAddCardOverlay();   // Card / Details: Enter closes (Escape does too)
    }

    // ───────────────────────── Card section ─────────────────────────

    private void Flip(int dir)
    {
        if (_overlay == null) return;
        if (dir > 0) _overlay.NextClicked(); else _overlay.PrevClicked();
        if (!_active || _overlay == null) return;   // flipping past the end closes the overlay
        ResetToCard();
        AnnounceCard(interrupt: true);
    }

    private void AnnounceCard(bool interrupt)
    {
        var card = SelectedCard();
        if (card == null) { _router.Speech.Say(Name, interrupt); return; }
        var row = ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(card.CardID);
        string name = row != null ? CardText.NameWithType(row) : card.CardID;
        string? rarity = CardText.RarityWithVariant(card) ?? CardText.RarityWithVariant(row);
        int inDeck = DeckQty(card), owned = Owned(card);

        var sb = new StringBuilder(name);
        if (!string.IsNullOrEmpty(rarity)) sb.Append(", ").Append(rarity);
        var set = CardText.SetName(card);
        if (!string.IsNullOrEmpty(set)) sb.Append(", ").Append(set);
        sb.Append(", ").Append(inDeck).Append(" in deck");
        if (owned > 0) sb.Append(", ").Append(owned).Append(" owned");
        else
        {
            sb.Append(", not owned");
            int other = TotalOwnedAllVersions();
            if (other > 0) sb.Append(", ").Append(other).Append(" owned in other versions");
        }
        _router.Speech.Say(sb.ToString(), interrupt);
    }

    private void Adjust(bool add)
    {
        // Detect a real change via the archetype's total deck count (robust to the game switching the
        // selected version on remove); report the now-selected version's count.
        int before = TotalDeckQty();
        if (add) _overlay!.AddClicked(); else _overlay!.RemoveClicked();
        int after = TotalDeckQty();
        if (after == before)
        {
            _router.Speech.Say(add ? "Can't add more" : "None in deck", interrupt: true);
            return;
        }
        _router.Speech.Say($"{(add ? "Added" : "Removed")}, {DeckQty(SelectedCard())} in deck", interrupt: true);
    }

    // ───────────────────────── Versions section ─────────────────────────

    private void StepVersion(int dir)
    {
        var list = VersionStacks();
        if (list.Count == 0) return;
        int next = _verIndex + dir;
        if (next < 0 || next >= list.Count) return;
        _verIndex = next;
        SelectVersionVisual(list[_verIndex]);
        AnnounceVersion(interrupt: true);
    }

    private void ChooseVersion()
    {
        var list = VersionStacks();
        if (_verIndex < 0 || _verIndex >= list.Count) return;
        Button? btn = null;
        try { btn = list[_verIndex].GetClickableCardStackButton(); } catch { }
        if (btn == null) return;
        btn.onClick?.Invoke();        // → SelectedAlternateRarity (+ the game hides the drawer)
        HideVersions();
        _section = Section.Card;
        _linesCardId = null;          // version changed → detail lines change
        AnnounceCard(interrupt: true);
    }

    private void AnnounceVersion(bool interrupt)
    {
        var list = VersionStacks();
        if (list.Count == 0) { _router.Speech.Say("No versions", interrupt); return; }
        if (_verIndex < 0) _verIndex = 0;
        if (_verIndex >= list.Count) _verIndex = list.Count - 1;
        var card = list[_verIndex].StoredCard;
        string rarity = CardText.RarityWithVariant(card) ?? $"version {_verIndex + 1}";
        int owned = Owned(card), inDeck = DeckQty(card);

        var sb = new StringBuilder(rarity);
        var set = CardText.SetName(card);
        if (!string.IsNullOrEmpty(set)) sb.Append(", ").Append(set);
        sb.Append(", ").Append(owned).Append(" owned, ").Append(inDeck).Append(" in deck");
        if (owned == 0) sb.Append(", not owned");
        if (IsSelectedVersion(card)) sb.Append(", selected");
        sb.Append(", ").Append(_verIndex + 1).Append(" of ").Append(list.Count);
        _router.Speech.Say(sb.ToString(), interrupt);
    }

    private int CurrentVersionIndex()
    {
        var list = VersionStacks();
        for (int i = 0; i < list.Count; i++)
            if (IsSelectedVersion(list[i].StoredCard)) return i;
        return 0;
    }

    private RarityDrawerParts? RarityDrawer()
    {
        if (_overlay == null || RarityDrawerField == null) return null;
        try { return RarityDrawerField.GetValue(_overlay) as RarityDrawerParts; } catch { return null; }
    }

    // The active per-version stack buttons the game built, in the drawer's own (rarity-sorted) order.
    // activeSelf (not activeInHierarchy) so they enumerate even while the drawer is visually collapsed.
    private List<ClickableCardStack> VersionStacks()
    {
        var result = new List<ClickableCardStack>();
        var drawer = RarityDrawer();
        if (drawer == null) return result;
        try
        {
            if (RarityInstancesField?.GetValue(drawer) is not IList raw) return result;
            int n = raw.Count;
            if (RarityNumActiveField?.GetValue(drawer) is int active) n = Mathf.Min(active, raw.Count);
            for (int i = 0; i < n; i++)
                if (raw[i] is ClickableCardStack cs && cs != null && cs.gameObject.activeSelf && cs.StoredCard != null)
                    result.Add(cs);
        }
        catch { }
        return result;
    }

    private void SelectVersionVisual(ClickableCardStack cs)
    {
        try { var b = cs.GetClickableCardStackButton(); if (b != null) b.Select(); } catch { }
    }

    private void ShowVersions() { try { RarityDrawer()?.ShowRarityDrawer(); } catch { } }
    private void HideVersions() { try { RarityDrawer()?.HideRarityDrawer(); } catch { } }

    private bool IsSelectedVersion(ICardDataRow? card)
    {
        var sel = SelectedCard();
        return card != null && sel != null && string.Equals(card.CardID, sel.CardID, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────── Details section ─────────────────────────

    private void StepLine(int dir)
    {
        EnsureLines();
        if (_lines.Count == 0) return;
        int next = _lineIndex + dir;
        if (next < 0 || next >= _lines.Count) return;
        _lineIndex = next;
        _router.Speech.Say(_lines[_lineIndex], interrupt: true);
    }

    // Up inside Details: previous line, or — when already at the first line — escape to the section above.
    private void DetailUp()
    {
        if (_lineIndex > 0) StepLine(-1);
        else MoveSection(-1);
    }

    private void EnsureLines()
    {
        var card = SelectedCard();
        var id = card?.CardID;
        if (id != null && id == _linesCardId) return;
        _lines.Clear();
        _linesCardId = id;
        if (id == null) return;
        var row = ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(id);
        if (row != null) _lines.AddRange(CardText.DetailLines(row));
    }

    // ───────────────────────── data ─────────────────────────

    private ICardDataRow? SelectedCard()
    {
        try { return CardGetter?.Invoke(_overlay, null) as ICardDataRow; } catch { return null; }
    }

    private ArchetypeDBCards? SelectedStack()
    {
        try { return StackGetter?.Invoke(_overlay, null) is ArchetypeDBCards s ? s : (ArchetypeDBCards?)null; }
        catch { return null; }
    }

    // This version's copies in the deck (keyed by exact CardID).
    private int DeckQty(ICardDataRow? card)
    {
        if (card == null) return 0;
        var s = SelectedStack();
        try { return s is { } st && !st.IsDefault ? st.TotalDeckQuantityForSpecificCard(card.CardID) : 0; } catch { return 0; }
    }

    // This version's copies owned (keyed by exact CardID) — the number that actually gates legality.
    private int Owned(ICardDataRow? card)
    {
        if (card == null) return 0;
        var s = SelectedStack();
        try { return s is { } st && !st.IsDefault ? st.TotalOwnedQuantityForSpecificCard(card.CardID) : 0; } catch { return 0; }
    }

    // Copies owned across every rarity of this card — used only to explain a not-owned version.
    private int TotalOwnedAllVersions()
    {
        var s = SelectedStack();
        try { return s is { } st && !st.IsDefault ? st.TotalOwnedQuantity() : 0; } catch { return 0; }
    }

    private int TotalDeckQty()
    {
        var s = SelectedStack();
        try { return s is { } st && !st.IsDefault ? st.TotalDeckQuantity() : 0; } catch { return 0; }
    }
}
