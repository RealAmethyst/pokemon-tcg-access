using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using MatchLogic;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.Match.Cards;
using TPCI.Rainier.Match.Cards.Ownership;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Handles the game's mid-action DECISION PROMPTS (take a prize, coin call, yes/no, mulligan, …).
// Every local prompt funnels through SelectionOverlayOpener.RunSelectionRequest (which is
// intrinsically local-only — the opponent/bot uses a separate message-bar path), so we postfix it,
// read the question + options off the game's own overlay, let the player arrow through and choose,
// and submit through the overlay's OWN methods. Closing is detected by polling the overlay.
//
// This pushes over BattleScreen (pausing board nav) and pops when the prompt closes. Handled
// families: PRIZE, TEXT (coin/quiz/number/yes-no/mulligan), entity selections (hand/pile/attached/
// board-attached), BOARD, DragActive, Reparent (attach distribution), ReparentDamage and ACTION
// (choose an attack to use as this attack). A family with no builder still announces its question,
// but leaves the player with nothing to press — so any new one must be wired up here.
internal sealed class BattlePromptScreen : IScreenHandler
{
    private static BattlePromptScreen? _instance;

    private readonly ScreenRouter _router;
    private bool _active;
    // Set while we ActivateBackground under an FTUE dialogue: OnEnter must NOT announce yet (it would
    // interrupt the professor mid-sentence). The announce happens in OnResume when the dialogue closes.
    private bool _deferInitialAnnounce;

    // One navigable choice. Eid is the entity-selection card id (so the label can show ", selected"
    // by reading the game's own SelectionGroup.selectedOptions); null for prize / text options.
    // Card is the resolved Card3D for prize options (their selected-state lives in a private list,
    // not a SelectionGroup, so ", selected" is read by membership). IsDone marks the synthetic
    // "Done" entry that submits a multi-pick selection.
    private sealed class Option
    {
        public string Name = "";
        public Action Choose = () => { };
        public string? Eid;
        public Card3D? Card;
        public bool IsDone;
        // The Pokémon this card is attached to (energy-discard menus). Spoken as a SECTION NAME — only
        // when it differs from the previously-focused option — so consecutive energy on the same Pokémon
        // doesn't repeat it. Null for cards with no parent (prizes, hand/deck cards).
        public string? Parent;
        // For a COLLAPSED carousel/pile stack (e.g. 18 identical Basic Fire Energy shown as one pile):
        // the entityIDs in the pile, used for the live available/selected counts. Null = not a stack.
        public List<string>? StackEids;
    }

    private ISelectionRequestHandler? _handler;   // the open overlay (also a BattleMenu)
    private string _question = "";
    private readonly List<Option> _options = new();
    private string? _lastSpokenParent;  // last announced section (parent Pokémon) — see SpeakOption
    private int _idx;
    private int _line = -1;        // detail-line cursor on the focused card option (-1 = on the header/name)
    private List<string>? _lines;  // cached detail lines for the focused option; null until first Up/Down resolves it
    private bool _submitted;
    private int _marked;
    private int _target;
    private SelectionGroup? _entityGroup;
    private EntitySelection? _entitySel;
    private EntitySelectionOverlay? _entityOverlay;
    private BoardAttachedCardSelectionMenu? _bacsMenu;   // multi-parent attached select (distribute-then-select)
    private bool _entityClickMenu;   // board-attached / attached: select the specific board card by entityID
    private int _entityMin;
    private int _entityMax;
    // Board-attached deferred select: after we distribute an energy's parent Pokémon (a native click on
    // it), the energy only becomes selectable once it settles into the distribution carousel — so we
    // finish the select over the next ticks. eid of the energy we're waiting to select, "" when idle.
    private string _pendingSelectEid = "";
    // A press that arrived before the card it targets had settled. Re-armed each frame from Tick until
    // the game says the card is clickable — see IsSettledForClick. Never dropped: the player pressed Enter.
    private Action? _pendingChoose;
    private string _pendingSelectParent = "";   // the energy's parent Pokémon we must "distribute" (open) to reach it
    private int _pendingSelectTicks;
    private PrizeCardSelectionMenu? _prizeMenu;
    private int _prizeMin;
    private int _prizeMax;
    private BoardSelectionMenu? _boardMenu;
    private SelectionGroup? _boardGroup;
    private ActivePromotionDragMenu? _dragMenu;
    private SelectionGroup? _dragGroup;
    private AttachmentDistributionSelectionMenu? _attachMenu;
    private ReparentSelection? _reparentSel;
    private string? _attachEnergyName;   // attach menu phase 2: which energy type the player chose to place
    private ReparentDamageMenu? _damageMenu;
    private DamageSelection? _damageSel;
    // The MENU'S OWN constraint evaluator, read by reflection — never constructed. Its constructor runs
    // InitializeFinalDamageToCurrent, which resets newDamageCounters = currentDamageCounters on the SHARED
    // DamageSelectionFinal objects — erasing every drop already recorded on the payload the game submits.
    // Constructing "read-only copies" per drop is exactly how Phantom Dive / Adrena-Brain lost counters:
    // visuals showed every drop, but SelectInput sent only the last one.
    private ReparentDamageConstraintEvaluator? _damageEval;
    private string? _damageSource;    // MOVE: the source Pokémon currently distributing (null = pick-source phase, or put-new-damage)
    private bool _damageSourceStarted; // have we lifted the chosen source's counters into the tray yet (first-drop trigger)
    private string _damageTargetEid = "";
    private bool _damageDropLanded;   // ReactToSuccessfulPlacement ran for the armed drop (discriminates silent refusal)
    private bool _damageDropInFlight; // an armed drop whose placement loop hasn't reported back yet
    private Action? _damageRetry;     // a drop held until the game is ready — completed by the coach-focus /
                                      // unblock / server-confirm EVENTS below, never by frame counting
    private bool _damagePlacementEndedHooked;   // OnPlacementEvaluationEnded subscribed for this prompt
    private readonly KbPlacementInitiator _damageInit = new();   // explicit-commit placement for a damage counter → target
    private readonly KbPlacementInitiator _attachInit = new();   // explicit-commit placement for attach-to-chosen-target
    private bool _attachHolding;
    private int _attachTicks;
    private ICardPlacementReceiver? _attachReceiver;
    private Card3D? _attachEnergy;
    private MatchManager? _mgr;

    public string Name => "BattlePrompt";

    public BattlePromptScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new BattlePromptScreen(router);

