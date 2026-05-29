using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MatchLogic;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Util;
using RainierClientSDK;
using TPCI.Rainier.Match.Cards;
using TPCI.Rainier.Match.Cards.Ownership;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Phase 3 — the in-battle handler. Serves BOTH FTUE tutorial battles and normal/online matches
// (they share the same MatchManager). Lifecycle hooks: StartGame (activate), SetState (phase/turn
// announcements), LoadEndBattleScreen (deactivate).
//
// Board cards aren't uGUI Selectables, so we synthesize navigation: HS-style review keys jump
// focus into a zone, Left/Right walk cards, Down/Up read the focused card line-by-line. Enter
// opens a menu of ONLY the legal actions for the focused card, invoked through the game's own
// MatchUserOperations (the same op the mouse path sends). E ends the turn.
//
// Reading never mutates the game — only Enter (→ legal-action menu) and E change anything.
internal sealed class BattleScreen : IScreenHandler
{
    private static BattleScreen? _instance;

    private readonly ScreenRouter _router;
    private MatchManager? _manager;
    private bool _active;
    private MatchManager.MatchState _phase = MatchManager.MatchState.None;

    private enum Mode { Nav, ActionMenu, CardView, ConfirmPlay }
    private Mode _mode = Mode.Nav;

    // Card-view reading state (locked on one card, reading its printed lines).
    private Card3D? _cvCard;
    private List<string>? _cvLines;
    private int _cvLine = -1;

    // Nav state
    private BattleBoardReader.Zone _zone = BattleBoardReader.Zone.MyActive;
    private List<Card3D>? _zoneCards;   // null for count-only zones
    private int _card;
    private int _line = -1;             // -1 = on the card name; 0.. = detail line index

    // Action-menu state
    private sealed class BattleAction
    {
        public string Label = "";
        public bool Usable = true;
        public Action Invoke = () => { };
    }
    private List<BattleAction> _actions = new();
    private int _actionIdx;

    // Native hand-card placement state ("hold and drop" — drives the game's CardTargetingHandler).
    private readonly KbPlacementInitiator _placeInit = new();
    private bool _holding;
    private Card3D? _heldCard;

    // No-target play state (Item / Trainer / Supporter): the game's targeting handler still runs,
    // but the receiver is always the local pending slot, so we auto-commit without any field nav.
    private bool _autoPlaying;
    private int _autoPlayTicks;

    // A no-target card awaiting the player's "Use X?" confirmation (Enter plays, Backspace cancels).
    private Card3D? _confirmCard;

    // True while a LOCAL selection request is open (prize / discard / search / choose-active / …),
    // tracked off the game's own SelectionStateChanged event. We block turn-ending and new actions
    // while it's set so we can't desync the match — even for selection families we don't drive yet.
    private bool _localSelecting;
    private MatchManager? _selSubManager;   // manager we've hooked SelectionStateChanged on
    private HandController? _handSub;        // local hand we've hooked OnCardAdded/OnCardRemoved on

    // Tutorial guidance: the entityID of the FTUE-spotlighted card we last announced.
    private string _ftueCardId = "";

    // True once we've announced the screen-space message bar's "Done" button (setup confirm etc.).
    private bool _doneBarShown;

    public string Name => "Battle";

    public BattleScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new BattleScreen(router);

