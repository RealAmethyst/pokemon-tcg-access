using System;
using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using MatchLogic;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Card-detail overlay — the SECOND add/remove method (the designer wanted both). Driver:
// AddCardOverlayManager : BigCardOverlayController : OverlayScreenGroup — the big card view the
// game opens when you click a card in the deck or the library. It shows the full card, the count
// in the deck, owned/craftable info, and real + / − buttons.
//
// We drive its OWN buttons (AddClicked/RemoveClicked → the same HUBEditDeckController.AddCard/
// RemoveCard the drag's drop calls; NextClicked/PrevClicked to flip cards; CloseAddCardOverlay to
// dismiss). Add/remove legality is the game's: AddClicked no-ops when CanAddCard is false and
// RemoveClicked no-ops when none are in the deck, so we report success by watching the deck count.
//
// Activates off the overlay's own Setup (opened by HUBEditDeckScreen's I key) and pops on its
// OnDeActivate — so it slots onto the router stack above the deck editor and hands control back
// when closed (the deck editor's OnResume then re-reads, picking up any +/− changes).
internal sealed class AddCardOverlayScreen : IScreenHandler
{
    private static AddCardOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private AddCardOverlayManager? _overlay;
    private bool _active;

    private readonly List<string> _lines = new();
    private int _lineIndex = -1;

    private static readonly MethodInfo? CardGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "selectedCard");
    private static readonly MethodInfo? StackGetter = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "selectedStack");

    public string Name => "Card details";

    public AddCardOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new AddCardOverlayScreen(router);

        var setup = AccessTools.Method(typeof(AddCardOverlayManager), "Setup",
            new[] { typeof(ArchetypeDBCards), typeof(AddCardOverlayManager.CardLocation), typeof(GameMode) });
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
        _lines.Clear();
        _lineIndex = -1;
        _router.Speech.Say("Card details. Plus to add, minus to remove, left and right for other cards, up and down to read, Escape to close.", interrupt: true);
        AnnounceCard(interrupt: false);
    }

    public void OnResume() => AnnounceCard(interrupt: true);
    public void OnExit() { }
    public void RespeakAll() => AnnounceCard(interrupt: false);

    public string StateSignature()
    {
        if (_overlay == null) return "CardDetail|dead";
        var card = SelectedCard();
        return $"CardDetail|{card?.CardID}|{InDeck()}";
    }

    public void Tick()
    {
        if (_overlay == null) { if (_active) { _active = false; _router.Deactivate(this); } return; }
        if (!Application.isFocused) return;

        if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace)
            || UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            _overlay.CloseAddCardOverlay();
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Equals) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadPlus)
            || UnityEngine.Input.GetKeyDown(KeyCode.Plus))
        {
            Adjust(add: true);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Minus) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadMinus))
        {
            Adjust(add: false);
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { _overlay.NextClicked(); FlipRead(); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { _overlay.PrevClicked(); FlipRead(); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { ReadLine(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { ReadLine(-1); return; }
    }

    private void Adjust(bool add)
    {
        int before = InDeck();
        if (add) _overlay!.AddClicked(); else _overlay!.RemoveClicked();
        int after = InDeck();
        if (after == before)
        {
            _router.Speech.Say(add ? "Can't add more" : "None in deck", interrupt: true);
            return;
        }
        _router.Speech.Say($"{(add ? "Added" : "Removed")}, {after} in deck", interrupt: true);
    }

    private void FlipRead()
    {
        if (!_active || _overlay == null) return;   // NextClicked at the end closes the overlay
        _lines.Clear();
        _lineIndex = -1;
        AnnounceCard(interrupt: true);
    }

    private void AnnounceCard(bool interrupt)
    {
        var card = SelectedCard();
        if (card == null) { _router.Speech.Say(Name, interrupt); return; }
        var row = ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(card.CardID);
        string name = row != null ? CardText.NameWithType(row) : card.CardID;
        int inDeck = InDeck(), owned = Owned();
        _lines.Clear();
        if (row != null) _lines.AddRange(CardText.DetailLines(row));
        _lineIndex = -1;
        _router.Speech.Say($"{name}. {inDeck} in deck, {owned} owned.", interrupt);
    }

    private void ReadLine(int dir)
    {
        if (_lines.Count == 0)
        {
            var card = SelectedCard();
            var row = card != null ? ManagerSingleton<CardDatabaseManager>.instance.TryGetCardFromDatabase(card.CardID) : null;
            if (row != null) _lines.AddRange(CardText.DetailLines(row));
        }
        int next = _lineIndex + dir;
        if (next < -1) return;
        if (next == -1) { _lineIndex = -1; AnnounceCard(interrupt: true); return; }
        if (next >= _lines.Count) return;
        _lineIndex = next;
        _router.Speech.Say(_lines[_lineIndex], interrupt: true);
    }

    private ICardDataRow? SelectedCard()
    {
        try { return CardGetter?.Invoke(_overlay, null) as ICardDataRow; } catch { return null; }
    }

    private ArchetypeDBCards? SelectedStack()
    {
        try { return StackGetter?.Invoke(_overlay, null) is ArchetypeDBCards s ? s : (ArchetypeDBCards?)null; }
        catch { return null; }
    }

    private int InDeck()
    {
        var s = SelectedStack();
        try { return s is { } stack && !stack.IsDefault ? stack.TotalDeckQuantity() : 0; } catch { return 0; }
    }

    private int Owned()
    {
        var s = SelectedStack();
        try { return s is { } stack && !stack.IsDefault ? stack.TotalOwnedQuantity() : 0; } catch { return 0; }
    }
}
