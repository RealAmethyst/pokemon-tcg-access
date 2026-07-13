using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CardDatabase.DataAccess;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// CARD DEX card-detail overlay — driver CardDexPurchaseCardOverlayManager : BigCardOverlayController :
// OverlayScreenGroup, the big single-card view a collection tile opens. It shows the card, its owned
// count, craft/exchange availability, and left/right arrows to browse the rest of the set.
//
// Sister to the deck editor's AddCardOverlayScreen (same BigCardOverlayController base) but simpler:
// the Card Dex has no deck (no +/− deck counts) and no rarity-version drawer — it's read-and-exchange.
//   • Card    — the card: "name, type, rarity, set, N owned / not owned[, exchangeable]". Left/Right
//               browse the previous/next card in the collection (the overlay's own Prev/NextClicked,
//               wired to the gallery's adjacent-card provider).
//   • Exchange — present whenever the game's purchase button is on screen. Enter opens the quantity/
//               cost popup (PurchaseCardButtonActions → CardPurchasePopupParts → CraftCardOverlayScreen),
//               exactly what clicking EXCHANGE does.
//   • Details — HP / type / attacks / rules (CardText.DetailLines), read line by line with Up/Down.
//
// Activates off the overlay's Setup, pops on its OnDeActivate. Ownership is read per the exact CardID
// (see [[project-card-rarity-variants]]).
internal sealed class CardDexCardOverlayScreen : IScreenHandler
{
    private enum Section { Card, Exchange, Details }

    private static CardDexCardOverlayScreen? _instance;

    // True while the Card Dex card detail is open — lets CraftCardOverlayScreen hand its "Exchanged"
    // confirmation back to this context too (the same popup it shows for the deck editor).
    public static bool IsOpen => _instance != null && _instance._active;

    private readonly ScreenRouter _router;
    private CardDexPurchaseCardOverlayManager? _overlay;
    private bool _active;

    private Section _section = Section.Card;
    private int _lineIndex;
    private readonly List<string> _lines = new();
    private string? _linesCardId;

    private static readonly MethodInfo? CardGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedCard");
    private static readonly MethodInfo? StackGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedStack");
    private static readonly FieldInfo? PurchasePartsField = AccessTools.Field(typeof(CardDexPurchaseCardOverlayManager), "addCardPurchaseParts");
    private static readonly FieldInfo? CountTextField = AccessTools.Field(typeof(CardDexPurchaseCardOverlayManager), "countText");

    public string Name => "Card details";

    public CardDexCardOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new CardDexCardOverlayScreen(router);

        // The Card Dex entry point is the 1-arg Setup declared on CardDexPurchaseCardOverlayManager
        // (it forwards to the base 2-arg Setup) — unique to this overlay, so the deck editor's overlay
        // isn't caught.
        var setup = AccessTools.Method(typeof(CardDexPurchaseCardOverlayManager), "Setup", new[] { typeof(ArchetypeDBCards) });
        if (setup == null)
        {
            Log.Error("CardDexCardOverlayScreen: could not resolve CardDexPurchaseCardOverlayManager.Setup");
            return;
        }
        harmony.Patch(setup, postfix: new HarmonyMethod(AccessTools.Method(typeof(CardDexCardOverlayScreen), nameof(Setup_Postfix))));

        var deact = AccessTools.Method(typeof(CardDexPurchaseCardOverlayManager), "OnDeActivate");
        if (deact != null)
            harmony.Patch(deact, postfix: new HarmonyMethod(AccessTools.Method(typeof(CardDexCardOverlayScreen), nameof(Deactivate_Postfix))));