        var start = AccessTools.Method(typeof(MatchManager), nameof(MatchManager.StartGame));
        if (start == null) { Log.Error("BattleScreen: MatchManager.StartGame not found"); return; }
        harmony.Patch(start, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(BattleScreen), nameof(StartGame_Postfix))));

        var setState = AccessTools.Method(typeof(MatchManager), "SetState");
        if (setState != null)
            harmony.Patch(setState, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattleScreen), nameof(SetState_Postfix))));
        else
            Log.Warn("BattleScreen: MatchManager.SetState not found — phase announcements disabled");

        var end = AccessTools.Method(typeof(MatchManager), nameof(MatchManager.LoadEndBattleScreen));
        if (end != null)
            harmony.Patch(end, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattleScreen), nameof(LoadEndBattleScreen_Postfix))));
        else
            Log.Warn("BattleScreen: MatchManager.LoadEndBattleScreen not found — end-of-match announcement disabled");

        // Coin flips (starting flip, attack effects) only show their result visually — speak it. FlipCoin
        // returns the per-coin result (isHeads) and fires once per coin in a multi-flip.
        var flipCoin = AccessTools.Method(typeof(CoinFlipOverlay), "FlipCoin");
        if (flipCoin != null)
            harmony.Patch(flipCoin, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattleScreen), nameof(FlipCoin_Postfix))));
        else
            Log.Warn("BattleScreen: CoinFlipOverlay.FlipCoin not found — coin flip results won't be announced");

        Log.Info("BattleScreen installed");
    }

    // Announce each coin's result. Queued (interrupt:false) so a multi-flip reads "Heads. Tails. …" in
    // order without cutting off, and so it never clips the prompt/turn speech around it.
    private static void FlipCoin_Postfix(bool __result)
    {
        if (_instance == null) return;
        _instance._router.Speech.Say($"Coin flip: {(__result ? "Heads" : "Tails")}", interrupt: false);
    }

    private static void StartGame_Postfix(MatchManager __instance)
    {
        if (_instance == null) return;
        _instance._manager = __instance;
        _instance._phase = __instance.currentState;
        _instance.HookSelectionState(__instance);
        _instance.HookHandChanges(__instance);
        if (!_instance._active)
        {
            _instance._active = true;
            // A tutorial Dialogue bubble can already be showing as the battle starts (the final FTUE
            // battle does this). It must stay the focused, only-interactable thing so the player can
            // advance it — so slot Battle in BELOW it; Battle becomes active when the dialogue closes.
            // (Normally StartGame fires with FTUEBattleIntro on top, and Battle correctly goes over it.)
            if (_instance._router.Active is DialogueScreen)
                _instance._router.ActivateBackground(_instance);
            else
                _instance._router.Activate(_instance);
        }
    }

    // Subscribe (once per match) to the game's selection-state signal. InputRequestOperation raises
    // this (LOCAL, true) before opening ANY local selection overlay and (LOCAL, false) when it
    // resolves, so it's a reliable "a choice is pending" flag across every selection family.
    private void HookSelectionState(MatchManager mgr)
    {
        if (_selSubManager == mgr) return;
        _selSubManager = mgr;
        _localSelecting = false;
        try { mgr.AddSelectionStateChangedCallback(OnSelectionStateChanged); }
        catch (Exception ex) { Log.Error($"BattleScreen: SelectionStateChanged subscribe threw: {ex.Message}"); }
    }

    private void OnSelectionStateChanged(PlayerID who, bool selecting)
    {
        if (who == PlayerID.LOCAL) _localSelecting = selecting;
    }

    // Subscribe to the LOCAL hand's own add/remove events (HandController.OnCardAdded/OnCardRemoved,
    // fired from ProcessCardGainedResult / ProcessCardRemovalResult). This is the game's native
    // "your hand changed" signal — it lets us (1) announce draws the battle log doesn't cover (your
    // per-turn / effect draws are private, so they never appear there), and (2) keep the focused hand
    // list live so a drawn card shows up and a played/attached card stops being navigable while you're
    // still standing in the hand (re-entering the zone already re-reads; this covers staying put).
    private void HookHandChanges(MatchManager mgr)
    {
        HandController? hand = null;
        try { hand = mgr.board?.GetPlayer(PlayerID.LOCAL)?.hand; } catch (Exception ex) { Log.Warn($"BattleScreen: hand lookup threw: {ex.Message}"); }
        if (ReferenceEquals(hand, _handSub)) return;
        UnhookHandChanges();
        _handSub = hand;
        if (_handSub != null)
        {
            try { _handSub.OnCardAdded += OnHandCardAdded; _handSub.OnCardRemoved += OnHandCardRemoved; }
            catch (Exception ex) { Log.Error($"BattleScreen: hand subscribe threw: {ex.Message}"); }
        }
    }

    private void UnhookHandChanges()
    {
        if (_handSub == null) return;
        try { _handSub.OnCardAdded -= OnHandCardAdded; _handSub.OnCardRemoved -= OnHandCardRemoved; } catch { }
        _handSub = null;
    }

    private void OnHandCardAdded(Card3D card)
    {
        try
        {
            if (card != null)
            {
                // Your own hand identity is always knowable — read the name directly rather than via
                // CardName (which would say "face-down card" if IsPublic hasn't flipped yet mid-draw).
                var nm = card.cardDataRow?.LocalizedCardName;
                if (string.IsNullOrEmpty(nm)) nm = card.cardInfo?.cardName;
                _router.Speech.Say(string.IsNullOrEmpty(nm) ? "Drew a card" : $"Drew {nm}", interrupt: false);
            }
            if (_active && _zone == BattleBoardReader.Zone.MyHand) RefreshZoneCards();   // appended card now navigable
        }
        catch (Exception ex) { Log.Error($"BattleScreen.OnHandCardAdded threw: {ex.Message}"); }
    }

    private void OnHandCardRemoved(Card3D card)
    {
        // A card left the hand (played / attached / discarded). If we're standing in the hand, drop it
        // from the live list so it can't be navigated to or targeted as a phantom ("Can't play X now").
        try { if (_active && _zone == BattleBoardReader.Zone.MyHand) RefreshZoneCards(); }
        catch (Exception ex) { Log.Error($"BattleScreen.OnHandCardRemoved threw: {ex.Message}"); }
    }

    private static void SetState_Postfix(MatchManager __instance, MatchManager.MatchState newState)
    {
        if (_instance == null || !_instance._active || _instance._manager != __instance) return;
        if (newState == _instance._phase) return;
        _instance._phase = newState;
        var text = PhaseText(newState);
        // Queued so it appends after any in-flight speech rather than cutting it off.
        if (text != null) _instance._router.Speech.Say(text, interrupt: false);
    }

    private static void LoadEndBattleScreen_Postfix(MatchManager __instance)
    {
        if (_instance == null || _instance._manager != __instance || !_instance._active) return;
        _instance._router.Speech.Say("Battle ended", interrupt: false);
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance.UnhookHandChanges();
        _instance._manager = null;
    }

    public void OnEnter()
    {
        _router.SetCurrentTitle(this, "Battle");
        _mode = Mode.Nav;
        _holding = false;
        _autoPlaying = false;
        _confirmCard = null;
        _ftueCardId = "";
        var phase = PhaseText(_phase);
        var opening = phase == null ? "Battle started" : $"Battle started. {phase}";
        _router.Speech.Say(opening, interrupt: false);
    }

    public void OnResume()
    {
        // A child handler popped (e.g. a decision prompt resolved). Don't re-announce the phase
        // (children push/pop after every tutorial coaching line — the genuine turn announcement
        // comes only from SetState_Postfix). But DO two things the resolved prompt may have changed:
        //  • Re-arm tutorial guidance so the next Tick re-announces what the FTUE now wants (a forced
        //    card step often becomes active right after a search/discard resolves).
        //  • Re-read the focused zone — playing a card / resolving a search mutates the board, so the
        //    cached card list is stale (this is the "left/right shows old hand cards" report).
        _ftueCardId = "";
        RefreshZoneCards();
    }

    public void OnExit() { /* preserve _manager across pause — Tick checks Unity-null */ }

    public void RespeakAll()
    {
        var phase = PhaseText(_phase);
        _router.Speech.Say(phase == null ? "Battle" : $"Battle. {phase}", interrupt: true);
    }

    public string StateSignature() =>
        _manager == null ? "Battle|dead" : $"Battle|{_phase}|{_mode}|{_zone}|{_card}|{_line}";

    public void Tick()
    {
        if (_manager == null)
        {
            if (_active) { _active = false; _router.Deactivate(this); }
            return;
        }
        if (!Application.isFocused) return;

        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (_mode == Mode.CardView) { TickCardView(); return; }
        if (_mode == Mode.ActionMenu) { TickActionMenu(); return; }
        if (_mode == Mode.ConfirmPlay) { TickConfirmPlay(); return; }   // "Use X?" — Enter plays, Backspace cancels
        if (_autoPlaying) { TickAutoPlay(); return; }   // committing a no-target play — field nav locked out
        if (!_holding) TutorialGuidanceTick();   // keep the player on the tutorial's spotlighted card
        TickNav(shift);
        if (_holding) RefreshHeldChosen();   // keep the placement loop aimed at the current focus
    }

    // ---- Navigation mode ----

    private void TickNav(bool shift)
    {
        bool back = Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape);

        // Backspace/Escape closes the game's big-card viewer when it's open.
        if (back && _manager?.bigCardOverlay != null && _manager.bigCardOverlay.isOpen) { CloseBigCard(); return; }

        // While holding a picked-up card, Backspace cancels the drag and returns it to hand.
        if (_holding && Input.GetKeyDown(KeyCode.Backspace)) { CancelPickup(); return; }

        // A "Done" / confirm button on the screen-space message bar (e.g. confirm your starting
        // Pokémon at setup) — announce it once when it appears, and press it with TAB (NOT Enter, so
        // Enter is still free to pick up / place more Bench Pokémon before you confirm). Reading the
        // board with the zone/arrow keys also still works.
        bool doneAvailable = MessageBarDoneAvailable();
        if (doneAvailable) { if (!_doneBarShown) { _doneBarShown = true; AnnounceDoneBar(); } }
        else _doneBarShown = false;
        if (doneAvailable && Input.GetKeyDown(KeyCode.Tab)) { PressMessageBarDone(); return; }

        // Zone jump keys (HS "look at" style). Shift = opponent's equivalent where it exists.
        if (Input.GetKeyDown(KeyCode.A)) { FocusZone(shift ? BattleBoardReader.Zone.OppActive : BattleBoardReader.Zone.MyActive); return; }
        if (Input.GetKeyDown(KeyCode.B)) { FocusZone(shift ? BattleBoardReader.Zone.OppBench : BattleBoardReader.Zone.MyBench); return; }
        if (Input.GetKeyDown(KeyCode.C)) { FocusZone(shift ? BattleBoardReader.Zone.OppHand : BattleBoardReader.Zone.MyHand); return; }
        if (Input.GetKeyDown(KeyCode.X)) { FocusZone(shift ? BattleBoardReader.Zone.OppDiscard : BattleBoardReader.Zone.MyDiscard); return; }
        if (Input.GetKeyDown(KeyCode.D)) { FocusZone(shift ? BattleBoardReader.Zone.OppDeck : BattleBoardReader.Zone.MyDeck); return; }
        if (Input.GetKeyDown(KeyCode.P)) { FocusZone(shift ? BattleBoardReader.Zone.OppPrize : BattleBoardReader.Zone.MyPrize); return; }
        if (Input.GetKeyDown(KeyCode.S)) { FocusZone(BattleBoardReader.Zone.Stadium); return; }
        if (Input.GetKeyDown(KeyCode.T)) { AnnounceStatus(); return; }
        if (Input.GetKeyDown(KeyCode.I)) { EnterCardView(); return; }
        if (Input.GetKeyDown(KeyCode.E)) { EndTurn(); return; }

        // Within-zone navigation
        if (Input.GetKeyDown(KeyCode.RightArrow)) { MoveCard(1); return; }
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { MoveCard(-1); return; }
        if (Input.GetKeyDown(KeyCode.DownArrow)) { ReadLine(1, shift); return; }
        if (Input.GetKeyDown(KeyCode.UpArrow)) { ReadLine(-1, shift); return; }
        if (Input.GetKeyDown(KeyCode.Home)) { JumpCard(0); return; }
        if (Input.GetKeyDown(KeyCode.End)) { JumpCard(int.MaxValue); return; }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { if (_holding) AttemptDrop(); else OpenActionMenu(); return; }

        // 1-9, 0 → jump to card N in the current zone
        for (int d = 1; d <= 9; d++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + (d - 1)) || Input.GetKeyDown(KeyCode.Keypad1 + (d - 1))) { JumpCard(d - 1); return; }
        if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) { JumpCard(9); return; }
    }

    private void FocusZone(BattleBoardReader.Zone z)
    {
        if (_manager == null) return;
        _zone = z; _card = 0; _line = -1;
        string zoneName = BattleBoardReader.ZoneName(z);

        if (BattleBoardReader.IsCountOnly(z))
        {
            _zoneCards = null;
            int n = BattleBoardReader.GetCount(z);
            _router.Speech.Say($"{zoneName}, {n} card{(n == 1 ? "" : "s")}", interrupt: true);
            return;
        }

        _zoneCards = BattleBoardReader.GetCards(_manager, z);
        if (_zoneCards.Count == 0) { _router.Speech.Say($"{zoneName}, empty", interrupt: true); return; }
        _router.Speech.Say($"{zoneName}. {CardLabel(0)}", interrupt: true);
    }

    private string CardLabel(int i)
    {
        var card = _zoneCards![i];
        string name = BattleBoardReader.CardNameWithType(card);
        // In the hand, a pile of identical cards reads as one entry — speak its count like the badge.
        if (_zone == BattleBoardReader.Zone.MyHand)
        {
            int n = BattleBoardReader.HandStackCount(_manager!, card);
            if (n > 1) name += $", {n} copies";
        }
        return $"{name}, {i + 1} of {_zoneCards.Count}";
    }

    // Re-read the current browsable zone's cards in place (board may have changed while a prompt was
    // up, or after a play). Keeps the cursor on a valid index; no speech — navigation reads fresh.
    private void RefreshZoneCards()
    {
        if (_manager == null || _zoneCards == null || BattleBoardReader.IsCountOnly(_zone)) return;
        _zoneCards = BattleBoardReader.GetCards(_manager, _zone);
        _card = _zoneCards.Count == 0 ? 0 : Mathf.Clamp(_card, 0, _zoneCards.Count - 1);
        _line = -1;
    }

    private void MoveCard(int delta)
    {
        if (_zoneCards == null || _zoneCards.Count == 0) return;        // nothing to browse — silent
        int next = Mathf.Clamp(_card + delta, 0, _zoneCards.Count - 1);
        if (next == _card) return;                                      // at an edge — stay silent, no filler
        _card = next; _line = -1;
        _router.Speech.Say(CardLabel(_card), interrupt: true);
    }

    private void JumpCard(int index)
    {
        if (_zoneCards == null || _zoneCards.Count == 0) { _router.Speech.Say("No cards here", interrupt: true); return; }
        int target = index == int.MaxValue ? _zoneCards.Count - 1 : index;
        if (target < 0 || target >= _zoneCards.Count) { _router.Speech.Say("No card there", interrupt: true); return; }
        _card = target; _line = -1;
        _router.Speech.Say(CardLabel(_card), interrupt: true);
    }

    private void ReadLine(int dir, bool shift)
    {
        if (_zoneCards == null || _card < 0 || _card >= _zoneCards.Count)
        { _router.Speech.Say("Nothing focused", interrupt: true); return; }

        var card = _zoneCards[_card];
        var lines = BattleBoardReader.DetailLines(card);

        if (shift && dir < 0) // Shift+Up: repeat current
        {
            _router.Speech.Say(_line < 0 || _line >= lines.Count ? BattleBoardReader.CardName(card) : lines[_line], interrupt: true);
            return;
        }
        if (shift && dir > 0) // Shift+Down: read from current line to the end
        {
            int from = _line < 0 ? 0 : _line;
            if (from >= lines.Count) { _router.Speech.Say("End of card", interrupt: true); return; }
            _router.Speech.Say(string.Join(". ", lines.Skip(from)), interrupt: true);
            _line = lines.Count - 1;
            return;
        }

        if (dir > 0)
        {
            if (_line < lines.Count - 1) { _line++; _router.Speech.Say(lines[_line], interrupt: true); }
            else if (lines.Count > 0) { _line = lines.Count - 1; _router.Speech.Say(lines[_line], interrupt: true); } // at bottom: re-speak last line, no filler
        }
        else
        {
            if (_line <= 0) { _line = -1; _router.Speech.Say(BattleBoardReader.CardName(card), interrupt: true); }
            else { _line--; _router.Speech.Say(lines[_line], interrupt: true); }
        }
    }

    private void AnnounceStatus()
    {
        if (_manager == null) return;
        string s = PhaseText(_phase) ?? "Battle";
        // Surface the tutorial objective on demand — gives a reliable "what does it want me to do?"
        // answer when the FTUE is mid-step (the T key the player already uses for status).
        var ftueCard = FtueFocusedCard();
        if (ftueCard != null) s += ". " + TutorialWantsText(ftueCard);
        s += $". You have {BattleBoardReader.GetCount(BattleBoardReader.Zone.MyPrize)} prizes left, " +
             $"opponent {BattleBoardReader.GetCount(BattleBoardReader.Zone.OppPrize)}.";
        var myActive = BattleBoardReader.GetCards(_manager, BattleBoardReader.Zone.MyActive).FirstOrDefault();
        var oppActive = BattleBoardReader.GetCards(_manager, BattleBoardReader.Zone.OppActive).FirstOrDefault();
        if (myActive != null) s += " Your active: " + BattleBoardReader.ShortSummary(myActive) + ".";
        if (oppActive != null) s += " Opponent's active: " + BattleBoardReader.ShortSummary(oppActive) + ".";
        _router.Speech.Say(s, interrupt: true);
    }

    private void EndTurn()
    {
        if (_manager == null) return;
        if (!_manager.IsMyTurn) { _router.Speech.Say("Not your turn", interrupt: true); return; }

        // A local selection is open (a card effect is asking you to choose) — ending the turn now
        // would desync the match. The game blocks it; so do we.
        if (_localSelecting) { _router.Speech.Say("Finish the current selection first", interrupt: true); return; }

        // Tutorial forced-card step: FTUECardIndicator sets FTUEMatchManager.focusedCard and loops
        // `while (focusedCard != null)` waiting for THAT card to be played — but it never blocks the
        // end-turn button and canEndTurn stays true, so ending turn here strands the FTUE coroutine
        // and freezes the match. Redirect to the wanted card instead (the native FTUE signal).
        var ftueCard = FtueFocusedCard();
        if (ftueCard != null)
        { _router.Speech.Say(TutorialWantsText(ftueCard) + ". Do that before ending your turn", interrupt: true); return; }

        // Defer to the game's OWN end-turn gate rather than rolling our own: canEndTurn (the match-rules
        // check, recomputed by UpdateEndTurn) plus the real End Turn button's interactable state — the
        // FTUE and turn-processing phases Block() that button during forced / non-idle moments. This is
        // what froze before: the bare op skipped both gates and sent end-turn mid-selection.
        var info = NetworkMatchController.currentMatchInfo;
        bool canEnd = info != null && info.UpdateEndTurn();
        if (!canEnd || IsEndTurnButtonBlocked())
        { _router.Speech.Say("You can't end your turn right now", interrupt: true); return; }

        MatchUserOperations.EndTurn("", false);   // the same op the real button sends; result via log / phase
    }

    // Is the real End Turn button non-clickable right now? Two independent flags gate it, and a
    // sighted player can't click it if EITHER is set, so neither may we:
    //  • button.enabled  — the FTUE's BlockAll() → ForceSetButtons(false) → ToggleButton(false) sets
    //    this false during EVERY forced tutorial step (this is the bug that froze us: we only checked
    //    interactable before, which stays true here).
    //  • button.interactable (== !isBlocking) — turn-processing / FTUE Block(key) sets this false.
    // Conservative: if we can't find the button, don't block (canEndTurn still gates).
    private bool IsEndTurnButtonBlocked()
    {
        try
        {
            var btn = _manager?.MatchInfoSidebar?.EndTurnButton;
            var t = btn?.EndButtonTransform;
            var uiBtn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return uiBtn != null && (!uiBtn.enabled || !uiBtn.interactable);
        }
        catch (Exception ex) { Log.Error($"BattleScreen: end-turn button read threw: {ex.Message}"); return false; }
    }

    // The screen-space message bar's "Done" / confirm button (set-up confirm, "press Done to continue"
    // moments). It's shown when the game wants a confirm; Enter presses it. CentralMessageBar.doneButton
    // is only active when a Done action is wired, so activeInHierarchy is the "is there a Done to press".
    private bool MessageBarDoneAvailable()
    {
        try
        {
            var t = _manager?.MessageBarScreenSpace?.DoneButtonTransform;
            return t != null && t.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    private void AnnounceDoneBar()
    {
        string msg = _manager?.MessageBarScreenSpace?.CurrText?.Trim() ?? "";
        _router.Speech.Say(msg.Length > 0 ? $"{msg}. Press Tab for Done." : "Press Tab for Done.", interrupt: true);
    }

    // Invoke the real Done button's click — the same as a sighted player tapping it (confirms setup, etc.).
    private void PressMessageBarDone()
    {
        try
        {
            var t = _manager?.MessageBarScreenSpace?.DoneButtonTransform;
            var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            if (btn != null && btn.isActiveAndEnabled) { btn.onClick.Invoke(); _router.Speech.Say("Done", interrupt: true); }
            else _router.Speech.Say("Can't confirm right now", interrupt: true);
        }
        catch (Exception ex) { Log.Error($"BattleScreen: Done press threw: {ex.Message}"); }
    }

    // Toggle the game's enlarged card viewer for the focused card. This drives the same inspection
    // path a mouse tap uses (owner.OnCardClicked → TryInitiateOwnerInspection → bigCardOverlay), and
    // it satisfies tutorial coaching that gates on the big-card view being open
    // (FTUEBigCardIndicator waits on bigCardOverlay.isOpen + blocks placement until you've looked).
    // I → open the game's card viewer for the focused card AND enter a locked reading mode: you
    // stay on this one card and read every printed line with Up/Down. Backspace/Escape/I exits.
    private void EnterCardView()
    {
        var overlay = _manager?.bigCardOverlay;
        if (_manager == null) return;
        if (overlay != null && overlay.isOpen) { CloseBigCard(); return; }   // toggle off an already-open viewer (e.g. FTUE)
        if (_zoneCards == null || _card < 0 || _card >= _zoneCards.Count) return;
        var card = _zoneCards[_card];
        if (card == null) return;

        if (card.owner is BaseCardOwner owner)
        {
            try { owner.OnCardClicked(card, false); }   // opens the real viewer (also clears FTUE big-card gates)
            catch (Exception ex) { Log.Error($"BattleScreen: inspect OnCardClicked threw: {ex.Message}"); }
        }
        _cvCard = card;
        _cvLines = BattleBoardReader.DetailLines(card);
        _cvLine = -1;
        _mode = Mode.CardView;
        _router.Speech.Say($"Card view, {BattleBoardReader.CardNameWithType(card)}", interrupt: true);
    }

    private void TickCardView()
    {
        // Exit if the viewer was closed externally (the game/FTUE dismissed it).
        if (_manager?.bigCardOverlay == null || !_manager.bigCardOverlay.isOpen)
        { _mode = Mode.Nav; _cvCard = null; _cvLines = null; return; }
        if (!Application.isFocused) return;

        if (Input.GetKeyDown(KeyCode.DownArrow)) { CvReadLine(1); return; }
        if (Input.GetKeyDown(KeyCode.UpArrow)) { CvReadLine(-1); return; }
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.I)) { ExitCardView(); return; }
        // Left/Right deliberately ignored — you stay on this one card while reading.
    }

    private void CvReadLine(int dir)
    {
        var lines = _cvLines;
        if (lines == null || lines.Count == 0) { _router.Speech.Say(BattleBoardReader.CardNameWithType(_cvCard!), interrupt: true); return; }
        if (dir > 0)
        {
            if (_cvLine < lines.Count - 1) { _cvLine++; _router.Speech.Say(lines[_cvLine], interrupt: true); }
            else { _cvLine = lines.Count - 1; _router.Speech.Say(lines[_cvLine], interrupt: true); }   // stay at the last line
        }
        else
        {
            if (_cvLine <= 0) { _cvLine = -1; _router.Speech.Say(BattleBoardReader.CardNameWithType(_cvCard!), interrupt: true); }
            else { _cvLine--; _router.Speech.Say(lines[_cvLine], interrupt: true); }
        }
    }

    private void ExitCardView()
    {
        CloseBigCard();
        _mode = Mode.Nav;
        _cvCard = null;
        _cvLines = null;
        _cvLine = -1;
    }

    private void CloseBigCard()
    {
        var overlay = _manager?.bigCardOverlay;
        if (overlay == null || !overlay.isOpen) return;
        try { overlay.Close(); } catch (Exception ex) { Log.Error($"BattleScreen: bigCard Close threw: {ex.Message}"); }
        _router.Speech.Say("Closed card view", interrupt: true);
    }

    // ---- Action menu mode ----

    private void OpenActionMenu()
    {
        Log.Info($"OpenActionMenu entry: zone={_zone} card={_card}/{_zoneCards?.Count ?? -1} localSel={_localSelecting} holding={_holding} mode={_mode}");
        // While the game is waiting on a local selection, starting a new action would desync — block it.
        if (_localSelecting)
        { _router.Speech.Say("Finish the current selection first", interrupt: true); return; }
        if (_zoneCards == null || _card < 0 || _card >= _zoneCards.Count)
        { _router.Speech.Say("Nothing to act on", interrupt: true); return; }

        var card = _zoneCards[_card];
        if (card == null) return;

        // Tutorial guard: only act on cards the tutorial currently PERMITS. DoesFTUEPermitCardTargetability
        // is the game's own rule — TRUE in free play (the guided part is over and you play freely to win),
        // TRUE for the spotlighted card during a forced step, FALSE otherwise (wrong card, or a step that
        // hasn't armed yet). This blocks the "I attacked before the tutorial allowed it" freeze WITHOUT
        // the false "tutorial isn't ready" during a free turn (the old blockInputs+focusedCard guard hit
        // that because blockInputs stays set under free play). Normal (non-FTUE) matches never block.
        if (!FtuePermitsCard(card))
        {
            var ftueCard = FtueFocusedCard();
            Log.Info($"OpenActionMenu FTUE-blocked '{BattleBoardReader.CardName(card)}' (spotlight={(ftueCard != null ? BattleBoardReader.CardName(ftueCard) : "none")}): {PlayDiag(card)}");
            _router.Speech.Say(ftueCard != null ? TutorialWantsText(ftueCard) + " first"
                                                : "The tutorial isn't ready for that yet", interrupt: true);
            return;
        }

        bool myTurn = _manager != null && _manager.CanPerformNewActions;
        // The Setup phase ("Drag a Basic Pokémon to your Active Spot") is NOT LocalTurn, so
        // CanPerformNewActions is false — yet you must be able to place your starting Pokémon then.
        // Hand placement is allowed during setup too; in-play actions (attack/ability/retreat) are not.
        bool canPlace = myTurn || (_manager != null && _manager.IsSetupPhase);

        // Route by the ZONE you navigated into, matching what you pressed to get here:
        //  • Your hand → play the card through the game's native placement (pick up → target →
        //    drop). Evolving is "play the evolution from your hand", so it lives on THIS path —
        //    pressing Enter on the board Pokémon opens its attack menu, not evolve.
        //  • Your active / bench → open that Pokémon's real attack / ability / retreat menu.
        if (_zone == BattleBoardReader.Zone.MyHand && canPlace)
        {
            StartPickup(card);
            return;
        }

        // For one of MY in-play Pokémon, open the game's REAL AttackOverlay FIRST. Show() populates
        // its option widgets synchronously and sets isOpen — exactly what the FTUE attack coach
        // waits on — then we navigate/click those real options rather than firing our own ops.
        bool overlayCard = (_zone == BattleBoardReader.Zone.MyActive || _zone == BattleBoardReader.Zone.MyBench) && myTurn;
        if (overlayCard) TryOpenAttackOverlay(card);

        _actions = BuildActions(card);
        if (_actions.Count == 0)
        {
            Log.Info($"OpenActionMenu no actions '{BattleBoardReader.CardName(card)}' zone={_zone} overlayCard={overlayCard} canPlace={canPlace}: {PlayDiag(card)}");
            if (overlayCard) TryCloseAttackOverlay();
            _router.Speech.Say($"No actions for {BattleBoardReader.CardName(card)}", interrupt: true);
            return;
        }

        _mode = Mode.ActionMenu;
        _actionIdx = 0;
        // If the FTUE is teaching the attack overlay, it just wants you to look and back out — opening
        // it satisfied the coach; say so and make clear actions are read-only here (TickActionMenu blocks
        // invoking them). Otherwise the normal action prompt.
        if (overlayCard && FTUEHook.AttackOverlayTeachingActive)
            _router.Speech.Say($"Attack menu for {BattleBoardReader.CardName(card)}. The tutorial is just showing you this — read with arrows, Backspace to go back. {ActionLabel(0)}", interrupt: true);
        else
            _router.Speech.Say($"Actions for {BattleBoardReader.CardName(card)}. {ActionLabel(0)}", interrupt: true);
    }

    private void TryOpenAttackOverlay(Card3D card)
    {
        try
        {
            var overlay = _manager?.attackOverlay;
            if (overlay != null && !overlay.isOpen) overlay.Show(card);
        }
        catch (Exception ex) { Log.Error($"BattleScreen: AttackOverlay.Show threw: {ex.Message}"); }
    }

    private void TryCloseAttackOverlay()
    {
        try
        {
            var overlay = _manager?.attackOverlay;
            if (overlay != null && overlay.isOpen) overlay.Close();
        }
        catch (Exception ex) { Log.Error($"BattleScreen: AttackOverlay.Close threw: {ex.Message}"); }
    }

    private string ActionLabel(int i) => $"{_actions[i].Label}, {i + 1} of {_actions.Count}";

    private void TickActionMenu()
    {
        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (_actionIdx < _actions.Count - 1) { _actionIdx++; _router.Speech.Say(ActionLabel(_actionIdx), interrupt: true); }
            else _router.Speech.Say("Last action", interrupt: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (_actionIdx > 0) { _actionIdx--; _router.Speech.Say(ActionLabel(_actionIdx), interrupt: true); }
            else _router.Speech.Say("First action", interrupt: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape))
        {
            // Back out to the board — close the real overlay if we opened it, and just re-announce
            // the card we're on, no "Cancelled" filler.
            _mode = Mode.Nav;
            TryCloseAttackOverlay();
            if (_zoneCards != null && _card >= 0 && _card < _zoneCards.Count)
                _router.Speech.Say(CardLabel(_card), interrupt: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            // The FTUE is only TEACHING the attack overlay right now (FTUEAttackOverlayIndicator) — it
            // never executes an attack and the game doesn't guard the op, so invoking one would freeze
            // the tutorial. Refuse and point the player at backing out (which satisfies the coach).
            if (FTUEHook.AttackOverlayTeachingActive)
            {
                _router.Speech.Say("The tutorial is just showing you this menu. Press Backspace to go back.", interrupt: true);
                return;
            }
            var action = _actions[_actionIdx];
            if (!action.Usable)
            {
                _router.Speech.Say("Not usable now", interrupt: true);   // greyed option — stay in the menu
                return;
            }
            _mode = Mode.Nav;
            try { action.Invoke(); }   // result narrated by the battle log (localized); no echo from us
            catch (Exception ex) { Log.Error($"BattleScreen action '{action.Label}' threw: {ex.Message}"); }
            return;
        }
    }

    // Builds the list of currently-legal actions for the focused card, each wired to the
    // game-native op that performs it (same message the mouse sends).
    private List<BattleAction> BuildActions(Card3D card)
    {
        var list = new List<BattleAction>();
        var info = card?.cardInfo;
        if (card == null || !card.IsPublic || info == null) return list;
        if (!info.isMyCard || _manager == null || !_manager.CanPerformNewActions) return list;

        var where = Board.GetPlayerPos(info.currentPos);

        if (where == PlayerPos.HAND)
        {
            // Hand cards are played through the game's native targeting flow, invoked directly from
            // OpenActionMenu (StartPickup → hold-and-drop for target cards, or the no-target
            // auto-play for Item/Trainer/Supporter) — never via direct ops or this action menu.
            return list;
        }
        if (where == PlayerPos.ACTIVE || where == PlayerPos.BENCH)
        {
            // Drive the game's REAL attack overlay options (opened in OpenActionMenu). Clicking an
            // option's OnClick() is the exact sighted-player flow (→ TryUseAction → confirmations →
            // op) and is what the FTUE watches — never our own UseCard.
            var overlay = _manager.attackOverlay;
            if (overlay == null || !overlay.isOpen) return list;   // overlay must be open (Show ran)

            if (overlay.AttackOptions != null)
                foreach (var opt in overlay.AttackOptions) AddOverlayOption(list, opt, "Attack", card);
            if (overlay.AbilityOptions != null)
                foreach (var opt in overlay.AbilityOptions) AddOverlayOption(list, opt, "Use ability", card);

            // Retreat: gate on the game's own authoritative canRetreat flag (the overlay's own
            // RetreatClicked checks the same), clean "Retreat" label (no card-action detail/name).
            var retreat = overlay.RetreatOption;
            if (retreat != null)
            {
                bool canRetreat = NetworkMatchController.currentMatchInfo?.canRetreat ?? false;
                list.Add(new BattleAction
                {
                    Label = "Retreat" + (canRetreat ? "" : ", unavailable"),
                    Usable = canRetreat,
                    Invoke = () => retreat.OnClick(),
                });
            }
        }

        return list;
    }

    // Wrap one real overlay option as a menu entry whose Invoke is the option's own click. The label
    // reads "<prefix>: <name>, <cost/damage + rules text>" so the player hears WHAT the attack/ability
    // does (important for the abilities tutorial) before the "X of Y" index.
    //
    // Usability comes from the option's OWN button.interactable — the exact state the game greys out
    // and that the mouse path respects. Crucially, the overlay computes that (CanUseAttack/CanUseAbility)
    // as activeUsableActions + CanPerformNewActions + !IsOperationProcessing AND the FTUE per-action
    // spotlight (card.attackFocused/abilityFocused): during a tutorial step that highlights ONE action,
    // only that one is interactable. A separate activeUsableActions cross-ref (the old approach) misses
    // the spotlight, so it would let the player fire a non-highlighted action and freeze the tutorial.
    // OnClick() does NOT self-gate (it always fires), so this gate is what stops an unusable invoke.
    private void AddOverlayOption(List<BattleAction> list, CardActionOption opt, string prefix, Card3D card)
    {
        if (opt == null) return;
        var action = opt.MatchAction;
        string nm = action != null && !string.IsNullOrEmpty(action.actionName) ? action.actionName : prefix;
        bool ok = OptionUsable(opt);
        string detail = BattleBoardReader.ActionDetail(card, nm);
        string label = $"{prefix}: {nm}";
        if (detail.Length > 0) label += $", {detail}";
        if (!ok) label += ", unavailable";
        list.Add(new BattleAction { Label = label, Usable = ok, Invoke = () => opt.OnClick() });
    }

    // The game's own "can the player click this option" state: the option button's interactable flag
    // (= _canInteractDelegate() && _isActionUsable, i.e. IsMyTurn && CanUse). This is exactly what the
    // mouse path obeys, so we obey it too rather than re-deriving usability ourselves.
    private static bool OptionUsable(CardActionOption opt)
    {
        try
        {
            var t = opt?.ButtonTransform;
            var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return btn != null && btn.interactable;
        }
        catch { return false; }
    }

    // ---- Native hand-card placement via the board navigation ----
    // Enter on a hand card runs it through the game's CardTargetingHandler. Cards that need a board
    // TARGET — Pokémon, Energy, Tool, Stadium — "hold and drop": pick up here, navigate the board
    // normally (A / B / arrows), then press Enter on the destination to drop — attach/evolve onto
    // the focused Pokémon, or play to the focused zone; Backspace cancels. Cards with NO board target
    // — Item / Trainer / Supporter — play immediately to the pending slot (no drag, no field nav);
    // see the no-target branch below. No separate target menu: the field shortcuts ARE the targeting.

    private void StartPickup(Card3D card)
    {
        var info = card.cardInfo;
        if (info == null || _manager == null) { _router.Speech.Say("Can't play this", interrupt: true); return; }
        if (info.placeableBoardPos.Count == 0 && info.attachableEntities.Count == 0 && info.activeUsableActions.Count == 0)
        {
            Log.Info($"StartPickup blocked (no legal play) '{BattleBoardReader.CardName(card)}': {PlayDiag(card)}");
            _router.Speech.Say($"You can't play {BattleBoardReader.CardName(card)} right now", interrupt: true); return;
        }

        // Act on the instance the targeting evaluator actually keys on (the hand sender's), not our
        // owner-list instance — see ResolveHandTargetingInstance. Fall back to our own if the hand
        // sender doesn't list it (shouldn't happen for a hand card, but keep the play path alive).
        var resolved = ResolveHandTargetingInstance(info.entityID);
        var targetCard = resolved ?? card;
        if (resolved == null)
            Log.Info($"StartPickup: '{BattleBoardReader.CardName(card)}' (eid={info.entityID}) NOT in hand sender's accessible cards — using owner-list instance");
        else if (!ReferenceEquals(resolved, card))
            Log.Info($"StartPickup: '{BattleBoardReader.CardName(card)}' owner-list instance differed from hand-sender instance — using sender's");

        // Does this card need the player to choose a board target? Pokémon / Energy / Tool / Stadium
        // do (a Pokémon to attach or evolve onto, or a zone to place into) — mirrors the game's
        // CardHasAttachableEntities / CardHasPlaceableBoardPositions purposes. Item / Trainer /
        // Supporter do not — they have only activeUsableActions and resolve to the fixed pending slot
        // (CardHasUsableActions → UseCard), so there's nothing to drag to.
        bool needsTarget = info.placeableBoardPos.Count > 0 || info.attachableEntities.Count > 0;
        if (!needsTarget)
        {
            // No board target → no drag, no field nav. Confirm in place: "Use X?" — Enter plays it,
            // Backspace cancels (TickConfirmPlay). The actual play runs in BeginNoTargetPlay. We don't
            // touch the targeting handler until confirmed, so cancelling leaves nothing to clean up.
            Log.Info($"StartPickup → ConfirmPlay (no-target) '{BattleBoardReader.CardName(targetCard)}': use={info.activeUsableActions.Count}");
            _confirmCard = targetCard;
            _mode = Mode.ConfirmPlay;
            _router.Speech.Say($"Use {BattleBoardReader.CardName(targetCard)}?", interrupt: true);
            return;
        }

        // Target card: pick it up through the game's targeting handler and enter the navigate-and-drop
        // hold (Backspace cancels; Enter on the destination drops). TryTargetCard IS the game's own
        // authority — it runs IsCardTargetable itself, so we don't pre-check (that was redundant).
        _manager.ApplyIdleStatePlacementContext();
        _placeInit.Reset();
        if (!_manager.TargetingHandler.TryTargetCard(targetCard, _placeInit))
        {
            Log.Info($"StartPickup: TryTargetCard rejected '{BattleBoardReader.CardName(targetCard)}': status={_manager.TargetingHandler.GetTargetabilityStatus(targetCard)} resolved={resolved != null} {PlayDiag(targetCard)}");
            _router.Speech.Say($"Can't play {BattleBoardReader.CardName(targetCard)} now", interrupt: true); return;
        }

        _holding = true;
        _heldCard = targetCard;
        _router.Speech.Say($"Holding {BattleBoardReader.CardName(targetCard)}", interrupt: true);
    }

    // Confirmed a no-target card → run it through the game's targeting handler aimed at the pending
    // slot and auto-commit over the next couple frames (TickAutoPlay). No _holding, so the field stays
    // un-navigable. Driving the real targeting handler (not a bare UseCard) keeps FTUE coaching and any
    // play confirmations firing.
    private void BeginNoTargetPlay(Card3D card)
    {
        _mode = Mode.Nav;
        if (_manager == null) return;
        // TryTargetCard is the game's own authority (it runs IsCardTargetable internally) — no pre-check.
        _manager.ApplyIdleStatePlacementContext();
        _placeInit.Reset();
        if (!_manager.TargetingHandler.TryTargetCard(card, _placeInit))
        {
            Log.Info($"BeginNoTargetPlay: TryTargetCard rejected '{BattleBoardReader.CardName(card)}': status={_manager.TargetingHandler.GetTargetabilityStatus(card)} {PlayDiag(card)}");
            _router.Speech.Say($"Can't play {BattleBoardReader.CardName(card)} now", interrupt: true); return;
        }
        _heldCard = card;
        _autoPlaying = true;
        _autoPlayTicks = 0;
        _placeInit.SetChosen(PendingReceiver());
    }

    // "Use X?" confirmation for a no-target card. Enter plays it, Backspace/Escape backs out without
    // playing. All other keys are ignored — it's a simple yes/no in place, no field navigation.
    private void TickConfirmPlay()
    {
        if (_manager == null) { _mode = Mode.Nav; _confirmCard = null; return; }
        if (!Application.isFocused) return;

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            var card = _confirmCard;
            _confirmCard = null;
            if (card != null) BeginNoTargetPlay(card);
            else _mode = Mode.Nav;
            return;
        }
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape))
        {
            _confirmCard = null;
            _mode = Mode.Nav;
            // Re-orient on the hand card (we never navigated away) without playing it.
            if (_zoneCards != null && _card >= 0 && _card < _zoneCards.Count)
                _router.Speech.Say(CardLabel(_card), interrupt: true);
            return;
        }
    }

    // Resolve the current drop target from where the player is focused: the focused Pokémon for
    // attach/evolve, or the current zone for play. Receiver is null if the focus isn't a legal spot.
    private (ICardPlacementReceiver? recv, string desc) ComputeDropTarget()
    {
        if (_heldCard == null || _manager == null) return (null, "");
        var info = _heldCard.cardInfo;
        if (info == null) return (null, "");

        if (info.attachableEntities.Count > 0)
        {
            var focused = (_zoneCards != null && _card >= 0 && _card < _zoneCards.Count) ? _zoneCards[_card] : null;
            if (focused != null && info.attachableEntities.Contains(focused.entityID) && focused is ICardPlacementReceiver fr)
                return (fr, (info.isPokemon ? "Evolved onto " : "Attached to ") + BattleBoardReader.CardName(focused));
        }
        if (info.placeableBoardPos.Count > 0)
        {
            var bp = ZoneToBoardPos(_zone);
            if (bp.HasValue && info.placeableBoardPos.Contains(bp.Value)
                && _manager.GetOwnerForBoardPos(bp.Value) is ICardPlacementReceiver zr)
                return (zr, "Played " + BattleBoardReader.CardName(_heldCard) + " to " + BattleBoardReader.ZoneName(_zone));
        }

        // Trainer / Item / Supporter → play it; no board target, so it goes to the local "pending"
        // slot (the game's own receiver for CardHasUsableActions → UseCard). Drops from any focus.
        if (info.activeUsableActions.Count > 0
            && _manager.GetOwnerForBoardPos(Board.GetBoardPos(PlayerPos.PENDING, PlayerID.LOCAL)) is ICardPlacementReceiver pend)
            return (pend, "Played " + BattleBoardReader.CardName(_heldCard));

        return (null, "");
    }

    // Point the game's placement loop at the current focus EVERY FRAME while holding. This is
    // required: the loop commits to whatever receiver it last saw as "imminent" the frame BEFORE
    // we raise Commit, so the chosen receiver must already be set before the Enter that drops.
    private void RefreshHeldChosen() => _placeInit.SetChosen(ComputeDropTarget().recv);

    // Drop the held card onto whatever the player is currently focused on.
    private void AttemptDrop()
    {
        if (!_holding || _heldCard == null || _manager == null) return;
        var (recv, desc) = ComputeDropTarget();
        if (recv == null) { _router.Speech.Say("Not a valid spot for this card", interrupt: true); return; }

        _placeInit.SetChosen(recv);
        _placeInit.Commit = true;   // loop has been seeing this receiver as imminent → commits next frame
        _holding = false;
        _heldCard = null;
        Log.Info($"BattleScreen: drop committed — {desc}");   // spoken result comes from the battle log (localized)
    }

    private void CancelPickup()
    {
        try { _manager?.TargetingHandler.ClearTarget(); }
        catch (Exception ex) { Log.Error($"BattleScreen: ClearTarget threw: {ex.Message}"); }
        _placeInit.Reset();
        var held = _heldCard;
        _holding = false;
        _heldCard = null;
        _router.Speech.Say(held != null ? $"Cancelled {BattleBoardReader.CardName(held)}" : "Cancelled", interrupt: true);
    }

    // No-target play (Item / Trainer / Supporter): drive the game's targeting handler to drop the
    // card onto the local pending slot, which fires UseCard — the same path a sighted player's drop
    // takes. The placement loop commits to whatever receiver it saw "imminent" the frame BEFORE
    // Commit flips true, so we keep the pending slot chosen and only raise Commit once it's been the
    // imminent receiver for a frame (>= 2 ticks). Runs instead of TickNav, so no field navigation.
    private void TickAutoPlay()
    {
        var recv = PendingReceiver();
        if (_heldCard == null || _manager == null || recv == null) { CancelAutoPlay(); return; }
        _placeInit.SetChosen(recv);
        if (++_autoPlayTicks >= 2)
        {
            _placeInit.Commit = true;   // pending slot has been imminent ≥1 frame → commits next frame
            _autoPlaying = false;
            _heldCard = null;
            Log.Info("BattleScreen: no-target play committed");   // result narrated by the battle log
        }
    }

    private void CancelAutoPlay()
    {
        try { _manager?.TargetingHandler.ClearTarget(); }
        catch (Exception ex) { Log.Error($"BattleScreen: auto-play ClearTarget threw: {ex.Message}"); }
        _placeInit.Reset();
        var held = _heldCard;
        _autoPlaying = false;
        _heldCard = null;
        if (held != null) _router.Speech.Say($"Couldn't play {BattleBoardReader.CardName(held)}", interrupt: true);
    }

    // The local "pending" slot owner — the receiver a no-target card drops onto (CardHasUsableActions
    // → UseCard). Same fallback receiver ComputeDropTarget resolves for activeUsableActions cards.
    private ICardPlacementReceiver? PendingReceiver() =>
        _manager != null && _manager.GetOwnerForBoardPos(Board.GetBoardPos(PlayerPos.PENDING, PlayerID.LOCAL)) is ICardPlacementReceiver p ? p : null;

    private BoardPos? ZoneToBoardPos(BattleBoardReader.Zone z) => z switch
    {
        BattleBoardReader.Zone.MyActive => Board.GetBoardPos(PlayerPos.ACTIVE, PlayerID.LOCAL),
        BattleBoardReader.Zone.MyBench => Board.GetBoardPos(PlayerPos.BENCH, PlayerID.LOCAL),
        BattleBoardReader.Zone.Stadium => Board.GetBoardPos(PlayerPos.STADIUM, PlayerID.LOCAL),
        _ => (BoardPos?)null,
    };

    // ---- Tutorial (FTUE) guidance: keep the player on the spotlighted card ----

    private Card3D? FtueFocusedCard()
    {
        if (!FTUEHook.FtueIsRunning) return null;
        var m = FTUEMatchManager.Instance;
        return m == null ? null : m.focusedCard;
    }

    // Does the FTUE permit acting on this card right now? Uses the game's OWN rule
    // (DoesFTUEPermitCardTargetability): true outside the FTUE, true in free play, true for the
    // spotlighted card during a forced step; false for a non-spotlighted card or a not-yet-armed step.
    // Replaces the old blockInputs check, which wrongly blocked during free play (blockInputs lingers).
    private static System.Reflection.MethodInfo? _ftuePermitMethod;
    private bool FtuePermitsCard(Card3D card)
    {
        if (!FTUEHook.FtueIsRunning) return true;
        var m = FTUEMatchManager.Instance;
        if (m == null) return true;
        try
        {
            // DoesFTUEPermitCardTargetability is private — reflect it (cached). It's the game's exact
            // rule: free play → true; spotlighted card (not blockDrag) → true; else false.
            if (_ftuePermitMethod == null)
                _ftuePermitMethod = typeof(FTUEMatchManager).GetMethod("DoesFTUEPermitCardTargetability",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (_ftuePermitMethod == null) return true;
            return (bool)_ftuePermitMethod.Invoke(m, new object[] { card });
        }
        catch { return true; }
    }

    // The targeting evaluator (PlacementPurposeEvaluator) decides "is this card placeable" with a
    // REFERENCE-keyed dictionary built from the hand SENDER's own Card3D instances —
    // HandController.GetCardsAccessibleForTargeting() = each hand slot's top card. We navigate the
    // hand off the card OWNER's list (PlayerCardOwner.GetOwnedCards via BattleBoardReader), and after
    // a hand mutation (a search puts a card into hand, a play removes one) those two sources can hold
    // DIFFERENT instances for the same logical card. Since IsCardPlaceable is ContainsKey(instance),
    // our owner-list instance then reads as "not targetable" even though the game says the card is
    // playable (attachableEntities non-empty). Re-fetch the sender's instance by entityID so we pick
    // up / target exactly what the evaluator validated. Returns null if the hand has no such card.
    private Card3D? ResolveHandTargetingInstance(string entityID)
    {
        if (string.IsNullOrEmpty(entityID) || _manager == null) return null;
        try
        {
            var hand = _manager.board?.GetPlayer(PlayerID.LOCAL)?.hand;
            if (hand == null) return null;
            // Exact instance, when it's the top of its slot.
            foreach (var c in hand.GetCardsAccessibleForTargeting())
                if (c != null && c.cardInfo != null && c.cardInfo.entityID == entityID) return c;
            // Otherwise it's a DUPLICATE buried in a stacked slot. HandCardSlot stacks identical cards
            // (same cardDataRow.ArchetypeID — shown as ONE pile with a count badge) and only the TOP
            // card is "accessible for targeting" (GetCardsAccessibleForTargeting = slot top-cards). So a
            // second copy of the same card reads as un-targetable. Target the slot's top card instead —
            // an interchangeable copy, so the play is identical: exactly what clicking the visual stack does.
            foreach (var slot in hand.HandSlots)
                if (slot != null && slot.DoesSlotContainCardEntityID(entityID))
                    return slot.GetTopCard();
        }
        catch (Exception ex) { Log.Warn($"ResolveHandTargetingInstance threw: {ex.Message}"); }
        return null;
    }

    // Diagnostic line for the "you can't play X" exits. The card's legal-play sets are CACHED on
    // cardInfo (the SDK recomputes them in CardInfo.UpdateAll on every server state update) and a
    // HAND card's sets are only filled when the SDK's myPlayerInfo.isMyTurn is true. We enter the
    // play path on CanPerformNewActions / IsSetupPhase, which is a different turn notion — so logging
    // both the counts AND the two turn flags side by side tells us whether a block is a real game
    // rule (counts 0 while it genuinely is our turn) or a turn-notion mismatch (mgr says go, SDK
    // hasn't filled legality). Only called on a block, so the cost is irrelevant.
    private string PlayDiag(Card3D card)
    {
        var info = card?.cardInfo;
        string counts = info != null
            ? $"place={info.placeableBoardPos.Count} attach={info.attachableEntities.Count} use={info.activeUsableActions.Count}"
            : "no cardInfo";
        bool sdkTurn = false;
        try { sdkTurn = NetworkMatchController.currentMatchInfo?.myPlayerInfo?.isMyTurn ?? false; } catch { }
        return $"{counts} canPerform={_manager?.CanPerformNewActions} setup={_manager?.IsSetupPhase} mgrTurn={_manager?.IsMyTurn} sdkTurn={sdkTurn} ftueRun={FTUEHook.FtueIsRunning}";
    }

    // On each new coaching step (the FTUE spotlights a different card), announce which card it wants
    // and where — WITHOUT moving the player's focus (jumping the cursor is disorienting). Fires only
    // when the spotlight changes; navigating between steps stays entirely up to the player.
    private void TutorialGuidanceTick()
    {
        var ftue = FtueFocusedCard();
        if (ftue == null) { _ftueCardId = ""; return; }
        if (ftue.entityID == _ftueCardId) return;
        _ftueCardId = ftue.entityID;
        _router.Speech.Say(TutorialWantsText(ftue), interrupt: false);
    }

    // Unified "Tutorial wants <card>, in <zone>" phrasing — shared by the spotlight announce, the
    // wrong-card action guard, and the end-turn block so the player always hears the same objective.
    private string TutorialWantsText(Card3D ftue)
    {
        var zone = ZoneOfCard(ftue);
        string where = zone != null ? $", in {BattleBoardReader.ZoneName(zone.Value)}" : "";
        return $"Tutorial wants {BattleBoardReader.CardName(ftue)}{where}";
    }

    private BattleBoardReader.Zone? ZoneOfCard(Card3D c)
    {
        var info = c?.cardInfo;
        if (info == null) return null;
        var pp = Board.GetPlayerPos(info.currentPos);
        bool mine = Board.GetPlayerI(info.currentPos) == PlayerID.LOCAL;
        return pp switch
        {
            PlayerPos.ACTIVE => mine ? BattleBoardReader.Zone.MyActive : BattleBoardReader.Zone.OppActive,
            PlayerPos.BENCH => mine ? BattleBoardReader.Zone.MyBench : BattleBoardReader.Zone.OppBench,
            PlayerPos.HAND => mine ? BattleBoardReader.Zone.MyHand : (BattleBoardReader.Zone?)null,
            PlayerPos.DISCARD => mine ? BattleBoardReader.Zone.MyDiscard : BattleBoardReader.Zone.OppDiscard,
            PlayerPos.STADIUM => BattleBoardReader.Zone.Stadium,
            _ => null,
        };
    }

    private static string? PhaseText(MatchManager.MatchState state) => state switch
    {
        MatchManager.MatchState.Setup => "Setup phase",
        MatchManager.MatchState.LocalTurn => "Your turn",
        MatchManager.MatchState.OpponentTurn => "Opponent's turn",
        _ => null,
    };
}
