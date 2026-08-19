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
        // LIVE, not a snapshot. The game re-evaluates option interactivity asynchronously — the FTUE
        // arms its wanted attack only once the overlay is open and settled (FTUEActionIndicator waits
        // on `!overlay.isOpen || overlay.isAnimating`, THEN calls Card3D.FocusAttack), and
        // RefreshActionUsability defers its repopulate by framesToWaitOnRefresh. So the interactable
        // flag we'd read while building this menu is always the pre-arm one.
        public Func<bool> CanUse = () => true;
        public Action Invoke = () => { };
    }
    // Usability of the option we last spoke, so the game's repopulate can re-announce only on a change.
    private bool _spokenActionUsable;
    private List<BattleAction> _actions = new();
    private int _actionIdx;

    // Native hand-card placement state ("hold and drop" — drives the game's CardTargetingHandler).
    private readonly KbPlacementInitiator _placeInit = new();
    private bool _holding;
    private Card3D? _heldCard;

    // No-target play state (Item / Trainer / Supporter): the game's targeting handler still runs,
    // but the receiver is always the local pending slot, so we auto-commit without any field nav.

    // A no-target card awaiting the player's "Use X?" confirmation (Enter plays, Backspace cancels).
    private Card3D? _confirmCard;

    // True while a LOCAL selection request is open (prize / discard / search / choose-active / …),
    // tracked off the game's own SelectionStateChanged event. We block turn-ending and new actions
    // while it's set so we can't desync the match — even for selection families we don't drive yet.
    private bool _localSelecting;
    private MatchManager? _selSubManager;   // manager we've hooked SelectionStateChanged on
    private HandController? _handSub;        // local hand we've hooked OnCardAdded/OnCardRemoved on

    // An active attach-distribution selection (e.g. Oricorio ex Excited Turbo: "attach Basic Fire Energy
    // to your Benched Fire Pokémon"). It's a placement, not a list — so instead of a mod overlay the
    // player attaches with the NORMAL board drag: pick up a hand energy (Enter in your hand), navigate to
    // a benched Pokémon (B + arrows), Enter to drop; Tab = Done. Set by BattlePromptScreen, which gives
    // the attach selection NO overlay. Everything below is gated on this being non-null, so normal play
    // and all other prompts are byte-for-byte unchanged.
    private AttachmentDistributionSelectionMenu? _attachSel;
    // Reparent (move-energy) first-press pickup waiting for the just-distributed energy to surface in the
    // temp hand. Non-null = retrying the pickup from Tick; see TickReparentPickup. Almost always resolves
    // on frame 1 (the game's own distribute refreshes targetability), so this is belt-and-suspenders.
    private Card3D? _reparentPickupPending;
    private int _reparentPickupTicks;

    // Tutorial guidance: the entityID of the FTUE-spotlighted card we last announced.
    private string _ftueCardId = "";

    // True once we've announced the screen-space message bar's "Done" button (setup confirm etc.).
    private bool _doneBarShown;
    // True between Tab (move focus to Done) and the confirming Enter — see the Done handling in TickNav.
    private bool _doneFocused;

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

        // The overlay rebuilds its option widgets — and with them each button's interactable flag —
        // whenever the game re-evaluates action usability. RefreshActionUsability only SCHEDULES that
        // (AnywhereCoroutine.YieldForFrames(framesToWaitOnRefresh)), so PopulateOptions is the moment
        // the answer actually changes. The tutorial's attack step arms its wanted attack this way, well
        // after we built the menu, so this is how an "unavailable" option becomes usable under the
        // player's cursor without them having to close and reopen the menu.
        var populate = AccessTools.Method(typeof(AttackOverlay), "PopulateOptions");
        if (populate != null)
            harmony.Patch(populate, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattleScreen), nameof(PopulateOptions_Postfix))));
        else
            Log.Warn("BattleScreen: AttackOverlay.PopulateOptions not found — action usability won't refresh live");

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

    // The game just recomputed which options are clickable. If the player is sitting in our action menu
    // on that card, the option they're focused on may have flipped — re-announce it, but only on a real
    // change (PopulateOptions also runs on every open, and on unrelated refreshes).
    private static void PopulateOptions_Postfix(AttackOverlay __instance)
    {
        var self = _instance;
        if (self == null || self._mode != Mode.ActionMenu) return;
        if (self._manager == null || !ReferenceEquals(__instance, self._manager.attackOverlay)) return;
        if (self._actionIdx < 0 || self._actionIdx >= self._actions.Count) return;
        if (self._actions[self._actionIdx].CanUse() == self._spokenActionUsable) return;
        self.SayAction(self._actionIdx);
    }

    private static void StartGame_Postfix(MatchManager __instance)
    {
        if (_instance == null) return;
        _instance._manager = __instance;
        _instance._phase = __instance.currentState;
        // Permanent one-line telemetry: the global lesson flag vs the game's per-match FTUE switch.
        // The old ranked "tutorial isn't ready" lockout's exact mechanism is UNPROVEN (a Codex verify
        // showed the global-true-at-start story implies the delegates were installed too) — if it
        // ever recurs, this line answers which state the match actually opened in.
        Log.Info($"Battle start: ftueRunning={FTUEHook.FtueIsRunning} ftueGoverns={FTUEHook.FtueGovernsMatch(__instance)}");
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
            _active = false; _router.Deactivate(this);
            return;
        }
        // The attach-distribution selection resolved/closed → leave attach mode.
        if (_attachSel != null && !_attachSel.isOpen) EndAttach();
        if (!Application.isFocused) return;
        // A move-energy pickup is waiting on the just-lifted energy to become targetable — finish it.
        if (_reparentPickupPending != null) { TickReparentPickup(); return; }

        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (_mode == Mode.CardView) { TickCardView(); return; }
        if (_mode == Mode.ActionMenu) { TickActionMenu(); return; }
        if (_mode == Mode.ConfirmPlay) { TickConfirmPlay(); return; }   // "Use X?" — Enter plays, Backspace cancels
        if (!_holding) TutorialGuidanceTick();   // keep the player on the tutorial's spotlighted card
        TickNav(shift);
        if (_holding) RefreshHeldChosen();   // keep the placement loop aimed at the current focus
    }

    // ---- Navigation mode ----

    private void TickNav(bool shift)
    {
        bool back = Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape);

        // Backspace/Escape closes the game's card viewer (big-card, stack, or pile inspection) when open.
        // This is the backstop for a viewer opened OUTSIDE card-view mode (e.g. by the FTUE); a viewer the
        // mod opened is handled in TickCardView. Must run before the Escape→battle-menu fall-through below.
        if (back && InspectionOpen()) { CloseInspection(); return; }

        // While holding a picked-up card, Backspace cancels the drag and returns it to hand.
        if (_holding && Input.GetKeyDown(KeyCode.Backspace)) { CancelPickup(); return; }

        // Escape opens the battle menu (settings + Concede) — the panel the gear icon shows. We only
        // reach here with the field clear: selection prompts push their own handler over us, and the
        // big-card viewer was handled just above. Not while holding a card (Backspace cancels that).
        if (Input.GetKeyDown(KeyCode.Escape) && !_holding && BattleSettingsScreen.TryOpen()) return;

        // A "Done" / confirm is available when the screen-space message bar shows one (setup confirm,
        // e.g. "Choose your starting Pokémon") or an attach-distribution is in progress (finish
        // attaching). Tab MOVES FOCUS to Done and announces it — it does NOT auto-confirm; Enter then
        // confirms, the same focus-then-Enter model as the selection prompts. Any other board key
        // releases that focus so Enter goes back to picking up / placing cards.
        bool barDone = MessageBarDoneAvailable();
        if (barDone) { if (!_doneBarShown) { _doneBarShown = true; AnnounceDoneBar(); } }
        else _doneBarShown = false;
        bool doneAvailable = barDone;
        // Focusable even when the game has it greyed out — the desktop convention this mod follows is
        // "you can tab to a dimmed control, you just can't activate it". Say which it is.
        if (doneAvailable && Input.GetKeyDown(KeyCode.Tab))
        { _doneFocused = true; _router.Speech.Say(MessageBarDoneUsable() ? "Done" : "Done, unavailable", interrupt: true); return; }
        if (doneAvailable && _doneFocused && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
        { _doneFocused = false; ConfirmDone(); return; }
        if (!doneAvailable) _doneFocused = false;
        else if (_doneFocused && Input.anyKeyDown && !Input.GetKeyDown(KeyCode.Tab)) _doneFocused = false;

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
        // A move-energy ability (Solar Transfer) never touches your hand — the energy is already on your
        // Pokémon. So C is a silent no-op here: don't switch zones, don't speak, just stay on the board.
        // (Attach-from-a-pile really does use the temp hand on C — that path is below.)
        if (_attachSel is ReparentCardMenu && z == BattleBoardReader.Zone.MyHand) return;
        _zone = z; _card = 0; _line = -1;
        string zoneName = BattleBoardReader.ZoneName(z);

        // During an attach-distribution, C (your hand) shows the menu's attach energies — the game lifts
        // them into a temporary hand, NOT your real hand — so you handle the energy then press B to drop
        // it on a Pokémon. The energies read by their localized card name; the zone label is a mod string.
        // Attach-from-a-pile (Oricorio ex, Teal Dance): C shows the temp hand the game lifts the pile
        // energy into, so you pick one and drop it on a Pokémon. (Move-energy never reaches here — handled
        // above — because for a move the energy is on the board, not in any hand.)
        if (_attachSel != null && z == BattleBoardReader.Zone.MyHand)
        {
            _zoneCards = AttachEnergyCards();
            if (_zoneCards.Count == 0) { _router.Speech.Say("No energy left to attach", interrupt: true); return; }
            _router.Speech.Say($"Energy to attach, {AttachEnergyCount()} available. {CardLabel(0)}", interrupt: true);
            return;
        }

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
        // (Not for the attach temp hand: its total is announced in the zone label instead.)
        if (_zone == BattleBoardReader.Zone.MyHand && _attachSel == null)
        {
            int n = BattleBoardReader.HandStackCount(_manager!, card);
            if (n > 1) name += $", {n} copies";
        }
        name += ReparentTag(card);   // ", has energy to move" / ", can receive energy" during a Solar Transfer-style move
        return $"{name}, {i + 1} of {_zoneCards.Count}";
    }

    // Re-read the current browsable zone's cards in place (board may have changed while a prompt was
    // up, or after a play). Keeps the cursor on a valid index; no speech — navigation reads fresh.
    private void RefreshZoneCards()
    {
        if (_manager == null || _zoneCards == null || BattleBoardReader.IsCountOnly(_zone)) return;
        _zoneCards = (_attachSel != null && _zone == BattleBoardReader.Zone.MyHand)
            ? AttachEnergyCards()
            : BattleBoardReader.GetCards(_manager, _zone);
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
    private bool MessageBarDoneAvailable() => MessageBarDoneButton() != null;

    // Whichever CentralMessageBar owns the Done the player is looking at. An attach distribution carries
    // its own bar reference (a [SerializeField] on the menu prefab, so it need not be the shared
    // screen-space one) and drives that bar's Done interactability itself — read it from the menu rather
    // than assuming. Everything else (setup confirm, "Choose your starting Pokémon") uses the screen-space bar.
    private static System.Reflection.FieldInfo? _attachMessageBarField;
    private CentralMessageBar? ActiveMessageBar()
    {
        if (_attachSel != null)
        {
            try
            {
                _attachMessageBarField ??= AccessTools.Field(typeof(AttachmentDistributionSelectionMenu), "messageBar");
                if (_attachMessageBarField?.GetValue(_attachSel) is CentralMessageBar bar && bar != null) return bar;
            }
            catch (Exception ex) { Log.Warn($"BattleScreen: attach messageBar read threw: {ex.Message}"); }
        }
        return _manager?.MessageBarScreenSpace;
    }

    private void AnnounceDoneBar()
    {
        // Read only the game's own message-bar prompt — no "Press Tab for Done" hint (the player
        // discovers Done by tabbing, the universal convention; see the no-control-hints rule). During an
        // attach distribution this is the menu's own instruction ("Attach N energy to your Pokémon"),
        // which is also the answer to "why is Done unavailable".
        string msg = ActiveMessageBar()?.CurrText?.Trim() ?? "";
        if (msg.Length > 0) _router.Speech.Say(msg, interrupt: true);
    }

    // The real Done button on the game's message bar (CentralMessageBar.DoneButtonTransform). Null when
    // the bar isn't showing one — some selections auto-submit and never offer Done.
    private UnityEngine.UI.Button? MessageBarDoneButton()
    {
        try
        {
            var t = ActiveMessageBar()?.DoneButtonTransform;
            var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return btn != null && btn.gameObject.activeInHierarchy ? btn : null;
        }
        catch { return null; }
    }

    // Can the game actually accept Done right now? The BUTTON's interactable flag is the answer, and it
    // is the only one. The attach distribution drives it through UpdateSubmitButtonInteractability →
    // messageBar.SetButtonInteractability(AttachmentConstraints.AreMinimumConstraintsSatisfied() && …),
    // and during the tutorial FTUECardIndicator sets SecondaryAttachedQuantityRequirement, so
    // AreMinimumConstraintsSatisfied means "you have attached exactly the N energies I asked for".
    private bool MessageBarDoneUsable() => MessageBarDoneButton() is { } b && b.IsInteractable();

    // Confirm the focused Done — the same call a sighted player's tap makes.
    //
    // Button.interactable does NOT gate onClick.Invoke(); it only stops the POINTER from raising the
    // click. So invoking a greyed-out Done runs its handler as if the rules were met. That's what let
    // Done submit an attach distribution with zero energies attached: CompleteReparentingAction ran, the
    // menu closed, and the tutorial's FTUECardIndicator — which loops `while (focusedCard != null)` once
    // per energy it wants placed — waited forever, leaving every card on the board FTUE-blocked. The game
    // itself only ever bypasses the minimum from OnTimeExpired(). So: never invoke a button the game has
    // disabled, and never reach past it to the op behind it (we used to reflect straight into the private
    // CompleteReparentingAction — that bypassed the gate even more thoroughly).
    private void ConfirmDone()
    {
        var btn = MessageBarDoneButton();
        if (btn == null) { _router.Speech.Say("Can't confirm right now", interrupt: true); return; }
        if (!btn.IsInteractable()) { _router.Speech.Say("Done, unavailable", interrupt: true); return; }
        // No "Done" speech on the press: Tab already announced it when focus moved there, so saying it
        // again on confirm is a redundant double-announce (user-reported). The game's own next-phase /
        // battle-log narration is the confirmation.
        try { btn.onClick.Invoke(); }
        catch (Exception ex) { Log.Error($"BattleScreen: Done press threw: {ex.Message}"); }
    }

    // The game has THREE single-card inspection overlays, and which one a click opens depends on the
    // card (verified in the decompiled controllers): a board Pokémon DEEP-inspects to the STACK overlay
    // (stackCardOverlay — Pokémon + its attached cards), and SHALLOW-inspects (or a hand card always
    // opens) the BIG card overlay (bigCardOverlay); a card pile opens the CARD INSPECTION menu
    // (cardInspectionMenu). All three are BattleMenus with isOpen/Close. The mod used to track only
    // bigCardOverlay, so inspecting a board Pokémon (which opens stackCardOverlay) left the handler
    // thinking nothing was open → it fell back to field nav and Escape opened the battle menu instead
    // of closing the viewer. "Is a viewer open" / "close the viewer" must cover all three.
    private bool InspectionOpen()
    {
        var m = _manager;
        return m != null &&
            ((m.bigCardOverlay != null && m.bigCardOverlay.isOpen)
             || (m.stackCardOverlay != null && m.stackCardOverlay.isOpen)
             || (m.cardInspectionMenu != null && m.cardInspectionMenu.isOpen));
    }

    // Close whichever inspection overlay is open (each routes through the game's menu navigation GoBack).
    private void CloseInspection()
    {
        var m = _manager;
        if (m == null) return;
        BattleMenu? open = (m.bigCardOverlay != null && m.bigCardOverlay.isOpen) ? m.bigCardOverlay
                         : (m.stackCardOverlay != null && m.stackCardOverlay.isOpen) ? m.stackCardOverlay
                         : (m.cardInspectionMenu != null && m.cardInspectionMenu.isOpen) ? (BattleMenu)m.cardInspectionMenu
                         : null;
        if (open == null) return;
        try { open.Close(); } catch (Exception ex) { Log.Error($"BattleScreen: inspection Close threw: {ex.Message}"); }
        _router.Speech.Say("Closed card view", interrupt: true);
    }

    // Toggle the game's enlarged card viewer for the focused card. This drives the same inspection
    // path a mouse tap uses (owner.OnCardClicked → TryInitiateOwnerInspection → the right overlay), and
    // it satisfies tutorial coaching that gates on the big-card view being open
    // (FTUEBigCardIndicator waits on bigCardOverlay.isOpen + blocks placement until you've looked).
    // I → open the game's card viewer for the focused card AND enter a locked reading mode: you
    // stay on this one card and read every printed line with Up/Down. Backspace/Escape/I exits.
    private void EnterCardView()
    {
        if (_manager == null) return;
        if (InspectionOpen()) { CloseInspection(); _mode = Mode.Nav; _cvCard = null; _cvLines = null; return; }   // toggle off an already-open viewer (e.g. FTUE)
        if (_zoneCards == null || _card < 0 || _card >= _zoneCards.Count) return;
        var card = _zoneCards[_card];
        if (card == null) return;

        // isAltPress: true forces the INSPECTION path (matching the game's own Card3D.OnCardClicked,
        // which always forwards true) — without it, your own Pokémon with a usable action would open the
        // ATTACK overlay instead of a card viewer. Enter is the mod's action key; I is purely inspect.
        if (card.owner is BaseCardOwner owner)
        {
            try { owner.OnCardClicked(card, true); }   // opens the real viewer (also clears FTUE big-card gates)
            catch (Exception ex) { Log.Error($"BattleScreen: inspect OnCardClicked threw: {ex.Message}"); }
        }
        // Only enter the locked reading mode if a viewer actually opened — the game blocks inspection in
        // some states (FTUEMatchManager.blockInspection) and some cards aren't inspectable. Entering
        // CardView with nothing open is exactly the stranded state this fix removes.
        if (!InspectionOpen()) { _router.Speech.Say("Can't inspect this card right now", interrupt: true); return; }
        _cvCard = card;
        _cvLines = BattleBoardReader.DetailLines(card);
        _cvLine = -1;
        _mode = Mode.CardView;
        _router.Speech.Say($"Card view, {BattleBoardReader.CardNameWithType(card)}", interrupt: true);
    }

    private void TickCardView()
    {
        // Exit if the viewer was closed externally (the game/FTUE dismissed it).
        if (!InspectionOpen())
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
        CloseInspection();
        _mode = Mode.Nav;
        _cvCard = null;
        _cvLines = null;
        _cvLine = -1;
    }

    // ---- Action menu mode ----

    private void OpenActionMenu()
    {
        Log.Info($"OpenActionMenu entry: zone={_zone} card={_card}/{_zoneCards?.Count ?? -1} localSel={_localSelecting} holding={_holding} mode={_mode} attach={_attachSel != null}");
        // Attach-distribution selection active: Enter on a hand energy picks it up to attach (the drop
        // onto a Pokémon goes through AttemptDrop). This is BEFORE the _localSelecting block because the
        // attach IS the pending selection — it's fulfilled by this drag, not blocked by it.
        if (_attachSel != null)
        {
            var focused = (_zoneCards != null && _card >= 0 && _card < _zoneCards.Count) ? _zoneCards[_card] : null;
            // Move-energy (Solar Transfer): Enter on a source Pokémon lifts its energy and holds it; you
            // then drop it on a target like any attach. Other board cards have nothing to move.
            if (_attachSel is ReparentCardMenu)
            {
                if (IsReparentSource(focused)) { DistributeFromReparentSource(focused!); return; }
                _router.Speech.Say(focused != null ? $"{BattleBoardReader.CardName(focused)} has no energy to move"
                                                   : "Choose a Pokémon to move energy from", interrupt: true);
                return;
            }
            // Attach-from-pile (Oricorio ex): Enter on a temp-hand energy picks it up to attach.
            if (focused != null) AttachPickup(focused);
            else _router.Speech.Say("Select an energy from your hand", interrupt: true);
            return;
        }
        // While the game is waiting on a local selection, starting a new action would desync — block it.
        if (_localSelecting)
        { _router.Speech.Say("Finish the current selection first", interrupt: true); return; }
        // Re-read the zone before acting: an earlier action (e.g. attaching energy via Excited Turbo)
        // churns card ownership, which can leave the cached _zoneCards holding a stale/null reference
        // for a slot. Refreshing here picks up the live Card3D so Enter on the Active spot acts on the
        // real Pokémon instead of silently no-oping on a dead reference.
        RefreshZoneCards();

        if (_zoneCards == null || _card < 0 || _card >= _zoneCards.Count)
        { _router.Speech.Say("Nothing to act on", interrupt: true); return; }

        var card = _zoneCards[_card];
        if (card == null)
        {
            // Stale/empty slot even after refresh — tell the player instead of silently doing nothing
            // (the "frozen, Enter does nothing on my Active" symptom).
            _router.Speech.Say("Nothing to act on", interrupt: true);
            return;
        }

        // Tutorial guard: only act on cards the tutorial currently PERMITS. DoesFTUEPermitCardTargetability
        // is the game's own rule — TRUE in free play (the guided part is over and you play freely to win),
        // TRUE for the spotlighted card during a forced step, FALSE otherwise (wrong card, or a step that
        // hasn't armed yet). This blocks the "I attacked before the tutorial allowed it" freeze WITHOUT
        // the false "tutorial isn't ready" during a free turn (the old blockInputs+focusedCard guard hit
        // that because blockInputs stays set under free play). Normal (non-FTUE) matches never block.
        if (!FtuePermitsCard(card))
        {
            var ftueCard = FtueFocusedCard();
            // Log the RAW spotlight — the filtered one is null whenever the board is mid-resolve, and
            // "spotlight=none" in a block line would hide exactly what we'd want to see afterwards.
            var raw = RawFtueFocusedCard();
            Log.Info($"OpenActionMenu FTUE-blocked '{BattleBoardReader.CardName(card)}' (spotlight={(raw != null ? BattleBoardReader.CardName(raw) : "none")}, actionable={ftueCard != null}): {PlayDiag(card)}");
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
        // The in-play Stadium is activated exactly like a Pokémon ability — the game's StadiumController
        // taps it open in the SAME AttackOverlay (focusMenu.Show) whenever it has a usable action. So a
        // stadium with a usable ability gets the overlay too (ownership doesn't matter — stadiums are
        // shared). Without a usable action it falls through to "No actions for <stadium>".
        bool stadiumCard = _zone == BattleBoardReader.Zone.Stadium && myTurn
                           && card.cardInfo != null && card.cardInfo.activeUsableActions != null
                           && card.cardInfo.activeUsableActions.Count > 0;
        if (overlayCard || stadiumCard) TryOpenAttackOverlay(card);

        _actions = BuildActions(card);
        if (_actions.Count == 0)
        {
            Log.Info($"OpenActionMenu no actions '{BattleBoardReader.CardName(card)}' zone={_zone} overlayCard={overlayCard} canPlace={canPlace}: {PlayDiag(card)}");
            if (overlayCard || stadiumCard) TryCloseAttackOverlay();
            _router.Speech.Say($"No actions for {BattleBoardReader.CardName(card)}", interrupt: true);
            return;
        }

        _mode = Mode.ActionMenu;
        _actionIdx = 0;
        // Options may read "unavailable" right now and become usable a moment later: during a tutorial
        // attack step the game only arms its wanted attack once this overlay is open and done animating
        // (FTUEActionIndicator → Card3D.FocusAttack), and the repopulate that follows is deferred by
        // AttackOverlay.framesToWaitOnRefresh. PopulateOptions_Postfix re-announces if that flips.
        _router.Speech.Say($"Actions for {BattleBoardReader.CardName(card)}.", interrupt: true);
        SayAction(0, interrupt: false);
    }

    private void TryOpenAttackOverlay(Card3D card)
    {
        try
        {
            var overlay = _manager?.attackOverlay;
            if (overlay == null) return;
            // ALWAYS point the overlay at the FOCUSED card — exactly what a sighted player gets by
            // clicking that Pokémon. The game's AttackOverlay.Show only populates when currentCard is
            // null (`if (!(currentCard != null))`), so calling Show on an overlay already open for a
            // DIFFERENT Pokémon is a NO-OP — it keeps the previous card's attack/ability options. That's
            // the freeze: after using Oricorio ex's Excited Turbo the overlay stayed open on Oricorio,
            // so opening Mega Charizard X's menu still listed Excited Turbo, and invoking it fired
            // Oricorio's ability out of context → desync. Close() flips isOpen=false and nulls
            // currentCard synchronously (BattleMenu.Close + AttackOverlay.OnClose), so closing first
            // then Show(card) cleanly repopulates for the focused card.
            if (overlay.isOpen && overlay.currentCard != card) overlay.Close();
            if (!overlay.isOpen) overlay.Show(card);
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

    // "unavailable" is resolved at speak time, never baked into the label — the game can arm or disarm
    // an option after this menu was built (see BattleAction.CanUse).
    private string ActionLabel(int i)
    {
        var a = _actions[i];
        var label = a.CanUse() ? a.Label : a.Label + ", unavailable";
        return $"{label}, {i + 1} of {_actions.Count}";
    }

    // Speak an action and remember whether it was usable, so a later repopulate can tell a real change
    // from a no-op and only re-announce when the answer actually flipped.
    private void SayAction(int i, bool interrupt = true)
    {
        _spokenActionUsable = _actions[i].CanUse();
        _router.Speech.Say(ActionLabel(i), interrupt);
    }

    private void TickActionMenu()
    {
        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (_actionIdx < _actions.Count - 1) { _actionIdx++; SayAction(_actionIdx); }
            else _router.Speech.Say("Last action", interrupt: true);
            return;
        }
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (_actionIdx > 0) { _actionIdx--; SayAction(_actionIdx); }
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
            var action = _actions[_actionIdx];
            // The option's own button is the ONLY gate. AttackOverlay.CanUseAttack/CanUseAbility and
            // WeakResistRetreatBar already fold in the FTUE's per-action spotlight (attackFocused /
            // abilityFocused / retreatFocused), and a teaching step calls Card3D.FocusNone() so every
            // option goes non-interactable. We used to layer our own "an FTUEAttackOverlayIndicator is
            // running ⇒ refuse" rule on top; that state outlives its coaching (only the Close/Card/Buffs/
            // Debuffs variants ever call NextState), so it kept refusing the attack the tutorial had
            // since asked for. Trust the game.
            if (!action.CanUse())
            {
                _router.Speech.Say("Not usable now", interrupt: true);   // greyed option — stay in the menu
                return;
            }
            _mode = Mode.Nav;
            try { action.Invoke(); }   // result narrated by the battle log (localized); no echo from us
            catch (Exception ex) { Log.Error($"BattleScreen action '{action.Label}' threw: {ex.Message}"); }
            // The overlay's own option click closes it (HandleOverlayActionClickCleanup), but a retreat
            // or an ability that leaves it open would otherwise linger on this card and re-surface as
            // stale options on the NEXT Pokémon's menu. Close defensively so the overlay never carries a
            // stale currentCard between Pokémon (the Excited-Turbo-on-Charizard freeze). No-op if already closed.
            TryCloseAttackOverlay();
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
        if (_manager == null || !_manager.CanPerformNewActions) return list;

        var where = Board.GetPlayerPos(info.currentPos);
        // Stadiums are shared: whoever's turn it is may use the in-play stadium's ability (the server
        // lists it in activeUsableActions) no matter who played it. Every other card needs ownership.
        bool isStadium = where == PlayerPos.STADIUM;
        if (!isStadium && !info.isMyCard) return list;

        if (where == PlayerPos.HAND)
        {
            // Hand cards are played through the game's native targeting flow, invoked directly from
            // OpenActionMenu (StartPickup → hold-and-drop for target cards, or the no-target
            // auto-play for Item/Trainer/Supporter) — never via direct ops or this action menu.
            return list;
        }
        if (where == PlayerPos.ACTIVE || where == PlayerPos.BENCH || isStadium)
        {
            // Drive the game's REAL attack overlay options (opened in OpenActionMenu). Clicking an
            // option's OnClick() is the exact sighted-player flow (→ TryUseAction → confirmations →
            // op) and is what the FTUE watches — never our own UseCard. A stadium surfaces its effect
            // here as an ability option.
            var overlay = _manager.attackOverlay;
            // The overlay must be open AND showing THIS card. The currentCard guard is the safety net
            // for the stale-overlay bug (see TryOpenAttackOverlay): if Show didn't re-point — e.g. a
            // mid-animation Close left it briefly on the old card — we must NOT read another Pokémon's
            // options onto this one. No match → no actions (caller closes + says "No actions").
            if (overlay == null || !overlay.isOpen || overlay.currentCard != card) return list;

            if (overlay.AttackOptions != null)
                foreach (var opt in overlay.AttackOptions) AddOverlayOption(list, opt, "Attack", card);
            if (overlay.AbilityOptions != null)
                foreach (var opt in overlay.AbilityOptions) AddOverlayOption(list, opt, "Use ability", card);

            // Retreat: same rule as the attacks — the RetreatOption's own button. WeakResistRetreatBar
            // builds it as `owner is ActiveController && canRetreat && CanPerformNewActions &&
            // retreatFocused > -2`, so it already folds in the match-info flag AND the FTUE retreat
            // spotlight. Reading canRetreat alone missed the spotlight, letting a retreat fire during a
            // tutorial step that forbids it. Clean "Retreat" label (no card-action detail/name). Never
            // on a stadium (it has no retreat — its overlay's RetreatOption is hidden anyway).
            var retreat = isStadium ? null : overlay.RetreatOption;
            if (retreat != null)
                list.Add(new BattleAction
                {
                    Label = "Retreat",
                    CanUse = () => OptionUsable(retreat) && !FtueStepUnarmed(card),
                    Invoke = () => retreat.OnClick(),
                });
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
        // The overlay POOLS its option widgets: AttackOptions / AbilityOptions keep leftover entries from
        // a previously-shown Pokémon and just SetActive(false) the ones the current card doesn't have
        // (SetupAbilities/SetupAttacks/HideAttacksForBenchedPokemon). A sighted player never sees those —
        // so we must skip any whose GameObject is inactive, or a phantom option from another Pokémon
        // leaks in (e.g. Oricorio ex's Excited Turbo showing on Mega Charizard X, which has no ability).
        if (!opt.gameObject.activeInHierarchy) return;
        var action = opt.MatchAction;
        string nm = action != null && !string.IsNullOrEmpty(action.actionName) ? action.actionName : prefix;
        string detail = BattleBoardReader.ActionDetail(card, nm);
        string label = $"{prefix}: {nm}";
        if (detail.Length > 0) label += $", {detail}";
        list.Add(new BattleAction { Label = label, CanUse = () => OptionUsable(opt) && !FtueStepUnarmed(card), Invoke = () => opt.OnClick() });
    }

    // An action-arming tutorial step owns this card's actions but hasn't named one yet.
    //
    // FTUEActionIndicator / FTUEAbilityIndicator set focusedCard (BlockOtherCards), wait `while
    // (!overlay.isOpen || overlay.isAnimating)` — i.e. for the PLAYER to open the attack overlay — and
    // only THEN call Card3D.FocusAttack / FocusAbility / FocusRetreat. Until that moment all three focus
    // fields are still their -1 default, and AttackOverlay.CanUseAttack reads -1 as "no tutorial
    // restriction", so every usable attack's button is interactable. A mouse player is shielded from that
    // window by the overlay's open animation plus reaction time; a keyboard crosses it in two frames.
    //
    // Firing there — even the attack the tutorial wants — strands it: CardActionOption.OnClick() calls
    // UnfocusCard() first, so focusedCard goes null, and the indicator is left waiting forever on an
    // overlay we then closed, never reaching FocusAttack or NextState(). focusedCard stays null,
    // FTUEMatchUnlockActions never runs, and DoesFTUEPermitCardTargetability denies every card on the
    // board while the animator never advances to the opponent's AI states. Hard freeze, observed
    // 2026-07-10 after attacking quickly.
    //
    // CRITICAL SCOPING: this pre-arm window only exists under an ARMING indicator. Other spotlight
    // states never arm a per-action focus at all, so their -1 fields mean "no per-action restriction",
    // NOT "not armed yet" — and the game's own button.interactable already governs usability there.
    // FTUECardIndicator drives a stadium ability that opens a selection (e.g. Surfing Beach): it
    // spotlights the stadium via BlockOtherCards and just waits for the ability to fire, never calling
    // FocusAbility. Treating its permanent -1 as "unarmed" greyed the ability out forever ("can't use
    // the ability"), which is exactly the reported bug. So gate on ActionArmingIndicatorActive first.
    //
    // Armed = any of the three focus fields is >= 0. FocusNone() parks them at -2, which CanUseAttack
    // already treats as "nothing allowed", so a teaching step is unaffected by this check. Free play and
    // ordinary matches have focusedCard == null, so it never applies there.
    private bool FtueStepUnarmed(Card3D card)
    {
        if (card == null || !ReferenceEquals(RawFtueFocusedCard(), card)) return false;
        if (!FTUEHook.ActionArmingIndicatorActive) return false;
        return card.attackFocused < 0 && card.abilityFocused < 0 && card.retreatFocused < 0;
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
    // slot. No _holding, so the field stays un-navigable. Driving the real targeting handler (not a
    // bare UseCard) keeps FTUE coaching and any play confirmations firing.
    //
    // Ordering matters and is dictated by the game's evaluation loop
    // (ReceiverPlacementHandler.EvaluateReceiversUntilPlacementAttemptIsEndedByInitiator): each pass
    // records the imminent receiver, yields a frame, and only THEN asks the initiator whether to
    // place. So the receiver must already be chosen when the loop's first pass runs — otherwise the
    // pass records nothing and the attempt resolves against a null receiver (→ CancelPlacement). We
    // therefore SetChosen before TryTargetCard starts the loop, then arm once. No frame counting: the
    // arm simply survives until the loop's next exit check, exactly like the game's own
    // MatchAutomationTargetingInitiator.
    private void BeginNoTargetPlay(Card3D card)
    {
        _mode = Mode.Nav;
        if (_manager == null) return;
        var pending = PendingReceiver();
        if (pending == null)
        {
            Log.Info($"BeginNoTargetPlay: no pending receiver for '{BattleBoardReader.CardName(card)}'");
            _router.Speech.Say($"Can't play {BattleBoardReader.CardName(card)} now", interrupt: true); return;
        }
        _manager.ApplyIdleStatePlacementContext();
        _placeInit.Reset();
        _placeInit.SetChosen(pending);
        // TryTargetCard is the game's own authority (it runs IsCardTargetable internally) — no pre-check.
        if (!_manager.TargetingHandler.TryTargetCard(card, _placeInit))
        {
            Log.Info($"BeginNoTargetPlay: TryTargetCard rejected '{BattleBoardReader.CardName(card)}': status={_manager.TargetingHandler.GetTargetabilityStatus(card)} {PlayDiag(card)}");
            _router.Speech.Say($"Can't play {BattleBoardReader.CardName(card)} now", interrupt: true); return;
        }
        _placeInit.ArmPlacement();
        Log.Info("BattleScreen: no-target play armed");   // result narrated by the battle log
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

        // Attach-distribution: the receiver is the focused board Pokémon, if the attach permits it
        // (ValidPlacementReceivers). The attach menu's placement context attaches the held energy onto it.
        if (_attachSel != null)
        {
            var focusedP = (_zoneCards != null && _card >= 0 && _card < _zoneCards.Count) ? _zoneCards[_card] : null;
            if (focusedP is ICardPlacementReceiver afr && IsAttachReceiver(focusedP))
                return (afr, "Attached to " + BattleBoardReader.CardName(focusedP));
            return (null, "");
        }

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
        // RefreshHeldChosen kept this receiver imminent every frame while holding, so the loop's
        // current pass has already recorded it — arming now commits onto exactly it.
        _placeInit.ArmPlacement();
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

    // ---- Attach-distribution (e.g. Oricorio ex Excited Turbo) handled via the normal board drag ----
    // BattlePromptScreen routes this selection here instead of opening a mod overlay: the player attaches
    // energy exactly like on their turn — pick up a hand energy, navigate to a benched Pokémon, drop;
    // Tab = Done. All of this is gated on _attachSel, so normal play / other prompts are untouched.

    // Called by BattlePromptScreen when the attach selection opens. Announce the game's prompt; the
    // player then uses C (hand) → Enter to grab an energy → B → drop on a Pokémon → Tab to finish.
    public static void BeginAttachSelection(AttachmentDistributionSelectionMenu menu, string question)
    {
        if (_instance == null || menu == null) return;
        _instance._attachSel = menu;
        _instance._holding = false;
        _instance._heldCard = null;
        if (!string.IsNullOrWhiteSpace(question)) _instance._router.Speech.Say(question, interrupt: true);
        Log.Info("BattleScreen: attach-distribution selection — using board drag");
    }

    private void EndAttach()
    {
        _attachSel = null;
        _reparentPickupPending = null;
        if (_holding)
        {
            try { _manager?.TargetingHandler.ClearTarget(); } catch { }
            _placeInit.Reset();
            _holding = false;
            _heldCard = null;
        }
    }

    // Pick up a hand energy to attach. Leaves the attach menu's placement context active (does NOT switch
    // to the idle context), so TryTargetCard routes the energy through the menu's AttachCardToOtherCard
    // purpose; the handler itself rejects a card that can't be attached.
    private void AttachPickup(Card3D card)
    {
        if (_attachSel == null || _manager == null || card == null) return;
        // The energies sit in the menu's TEMPORARY hand; like any carousel/hand card they only become
        // targetable once the placement context re-evaluates their just-settled positions. StartPickup
        // gets this for free via ApplyIdleStatePlacementContext, but we must NOT switch contexts here —
        // that would drop the menu's AttachCardToOtherCard purpose. So refresh the CURRENT (attach)
        // context instead. This was the missing step and the likely cause of the reattach intermittently
        // not taking online (the energy wasn't targetable yet, so TryTargetCard silently rejected it).
        try { _manager.TargetingHandler.RefreshPlacementContext(); }
        catch (Exception ex) { Log.Error($"BattleScreen: attach RefreshPlacementContext threw: {ex.Message}"); }
        _placeInit.Reset();
        if (!_manager.TargetingHandler.TryTargetCard(card, _placeInit))
        { _router.Speech.Say($"Can't attach {BattleBoardReader.CardName(card)}", interrupt: true); return; }
        _holding = true;
        _heldCard = card;
        _router.Speech.Say($"Holding {BattleBoardReader.CardName(card)}", interrupt: true);
    }

    // Is the focused board card a Pokémon the held energy may attach to (per the attach menu's own rule)?
    private bool IsAttachReceiver(Card3D c)
    {
        if (_attachSel == null || _heldCard == null || c == null) return false;
        try { return _attachSel.ValidPlacementReceivers(_heldCard).Any(r => ReferenceEquals(r, c)); }
        catch { return false; }
    }

    // The menu's temporary hand (a ReparentHandController : HandController) holding the attachable
    // energies. Private on the menu → reflected. This is where the energies live during the attach, NOT
    // board.GetPlayer(LOCAL).hand — so C reads from here while _attachSel is set.
    private static System.Reflection.FieldInfo? _tempHandField;
    private HandController? AttachHand()
    {
        if (_attachSel == null) return null;
        try
        {
            _tempHandField ??= typeof(AttachmentDistributionSelectionMenu).GetField(
                "tempHand", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return _tempHandField?.GetValue(_attachSel) as HandController;
        }
        catch (Exception ex) { Log.Error($"BattleScreen: attach tempHand read threw: {ex.Message}"); return null; }
    }

    // The attach energies as navigable cards (stacked pile tops, like the real hand). Pickup peels the top.
    private List<Card3D> AttachEnergyCards()
    {
        var hand = AttachHand();
        if (hand == null) return new List<Card3D>();
        try { return hand.GetCardsAccessibleForTargeting().Where(c => c != null).ToList(); }
        catch (Exception ex) { Log.Error($"BattleScreen: attach hand cards threw: {ex.Message}"); return new List<Card3D>(); }
    }

    // How many energies are still available to attach (total, across the pile) — for the "N available" label.
    private int AttachEnergyCount()
    {
        var hand = AttachHand();
        if (hand == null) return 0;
        try { return hand.GetOwnedCards().Count(c => c != null); }
        catch { return AttachEnergyCards().Count; }
    }

    // ---- Reparent (MOVE energy already on your Pokémon — e.g. Mega Venusaur ex's Solar Transfer) ----
    // Unlike attach-from-a-pile, the energy starts ON a Pokémon and only becomes grabbable after you pick
    // which Pokémon to take it FROM: a ReparentCardMenu puts a "distribute" click override on each eligible
    // source, and clicking one lifts its movable energy into the temp hand. So in a ReparentCardMenu, Enter
    // on a source Pokémon distributes + picks up its energy; you then drop it on a target exactly like any
    // other attach (the drop path in ComputeDropTarget/AttemptDrop is unchanged). Matches the sighted flow:
    // click a source, then click a target. AttachFromPile (Oricorio ex) is untouched — its energy is
    // already in the temp hand, so it never reaches these methods.

    // The temp hand typed so we can read which source is currently lifted.
    private ReparentHandController? ReparentHand() => AttachHand() as ReparentHandController;

    // The source Pokémon whose energy is currently lifted into the temp hand (null if none).
    private string? CurrentReparentSourceId()
    {
        try { return ReparentHand()?.CurrentDistributingEndpoint?.EndpointID; }
        catch { return null; }
    }

    // The Pokémon you can move energy FROM right now — the ones still holding a movable energy. Passing
    // null does NOT exclude the one mid-distribute, so Enter on it again grabs its next energy. Empty for
    // a non-reparent attach (AttachFromPile) or a non-reparent context.
    private HashSet<string> ReparentSources()
    {
        var set = new HashSet<string>();
        if (_attachSel is not ReparentCardMenu) return set;
        try
        {
            foreach (var id in _attachSel.AttachmentConstraints.GetCardIDsEligibleToDistributeAttachments(null))
                if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        catch (Exception ex) { Log.Error($"BattleScreen: ReparentSources threw: {ex.Message}"); }
        return set;
    }

    private bool IsReparentSource(Card3D? card) =>
        card != null && _attachSel is ReparentCardMenu && ReparentSources().Contains(card.entityID);

    // Enter on a source Pokémon during a move: lift a movable energy off it (the game's own distribute
    // click) and hold it, ready to drop on a target. Reuses AttachPickup, so the whole drop path is shared.
    private void DistributeFromReparentSource(Card3D source)
    {
        if (_attachSel == null || _manager == null || source == null) return;
        // Only click to distribute when this source isn't already the lifted one — the game swaps the
        // current distributor's override to Inspect, so clicking it again would open the card viewer.
        if (CurrentReparentSourceId() != source.entityID)
        {
            var listener = source.Interactions != null ? source.Interactions.OverrideClickListener : null;
            if (listener == null)   // no distribute override (a server round-trip is paused it, or it isn't a source)
            { _router.Speech.Say($"Can't move energy from {BattleBoardReader.CardName(source)} right now", interrupt: true); return; }
            try { listener.OnCardClicked(source, false); }   // SendCardToDistributorOnClick → lifts its energy into the temp hand
            catch (Exception ex) { Log.Error($"BattleScreen: reparent distribute threw: {ex.Message}"); }
            try { _manager.TargetingHandler.RefreshPlacementContext(); } catch { }
        }
        if (!TryPickUpLiftedEnergy(source))
        {
            // The lifted energy hasn't surfaced in the temp hand this frame — retry from Tick rather than
            // dropping the keypress on a blind player. Clears itself once it takes or the menu closes.
            _reparentPickupPending = source;
            _reparentPickupTicks = 0;
        }
    }

    // Pick up the energy the source just lifted into the temp hand. Returns false ONLY when nothing has
    // surfaced yet (empty temp hand) — the one retry-worthy case. If energy IS present but the pickup is
    // rejected, AttachPickup already announced it and we return true, so the retry never spams "Can't
    // attach". Shared by the immediate path and the Tick retry.
    private bool TryPickUpLiftedEnergy(Card3D source)
    {
        var energies = AttachEnergyCards();
        if (energies.Count == 0) return false;   // not surfaced yet → caller may retry
        AttachPickup(energies[0]);               // says "Holding <energy>" on success, "Can't attach <energy>" on reject
        if (_holding) _router.Speech.Say($"from {BattleBoardReader.CardName(source)}", interrupt: false);
        return true;                             // handled either way — do not retry
    }

    // Finish a move-energy pickup whose energy wasn't in the temp hand on the distribute frame. No re-click
    // (the source is already the distributing one), just re-check until the energy surfaces or we give up.
    private void TickReparentPickup()
    {
        if (_attachSel == null || !_attachSel.isOpen || _reparentPickupPending == null) { _reparentPickupPending = null; return; }
        try { _manager?.TargetingHandler.RefreshPlacementContext(); } catch { }
        var source = _reparentPickupPending;
        if (TryPickUpLiftedEnergy(source)) { _reparentPickupPending = null; return; }
        if (++_reparentPickupTicks >= 10)   // ~10 frames and still nothing — tell the player, don't hang
        {
            _reparentPickupPending = null;
            _router.Speech.Say($"No energy to move from {BattleBoardReader.CardName(source)}", interrupt: true);
        }
    }

    // The move-energy suffix appended to a board Pokémon: valid sources while choosing, valid receivers
    // while holding. Returns "" for anything that isn't a move target (so opponent/energy/pile read plain).
    private string ReparentTag(Card3D card)
    {
        if (_attachSel is not ReparentCardMenu || card == null) return "";
        if (_holding) return IsAttachReceiver(card) ? ", can receive energy" : "";
        return IsReparentSource(card) ? ", has energy to move" : "";
    }

    // The local "pending" slot owner — the receiver a no-target card drops onto. It is the single
    // valid receiver the game's own CardHasUsableActions purpose yields
    // (board.GetPlayer(LOCAL).pendingCard), and its RecordPlacement fires MatchUserOperations.UseCard.
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

    // The FTUE's spotlighted card exactly as the game reports it. Non-null does NOT mean the player
    // still has to go and act on it — use FtueFocusedCard for that. Gated on the game's per-match
    // FTUE switch (the installed permit delegate), NOT the global lesson flag — that flag stays true
    // for parked / always-running lessons right through ranked matches (see FTUEHook.MatchFtuePermit).
    private Card3D? RawFtueFocusedCard()
    {
        if (!FTUEHook.FtueGovernsMatch(_manager)) return null;
        var m = FTUEMatchManager.Instance;
        return m == null ? null : m.focusedCard;
    }

    // The spotlight, but only while it names something the player can actually walk to and act on.
    //
    // `focusedCard` outlives the action that satisfies it. The FTUE clears it through
    // Card3D.UnfocusCard → WaitToUnfocus, and that coroutine doesn't run until the card has settled
    // with its new owner. So between "your selection / play committed" and "the card finished moving",
    // focusedCard still names a card you already dealt with — spam Enter in that gap and we'd keep
    // repeating an objective you've met. (Observed: picking the energy out of Night Stretcher's
    // discard-pile selection.) Three game-owned conditions say the board isn't ready to be acted on:
    //   • a battle menu is open — the card lives inside it, and BattlePromptScreen owns it;
    //   • the match is still resolving — PerformingAnimations(), which MatchReadyHook makes true for
    //     an operation still in flight (MatchInfo.sendingMsg);
    //   • the card isn't idle with the owner its server position claims — Card3D's own test.
    private Card3D? FtueFocusedCard()
    {
        var card = RawFtueFocusedCard();
        if (card == null) return null;
        if (IsAnyBattleMenuOpen()) return null;
        if (MatchNetworkParser.PerformingAnimations()) return null;
        return IsSettledInOwner(card) ? card : null;
    }

    // Does the FTUE permit acting on this card right now? Calls the permit the game itself installed
    // on THIS match's targeting handler (FTUEMatchManager.Start → SecondaryIsCardTargetable =
    // DoesFTUEPermitCardTargetability): true in free play, true for the spotlighted card during a
    // forced step; false for a non-spotlighted card or a not-yet-armed step. No delegate installed
    // means the FTUE does not govern this match — never impose tutorial rules there. (The old gate
    // read the GLOBAL lesson flag + the always-present FTUEMatchManager and reflected the private
    // method, which locked a ranked player out of every action with "the tutorial isn't ready for
    // that yet" when a parked/always-running lesson kept the flag true — field report, 2026-07-23.)
    private bool FtuePermitsCard(Card3D card)
    {
        if (card == null) return true;
        var permit = FTUEHook.MatchFtuePermit(_manager);
        if (permit == null) return true;
        try { return permit(card); }
        catch (Exception ex) { Log.Error($"BattleScreen: FTUE permit threw: {ex.Message}"); return true; }
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
        return $"{counts} canPerform={_manager?.CanPerformNewActions} setup={_manager?.IsSetupPhase} mgrTurn={_manager?.IsMyTurn} sdkTurn={sdkTurn} ftueRun={FTUEHook.FtueIsRunning} ftueGoverns={FTUEHook.FtueGovernsMatch(_manager)}";
    }

    // On each new coaching step (the FTUE spotlights a different card), announce which card it wants
    // and where — WITHOUT moving the player's focus (jumping the cursor is disorienting). Fires only
    // when the spotlight changes; navigating between steps stays entirely up to the player.
    private void TutorialGuidanceTick()
    {
        // No spotlight at all → the step is genuinely over; forget it so the next one announces.
        if (RawFtueFocusedCard() == null) { _ftueCardId = ""; return; }
        // Spotlit, but the board isn't ready to be acted on (menu open / still resolving / card in
        // flight). Say nothing and remember nothing: if it's still wanted once things settle, we
        // announce it then — and if the player's action already satisfied it, focusedCard clears first
        // and we never speak a stale objective.
        var ftue = FtueFocusedCard();
        if (ftue == null) return;
        if (ftue.entityID == _ftueCardId) return;
        _ftueCardId = ftue.entityID;
        _router.Speech.Say(TutorialWantsText(ftue), interrupt: false);
    }

    private bool IsAnyBattleMenuOpen()
    {
        try { return _manager != null && _manager.MenuNavigation != null && _manager.MenuNavigation.IsAnyMenuOpen(); }
        catch { return false; }
    }

    // Unified "Tutorial wants <card>, in <zone>" phrasing — shared by the spotlight announce, the
    // wrong-card action guard, and the end-turn block so the player always hears the same objective.
    private string TutorialWantsText(Card3D ftue)
    {
        var zone = ZoneOfCard(ftue);
        string where = zone != null ? $", in {BattleBoardReader.ZoneName(zone.Value)}" : "";
        return $"Tutorial wants {BattleBoardReader.CardName(ftue)}{where}";
    }

    // Where the card IS, for a player who has to walk the board to reach it. `cardInfo.currentPos` is
    // SERVER state and lags reality twice over: while an operation is in flight the card still reports
    // the zone it left, and a card being shown inside a selection overlay (Night Stretcher's "pick a
    // Pokémon from your discard pile") reports DISCARD while its real owner is the overlay's
    // CardSelectionSource. Naming a zone in either case points the player at somewhere they can't act.
    //
    // The game already defines "settled where the server says it is" — Card3D.WaitUntilCardIsIdleInOwner
    // waits on exactly `owner == GetOwnerForBoardPos(cardInfo.currentPos)`. Borrow that test; when it
    // fails there is no board zone to name, so callers drop the clause rather than guess.
    private BattleBoardReader.Zone? ZoneOfCard(Card3D c)
    {
        var info = c?.cardInfo;
        if (info == null) return null;
        if (!IsSettledInOwner(c!)) return null;
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

    // The game's own idle-in-owner test (Card3D.WaitUntilCardIsIdleInOwner). ICardOwner is an
    // interface, so != / ReferenceEquals is plain reference comparison, same as the game does.
    private bool IsSettledInOwner(Card3D c)
    {
        var info = c.cardInfo;
        if (info == null || _manager == null) return false;
        try { return ReferenceEquals(c.owner, _manager.GetOwnerForBoardPos(info.currentPos)); }
        catch { return false; }
    }

    private static string? PhaseText(MatchManager.MatchState state) => state switch
    {
        MatchManager.MatchState.Setup => "Setup phase",
        MatchManager.MatchState.LocalTurn => "Your turn",
        MatchManager.MatchState.OpponentTurn => "Opponent's turn",
        _ => null,
    };
}