        Log.Info("CardDexCardOverlayScreen installed");
    }

    private static void Setup_Postfix(CardDexPurchaseCardOverlayManager __instance)
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
            _instance.AnnounceCard(interrupt: true);
        }
    }

    private static void Deactivate_Postfix(CardDexPurchaseCardOverlayManager __instance)
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

    public void OnResume()
    {
        ResetToCard();
        if (CraftCardOverlayScreen.JustCrafted)
        {
            CraftCardOverlayScreen.JustCrafted = false;
            _router.Speech.Say("Exchanged", interrupt: true);
            AnnounceCard(interrupt: false);
        }
        else AnnounceCard(interrupt: true);
    }

    public void OnExit() { }
    public void RespeakAll() => AnnounceCard(interrupt: false);

    public string StateSignature()
    {
        if (_overlay == null) return "CardDexDetail|dead";
        var card = SelectedCard();
        return $"CardDexDetail|{_section}|{card?.CardID}|{Owned(card)}";
    }

    public void Tick()
    {
        if (_overlay == null) { _active = false; _router.Deactivate(this); return; }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            _overlay.ClosePurchaseCardOverlay();
            return;
        }

        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { if (_section == Section.Details) StepLine(+1); else MoveSection(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { if (_section == Section.Details) DetailUp(); else MoveSection(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { if (_section == Section.Card) Flip(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { if (_section == Section.Card) Flip(-1); return; }

        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) { Activate(); return; }
    }

    private void ResetToCard()
    {
        _section = Section.Card;
        _lineIndex = 0;
        _linesCardId = null;
    }

    // ---- section movement -----------------------------------------------------------------------

    private List<Section> Sections()
    {
        var list = new List<Section> { Section.Card };
        if (IsExchangeable()) list.Add(Section.Exchange);
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
        if (next < 0 || next >= secs.Count) return;
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
            case Section.Exchange:
                _router.Speech.Say(ExchangeLabel(), interrupt: true);
                break;
            case Section.Details:
                EnsureLines();
                _lineIndex = 0;
                _router.Speech.Say("Details", interrupt: true);
                if (_lines.Count > 0) _router.Speech.Say(_lines[0], interrupt: false);
                break;
        }
    }

    private void Activate()
    {
        if (_section == Section.Exchange) { OpenExchange(); return; }
        _overlay?.ClosePurchaseCardOverlay();   // Card / Details: Enter closes (Escape does too)
    }

    // ---- Card section ---------------------------------------------------------------------------

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
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID);
        string name = row != null ? CardText.NameWithType(row) : card.CardID;
        string? rarity = CardText.RarityWithVariant(card) ?? CardText.RarityWithVariant(row);
        int owned = Owned(card);

        var sb = new StringBuilder(name);
        if (!string.IsNullOrEmpty(rarity)) sb.Append(", ").Append(rarity);
        var set = CardText.SetName(card);
        if (!string.IsNullOrEmpty(set)) sb.Append(", ").Append(set);
        sb.Append(owned > 0 ? $", {owned} owned" : ", not owned");
        if (IsExchangeable()) sb.Append(", exchangeable");
        _router.Speech.Say(sb.ToString(), interrupt);
    }

    // ---- Exchange section -----------------------------------------------------------------------

    private Component? PurchaseParts()
    {
        if (_overlay == null) return null;
        try { return PurchasePartsField?.GetValue(_overlay) as Component; } catch { return null; }
    }

    private Button? ExchangeButton()
    {
        var p = PurchaseParts();
        return p != null ? p.GetComponentInParent<Button>(includeInactive: true) : null;
    }

    // The game shows addCardPurchaseParts only when the card can be exchanged (CardPurchaseEvaluator
    // says so) — that's our "Exchange section present" signal, matching the deck editor's read.
    private bool IsExchangeable()
    {
        var p = PurchaseParts();
        if (p != null && p.gameObject.activeInHierarchy) return true;
        var b = ExchangeButton();
        return b != null && b.isActiveAndEnabled;
    }

    private string ExchangeLabel()
    {
        var label = "Exchange";
        var b = ExchangeButton();
        if (b != null && !b.IsInteractable()) label += ", unavailable";
        return label;
    }

    private void OpenExchange()
    {
        if (_overlay == null) return;
        try { _overlay.PurchaseCardButtonActions(); } catch { }
    }

    // ---- Details section ------------------------------------------------------------------------

    private void EnsureLines()
    {
        var card = SelectedCard();
        if (card == null) { _lines.Clear(); _linesCardId = null; return; }
        if (_linesCardId == card.CardID) return;
        _lines.Clear();
        try
        {
            var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID);
            if (row != null) _lines.AddRange(CardText.DetailLines(row));
        }
        catch { }
        _linesCardId = card.CardID;
    }

    private void StepLine(int dir)
    {
        EnsureLines();
        if (_lines.Count == 0) return;
        int next = _lineIndex + dir;
        if (next < 0 || next >= _lines.Count) return;
        _lineIndex = next;
        _router.Speech.Say(_lines[_lineIndex], interrupt: true);
    }

    private void DetailUp()
    {
        if (_lineIndex <= 0) { MoveSection(-1); return; }   // step back to the section above at the first line
        StepLine(-1);
    }

    // ---- card data ------------------------------------------------------------------------------

    private ICardDataRow? SelectedCard()
    {
        if (_overlay == null || CardGetter == null) return null;
        try { return CardGetter.Invoke(_overlay, null) as ICardDataRow; } catch { return null; }
    }

    private int Owned(ICardDataRow? card)
    {
        if (_overlay == null || card == null || StackGetter == null) return 0;
        try
        {
            if (StackGetter.Invoke(_overlay, null) is ArchetypeDBCards stack)
                return stack.TotalOwnedQuantityForSpecificCard(card.CardID);
        }
        catch { }
        return 0;
    }
}