        var target = AccessTools.Method(typeof(SelectionOverlayOpener), nameof(SelectionOverlayOpener.RunSelectionRequest));
        if (target == null)
        {
            Log.Error("BattlePromptScreen: SelectionOverlayOpener.RunSelectionRequest not found");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(BattlePromptScreen), nameof(RunSelectionRequest_Postfix))));

        // The game's own "a damage counter just landed" callback — announce and rebuild HERE, the same
        // moment the game itself refreshes its click overrides. No flag, no next-frame deferral.
        var reactDrop = AccessTools.Method(typeof(ReparentDamageMenu), nameof(ReparentDamageMenu.ReactToSuccessfulPlacement));
        if (reactDrop != null)
            harmony.Patch(reactDrop, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattlePromptScreen), nameof(ReactToSuccessfulPlacement_Postfix))));
        else Log.Error("BattlePromptScreen: ReparentDamageMenu.ReactToSuccessfulPlacement not found");

        // Tutorial drag coach (FTUEDragDamage): DragDamageBlockCards is the exact moment the coach focuses
        // the tray counter it wants dropped — a drop the player already asked for completes right here.
        var dragBlock = AccessTools.Method(typeof(FTUEMatchManager), nameof(FTUEMatchManager.DragDamageBlockCards));
        if (dragBlock != null)
            harmony.Patch(dragBlock, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattlePromptScreen), nameof(DragDamageBlockCards_Postfix))));
        else Log.Warn("BattlePromptScreen: FTUEMatchManager.DragDamageBlockCards not found — tutorial damage drops rely on the unblock hook");

        // The focused counter becomes grabbable when its FocusCard coroutine clears blockDrag —
        // FocusBlockDrag(false) is that exact moment. Completes a drop held on targetability.
        var focusBlock = AccessTools.Method(typeof(Card3D), nameof(Card3D.FocusBlockDrag));
        if (focusBlock != null)
            harmony.Patch(focusBlock, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattlePromptScreen), nameof(FocusBlockDrag_Postfix))));
        else Log.Warn("BattlePromptScreen: Card3D.FocusBlockDrag not found — tutorial damage drops rely on the coach hook");

        // Repeatable / unlimited damage effects: after each drop the menu locks itself and waits for the
        // server echo (isWaitingForUpdate); UnlimitedConfirm is the game re-opening for the next counter.
        var unlimited = AccessTools.Method(typeof(ReparentDamageMenu), "UnlimitedConfirm");
        if (unlimited != null)
            harmony.Patch(unlimited, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BattlePromptScreen), nameof(UnlimitedConfirm_Postfix))));
        else Log.Warn("BattlePromptScreen: ReparentDamageMenu.UnlimitedConfirm not found — repeatable damage effects won't re-announce");

        Log.Info("BattlePromptScreen installed");
    }

    // Runs inside the game's placement callback (main thread) the instant a counter is applied. Everything
    // is try-wrapped — a throw here would unwind into the match's own stack (feedback_exceptions_unwind_into_game).
    private static void ReactToSuccessfulPlacement_Postfix(ReparentDamageMenu __instance, Card3D placedDamage, ICardPlacementReceiver receiver)
    {
        var s = _instance;
        if (s == null || !ReferenceEquals(__instance, s._damageMenu)) return;
        s._damageDropLanded = true;
        s._damageDropInFlight = false;
        try { s.AfterDamageDropApplied(receiver); }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage drop follow-up threw: {ex.Message}"); }
    }

    private static void DragDamageBlockCards_Postfix()
    {
        var s = _instance;
        if (s == null || s._damageMenu == null) return;
        s.CompleteDamageRetry();   // the coach just focused the counter it wants dropped
    }

    private static void FocusBlockDrag_Postfix(Card3D __instance, bool state)
    {
        var s = _instance;
        if (s == null || state || s._damageMenu == null || s._damageRetry == null) return;
        bool isTrayCounter;
        try { isTrayCounter = s.TrayCounters().Contains(__instance); } catch { return; }
        if (isTrayCounter) s.CompleteDamageRetry();   // the game just freed the counter for grabbing
    }

    private static void UnlimitedConfirm_Postfix(ReparentDamageMenu __instance)
    {
        var s = _instance;
        if (s == null || !ReferenceEquals(__instance, s._damageMenu)) return;
        try
        {
            if (!s.IsPromptOpen()) return;
            s.RebuildDamageOptions();
            s._idx = 0; s._line = -1; s._lines = null;
            s._router.Speech.Say("Ready for the next counter", interrupt: true);
            if (s._options.Count > 0) s.SpeakOption(s._idx, interrupt: false);
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: unlimited-confirm follow-up threw: {ex.Message}"); }
    }

    private static void RunSelectionRequest_Postfix(PlayerSelection selectionRequest, ISelectionRequestHandler __result)
    {
        if (_instance == null || selectionRequest == null) return;
        try { _instance.OnPromptOpened(selectionRequest, __result); }
        catch (Exception ex) { Log.Error($"BattlePromptScreen.OnPromptOpened threw: {ex.Message}"); }
    }

    private void OnPromptOpened(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        // Attach-distribution ("attach energy onto a Pokémon") is a placement, not a list — so no mod
        // overlay. Hand it to the field handler: the game lifts the energies into a temporary hand, and
        // BattleScreen points C (your hand) at THAT temp hand, so you pick the energy, press B, and drop
        // it on the Pokémon — the same native board drag as attaching on your turn. Tab = Done.
        if (handler is AttachmentDistributionSelectionMenu attachMenu)
        {
            string q = "";
            try { q = attachMenu.DynamicStringProvider.UpdateSelectionLocStrings(sel.variableSelection.GetLocId(), sel.variableSelection.locParams) ?? ""; }
            catch { }
            BattleScreen.BeginAttachSelection(attachMenu, q);
            return;
        }

        BuildPrompt(sel, handler);

        // A recognized card selection that opened with NO valid options — e.g. a deck search (Nest
        // Ball) when nothing in the deck matches, or "choose a card to discard" with an empty pile.
        // The game still opens the overlay with a "no valid cards" notice + a Done button; speak that
        // message and offer a Done that submits the empty selection (exactly what clicking Done does).
        // Without this the player heard "isn't supported yet" and had no way to dismiss the overlay.
        if (_options.Count == 0 && IsEmptyCardSelection(sel, handler))
        {
            var msg = NoValidCardsMessage();
            _question = string.IsNullOrWhiteSpace(_question) ? msg : $"{_question}. {msg}";
            _options.Add(new Option { Name = "Done", IsDone = true, Choose = () => DismissEmptySelection(handler) });
        }

        if (_options.Count == 0)
        {
            // A selection family we don't drive yet. Announce the question so the player knows a
            // choice is pending — BattleScreen stays on top, but its actions are gated on the live
            // selection state (BattleScreen._localSelecting), so this can't soft-lock the match.
            // The diagnostics logged in BuildPrompt capture exactly what to wire up next.
            string q = string.IsNullOrWhiteSpace(_question) ? "A choice is required" : _question;
            _router.Speech.Say($"{q}. This selection isn't supported yet.", interrupt: false);
            return;
        }

        if (!_active)
        {
            _active = true;
            // During the tutorial a professor DialogueScreen can still be mid-sentence when this
            // selection opens (e.g. Hilda's "put an Evolution Pokémon and an Energy into your hand"
            // step). The FTUE parks on that dialogue and only ARMS the selection — makes the cards
            // targetable, via focusedCard or free play — once the player advances it. Stealing the top
            // of the stack buries the dialogue so it can never be advanced, and the FTUE's
            // DoesFTUEPermitCardTargetability then blocks every card (observed 2026-07-11: acted=False
            // on every pick, and the dialogue was never resumed). So slot in BELOW the dialogue: it
            // stays the focused, tickable thing the player can advance, and we become the top (and
            // announce) when it closes. Mirrors BattleScreen.StartGame_Postfix's same-shape fix.
            if (_router.Active is DialogueScreen)
            {
                _deferInitialAnnounce = true;
                try { _router.ActivateBackground(this); }
                finally { _deferInitialAnnounce = false; }   // never leave the flag stale-true for a later OnEnter
            }
            else _router.Activate(this);
        }
        else Announce();   // a new prompt replaced the previous one without closing the screen
    }

    private void BuildPrompt(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        _handler = handler;
        _options.Clear();
        _lastSpokenParent = null;
        _idx = 0;
        _line = -1;
        _lines = null;
        _submitted = false;
        _marked = 0;
        _target = 0;
        _entityGroup = null;
        _entitySel = null;
        _entityOverlay = null;
        _bacsMenu = null;
        _entityClickMenu = false;
        _entityMin = 0;
        _entityMax = 0;
        _pendingSelectEid = "";
        _pendingSelectParent = "";
        _pendingSelectTicks = 0;
        _pendingChoose = null;
        _prizeMenu = null;
        _prizeMin = 0;
        _prizeMax = 0;
        _boardMenu = null;
        _boardGroup = null;
        _dragMenu = null;
        _dragGroup = null;
        _attachMenu = null;
        _reparentSel = null;
        UnhookDamagePlacementEnded();
        _damageMenu = null;
        _damageSel = null;
        _damageEval = null;
        _damageSource = null;
        _damageSourceStarted = false;
        _damageDropLanded = false;
        _damageDropInFlight = false;
        _damageRetry = null;
        _damageTargetEid = "";
        _damageInit.Reset();
        _attachHolding = false;
        _attachReceiver = null;
        _attachEnergy = null;
        _attachInit.Reset();
        _mgr = MatchManager.GetInstanceFromScene();
        _question = "";

        // The localized prompt text the overlay itself shows (works for every BattleMenu family).
        if (handler is BattleMenu menu)
        {
            try { _question = menu.DynamicStringProvider.UpdateSelectionLocStrings(sel.variableSelection.GetLocId(), sel.variableSelection.locParams) ?? ""; }
            catch { _question = ""; }
        }

        if (handler is PrizeCardSelectionMenu prize) BuildPrizeOptions(prize);
        else if (handler is TextSelectionOverlay text) BuildTextOptions(text);
        else if (handler is EntitySelectionOverlay entity && sel.variableSelection is EntitySelection esel) BuildEntityOptions(entity, esel);
        else if (handler is BoardSelectionMenu board && sel.variableSelection is EntitySelection besel) BuildBoardOptions(board, besel);
        else if (handler is ActivePromotionDragMenu drag && sel.variableSelection is EntitySelection dsel) BuildActiveDragOptions(drag, dsel);
        else if (handler is AttachmentDistributionSelectionMenu attach && sel.variableSelection is ReparentSelection rsel) BuildAttachOptions(attach, rsel);
        else if (handler is ReparentDamageMenu dmg && sel.variableSelection is DamageSelection dmgSel) BuildDamageOptions(dmg, dmgSel);
        else if (handler is ActionSelectionOverlay act && sel.variableSelection is ActionSelection actSel) BuildActionOptions(act, actSel, sel);

        LogPromptDiagnostics(sel, handler);
    }

    // Record what every selection request looked like and whether we built options. Families we don't
    // drive yet build 0 options (BattleScreen stays on top — gated so it can't soft-lock); this line
    // tells us the exact overlay + selection method + group shape to wire up next (e.g. the Ultra Ball
    // deck-search). Cheap, fires once per prompt.
    private void LogPromptDiagnostics(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        try
        {
            var vs = sel.variableSelection;
            string groups = "n/a";
            string extra = "";
            if (vs is EntitySelection es && es.selectionGroups != null)
            {
                groups = "[" + string.Join(", ", es.selectionGroups.Select(g => $"{g?.validOptions?.Count ?? -1} valid / max {g?.maxAmount} / min {g?.minAmount}")) + "]";
                // Log totals + each option's card name and the PARENT Pokémon it's attached to — this is
                // what tells us a discard like "choose any Fire Energy" spans several Pokémon (so the
                // options must be labelled by parent), not just the active.
                var opts = es.selectionGroups.Where(g => g != null).SelectMany(g => g.validOptions)
                    .Select(o => $"{ResolveName(o.cardAddress.entityID)}@{(string.IsNullOrEmpty(o.parentEntityID) ? "-" : ResolveName(o.parentEntityID))}");
                extra = $" total={es.totalMinAmount}/{es.totalMaxAmount} opts=[{string.Join(", ", opts)}]";
            }
            else if (vs is ReparentSelection rs)
                groups = $"reparentOptions={rs.reparentOptions?.Count ?? -1} min/max={rs.minReparentedOptions}/{rs.maxReparentedOptions} unlimited={rs.unlimitedReparent}";
            else if (vs is DamageSelection dsg)
                groups = $"to={dsg.toDamageSelections?.Count ?? -1} from={dsg.fromDamageSelections?.Count ?? -1} movable={dsg.minMovableDamageTotal}/{dsg.maxMovableDamageTotal} perTarget={dsg.minRecvDamageSingle}/{dsg.maxRecvDamageSingle} targets={dsg.minTargetSelectionCount}/{dsg.maxTargetSelectionCount}";
            else if (vs is ActionSelection asg)
            {
                var acts = asg.options ?? new List<ActionOption>();
                groups = $"actions={acts.Count} energyFill={asg.energyFillDisplayMode} opts=[{string.Join(", ", acts.Select(o => $"{o.actionSourceID}@{ResolveName(o.parentEntityID)}{(o.isSelectable ? "" : " (not selectable)")}"))}]";
            }
            Log.Info($"BattlePrompt: handler={handler?.GetType().Name ?? "null"} method={vs?.selectionMethod} selType={vs?.GetType().Name} groups={groups}{extra} built={_options.Count} q='{_question}'");
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: diagnostics threw: {ex.Message}"); }
    }

    // ---- no valid targets ----

    // A recognized card-choosing overlay that has no selectable options at all (every group's
    // validOptions is empty) — the game opens it anyway with a "no valid cards" notice + Done.
    private static bool IsEmptyCardSelection(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        if (handler is not EntitySelectionOverlay) return false;
        if (sel.variableSelection is not EntitySelection es || es.selectionGroups == null) return false;
        return es.selectionGroups.All(g => g?.validOptions == null || g.validOptions.Count == 0);
    }

    // The game's own "no valid cards" message. We use the plain key, not the "...press done" variant —
    // the Done is presented as a real navigable option, not spoken as a control hint.
    private string NoValidCardsMessage()
    {
        var s = LocalizationHook.Get("battle_select_no_valid_cards");
        return string.IsNullOrWhiteSpace(s) ? "There are no valid cards to choose" : s!;
    }

    // Dismiss an empty card selection by submitting it with nothing chosen — the overlay's own
    // SubmitClicked, the same call its Done button makes (minAmount is 0 for these, so it completes).
    private void DismissEmptySelection(ISelectionRequestHandler? handler)
    {
        _submitted = true;
        try { (handler as EntitySelectionOverlay)?.SubmitClicked(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: dismiss empty selection threw: {ex.Message}"); }
    }

    // ---- PRIZE ----

    private void BuildPrizeOptions(PrizeCardSelectionMenu prize)
    {
        var group = prize.OnlyGroup;
        if (group == null) return;
        _prizeMenu = prize;
        var owner = ReadPrizeOwner(prize);
        var opts = group.validOptions;
        int n = opts.Count;
        _prizeMax = group.maxAmount < 1 ? 1 : group.maxAmount;
        // Mirror the menu's own _minPrizesToSelect: minAmount unless it's -1 (= "exactly max").
        _prizeMin = group.minAmount != -1 ? group.minAmount : _prizeMax;
        _target = _prizeMax;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var card = owner?.GetPrivateCardAtIndex(opts[idx].cardAddress.index);
            _options.Add(new Option { Name = "Prize card", Card = card, Choose = () => MarkPrize(prize, idx) });
        }
        // A multi / variable take gets an explicit Done; single forced takes auto-submit on the pick.
        if (_prizeMax > 1) _options.Add(new Option { Name = "Done", IsDone = true, Choose = () => SubmitPrize(prize) });
        if (string.IsNullOrWhiteSpace(_question))
            _question = _prizeMax > 1 ? $"Take {_prizeMax} prize cards" : "Take a prize card";
    }

    // Toggle a prize via the card's OWN click override — the exact sighted click. The menu wires each
    // valid prize with a Select override (TryMarkCardSelected); once a card is selected its override
    // swaps to Deselect (MarkCardDeselected), so a second press DESELECTS instead of re-adding. That
    // fixes the freeze where calling TryMarkCardSelected directly added the same card to _selectedPrizes
    // twice (duplicate → bad submit / soft-lock). The count is read from the menu's own _selectedPrizes.
    private void MarkPrize(PrizeCardSelectionMenu prize, int optionIndex)
    {
        var group = prize.OnlyGroup;
        if (group == null || optionIndex < 0 || optionIndex >= group.validOptions.Count) return;

        var owner = ReadPrizeOwner(prize);
        var card = owner?.GetPrivateCardAtIndex(group.validOptions[optionIndex].cardAddress.index);
        if (card == null) { _router.Speech.Say("Couldn't take that prize", interrupt: true); return; }

        int prev = ReadSelectedPrizes(prize).Count;
        var listener = card.Interactions != null ? card.Interactions.OverrideClickListener : null;
        if (listener != null)
        {
            try { listener.OnCardClicked(card, false); }   // routes to Select or Deselect per current state
            catch (Exception ex) { Log.Error($"BattlePrompt prize click threw: {ex.Message}"); }
        }
        else
        {
            // Fallback: the private select directly (only adds when valid + below max).
            try
            {
                typeof(PrizeCardSelectionMenu).GetMethod("TryMarkCardSelected", BindingFlags.Instance | BindingFlags.NonPublic)?
                    .Invoke(prize, new object[] { card, false });
            }
            catch (Exception ex) { Log.Error($"BattlePrompt prize mark threw: {ex.Message}"); }
        }

        int now = ReadSelectedPrizes(prize).Count;
        _marked = now;

        if (now > prev)
        {
            // A forced exact take (min == max) auto-submits at the count — keeps "take 1" / "take 2"
            // frictionless. A variable take waits for the Done option.
            if (_prizeMin == _prizeMax && now >= _prizeMax) { _submitted = true; prize.SubmitSelection(); return; }
            _router.Speech.Say($"Prize selected, {now} of {_prizeMax}" + (now < _prizeMax ? $"; choose {_prizeMax - now} more" : ""), interrupt: true);
        }
        else if (now < prev)
        {
            _router.Speech.Say($"Prize deselected, {now} selected", interrupt: true);
        }
        else
        {
            _router.Speech.Say(now >= _prizeMax ? "Already taken the maximum" : "Couldn't take that prize", interrupt: true);
        }
    }

    // Submit a multi / variable prize take via the menu's own SubmitSelection, once the count is in range.
    private void SubmitPrize(PrizeCardSelectionMenu prize)
    {
        int now = ReadSelectedPrizes(prize).Count;
        if (now < _prizeMin) { _router.Speech.Say($"Choose at least {_prizeMin}; you have {now}", interrupt: true); return; }
        if (now > _prizeMax) { _router.Speech.Say("Too many selected", interrupt: true); return; }
        _submitted = true;
        prize.SubmitSelection();
    }

    private static FieldInfo? _prizeOwnerField;
    private static PrizeController? ReadPrizeOwner(PrizeCardSelectionMenu prize)
    {
        try
        {
            _prizeOwnerField ??= typeof(PrizeCardSelectionMenu).GetField("_currentPrizeOwner", BindingFlags.Instance | BindingFlags.NonPublic);
            return _prizeOwnerField?.GetValue(prize) as PrizeController;
        }
        catch { return null; }
    }

    private static readonly List<Card3D> _noPrizes = new();
    private static FieldInfo? _selectedPrizesField;
    private static List<Card3D> ReadSelectedPrizes(PrizeCardSelectionMenu prize)
    {
        try
        {
            _selectedPrizesField ??= typeof(PrizeCardSelectionMenu).GetField("_selectedPrizes", BindingFlags.Instance | BindingFlags.NonPublic);
            return (_selectedPrizesField?.GetValue(prize) as List<Card3D>) ?? _noPrizes;
        }
        catch { return _noPrizes; }
    }

    // ---- HAND / PILE / entity selection (discard N, search, choose card(s) from hand or a pile) ----

    private void BuildEntityOptions(EntitySelectionOverlay overlay, EntitySelection esel)
    {
        if (esel.selectionGroups.Count == 0) return;
        _entityOverlay = overlay;
        _entitySel = esel;
        _entityGroup = esel.selectionGroups[0];   // kept for the data-fallback path
        // Board-attached / attached selections (e.g. "discard Fire Energy off your Pokémon") move the
        // SPECIFIC public board card; resolve those by entityID so re-selecting toggles that exact card.
        // Carousel/hand overlays (deck search, discard-from-hand) keep the source-owner / FTUE-focused
        // resolution. (See MarkEntity.)
        _bacsMenu = overlay as BoardAttachedCardSelectionMenu;   // the multi-parent kind needs distribute-first
        _entityClickMenu = _bacsMenu != null || overlay is AttachedCardSelectionMenu;
        // Totals across ALL groups — multi-group prompts ("1 Pokémon OR 1 Basic Energy", Night Stretcher)
        // have one group per kind; the chosen card lands in whichever group accepts it.
        _entityMin = esel.totalMinAmount < 0 ? 0 : esel.totalMinAmount;
        _entityMax = esel.totalMaxAmount < 1 ? 1 : esel.totalMaxAmount;
        _target = _entityMax;
        // Build the card options to match what a sighted player sees:
        if (_entityClickMenu && _bacsMenu == null)
        {
            // Plain AttachedCardSelectionMenu ("Discard any Fire Energy from among your Pokémon", e.g.
            // Mega Charizard X's Inferno X). The energy is lifted into a temp HAND (ReparentHandController)
            // that STACKS identical cards PER POKÉMON — only each stack's FRONT is targetable. So collapse
            // the energy into one option per (name, parent Pokémon) — the "each Pokémon gets its own stack"
            // model — and peel the front on each press (MarkAttachedHandStack). The old per-card path
            // (GetCard(eid)+TryTargetCard on every buried energy) failed for all but the one card that
            // happened to be the front (3 of 4 acted=False), so only one energy could ever be discarded.
            var stacks = new Dictionary<(string name, string parent), List<string>>();
            var order = new List<(string name, string parent)>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    string eid = opt.cardAddress.entityID;
                    string parentEid = opt.parentEntityID ?? "";
                    string name = ResolveName(eid);
                    var key = (name, parentEid);
                    if (!stacks.TryGetValue(key, out var eids)) { eids = new List<string>(); stacks[key] = eids; order.Add(key); }
                    eids.Add(eid);
                }
            }
            // Order by parent so each Pokémon's energy reads consecutively (parent = spoken section name).
            foreach (var key in order.OrderBy(k => k.parent, StringComparer.Ordinal))
            {
                var (name, parentEid) = key;
                string? parentName = !string.IsNullOrEmpty(parentEid) ? ResolveParentName(parentEid) : null;
                string cn = name; string cp = parentEid; var eids = stacks[key];
                _options.Add(new Option { Name = name, Parent = parentName, Eid = eids[0], StackEids = eids, Choose = () => MarkAttachedHandStack(cn, cp, eids) });
            }
        }
        else if (_entityClickMenu)
        {
            // BoardAttachedCardSelectionMenu (distribute one Pokémon at a time): ONE option per energy,
            // tagged with its PARENT Pokémon (spoken as a section name, see SpeakOption) and grouped by
            // parent. Selection defers until the parent is the open one (MarkEntity → TickPendingEnergySelect).
            var cardOpts = new List<(string sortKey, Option opt)>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    var addr = opt.cardAddress;             // copy per-iteration so the lambda captures the right one
                    string parentEid = opt.parentEntityID;  // the Pokémon this card is attached to (empty off-board)
                    string cardName = ResolveName(addr.entityID);
                    string? parentName = !string.IsNullOrEmpty(parentEid) ? ResolveParentName(parentEid) : null;
                    cardOpts.Add((parentEid ?? "", new Option { Name = cardName, Parent = parentName, Eid = addr.entityID, Choose = () => MarkEntity(addr, cardName, parentEid) }));
                }
            }
            cardOpts = cardOpts.OrderBy(c => c.sortKey, StringComparer.Ordinal).ToList();
            foreach (var c in cardOpts) _options.Add(c.opt);
        }
        else
        {
            // Carousel / pile / hand (deck search, discard-from-hand): the overlay STACKS identical cards
            // into one pile with a count badge (e.g. 18 Basic Fire Energy). Mirror that — COLLAPSE
            // identical cards (same displayed name) into ONE option carrying the pile's entityIDs
            // (StackEids, for the live count). Selecting it peels the current targetable stack top
            // (MarkStackOption), so a big pile reads as one entry and there's no stale-id problem as it
            // shrinks. First-seen order is preserved.
            var stacks = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    string eid = opt.cardAddress.entityID;
                    string name = ResolveName(eid);
                    if (!stacks.TryGetValue(name, out var eids)) { eids = new List<string>(); stacks[name] = eids; order.Add(name); }
                    eids.Add(eid);
                }
            }
            foreach (var name in order)
            {
                string capturedName = name;
                _options.Add(new Option { Name = name, Eid = stacks[name][0], StackEids = stacks[name], Choose = () => MarkStackOption(capturedName) });
            }
        }
        // Multi-pick ("choose any / up to N") needs an explicit Done so the player can stop short of the
        // max and submit the few they chose. Single-pick still auto-submits on the pick (no Done shown).
        if (_entityMax > 1) _options.Add(new Option { Name = "Done", IsDone = true, Choose = SubmitEntity });
        if (string.IsNullOrWhiteSpace(_question))
            _question = _entityMax > 1
                ? (_entityMin == _entityMax ? $"Choose {_entityMax} cards" : $"Choose up to {_entityMax} cards")
                : "Choose a card";
    }

    // The attached-to Pokémon's name for a section header, or null if it can't be resolved to a real
    // name (ResolveName returns "card" as its fallback — not useful as a section).
    private static string? ResolveParentName(string parentEntityID)
    {
        string parent = ResolveName(parentEntityID);
        return string.IsNullOrEmpty(parent) || parent == "card" ? null : parent;
    }

    // Select a card the FULLY NATIVE way — drive the exact card movement a mouse click makes, NEVER a
    // logical poke. TryTargetCard → AddCardToSelection MOVES the card → Card3D.SetOwnership → UnfocusCard
    // → FTUEMatchManager.UnfocusCard clears focusedCard, which is what lets an FTUE "select N" step
    // advance (its coroutine loops `while (focusedCard != null)`, FTUECardIndicator). The old
    // AddToSelectedOptionsByEntityID data-poke never moved the card, so focusedCard never cleared → the
    // tutorial froze (the Dawn "select 2 Basic Pokémon" freeze testers hit). So there is NO data-poke
    // fallback: if the native select can't run we say so and leave the selection untouched (recoverable),
    // never a half-selection that desyncs.
    private void MarkEntity(CardAddress addr, string label, string? parentEid)
    {
        if (_entityOverlay == null || _mgr == null) return;
        var th = _mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        bool wasSelected = IsSelectedEid(addr.entityID);

        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        // Board/attached selects act on the SPECIFIC public card by entityID (registry) so re-selecting
        // toggles that exact card; carousel/hand use the source-owner held card, or the FTUE-forced
        // focused card during a guided step.
        Card3D? card = _entityClickMenu
            ? _mgr.GetCard(addr.entityID)
            : ResolveSelectionCard(addr, FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null);

        if (card == null) { _router.Speech.Say("Couldn't select that card", interrupt: true); Log.Info($"BattlePrompt entity: card null eid={addr.entityID}"); return; }

        // A selection carousel STACKS identical cards (e.g. 18 Basic Fire Energy shown as one pile); only
        // the stack's TOP card is targetable, so map a buried duplicate to its top — exactly what the
        // game's own SelectionCardOwner.OnCardClicked does (GetFrontCardOfStackThatHoldsCard) before it
        // targets. Without this, TryTargetCard on the option's own (buried) instance just returns false.
        card = ResolveTargetableCard(card);

        bool acted = false;
        try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
        catch (Exception ex) { Log.Error($"BattlePrompt: TryTargetCard threw: {ex.Message}"); }

        Log.Info($"BattlePrompt entity eid={addr.entityID} clickMenu={_entityClickMenu} acted={acted} wasSelected={wasSelected} owner={card.owner?.GetType().Name ?? "null"}" + (acted ? "" : $" [{TargetDebug(card)}]"));

        // Board-attached, SELECTING an energy whose parent Pokémon isn't "open" yet: the energy isn't
        // targetable until its parent is the one "distributing". The game opens ONE Pokémon at a time and
        // BLOCKS clicking another while one is open — you must return to the mini-board first. So defer to
        // TickPendingEnergySelect, which drives the whole sequence (close the wrong Pokémon → open this
        // one → select). Only on a fresh select, never a deselect (a selected energy is already targetable).
        if (!acted && !wasSelected && _bacsMenu != null && !string.IsNullOrEmpty(parentEid))
        {
            _pendingSelectEid = addr.entityID;
            _pendingSelectParent = parentEid ?? "";
            _pendingSelectTicks = 0;
            Log.Info($"BattlePrompt entity: deferring select of {addr.entityID} on parent {parentEid}");
            return;   // TickPendingEnergySelect opens the parent + selects + announces
        }

        AnnounceSelectionResult(prev, label, acted);
    }

    // Announce a select / deselect / failure from the game's OWN selection count, and auto-submit a
    // single-pick prompt. Shared by the immediate path and the deferred board-attached path.
    private void AnnounceSelectionResult(int prev, string nm, bool acted)
    {
        int now = TotalSelected();
        _marked = now;
        if (now > prev)
        {
            // Single-pick prompts (search a card, choose 1 Pokémon) submit immediately; multi-pick waits
            // for the Done option so the player controls how many.
            if (_entityMax == 1) { _submitted = true; _entityOverlay?.SubmitClicked(); return; }
            _router.Speech.Say($"Selected {nm}, {now} of {_entityMax} chosen", interrupt: true);
        }
        else if (now < prev)
        {
            _router.Speech.Say($"Deselected {nm}, {now} chosen", interrupt: true);
        }
        else
        {
            _router.Speech.Say(acted ? "Couldn't change that selection" : "Couldn't select that card", interrupt: true);
        }
    }

    // Finish a board-attached select after its parent began distributing: the energy becomes targetable
    // once the carousel settles, so retry the native select each tick until it takes, then announce.
    // Times out (no freeze) if it never becomes selectable.
    private void TickPendingEnergySelect()
    {
        if (_mgr == null || string.IsNullOrEmpty(_pendingSelectEid)) { _pendingSelectEid = ""; _pendingSelectParent = ""; return; }
        var th = _mgr.TargetingHandler;
        try { th?.RefreshPlacementContext(); } catch { }

        // The game opens ONE Pokémon's attached energies at a time and blocks switching while one is open.
        // So make sure the energy's PARENT is the open one before selecting: a DIFFERENT Pokémon open →
        // return to the mini-board to close it; NONE open → open this parent (its distribute click); the
        // RIGHT one open → fall through and select. Act every ~8 frames so each close/open animation
        // settles before we re-issue.
        if (_bacsMenu != null && !string.IsNullOrEmpty(_pendingSelectParent))
        {
            string? curr = null;
            try { curr = _bacsMenu.GetAttachmentDistributor()?.CurrentDistributingEndpoint?.EndpointID; } catch { }
            if (curr != _pendingSelectParent)
            {
                if (_pendingSelectTicks % 8 == 0)
                {
                    if (!string.IsNullOrEmpty(curr))
                    {
                        try { _bacsMenu.ReturnCardsInSelectionModeToTinyBoard(); }
                        catch (Exception ex) { Log.Error($"BattlePrompt: return-to-board threw: {ex.Message}"); }
                    }
                    else
                    {
                        var parent = _mgr.GetCard(_pendingSelectParent);
                        if (parent != null) NativeClick(parent);
                    }
                }
                if (++_pendingSelectTicks >= 120) FailPendingSelect();
                return;
            }
        }

        int prev = TotalSelected();
        var card = _mgr.GetCard(_pendingSelectEid);
        if (card != null) card = ResolveTargetableCard(card);   // once open, the energy is a stacked carousel card
        bool acted = false;
        if (card != null && th != null)
        {
            try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt: deferred TryTargetCard threw: {ex.Message}"); }
        }
        if (acted)
        {
            string nm = _options.FirstOrDefault(o => o.Eid == _pendingSelectEid)?.Name ?? "card";
            _pendingSelectEid = ""; _pendingSelectParent = "";
            AnnounceSelectionResult(prev, nm, acted: true);
            return;
        }
        if (++_pendingSelectTicks >= 120) FailPendingSelect();
    }

    private void FailPendingSelect()
    {
        Log.Info($"BattlePrompt entity: deferred select timed out for {_pendingSelectEid} (parent {_pendingSelectParent})");
        _pendingSelectEid = ""; _pendingSelectParent = "";
        _router.Speech.Say("Couldn't select that energy", interrupt: true);
    }

    // The exact dispatch CardInteractions.OnUp makes for a real click: the card's override click listener
    // if one is set (board / prize / a distributing parent / attached), else its owner's click listener
    // (carousel / hand → the selection owner). We use it to distribute a board-attached parent Pokémon.
    private bool NativeClick(Card3D card)
    {
        var ci = card.Interactions;
        if (ci == null) return false;
        try
        {
            if (ci.OverrideClickListener != null) { ci.OverrideClickListener.OnCardClicked(card, false); return true; }
            if (ci.CardClickListener != null) { ci.CardClickListener.OnCardClicked(card, false); return true; }
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: NativeClick threw: {ex.Message}"); }
        return false;
    }

    private bool IsSelectedEid(string eid) =>
        _entitySel != null && _entitySel.selectionGroups.Any(g => g.selectedOptions.Any(s => s.entityID == eid));

    // Pick one card from a COLLAPSED pile (carousel/hand). Always selects the pile's current targetable
    // TOP, so repeated presses peel cards off in order (up to the prompt's max) with no stale-id issues.
    // Fully native: TryTargetCard moves the real card → clears FTUE focus. No deselect here — for a
    // "choose up to N" you simply stop and press Done (board-attached discards keep per-card toggling).
    // Is the game willing to be clicked on this card yet?
    //
    // SelectionCardOwner.OnCardClicked — the real click path — opens with
    // `if (clickedCard.positioner.isAnimating) return;`. A card still flying into the pile is simply not
    // clickable, and acting anyway is worse than failing, because TryTargetCard SUCCEEDS: targetability
    // was already computed, so the card moves and Card3D.SetOwner calls UnfocusCard(). That early-returns
    // unless `cardFocused`, and cardFocused is set only at the END of FocusCard →
    // WaitUntilCardIsIdleInOwner, which is itself parked on `while (positioner.isAnimating)`. So
    // FTUEMatchManager.focusedCard is never cleared, FTUECardIndicator's `while (focusedCard != null)`
    // never completes, the tutorial never spotlights the next card, and DoesFTUEPermitCardTargetability
    // then denies every remaining one. Observed 2026-07-10: a pile pick taken 175 ms after the overlay
    // opened selected card 1 and made cards 2 and 3 permanently unselectable.
    //
    // Second clause: the FTUE's focus handshake. positioner.isAnimating can go false in Update before the
    // FTUE's coroutine (which runs after Update) has set cardFocused, so wait for the flag UnfocusCard
    // actually reads rather than for the condition it happens to be waiting on.
    private static bool IsSettledForClick(Card3D card)
    {
        if (card == null) return false;
        try
        {
            if (card.positioner != null && card.positioner.isAnimating) return false;
            var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;
            if (focused != null && ReferenceEquals(focused, card) && !card.cardFocused) return false;
        }
        catch { }
        return true;
    }

    // Hold a press that can't land yet and retry it from Tick. The delegate re-enters the same Mark…
    // method, so it re-reads the live front and re-checks the guard each frame — no timers, no counters.
    private void ArmPendingChoose(Action retry) => _pendingChoose = retry;

    // Diagnostic for a TryTargetCard that refused a card the owner reported as accessible-for-targeting.
    // Distinguishes the causes that all read the same in the plain "acted=False" line: no active placement
    // context (ctx isn't the selection overlay), the FTUE targetability gate (only the spotlighted
    // focusedCard is targetable until the tutorial arms free play — Hilda hit this), or an
    // eligibility/instance mismatch (anyTargetable is true but not for this exact instance). Reflection +
    // FindObject are fine here: it only runs when a select already failed.
    private static MethodInfo? _ftuePermitTargetMethod;
    private string TargetDebug(Card3D card)
    {
        try
        {
            var th = _mgr?.TargetingHandler;
            string ctx = th != null ? th.CurrentContextLabel : "no-mgr";
            bool anyTargetable = th != null && th.AreAnyCardsTargetable;
            var ftue = FTUEMatchManager.Instance;
            if (ftue == null) return $"ctx={ctx} anyTargetable={anyTargetable} ftue=off";
            string focused = ftue.focusedCard == null ? "null" : ftue.focusedCard.entityID;
            bool permits;
            try
            {
                _ftuePermitTargetMethod ??= typeof(FTUEMatchManager).GetMethod("DoesFTUEPermitCardTargetability", BindingFlags.Instance | BindingFlags.NonPublic);
                permits = _ftuePermitTargetMethod == null || (bool)_ftuePermitTargetMethod.Invoke(ftue, new object[] { card });
            }
            catch { permits = true; }
            return $"ctx={ctx} anyTargetable={anyTargetable} focused={focused} blockDrag={card.blockDrag} ftuePermits={permits}";
        }
        catch (Exception ex) { return $"debug-threw:{ex.Message}"; }
    }

    private void MarkStackOption(string name)
    {
        if (_entityOverlay == null || _mgr == null) return;
        var th = _mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        var top = FindSelectableTop(name);
        if (top != null && !IsSettledForClick(top)) { ArmPendingChoose(() => MarkStackOption(name)); return; }
        if (top == null)
        {
            // No unselected front of this name remains → the whole pile is already chosen. State it
            // (not a failure) so the player knows to move on / press Done, rather than hearing a
            // misleading "couldn't select".
            _router.Speech.Say($"No more {name} to choose, {TotalSelected()} of {_entityMax} chosen", interrupt: true);
            Log.Info($"BattlePrompt stack: no targetable front for '{name}' selected={TotalSelected()}/{_entityMax}");
            return;
        }

        bool acted = false;
        try { acted = th.TryTargetCard(top, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
        catch (Exception ex) { Log.Error($"BattlePrompt: stack TryTargetCard threw: {ex.Message}"); }
        Log.Info($"BattlePrompt stack '{name}' acted={acted} top={top.entityID}" + (acted ? "" : $" [{TargetDebug(top)}]"));

        AnnounceSelectionResult(prev, name, acted);
    }

    // Select the next energy from one Pokémon's attached-energy stack (plain AttachedCardSelectionMenu —
    // "discard any Fire Energy from among your Pokémon"). The energy sits in a temp HAND that stacks
    // identical cards and only exposes each stack's FRONT to targeting; the front isn't identifiable up
    // front (the hand owner returns ALL its cards, not just tops), but TryTargetCard on a buried card is
    // a harmless no-op (returns false, changes nothing — verified in the log). So peel the front the
    // robust native way: walk this pile's unselected eids and TryTargetCard each until one takes — that
    // one IS the current front, and it's the exact call a real click makes. One per press; Done finishes.
    private void MarkAttachedHandStack(string name, string parentEid, List<string> eids)
    {
        if (_entityOverlay == null || _mgr == null) return;
        var th = _mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        bool acted = false;
        string actedEid = "";
        foreach (var eid in eids)
        {
            if (IsSelectedEid(eid)) continue;                 // already discarded — skip
            var card = _mgr.GetCard(eid);
            if (card == null) continue;
            card = ResolveTargetableCard(card);               // no-op for a hand owner, but safe
            // Same rule as the pile: never poke a card the game itself would refuse to be clicked on.
            if (!IsSettledForClick(card)) { ArmPendingChoose(() => MarkAttachedHandStack(name, parentEid, eids)); return; }
            try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt: attached TryTargetCard threw: {ex.Message}"); }
            if (acted) { actedEid = eid; break; }             // hit the current front
        }
        Log.Info($"BattlePrompt attached '{name}' parent={parentEid} acted={acted} eid={actedEid} selected={TotalSelected()}/{_entityMax}");

        if (!acted && TotalSelected() == prev)
        {
            // Nothing took and nothing changed → every energy of this pile is already chosen. State it
            // (not a failure) so the player moves on / presses Done.
            _router.Speech.Say($"No more {name} to choose, {TotalSelected()} of {_entityMax} chosen", interrupt: true);
            return;
        }
        AnnounceSelectionResult(prev, name, acted);
    }

    // The current targetable TOP card of a pile (matched by displayed name). A real click only ever
    // hits a stack's CURRENT FRONT: SelectionCardOwner.OnCardClicked → GetFrontCardOfStackThatHoldsCard
    // → TryTargetCard(front). So we must return a genuine live front of the UNSELECTED (source) side —
    // never a stale reference, never an already-selected card. GetCardsAccessibleForTargeting() yields
    // exactly the per-stack fronts, and a selected card has moved to the selected-side owner, so it
    // naturally drops out of this scan.
    //
    // FTUE: a forced "select this card" step makes only FTUEMatchManager.focusedCard targetable, so we
    // honour it — but ONLY when it's actually one of those current unselected fronts. The old code
    // returned focusedCard unconditionally on a name match; that was the deck-search bug — after a
    // selection the focus stayed pointing at the just-selected card (now on the selected side), so
    // repeated Enter re-targeted that same card (thrashing the count, the top=… repeats in the log)
    // and the genuine remaining front was never reached. Requiring focus to be a live front, and
    // skipping selected cards, fixes the multi-select while still serving forced FTUE picks (there,
    // focus IS the one allowed front).
    private Card3D? FindSelectableTop(string name)
    {
        var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;
        Card3D? front = null;
        try
        {
            // Pile sources backed by a SelectionCardOwner (deck search, attached lists): their
            // GetCardsAccessibleForTargeting() is exactly the per-stack fronts.
            foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
            {
                if (owner == null || owner.AreHeldCardsConsideredSelected) continue;   // source / unselected side only
                foreach (var c in owner.GetCardsAccessibleForTargeting())
                {
                    if (c == null || ResolveName(c.entityID) != name || IsSelectedEid(c.entityID)) continue;
                    if (focused != null && ReferenceEquals(c, focused)) return c;   // the forced-FTUE card, and it's a live front
                    front ??= c;                                                    // first genuine current front
                }
            }
            if (front != null) return front;

            // Discard-from-hand (HandSelectionOverlay: Ultra Ball "discard 2 cards", etc.) uses a
            // HandFanController, which is a HandController — NOT a SelectionCardOwner — so the scan above
            // misses it entirely (that was the "no targetable front" bug for every hand card). The hand's
            // GetCardsAccessibleForTargeting() returns each slot's GetTopCard() (the targetable front of
            // each stack), so consult the active overlay's hand the same way. Selecting a hand card is the
            // identical native verb (TryTargetCard on that front).
            if (_entityOverlay is IHandProvider hp && hp.Hand is { } hand)
            {
                foreach (var c in hand.GetCardsAccessibleForTargeting())
                {
                    if (c == null || ResolveName(c.entityID) != name || IsSelectedEid(c.entityID)) continue;
                    if (focused != null && ReferenceEquals(c, focused)) return c;
                    front ??= c;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: FindSelectableTop threw: {ex.Message}"); }
        return front;
    }

    // Submit a multi-pick entity selection through the overlay's OWN confirm path (its Done button).
    // SubmitClicked validates min/max; if the player chose fewer than the max but met the minimum it
    // raises the game's "you can still select more" confirmation — itself a ModalElementContainer the
    // mod's ModalContainerScreen speaks and navigates. So we just press it and let the game decide.
    // We do NOT set _submitted: if a confirm/error modal appears and the player cancels, the overlay
    // stays open and we must remain interactive — Tick / OnResume detect the real close via IsPromptOpen.
    private void SubmitEntity()
    {
        if (_entityOverlay == null) return;
        try { _entityOverlay.SubmitClicked(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: SubmitClicked threw: {ex.Message}"); }
    }

    // Map a card to the TARGETABLE instance the game would actually act on. A selection carousel (and the
    // hand) STACKS identical cards by archetype and exposes only each stack's top via
    // GetCardsAccessibleForTargeting() — a buried duplicate isn't targetable. SelectionCardOwner.OnCardClicked
    // resolves the clicked card to its stack front (GetFrontCardOfStackThatHoldsCard) before targeting; we
    // do the same: if the card isn't itself an accessible top, return the accessible top of its stack (the
    // same-archetype one). Selecting any copy of an identical pile is equivalent. No-op for non-selection
    // owners (board Pokémon, etc.), so it's safe to call on every path.
    private static Card3D ResolveTargetableCard(Card3D card)
    {
        if (card.owner is not SelectionCardOwner sco) return card;
        try
        {
            var tops = sco.GetCardsAccessibleForTargeting();
            foreach (var c in tops) if (ReferenceEquals(c, card)) return card;   // already a stack top
            int arch = card.cardDataRow != null ? card.cardDataRow.ArchetypeID : int.MinValue;
            if (arch != int.MinValue)
                foreach (var c in tops)
                    if (c != null && c.cardDataRow != null && c.cardDataRow.ArchetypeID == arch) return c;
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: ResolveTargetableCard threw: {ex.Message}"); }
        return card;
    }

    // Resolve the EXACT Card3D to select. During the FTUE, DoesFTUEPermitCardTargetability makes ONLY
    // FTUEMatchManager.focusedCard targetable (everything else is blocked), so it IS the card to select
    // for a forced step regardless of any entityID quirks — prefer it unconditionally. Outside the FTUE
    // (focused == null), use the open overlay's source owner's held card with this entityID
    // (AreHeldCardsConsideredSelected==false = unselected side); GetCard only as a last resort (it can
    // return a big-card-view / non-overlay instance the context won't accept).
    private Card3D? ResolveSelectionCard(CardAddress addr, Card3D? focused)
    {
        if (focused != null) return focused;

        foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
        {
            if (owner == null || owner.AreHeldCardsConsideredSelected) continue;   // source (unselected) side only
            foreach (var c in owner.GetOwnedCards())
                if (c != null && c.entityID == addr.entityID) return c;
        }

        return _mgr != null ? _mgr.GetCard(addr.entityID, addr.pos, addr.index) : null;
    }

    // ---- BOARD selection (choose a Pokémon ON the board — e.g. switch a Benched Pokémon to Active,
    // choose a new Active after a knockout, pick an attack target) ----
    // Routes through BoardSelectionMenu (a BattleMenu, NOT an EntitySelectionOverlay). The valid cards
    // are the public board Card3Ds; a click on one fires the menu's DelegatedClickOverride →
    // MarkCardSelected (adds to the group + auto-submits for a single-card pick). We invoke that exact
    // override — what a sighted player's board click does — via CardInteractions.OverrideClickListener.

    private void BuildBoardOptions(BoardSelectionMenu board, EntitySelection esel)
    {
        var group = esel.selectionGroups.Count > 0 ? esel.selectionGroups[0] : null;   // BoardSelectionMenu uses [0]
        if (group == null) return;
        _boardMenu = board;
        _boardGroup = group;
        _target = group.maxAmount < 1 ? 1 : group.maxAmount;
        foreach (var opt in group.validOptions)
        {
            var eid = opt.cardAddress.entityID;
            _options.Add(new Option { Name = ResolveName(eid), Choose = () => MarkBoard(eid), Eid = eid });
        }
        if (string.IsNullOrWhiteSpace(_question))
            _question = _target > 1 ? $"Choose {_target} Pokémon" : "Choose a Pokémon";
    }

    private void MarkBoard(string entityID)
    {
        if (_boardMenu == null || _boardGroup == null || _mgr == null) return;
        var card = _mgr.GetCard(entityID);   // the public board card — same instance BoardSelectionMenu holds
        var listener = card != null && card.Interactions != null ? card.Interactions.OverrideClickListener : null;
        if (card == null || listener == null)
        {
            Log.Warn($"BattlePrompt board: no override click listener for {entityID} (card={(card == null ? "null" : "found")})");
            _router.Speech.Say("Couldn't select that Pokémon", interrupt: true);
            return;
        }

        // The exact call a board click makes (CardInteractions routes clicks to OverrideClickListener).
        // For a valid card → MarkCardSelected (+ auto-submit on a single-card pick); for an already
        // selected card → MarkCardDeselected. So this naturally supports select AND deselect.
        try { listener.OnCardClicked(card, false); }
        catch (Exception ex) { Log.Error($"BattlePrompt board select threw: {ex.Message}"); }

        Log.Info($"BattlePrompt board select eid={entityID} marked={_boardGroup.selectedOptions.Count} open={IsPromptOpen()}");

        if (!IsPromptOpen()) { _submitted = true; return; }   // auto-submitted and closed
        _marked = _boardGroup.selectedOptions.Count;
        if (_marked >= _target)
        {
            _submitted = true;
            TrySubmitBoard();   // multi-card board pick that didn't auto-submit on the last click
        }
        else
        {
            _router.Speech.Say($"Selected, {_marked} of {_target}; choose {_target - _marked} more", interrupt: true);
        }
    }

    // Submit a board selection that didn't auto-submit — via the menu's own SubmitSelection (the method
    // its message-bar confirm button calls). Private, so reached by reflection (same pattern as PRIZE).
    private void TrySubmitBoard()
    {
        try
        {
            typeof(BoardSelectionMenu).GetMethod("SubmitSelection", BindingFlags.Instance | BindingFlags.NonPublic)?
                .Invoke(_boardMenu, null);
        }
        catch (Exception ex) { Log.Error($"BattlePrompt board submit threw: {ex.Message}"); }
    }

    // ---- ACTION selection (choose one of a Pokémon's attacks and use it as this attack) ----
    // Slowking's Seek Inspiration (an attack of the Pokémon you just discarded off your deck) and
    // Zoroark's Foul Play (an attack of the Defending Pokémon) both raise SelectionMethod.Action →
    // ActionSelectionOverlay, a modal drawer of AttackOption widgets built from ActionSelection.options.
    //
    // Each widget's click command calls `overlay.SelectOption(index)` — which writes selectedIndex and
    // submits (SubmitSelectionAndClose → MatchUserOperations.SelectInput) — so that public method IS the
    // click endpoint, not a parallel path around it. It's the same one the game's action timer uses to
    // auto-pick (OnTimeExpired → SelectOption(0)). Two things the widget adds, both no-ops here:
    // CardActionOption.OnClick's UnfocusCard(), guarded on the FTUE-only card.cardFocused, and the
    // button's interactable gate — GetActionOptionSetupList passes canUse: true for every option and
    // never calls SetCanInteractDelegate, so no option is ever greyed out. There is no Back button
    // either (ShowBackButton => false): a sighted player must pick one, and so must we.
    //
    // The index we submit is the raw position in ActionSelection.options — the overlay's builder bumps
    // its counter for EVERY option and only renders the ones whose card-action row and CardAction both
    // resolve. Mirror that filter exactly so our indices stay aligned with the ones SelectOption expects.
    private void BuildActionOptions(ActionSelectionOverlay overlay, ActionSelection asel, PlayerSelection sel)
    {
        var db = ManagerSingleton<CardDatabaseManager>.instance;
        var matchInfo = NetworkMatchController.currentMatchInfo;
        if (db == null || matchInfo == null || asel.options == null) return;

        // The overlay only draws the energy cost when it's filling it against a player's energy; with
        // EnergyFillDisplayMode.NONE (Seek Inspiration — the cost is already paid) no cost is shown.
        bool showCost = asel.energyFillDisplayMode != ActionSelection.EnergyFillDisplayMode.NONE;
        string origin = ResolveName(sel.originCardEntityID);   // the drawer's title, e.g. "Slowking"

        for (int i = 0; i < asel.options.Count; i++)
        {
            var opt = asel.options[i];
            if (opt == null || !int.TryParse(opt.actionSourceID, out int actionId)) continue;
            var row = db.TryGetActionFromDatabase(actionId);
            var action = matchInfo.GetAction(opt.actionGUID) as CardAction;
            if (row == null || action == null) continue;   // the overlay skips these too — keep i aligned

            int index = i;
            // The Pokémon the attack belongs to (the discarded card / the Defending Pokémon) — spoken
            // once as a section name, since it isn't the card named in the header.
            string parent = ResolveParentName(opt.parentEntityID) ?? "";
            _options.Add(new Option
            {
                Name = ActionOptionLabel(row, action, showCost),
                Parent = (parent.Length > 0 && parent != origin) ? parent : null,
                Eid = opt.parentEntityID,   // Up/Down reads that Pokémon's card
                Choose = () => { _submitted = true; overlay.SelectOption(index); },
            });
        }

        if (string.IsNullOrWhiteSpace(_question)) _question = "Choose an attack";
        if (origin != "card") _question = $"{origin}. {_question}";
    }

    // "<name>[, cost …][, N damage][. rules text]" — the same four fields the AttackOption widget draws,
    // read straight off the card-action row the widget itself renders from. An attack renamed or rewritten
    // for this match carries override loc ids on its CardAction, which win over the printed text (exactly
    // as AttackOption.SetupCustom resolves them). SpeakIcons runs first because energy symbols live in
    // this text as {W}-style tokens, which a later strip would silently delete.
    private static string ActionOptionLabel(CardActionDataRow row, CardAction action, bool showCost)
    {
        string name = !string.IsNullOrEmpty(action.overrideNameLocID)
            ? LocalizationHook.Get(action.overrideNameLocID) ?? row.Name
            : row.Name;
        if (string.IsNullOrWhiteSpace(name)) name = action.actionName;

        string text = !string.IsNullOrEmpty(action.overrideBodyLocID)
            ? LocalizationHook.Get(action.overrideBodyLocID) ?? ""
            : row.Text ?? "";

        var head = new List<string> { CardText.SpeakIcons(name).Trim() };
        if (showCost)
        {
            string cost = BattleBoardReader.EnergyCostWords(row.Cost);
            if (cost.Length > 0) head.Add(cost);
        }
        if (!string.IsNullOrEmpty(row.Damage)) head.Add($"{row.Damage} damage");

        string label = string.Join(", ", head);
        text = CardText.SpeakIcons(text).Trim().TrimEnd('.');
        return text.Length > 0 ? $"{label}. {text}" : label;
    }

    // ---- ACTIVE-PROMOTION DRAG (switch a Benched Pokémon to the Active spot) ----
    // "Choose 1 Benched Pokémon to switch with the Active Pokémon" routes to ActivePromotionDragMenu
    // (SelectionMethod.DragActive). It's a placement CONTEXT (ITargetCardPlacementContext +
    // ISupportsAutoPlacement) with a DragBenchToActive purpose — i.e. you DRAG the bench Pokémon onto
    // the Active slot, the same targeting-handler flow as the discard/search overlays. Native select =
    // TryTargetCard(benchCard, ClickReleaseAutoPlacementInitiator) → auto-places onto the single Active
    // receiver → ReactToSuccessfulPlacement → SubmitSelectionAndClose (auto-submits; no explicit submit).

    private void BuildActiveDragOptions(ActivePromotionDragMenu menu, EntitySelection esel)
    {
        var group = esel.selectionGroups.Count > 0 ? esel.selectionGroups[0] : null;
        if (group == null) return;
        _dragMenu = menu;
        _dragGroup = group;
        _target = group.maxAmount < 1 ? 1 : group.maxAmount;
        foreach (var opt in group.validOptions)
        {
            var eid = opt.cardAddress.entityID;
            _options.Add(new Option { Name = ResolveName(eid), Choose = () => MarkActiveDrag(eid), Eid = eid });
        }
        if (string.IsNullOrWhiteSpace(_question))
            _question = "Choose a Pokémon to switch to the Active spot";
    }

    private void MarkActiveDrag(string entityID)
    {
        if (_dragMenu == null || _mgr == null) return;
        var th = _mgr.TargetingHandler;

        // During the FTUE only focusedCard is targetable (the tutorial forces which bench Pokémon);
        // otherwise the chosen public bench card. Same resolution as the entity selections.
        var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;
        Card3D? card = focused ?? _mgr.GetCard(entityID);

        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt activeDrag refresh threw: {ex.Message}"); }

        bool targetable = card != null && th.IsCardTargetable(card);
        bool ok = false;
        if (card != null && targetable)
        {
            // Auto-places onto the single Active receiver; the menu's ReactToSuccessfulPlacement submits.
            try { ok = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt activeDrag TryTargetCard threw: {ex.Message}"); }
        }

        Log.Info($"BattlePrompt activeDrag eid={entityID} focused={(focused == null ? "null" : focused.entityID)} " +
                 $"targetable={targetable} ok={ok} autoSupported={_dragMenu.IsAutoPlacementSupported} open={IsPromptOpen()}");

        if (!IsPromptOpen()) { _submitted = true; return; }   // placed + auto-submitted → done

        // Didn't place (not targetable, or the menu needs a tap-to-confirm we didn't drive). Clear any
        // held target so we don't strand the card, and tell the player.
        if (ok) { try { th.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt activeDrag ClearTarget threw: {ex.Message}"); } }
        _router.Speech.Say("Couldn't switch to that Pokémon", interrupt: true);
    }

    // ---- ATTACHMENT DISTRIBUTION (attach energy onto Pokémon — e.g. Oricorio ex's "attach Basic Fire
    // Energy to your Benched Fire Pokémon") ----
    // AttachmentDistributionSelectionMenu (AttachFromPile / Reparent) holds a ReparentSelection: a set
    // of energy cards (reparentOptions) you attach onto target Pokémon, usually optional/unlimited with
    // a DONE button. It's a placement CONTEXT (purpose AttachCardToOtherCard); attaching = drag the
    // energy onto a Pokémon. Native attach = TryTargetCard(energy, auto-initiator); during the FTUE
    // DoesFTUEPermitCardReceivability constrains the target to one Pokémon, so it auto-attaches. DONE =
    // the menu's own CompleteReparentingAction (reached by reflection — it's protected, same as PRIZE).

    private void BuildAttachOptions(AttachmentDistributionSelectionMenu menu, ReparentSelection rsel)
    {
        _attachMenu = menu;
        _reparentSel = rsel;
        _target = 0;   // optional / unlimited — finish with the Done option
        ShowAttachEnergyPiles(announce: false);
    }

    // PHASE 1 — handle the ENERGY first, exactly like grabbing a card: the energy you have to attach,
    // as a pile with a LIVE count ("Basic Fire Energy, N available") so you always know how many are
    // left, plus Done. (The game shows these in hand; you drag each onto a Pokémon.)
    private void ShowAttachEnergyPiles(bool announce = true)
    {
        _options.Clear();
        _attachEnergyName = null;
        if (_reparentSel != null)
        {
            var piles = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (var ro in _reparentSel.reparentOptions)
            {
                if (ro == null || string.IsNullOrEmpty(ro.entityID) || !string.IsNullOrEmpty(ro.newParentEntityID)) continue;   // skip already-attached
                string name = ResolveName(ro.entityID);
                if (!piles.TryGetValue(name, out var eids)) { eids = new List<string>(); piles[name] = eids; order.Add(name); }
                eids.Add(ro.entityID);
            }
            foreach (var name in order)
            {
                string captured = name;
                _options.Add(new Option { Name = name, StackEids = piles[name], Choose = () => PickUpAttachEnergy(captured) });
            }
        }
        _options.Add(new Option { Name = "Done", IsDone = true, Choose = DoneAttach });
        _idx = 0;
        _question = "Attach energy to your Pokémon, or Done";
        if (announce) Announce();
    }

    // Chose an energy → PHASE 2: which Pokémon to put it on. List the eligible Pokémon as real, navigable
    // board cards (Up/Down reads each, so two identical Charmeleon are tellable apart) + Back. The attach
    // is the native targeting-handler drag (StartAttachHold), fired when you pick the Pokémon.
    private void PickUpAttachEnergy(string name)
    {
        _attachEnergyName = name;
        ShowAttachReceivers();
    }

    private void ShowAttachReceivers()
    {
        _options.Clear();
        var energy = AvailableAttachEnergy(_attachEnergyName);
        int remaining = CountAvailableEnergy(_attachEnergyName);
        if (energy != null && _attachMenu != null && _mgr != null)
        {
            try { _mgr.TargetingHandler?.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt attach refresh threw: {ex.Message}"); }
            var receivers = _attachMenu.ValidPlacementReceivers(energy).Select(r => r as Card3D).Where(c => c != null).Select(c => c!);
            if (FTUEHook.FtueGovernsMatch(_mgr) && FTUEMatchManager.Instance != null) receivers = receivers.Where(SafeFtuePermits);
            foreach (var r in receivers)
            {
                var eid = r.entityID;
                _options.Add(new Option { Name = ResolveName(eid), Eid = eid, Choose = () => AttachHeldEnergyTo(eid) });
            }
        }
        _options.Add(new Option { Name = "Back", Choose = () => ShowAttachEnergyPiles() });
        _idx = 0;
        _question = $"Attach {_attachEnergyName ?? "energy"} to which Pokémon? {remaining} left.";
        Announce();
    }

    private void AttachHeldEnergyTo(string receiverEid)
    {
        if (_attachMenu == null || _mgr == null) return;
        var energy = AvailableAttachEnergy(_attachEnergyName);
        if (energy == null) { _router.Speech.Say("No energy left to attach", interrupt: true); ShowAttachEnergyPiles(); return; }
        var receiver = _mgr.GetCard(receiverEid);
        if (receiver == null) { _router.Speech.Say("Couldn't find that Pokémon", interrupt: true); return; }
        StartAttachHold(energy, receiver);
    }

    // An available (not-yet-attached) energy of the given type, preferring the FTUE-forced card.
    private Card3D? AvailableAttachEnergy(string? name)
    {
        if (_mgr == null || _reparentSel == null) return null;
        var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;
        if (focused != null && _reparentSel.reparentOptions.Any(r => r != null && r.entityID == focused.entityID && string.IsNullOrEmpty(r.newParentEntityID))
            && (name == null || ResolveName(focused.entityID) == name))
            return focused;
        foreach (var ro in _reparentSel.reparentOptions)
        {
            if (ro == null || string.IsNullOrEmpty(ro.entityID) || !string.IsNullOrEmpty(ro.newParentEntityID)) continue;
            if (name != null && ResolveName(ro.entityID) != name) continue;
            return _mgr.GetCard(ro.entityID);
        }
        return null;
    }

    private int CountAvailableEnergy(string? name)
    {
        if (_reparentSel == null) return 0;
        int n = 0;
        foreach (var ro in _reparentSel.reparentOptions)
        {
            if (ro == null || string.IsNullOrEmpty(ro.entityID) || !string.IsNullOrEmpty(ro.newParentEntityID)) continue;
            if (name != null && ResolveName(ro.entityID) != name) continue;
            n++;
        }
        return n;
    }

    private bool SafeFtuePermits(Card3D c)
    {
        try { return FTUEMatchManager.Instance.DoesFTUEPermitCardReceivability(c); }
        catch { return true; }
    }

    // Drive the game's targeting handler to attach the energy onto THIS Pokémon — the same drag a
    // sighted player does. Our raycast-free initiator commits onto the chosen receiver; the loop needs
    // the receiver to have been "imminent" for a frame before Commit, so the actual commit is in
    // TickAttachCommit (2 ticks later), mirroring the hand-placement hold-and-drop.
    private void StartAttachHold(Card3D energy, Card3D target)
    {
        if (_mgr == null) return;
        var th = _mgr.TargetingHandler;
        if (!th.IsCardTargetable(energy)) { _router.Speech.Say("Can't attach that energy now", interrupt: true); ShowAttachEnergyPiles(); return; }
        _attachInit.Reset();
        if (!th.TryTargetCard(energy, _attachInit)) { _router.Speech.Say("Couldn't pick up that energy", interrupt: true); ShowAttachEnergyPiles(); return; }
        _attachEnergy = energy;
        _attachReceiver = target;
        _attachInit.SetChosen(target);
        _attachHolding = true;
        _attachTicks = 0;
        _router.Speech.Say($"Attaching to {ResolveName(target.entityID)}", interrupt: true);
    }

    private void TickAttachCommit()
    {
        if (_mgr == null || _attachReceiver == null) { CancelAttachHold(); return; }
        _attachInit.SetChosen(_attachReceiver);   // keep the chosen Pokémon imminent every frame
        if (++_attachTicks >= 2)
        {
            _attachInit.ArmPlacement();   // loop has seen the receiver ≥1 frame → commits at its next check
            _attachHolding = false;
            _attachReceiver = null;
            _attachEnergy = null;
            // Attach commits next frame; menu stays open for more (or auto-submits). Back to the energy
            // list (count now lower) — if it auto-submitted, the next Tick's IsPromptOpen pops us instead.
            if (IsPromptOpen()) ShowAttachEnergyPiles();
        }
    }

    private void CancelAttachHold()
    {
        try { _mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt attach cancel threw: {ex.Message}"); }
        _attachInit.Reset();
        _attachHolding = false;
        _attachReceiver = null;
        _attachEnergy = null;
        if (IsPromptOpen()) ShowAttachEnergyPiles();
    }

    // Finish the attachment step via the menu's own CompleteReparentingAction (what its DONE button calls).
    private void DoneAttach()
    {
        if (_attachMenu == null) return;
        _submitted = true;
        try
        {
            var m = typeof(AttachmentDistributionSelectionMenu).GetMethod("CompleteReparentingAction", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null)
            {
                var reasonType = m.GetParameters()[0].ParameterType;   // AttachmentDistributionConstraintEvaluator.AutoSubmitReason
                m.Invoke(_attachMenu, new object[] { Enum.ToObject(reasonType, 0) });   // .None
            }
        }
        catch (Exception ex) { Log.Error($"BattlePrompt attach Done threw: {ex.Message}"); }
    }

    // ---- DAMAGE distribution (ReparentDamageMenu / DamageSelection) — NATIVE, like a sighted player ----
    // Two shapes: MOVE existing counters ("move up to N damage counters ..." — fromDamageSelections
    // non-empty) and PUT new counters (Phantom Dive: "put N damage counters ..." — fromDamageSelections
    // empty).
    //
    // We DRIVE THE GAME'S OWN BOARD, not a data poke — because the tutorial has TWO coaches that each watch
    // for a physical step, and a data poke satisfies neither (it hangs the tutorial after the counters
    // silently land): FTUEReparentDamageIndicator waits for a source to START DISTRIBUTING (you click the
    // source Pokémon and its counters lift into a tray) and FTUEDragDamage waits for a counter to be DROPPED
    // on the target. So: (1) MOVE — click the source Pokémon via its distribute override
    // (SendCardToDistributorOnClick → ReparentDamageController.StartDistributionFromOwner spawns the tray
    // counters + fires the pick-up coach's event); PUT — the counters are pre-spawned at menu-open. (2) Drag
    // a tray counter onto the target Pokémon with the placement initiator (same hold-and-commit as a hand
    // play). ReparentDamageMenu.ReactToSuccessfulPlacement then applies the damage, clears the counter's FTUE
    // focus (advancing the drag coach), and auto-submits at the max. The counter's own denomination is the
    // amount moved — the game breaks the total into counters; we drop them one at a time. The evaluator only
    // TELLS us which sources/targets are valid and when the minimum is met; the drop does the real work.

    // The menu's own private state, read (never written, never re-created) so our reads and the game's
    // submit payload are always the same object graph.
    private static readonly FieldInfo? DamageEvaluatorField = AccessTools.Field(typeof(ReparentDamageMenu), "constraintEvaluator");
    private static readonly FieldInfo? DamageWaitingField = AccessTools.Field(typeof(ReparentDamageMenu), "isWaitingForUpdate");

    private void BuildDamageOptions(ReparentDamageMenu menu, DamageSelection ds)
    {
        _damageMenu = menu;
        _damageSel = ds;
        _damageEval = DamageEvaluatorField?.GetValue(menu) as ReparentDamageConstraintEvaluator;
        if (_damageEval == null)
            Log.Error("BattlePrompt: couldn't read ReparentDamageMenu.constraintEvaluator — damage options unavailable");
        _damageSource = null;
        _damageSourceStarted = false;
        _damageDropLanded = false;
        _damageDropInFlight = false;
        _damageRetry = null;
        // The menu auto-clicks a LONE eligible source before our postfix runs (SetupSelectableCards'
        // single-source branch), so distribution can already be live here. Adopt that source — the
        // evaluator excludes an active distributor from GetCardsWithDistributableDamage, so re-listing
        // would offer an empty source list and dead-end the prompt (Codex review finding, 2026-07-23).
        // Deliberately NOT marking it started: the first drop still runs EnsureDamageSourceDistributing,
        // whose already-distributing branch re-fires the coach event the tutorial missed at auto-start.
        var activeSource = CurrentDamageSourceId();
        if (ds.fromDamageSelections.Count > 0 && !string.IsNullOrEmpty(activeSource) && activeSource != "NewDC")
            _damageSource = activeSource;
        HookDamagePlacementEnded();
        _target = 0;
        RebuildDamageOptions();
        if (string.IsNullOrWhiteSpace(_question))
            _question = IsDamageMove
                ? "Move damage counters from one Pokémon to another"
                : $"Put {ds.maxMovableDamageTotal} damage counter{(ds.maxMovableDamageTotal == 1 ? "" : "s")} on your opponent's Pokémon";
    }

    private bool IsDamageMove => _damageSel != null && _damageSel.fromDamageSelections.Count > 0;

    // A MOVE needs a source distributing before you can drop its counters; a PUT already has counters ready.
    private void RebuildDamageOptions()
    {
        _options.Clear();
        if (_damageSel == null || _damageEval == null) return;
        if (IsDamageMove && _damageSource == null) BuildDamageSourceOptions();
        else BuildDamageTargetOptions();
    }

    private void BuildDamageSourceOptions()
    {
        if (_damageEval == null) return;
        // Same call the game's SetupSelectableCards makes: exclude the source that's already distributing
        // (its click is Inspect now; picking another source ends its distribution natively).
        var sources = _damageEval.GetCardsWithDistributableDamage(CurrentDamageSourceId()).Distinct().ToList();
        // During the tutorial only the spotlighted source may distribute — offer just it, so you can't pick
        // a Pokémon the tutorial forbids and land on a dead end. (Answers the "select a different Pokémon"
        // question: the tutorial does NOT allow another source.)
        var forced = FtueForcedCardEid();
        if (forced != null && sources.Contains(forced)) sources = new List<string> { forced };
        if (sources.Count == 1) { ChooseDamageSource(sources[0], announce: false); return; }   // lone source → auto-pick + distribute
        foreach (var eid in sources)
        {
            var e = eid;
            _options.Add(new Option { Name = $"Move damage from {ResolveName(e)}", Eid = e, Choose = () => ChooseDamageSource(e) });
        }
        if (_damageEval.AreMinimumConstraintsSatisfied())
            _options.Add(new Option { Name = "Done", IsDone = true, Choose = SubmitDamage });
    }

    // Chose a source: drive the game's own "pick up damage from this Pokémon" click (the distribute override
    // SetupSelectableCards put on eligible sources), lifting its counters into the tray and firing the
    // pick-up coach's event. Skip the click when this source is already the one distributing (a lone source
    // auto-starts at menu-open). Then move to the target list.
    private void ChooseDamageSource(string eid, bool announce = true)
    {
        // The tutorial forces which source you move from (coach 1 spotlights it), but the source list is
        // built at menu-open — BEFORE the coach spotlights — so it can still list every Pokémon. If the
        // player lands on a different one, redirect to the forced source so the tutorial isn't desynced.
        var forced = FtueForcedCardEid();
        if (forced != null && forced != eid && _damageEval != null
            && _damageEval.GetCardsWithDistributableDamage(null).Contains(forced))
            eid = forced;
        _damageRetry = null;   // a drop held for an old target must not survive a source change
        _damageSource = eid;
        // Distribution is triggered LAZILY on the first drop (EnsureDamageSourceDistributing), not here —
        // the tutorial's pick-up coach subscribes ~90 ms after the menu opens, and this can run at menu-open
        // (a lone source auto-picks in the postfix), so clicking now would fire the coach's event before it
        // is listening (exactly the race the old event-fire fix hit). The first drop is always well after.
        _damageSourceStarted = false;
        RebuildDamageOptions();
        _idx = 0; _line = -1; _lines = null;
        if (announce)
        {
            _router.Speech.Say($"Move damage from {ResolveName(eid)} to which Pokémon?", interrupt: true);
            if (_options.Count > 0) SpeakOption(_idx, interrupt: false);
        }
    }

    // Lift the chosen source's counters into the tray so we can drop them — driving the game's OWN distribute
    // click (which fires the pick-up coach's event). If the game already auto-started this source's
    // distribution at menu-open (lone-source case, before the coach subscribed), re-fire the event so the
    // coach — which missed that first fire — still advances. Called on the first drop, once per source.
    private static MethodInfo? _damageSrcEventMethod;
    private bool EnsureDamageSourceDistributing()
    {
        if (_damageSource == null || _mgr == null || _damageMenu == null) return false;
        var src = _mgr.GetCard(_damageSource);
        if (src == null) return false;
        if (CurrentDamageSourceId() != _damageSource)
        {
            // Only click a source that still carries its DISTRIBUTE override. Once a source has been the
            // distributor the game swaps its click to INSPECT (the big card viewer) — clicking it THEN pops
            // that overlay and strands the player behind it instead of distributing. Bail cleanly, never click.
            if (src.Interactions?.OverrideClickListener is not DistributeDamageFromCardOnClick)
            { Log.Warn($"BattlePrompt: {_damageSource} click is inspect (already distributed), not distributing — would open the card viewer"); return false; }
            // DistributeDamageFromCardOnClick → StartDistributionFromOwner: ends any other source's
            // distribution, spawns the tray SYNCHRONOUSLY, fires the coach's event, and refreshes the
            // placement context itself — nothing to wait for afterwards.
            NativeClick(src);
            if (CurrentDamageSourceId() != _damageSource) { Log.Warn($"BattlePrompt: couldn't start damage distribution from {_damageSource}"); return false; }
            _damageSourceStarted = true;
            Log.Info($"BattlePrompt damage: distributed source {_damageSource}, tray counters now {TrayCounters().Count}");
            return true;
        }
        // Already distributing (a lone source auto-starts at menu-open) → its event fired before the coach
        // subscribed; re-fire it. Only inside a match the FTUE actually governs (else a harmless no-op).
        _damageSourceStarted = true;
        if (FTUEHook.FtueGovernsMatch(_mgr) && FTUEMatchManager.Instance != null)
        {
            try
            {
                _damageSrcEventMethod ??= typeof(ReparentDamageMenu).GetMethod(
                    "TriggerOnDamageDistributionSourceChangedEvent", BindingFlags.Instance | BindingFlags.NonPublic);
                _damageSrcEventMethod?.Invoke(_damageMenu, new object[] { src, true });
                Log.Info($"BattlePrompt: re-fired FTUE damage-distribution event for {src.entityID}");
            }
            catch (Exception ex) { Log.Error($"BattlePrompt: damage source event threw: {ex.Message}"); }
        }
        return true;
    }

    private void BackToDamageSources()
    {
        // List the OTHER eligible sources; the current one keeps distributing. Choosing another source
        // clicks it, and the game's own SendCardToDistributorOnClick ends this distribution and starts
        // there — the same atomic switch a sighted player's click performs. (Never call EndDistribution
        // ourselves; no real click path does.)
        _damageRetry = null;   // a drop held for an old target must not survive the switch flow
        _damageSource = null;
        RebuildDamageOptions();
        _idx = 0; _line = -1; _lines = null;
        if (!string.IsNullOrWhiteSpace(_question)) _router.Speech.Say(_question, interrupt: true);
        if (_options.Count > 0) SpeakOption(_idx, interrupt: false);
    }

    // One option per eligible target (the evaluator excludes the source, full targets, and targets beyond
    // the cap). Each Enter drops one tray counter on it; press again to move another. Plus Back (move) and
    // Done (once the minimum is met; the game also auto-submits when you drop the last counter).
    private void BuildDamageTargetOptions()
    {
        _options.Clear();
        if (_damageSel == null || _damageEval == null) return;
        bool move = _damageSource != null;
        foreach (var eid in _damageEval.GetEntityIDsForValidDamageReceivers(_damageSource).Distinct())
        {
            var e = eid;
            _options.Add(new Option { Name = (move ? "Move damage to " : "Put damage on ") + ResolveName(e), Eid = e, Choose = () => PlaceDamage(e) });
        }
        // Back = switch source. Only offered while another source is actually eligible — the game
        // excludes the current distributor, so with one source there is nothing to go back to.
        if (move && _damageEval.GetCardsWithDistributableDamage(CurrentDamageSourceId()).Any())
            _options.Add(new Option { Name = "Back", Choose = BackToDamageSources });
        if (_damageEval.AreMinimumConstraintsSatisfied())
            _options.Add(new Option { Name = "Done", IsDone = true, Choose = SubmitDamage });
    }

    // Drop ONE damage counter on the target — the game's own drag. Pick the tray counter the tutorial
    // spotlights (else the first available), hold it with the placement initiator aimed at the target, and
    // arm the commit — all on this one keypress. The game answers through its own events: success is
    // ReactToSuccessfulPlacement (announce + rebuild there), a refusal is the placement loop ending without
    // it (OnDamagePlacementEnded speaks up), and a not-ready tutorial counter completes via the coach-focus
    // / unblock hooks. No frame counting anywhere.
    private void PlaceDamage(string targetEid)
    {
        if (_damageMenu == null || _mgr == null) return;
        if (_damageDropInFlight) return;   // the previous drop's placement loop hasn't reported back — a double-press, ignore
        if (DamageWaitingForServer()) { _router.Speech.Say("Waiting for the game", interrupt: true); return; }
        // MOVE: on the very first drop, lift the source's counters into the tray (and fire the pick-up
        // coach). StartDistributionFromOwner spawns the tray synchronously, so keep going right here.
        if (IsDamageMove && _damageSource != null && !_damageSourceStarted)
        {
            if (!EnsureDamageSourceDistributing()) { _router.Speech.Say("Couldn't lift damage from that Pokémon", interrupt: true); return; }
        }
        var target = _mgr.GetCard(targetEid);
        if (target is not ICardPlacementReceiver recv) { _router.Speech.Say("Can't put damage there", interrupt: true); return; }
        // While the tutorial owns the cards, a not-ready counter is a COACH-TIMING state, not a
        // refusal, so hold the drop and let the game's own events complete it. The signal is the
        // FTUE's OWN restriction flag, blockInputs (set by BlockAll for every coached step), plus a
        // tracked drag coach — NOT "a tutorial battle exists": FTUEMatchManager.Instance is present
        // in NORMAL matches too (CardInteractions dereferences Instance.blockInputs on every click),
        // so gating on the instance alone would swallow real refusals in ranked (Codex review
        // finding 2). The hold covers the observed live gap where Enter lands after the pick-up
        // coach exits and before FTUEDragDamage arms (21.8s vs 22.8s in the log) — completed by
        // DragDamageBlockCards_Postfix / FocusBlockDrag_Postfix / OnTargetabilityEvaluated.
        // Backspace abandons a held drop.
        bool ftueOwnsCards = FTUEHook.DragDamageCoachActive
            || (FTUEHook.FtueGovernsMatch(_mgr)
                && FTUEMatchManager.Instance != null && FTUEMatchManager.Instance.blockInputs);
        var counter = NextDamageCounter();
        if (counter == null)
        {
            if (ftueOwnsCards) { _damageRetry = () => PlaceDamage(targetEid); return; }
            _router.Speech.Say("No damage counter to move", interrupt: true); return;
        }
        var th = _mgr.TargetingHandler;
        if (!th.IsCardTargetable(counter))
        {
            if (ftueOwnsCards) { _damageRetry = () => PlaceDamage(targetEid); return; }
            _router.Speech.Say("Can't move that counter now", interrupt: true); return;
        }
        // Same receiver-first order as the energy move / no-target play (BattleScreen.BeginNoTargetPlay):
        // TryTargetCard runs the placement loop's first pass synchronously, and that pass records the
        // receiver from KbPlacementInitiator.Chosen — so choose BEFORE the pickup, then arm.
        _damageInit.Reset();
        _damageInit.SetChosen(recv);
        bool acted = false;
        try { acted = th.TryTargetCard(counter, _damageInit); }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage TryTargetCard threw: {ex.Message}"); }
        Log.Info($"BattlePrompt damage pickup: counter={counter.entityID} target={targetEid} acted={acted}");
        if (!acted) { _damageInit.Reset(); _router.Speech.Say("Can't move that counter now", interrupt: true); return; }
        _damageTargetEid = targetEid;
        _damageDropLanded = false;
        _damageDropInFlight = true;
        _damageInit.ArmPlacement();   // the player's Enter is the trigger; the game reports the outcome
    }

    // The tray counter to drop. While an FTUEDragDamage coach is active it focuses the EXACT counter it
    // wants dropped (GetDamageCards()[counterPos]) and waits for THAT instance to clear — dropping any
    // other leaves it focused and hangs the coach — so then drop only the focused counter, and return
    // null (hold the drop) until it's focused. A tutorial step WITHOUT that coach, and every normal
    // match, drops any tray counter. Gating on the tracked coach state (not just "a tutorial battle")
    // is what keeps un-coached reparent steps from deadlocking on a focus that never comes.
    private Card3D? NextDamageCounter()
    {
        var cards = TrayCounters();
        if (cards.Count == 0) return null;
        if (FTUEHook.DragDamageCoachActive && FTUEMatchManager.Instance != null)
        {
            var focused = FTUEMatchManager.Instance.focusedCard;
            if (focused == null) return null;
            foreach (var c in cards) if (ReferenceEquals(c, focused)) return c;
            return null;
        }
        return cards[0];
    }

    private List<Card3D> TrayCounters()
    {
        var ctrl = _damageMenu != null ? _damageMenu.GetDamageController() : null;
        if (ctrl == null) return new List<Card3D>();
        try { return ctrl.GetDamageCards().Where(c => c != null).ToList(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: GetDamageCards threw: {ex.Message}"); return new List<Card3D>(); }
    }

    // The source Pokémon whose counters are currently lifted into the tray (null if none distributing).
    private string? CurrentDamageSourceId()
    {
        try { return _damageMenu?.GetDamageController()?.CurrentDistributingEndpoint?.EndpointID; }
        catch { return null; }
    }

    // A drop just landed (called from the ReactToSuccessfulPlacement postfix, the same moment the game
    // refreshes its own click overrides). Announce the VERIFIED result — read back from
    // finalDamageSelections, the very payload SelectInput will send — and re-offer targets.
    // Do NOT reset _damageSource: a single-source MOVE keeps its one source for the whole prompt (the
    // just-used source's click is INSPECT now; collapsing back to it would pop the card viewer).
    private void AfterDamageDropApplied(ICardPlacementReceiver receiver)
    {
        _damageRetry = null;
        string eid = _damageTargetEid;
        try { if (receiver is Card3D rc && !string.IsNullOrEmpty(rc.entityID)) eid = rc.entityID; } catch { }
        if (_damageMenu == null || !IsPromptOpen()) return;   // auto-submitted at the limit → the close path pops us
        RebuildDamageOptions();
        _idx = 0; _line = -1; _lines = null;
        _router.Speech.Say(DamageDropSummary(eid), interrupt: true);
        if (_options.Count > 0) SpeakOption(_idx, interrupt: false);
    }

    // "Dunsparce has 60 damage." — read from the selection state the server will receive, so what the
    // player hears is what actually got recorded, never an assumption. Just the target's total: the
    // "N counters left to place" tally that followed it was cut on Amethyst's feedback (2026-07-23) —
    // the focused option that queues right after carries the flow.
    private string DamageDropSummary(string eid)
    {
        var name = ResolveName(eid);
        try
        {
            var fin = _damageSel?.finalDamageSelections.FirstOrDefault(f => f.entityID == eid);
            return fin != null ? $"{name} has {fin.newDamageCounters * 10} damage." : $"Moved damage to {name}.";
        }
        catch { return $"Moved damage to {name}"; }
    }

    // The placement loop ended. Success already went through the ReactToSuccessfulPlacement postfix; if our
    // armed drop ends WITHOUT it, the receiver was refused (FTUE gate, staleness) and the counter snapped
    // back to the tray — say so instead of silently doing nothing. (CardTargetingHandler fires this event
    // strictly AFTER the success callbacks, so the discrimination can't race.)
    private void OnDamagePlacementEnded()
    {
        if (!_damageDropInFlight) return;
        _damageDropInFlight = false;
        if (_damageDropLanded) return;
        try
        {
            _damageInit.Reset();
            if (!IsPromptOpen()) return;
            RebuildDamageOptions();
            _idx = 0; _line = -1; _lines = null;
            _router.Speech.Say("Couldn't place the counter there", interrupt: true);
            if (_options.Count > 0) SpeakOption(_idx, interrupt: false);
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage cancel follow-up threw: {ex.Message}"); }
    }

    // A drop the player asked for was held for the game (coach focus / unblock / server echo); run it now.
    private void CompleteDamageRetry()
    {
        var r = _damageRetry;
        if (r == null) return;
        _damageRetry = null;
        try { if (IsPromptOpen()) r(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage retry threw: {ex.Message}"); }
    }

    private bool DamageWaitingForServer()
    {
        try { return _damageMenu != null && DamageWaitingField?.GetValue(_damageMenu) is true; }
        catch { return false; }
    }

    private void HookDamagePlacementEnded()
    {
        if (_damagePlacementEndedHooked || _mgr == null) return;
        try
        {
            _mgr.TargetingHandler.OnPlacementEvaluationEnded += OnDamagePlacementEnded;
            // A held drop can also become possible through a plain targetability rebuild — e.g.
            // FTUEMatchManager.UnblockAllCards ends in RefreshPlacementContext with no coach call —
            // so listen to the game's own "targetability re-evaluated" broadcast too (filtered to
            // our menu's context). The retry re-validates and re-arms itself if still blocked.
            _mgr.TargetingHandler.OnTargetabilityEvaluated += OnDamageTargetabilityEvaluated;
            _damagePlacementEndedHooked = true;
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: placement-ended subscribe threw: {ex.Message}"); }
    }

    private void UnhookDamagePlacementEnded()
    {
        if (!_damagePlacementEndedHooked) return;
        try
        {
            if (_mgr != null)
            {
                _mgr.TargetingHandler.OnPlacementEvaluationEnded -= OnDamagePlacementEnded;
                _mgr.TargetingHandler.OnTargetabilityEvaluated -= OnDamageTargetabilityEvaluated;
            }
        }
        catch { }
        _damagePlacementEndedHooked = false;
    }

    private void OnDamageTargetabilityEvaluated(TPCI.Rainier.Match.Targeting.ITargetCardPlacementContext context)
    {
        if (_damageRetry == null || _damageMenu == null) return;
        if (!ReferenceEquals(context, _damageMenu)) return;   // another menu's evaluation — not ours
        CompleteDamageRetry();
    }

    // Abandon a counter the player Backspaced out of while it was still surfacing (a bounded pickup retry) —
    // clear the targeting so nothing stays half-grabbed. ClearTarget → OnPlacementCancelled → the counter
    // returns to the tray; the menu returns any un-dropped counters to the source at submit anyway.
    private void CancelDamageHold()
    {
        _damageRetry = null;
        _damageDropInFlight = false;
        try { _mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt: damage cancel threw: {ex.Message}"); }
        _damageInit.Reset();
        if (IsPromptOpen()) RebuildDamageOptions();
    }

    // The FTUE-forced card id (the game's spotlighted card) while a tutorial battle is guiding this
    // selection, else null. Used to restrict the source list to the one the tutorial permits.
    private string? FtueForcedCardEid()
    {
        if (!FTUEHook.FtueGovernsMatch(_mgr)) return null;
        var m = FTUEMatchManager.Instance;
        var c = m != null ? m.focusedCard : null;
        return c != null ? c.entityID : null;
    }

    // Submit through the menu's OWN CompleteReparentingAction (private — reflect it, same as the attach
    // menu's submit). It sends the selection the native drops built (SelectInput) and tears the menu down.
    // Re-read the live evaluator first so the minimum check reflects the counters actually dropped.
    private void SubmitDamage()
    {
        if (_damageMenu == null) return;
        // A drop is still inside the placement coroutine (the release is observed after a yield) —
        // submitting now would tear the tray down under it and lose that counter. One beat later the
        // drop lands and its own follow-up re-offers Done.
        if (_damageDropInFlight) { _router.Speech.Say("Still placing the counter", interrupt: true); return; }
        if (DamageWaitingForServer()) { _router.Speech.Say("Waiting for the game", interrupt: true); return; }
        // The MENU's own evaluator (never a fresh copy — constructing one wipes the recorded drops).
        if (_damageEval != null && !_damageEval.AreMinimumConstraintsSatisfied())
        { _router.Speech.Say("You haven't moved enough damage yet", interrupt: true); return; }
        _submitted = true;
        try
        {
            var m = typeof(ReparentDamageMenu).GetMethod("CompleteReparentingAction", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null) m.Invoke(_damageMenu, null);
            else Log.Error("BattlePrompt: ReparentDamageMenu.CompleteReparentingAction not found");
        }
        catch (Exception ex) { Log.Error($"BattlePrompt damage submit threw: {ex.Message}"); }
    }

    private static string ResolveName(string entityID)
    {
        var mm = MatchManager.GetInstanceFromScene();
        if (mm != null && mm.TryGetCardNameFromEntityID(entityID, out var name, true)) return name;
        return "card";
    }

    // ---- TEXT (coin / quiz / number / yes-no / mulligan) ----

    private void BuildTextOptions(TextSelectionOverlay text)
    {
        var ids = text.GetOptions();
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (string.IsNullOrEmpty(id) || id == "REMOVED_OPTION") continue;
            int idx = i;
            string label = LocalizationHook.Get(id) ?? id;
            _options.Add(new Option { Name = label, Choose = () => { _submitted = true; text.SelectOption(idx); } });
        }
    }

    // ---- Screen handler ----

    public void OnEnter() { if (_deferInitialAnnounce) return; Announce(); }
    // A child popped (e.g. the "still have selections" / overpay confirm modal). If submitting it
    // actually closed the overlay, pop us; otherwise re-announce so the player can keep choosing.
    public void OnResume() { if (!IsPromptOpen()) { Close(); return; } Announce(); }
    public void OnExit() { }
    public void RespeakAll() => Announce();
    public string StateSignature() => $"Prompt|{_question}|{_idx}|{IsPromptOpen()}";

    private void Announce()
    {
        _line = -1; _lines = null;   // re-orient to the header whenever we (re)announce
        _lastSpokenParent = null;    // force the section header to speak on the first option
        if (!string.IsNullOrWhiteSpace(_question)) _router.Speech.Say(_question, interrupt: true);
        if (_options.Count > 0) SpeakOption(_idx, interrupt: false);
    }

    // Speak an option, prefixing the parent Pokémon as a SECTION NAME only when it changes from the last
    // option we announced — mirrors how menu groups announce their name on entry (KeyboardNavigator.
    // GroupName) but not on every item. So energy on the same Pokémon reads "Basic Fire Energy, 2 of 4"
    // while crossing to another Pokémon reads "Charmeleon. Basic Fire Energy, 3 of 4".
    private void SpeakOption(int i, bool interrupt)
    {
        if (i < 0 || i >= _options.Count) return;
        var o = _options[i];
        string label = OptionLabel(i);
        if (!string.IsNullOrEmpty(o.Parent) && o.Parent != _lastSpokenParent)
            _router.Speech.Say($"{o.Parent}. {label}", interrupt);
        else
            _router.Speech.Say(label, interrupt);
        _lastSpokenParent = o.Parent;
    }

    // Append ", selected" when the game's own selection data says this card is currently chosen — so
    // the player can tell what's already selected as they browse (the game shows it by moving the card
    // to its "selected" area). Reads SelectionGroup.selectedOptions; no separate tracking of our own.
    private string OptionLabel(int i)
    {
        var o = _options[i];
        if (o.StackEids != null)
        {
            // A collapsed pile: read its live remaining count + how many of it are chosen (the running
            // total across the whole prompt is spoken on each pick).
            int sel = o.StackEids.Count(IsSelectedEid);
            int remaining = o.StackEids.Count - sel;
            string body = $"{o.Name}, {remaining} available";
            if (sel > 0) body += $", {sel} selected";
            return $"{body}, {i + 1} of {_options.Count}";
        }
        string mark = IsOptionSelected(o) ? ", selected" : "";
        return $"{o.Name}{mark}, {i + 1} of {_options.Count}";
    }

    // Is this option currently selected, per the game's own data? Entity / board / drag picks live in a
    // SelectionGroup's selectedOptions; prize picks live in the menu's private _selectedPrizes (read by
    // Card3D membership, since the prize group's selectedOptions isn't populated until submit). The
    // synthetic Done entry is never "selected".
    private bool IsOptionSelected(Option o)
    {
        if (o.IsDone) return false;
        if (_prizeMenu != null && o.Card != null) return ReadSelectedPrizes(_prizeMenu).Contains(o.Card);
        var eid = o.Eid;
        if (eid == null) return false;
        if (_entitySel != null) return _entitySel.selectionGroups.Any(g => g.selectedOptions.Any(s => s.entityID == eid));
        if (_boardGroup != null) return _boardGroup.selectedOptions.Any(s => s.entityID == eid);
        if (_dragGroup != null) return _dragGroup.selectedOptions.Any(s => s.entityID == eid);
        return false;
    }

    // Total cards selected across all entity groups (multi-group prompts spread picks over groups).
    private int TotalSelected() =>
        _entitySel != null ? _entitySel.selectionGroups.Sum(g => g.selectedOptions.Count)
        : (_entityGroup != null ? _entityGroup.selectedOptions.Count : 0);

    public void Tick()
    {
        if (!IsPromptOpen()) { Close(); return; }          // submitted/dismissed → pop
        if (!Application.isFocused || _submitted) return;  // wait silently for the close after submit
        if (_attachHolding) { TickAttachCommit(); return; }   // committing an attach onto the chosen Pokémon
        if (!string.IsNullOrEmpty(_pendingSelectEid)) { TickPendingEnergySelect(); return; }   // distributing then selecting an energy
        // A damage drop can be held for the game (the coach hasn't focused / freed its counter yet — the
        // event hooks complete it). Let Backspace/Escape abandon that hold rather than leaving it armed.
        if (_damageMenu != null && _damageRetry != null && (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Escape)))
        { CancelDamageHold(); return; }
        // A press is waiting on a card that was still moving. Retry it; it re-arms itself if still unsettled.
        if (_pendingChoose != null) { var retry = _pendingChoose; _pendingChoose = null; retry(); return; }

        // Tutorial: the damage source list is built at menu-open, BEFORE the coach spotlights which Pokémon
        // to move damage from, so it can still list every eligible source. The moment the coach points at
        // one, collapse to it (the tutorial doesn't let you use any other — no more "select each Pokémon").
        if (_damageMenu != null && IsDamageMove && _damageSource == null)
        {
            var forced = FtueForcedCardEid();
            if (forced != null && _damageEval != null && _damageEval.GetCardsWithDistributableDamage(null).Contains(forced))
            { ChooseDamageSource(forced); return; }
        }

        // Tab jumps to the Done option (multi-pick confirm) for quick access — no-op if there isn't one.
        if (Input.GetKeyDown(KeyCode.Tab)) { JumpToDone(); return; }

        // ONLY Left/Right navigate between options. Up/Down read the focused card's detail lines (like
        // board-card reading) and NEVER move between options — on a card with no readable lines they just
        // re-read the option, they don't jump to another one.
        if (Input.GetKeyDown(KeyCode.RightArrow)) { Move(1); return; }
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { Move(-1); return; }
        if (Input.GetKeyDown(KeyCode.DownArrow)) { ReadLine(1); return; }
        if (Input.GetKeyDown(KeyCode.UpArrow)) { ReadLine(-1); return; }
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { Choose(); return; }
        for (int d = 1; d <= 9; d++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + (d - 1)) || Input.GetKeyDown(KeyCode.Keypad1 + (d - 1))) { JumpTo(d - 1); return; }
    }

    private bool IsPromptOpen()
    {
        // PrizeCardSelectionMenu doesn't drive BattleMenu.isOpen the way the overlays do, so use its
        // own SELECT-state flag; other overlays use isOpen.
        if (_handler is PrizeCardSelectionMenu p) return p.IsAwaitingSelection;
        if (_handler is BattleMenu bm) return bm.isOpen;
        return false;
    }

    private void Move(int delta)
    {
        if (_options.Count == 0) return;
        int next = Mathf.Clamp(_idx + delta, 0, _options.Count - 1);
        if (next == _idx) return;
        _idx = next;
        _line = -1; _lines = null;   // new option → reset line cursor; lines re-resolve on next Up/Down
        SpeakOption(_idx, interrupt: true);
    }

    private void JumpTo(int i)
    {
        if (i < 0 || i >= _options.Count) return;
        _idx = i;
        _line = -1; _lines = null;
        SpeakOption(_idx, interrupt: true);
    }

    // Tab → jump straight to the Done option (multi-pick menus) so the player doesn't have to arrow past
    // every card to confirm. They still press Enter on it themselves.
    private void JumpToDone()
    {
        int di = _options.FindIndex(o => o.IsDone);
        if (di >= 0) JumpTo(di);
    }

    // Read the focused option's card line-by-line (Down = next line, Up = previous, back past line 0 =
    // the option header). Mirrors board-card reading so "card-related stuff" reads the same everywhere.
    // An option with no resolvable card (text choice, Done/Back, face-down prize) has nothing to read,
    // so Up/Down move between options there instead — the player never gets stuck on a dead axis.
    private void ReadLine(int dir)
    {
        if (_options.Count == 0) return;
        EnsureLines();
        // No detail to read (face-down prize, energy, synthetic option) → re-read the option; NEVER move
        // between options (Left/Right is the only navigation).
        if (_lines == null || _lines.Count == 0) { _router.Speech.Say(OptionLabel(_idx), interrupt: true); return; }
        if (dir > 0)
        {
            if (_line < _lines.Count - 1) { _line++; _router.Speech.Say(_lines[_line], interrupt: true); }
            else _router.Speech.Say("Last line", interrupt: true);
        }
        else if (_line >= 0)
        {
            _line--;
            _router.Speech.Say(_line < 0 ? OptionLabel(_idx) : _lines[_line], interrupt: true);
        }
        else _router.Speech.Say(OptionLabel(_idx), interrupt: true);
    }

    // Resolve + cache the focused option's detail lines on first Up/Down. Empty list = no card to read.
    private void EnsureLines()
    {
        if (_lines != null) return;
        var card = ResolveCardForRead(_options[_idx].Eid);
        _lines = card != null ? BattleBoardReader.DetailLines(card) : new List<string>();
        _line = -1;
    }

    private static readonly BattleBoardReader.Zone[] _readZones =
    {
        BattleBoardReader.Zone.MyActive, BattleBoardReader.Zone.MyBench,
        BattleBoardReader.Zone.OppActive, BattleBoardReader.Zone.OppBench, BattleBoardReader.Zone.MyDiscard,
    };

    // Find the live Card3D for an option's entityID WITHOUT side effects (NOT MatchManager.GetCard,
    // which rewrites CardAddress / returns a big-card view for hidden cards). Selection overlays hold
    // their cards in SelectionCardOwners (the carousel/list); board & active-drag picks are public board
    // cards. Read-only and run on demand (a key press), so the scan cost is irrelevant.
    private Card3D? ResolveCardForRead(string? eid)
    {
        if (string.IsNullOrEmpty(eid)) return null;
        try
        {
            foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
            {
                if (owner == null) continue;
                foreach (var c in owner.GetOwnedCards())
                    if (c != null && c.entityID == eid) return c;
            }
            if (_mgr != null)
            {
                // The hand (discard-from-hand prompts like Ultra Ball's cost): scan EVERY hand card —
                // the discard source is the HandFanController, not a SelectionCardOwner, so the scan
                // above misses it, and GetCards(MyHand) returns only pile top-cards (would miss a
                // buried duplicate). GetHandCardsAll returns all copies.
                foreach (var c in BattleBoardReader.GetHandCardsAll(_mgr))
                    if (c != null && c.entityID == eid) return c;
                foreach (var z in _readZones)
                    foreach (var c in BattleBoardReader.GetCards(_mgr, z))
                        if (c != null && c.entityID == eid) return c;
            }
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: ResolveCardForRead threw: {ex.Message}"); }
        return null;
    }

    private void Choose()
    {
        if (_idx < 0 || _idx >= _options.Count) return;
        try { _options[_idx].Choose(); }
        catch (Exception ex) { Log.Error($"BattlePromptScreen.Choose threw: {ex.Message}"); }
        // The submit closes the overlay; Tick detects that and pops. Result narrates via the log.
    }

    private void Close()
    {
        if (!_active) return;
        _active = false;
        _handler = null;
        _pendingChoose = null;
        // Release any card the targeting handler might still be holding (an attach energy mid-drag, or a damage
        // counter caught mid surface-retry) so it isn't left with a stranded target — that makes later
        // board/prompt input read as dead. Unconditional now: the damage drop is atomic, but a pickup could be
        // in flight, and ClearTarget/Reset are harmless no-ops when nothing is held.
        try { _mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt: close ClearTarget threw: {ex.Message}"); }
        _damageInit.Reset(); _attachInit.Reset();
        UnhookDamagePlacementEnded();
        _damageRetry = null;
        _damageDropLanded = false;
        _damageDropInFlight = false;
        if (_attachHolding) { _attachHolding = false; _attachReceiver = null; _attachEnergy = null; }
        _options.Clear();
        _router.Deactivate(this);
    }
}
