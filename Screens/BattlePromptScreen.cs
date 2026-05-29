using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
// families so far: PRIZE (PrizeCardSelectionMenu) and TEXT (TextSelectionOverlay — coin/quiz/
// number/yes-no/mulligan). Other families (Board/Hand/Pile/…) are announced but not yet driven.
internal sealed class BattlePromptScreen : IScreenHandler
{
    private static BattlePromptScreen? _instance;

    private readonly ScreenRouter _router;
    private bool _active;

    // One navigable choice. Eid is the entity-selection card id (so the label can show ", selected"
    // by reading the game's own SelectionGroup.selectedOptions); null for prize / text options.
    private sealed class Option
    {
        public string Name = "";
        public Action Choose = () => { };
        public string? Eid;
    }

    private ISelectionRequestHandler? _handler;   // the open overlay (also a BattleMenu)
    private string _question = "";
    private readonly List<Option> _options = new();
    private int _idx;
    private bool _submitted;
    private int _marked;
    private int _target;
    private SelectionGroup? _entityGroup;
    private EntitySelection? _entitySel;
    private EntitySelectionOverlay? _entityOverlay;
    private BoardSelectionMenu? _boardMenu;
    private SelectionGroup? _boardGroup;
    private ActivePromotionDragMenu? _dragMenu;
    private SelectionGroup? _dragGroup;
    private AttachmentDistributionSelectionMenu? _attachMenu;
    private ReparentSelection? _reparentSel;
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
        Log.Info("BattlePromptScreen installed");
    }

    private static void RunSelectionRequest_Postfix(PlayerSelection selectionRequest, ISelectionRequestHandler __result)
    {
        if (_instance == null || selectionRequest == null) return;
        try { _instance.OnPromptOpened(selectionRequest, __result); }
        catch (Exception ex) { Log.Error($"BattlePromptScreen.OnPromptOpened threw: {ex.Message}"); }
    }

    private void OnPromptOpened(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        BuildPrompt(sel, handler);

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

        if (!_active) { _active = true; _router.Activate(this); }
        else Announce();   // a new prompt replaced the previous one without closing the screen
    }

    private void BuildPrompt(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        _handler = handler;
        _options.Clear();
        _idx = 0;
        _submitted = false;
        _marked = 0;
        _target = 0;
        _entityGroup = null;
        _entitySel = null;
        _entityOverlay = null;
        _boardMenu = null;
        _boardGroup = null;
        _dragMenu = null;
        _dragGroup = null;
        _attachMenu = null;
        _reparentSel = null;
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
            if (vs is EntitySelection es && es.selectionGroups != null)
                groups = "[" + string.Join(", ", es.selectionGroups.Select(g => $"{g?.validOptions?.Count ?? -1} valid / max {g?.maxAmount}")) + "]";
            else if (vs is ReparentSelection rs)
                groups = $"reparentOptions={rs.reparentOptions?.Count ?? -1} min/max={rs.minReparentedOptions}/{rs.maxReparentedOptions} unlimited={rs.unlimitedReparent}";
            Log.Info($"BattlePrompt: handler={handler?.GetType().Name ?? "null"} method={vs?.selectionMethod} selType={vs?.GetType().Name} groups={groups} built={_options.Count} q='{_question}'");
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: diagnostics threw: {ex.Message}"); }
    }

    // ---- PRIZE ----

    private void BuildPrizeOptions(PrizeCardSelectionMenu prize)
    {
        var group = prize.OnlyGroup;
        if (group == null) return;
        var opts = group.validOptions;
        int n = opts.Count;
        _target = group.maxAmount < 1 ? 1 : group.maxAmount;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            _options.Add(new Option { Name = $"Prize card {idx + 1} of {n}", Choose = () => MarkPrize(prize, idx) });
        }
        if (string.IsNullOrWhiteSpace(_question))
            _question = _target > 1 ? $"Take {_target} prize cards" : "Take a prize card";
    }

    private void MarkPrize(PrizeCardSelectionMenu prize, int optionIndex)
    {
        var group = prize.OnlyGroup;
        if (group == null || optionIndex < 0 || optionIndex >= group.validOptions.Count) return;

        // Resolve the face-down prize Card3D via the menu's own prize owner + the public index lookup,
        // then mark it through the menu's own selection method (so highlights/state stay correct).
        var owner = (PrizeController?)typeof(PrizeCardSelectionMenu)
            .GetField("_currentPrizeOwner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(prize);
        var card = owner?.GetPrivateCardAtIndex(group.validOptions[optionIndex].cardAddress.index);
        if (card == null) { _router.Speech.Say("Couldn't take that prize", interrupt: true); return; }

        typeof(PrizeCardSelectionMenu)
            .GetMethod("TryMarkCardSelected", BindingFlags.Instance | BindingFlags.NonPublic)?
            .Invoke(prize, new object[] { card, false });
        _marked++;

        if (_marked >= _target)
        {
            _submitted = true;
            prize.SubmitSelection();   // sends SelectInput, closes, resolves the held attack/KO log
        }
        else
        {
            _router.Speech.Say($"Prize selected, {_marked} of {_target}; choose {_target - _marked} more", interrupt: true);
        }
    }

    // ---- HAND / PILE / entity selection (discard N, search, choose card(s) from hand or a pile) ----

    private void BuildEntityOptions(EntitySelectionOverlay overlay, EntitySelection esel)
    {
        if (esel.selectionGroups.Count == 0) return;
        _entityOverlay = overlay;
        _entitySel = esel;
        _entityGroup = esel.selectionGroups[0];   // kept for the data-fallback path
        // Total across ALL groups, not a single group's max — multi-group prompts like "1 Pokémon OR
        // 1 Basic Energy" (Night Stretcher) have one group per kind, and the chosen card lands in
        // whichever group accepts it. totalMaxAmount is the real count to submit at.
        _target = esel.totalMaxAmount < 1 ? 1 : esel.totalMaxAmount;
        // List EVERY group's options (was: only the first group — so the player only saw the Pokémon
        // and never the Energy). Each option carries its own entityID; the native select routes it to
        // the correct group.
        foreach (var group in esel.selectionGroups)
        {
            if (group == null) continue;
            foreach (var opt in group.validOptions)
            {
                var addr = opt.cardAddress;   // copy per-iteration so the lambda captures the right one
                _options.Add(new Option { Name = ResolveName(addr.entityID), Choose = () => MarkEntity(addr), Eid = addr.entityID });
            }
        }
        if (string.IsNullOrWhiteSpace(_question))
            _question = $"Choose {_target} card" + (_target > 1 ? "s" : "");
    }

    // Select a card the NATIVE way: drive the same card-placement a click does. While the overlay is
    // open the targeting handler's context is the overlay (BattleMenuNavigation applied it), so
    // TryTargetCard routes to AddCardToSelection — which MOVES the card, runs ReactToSuccessfulPlacement
    // → AddSelection (populates selectedOptions AND allSelectedCards so CleanUp transfers the card),
    // and fires the card's UnfocusCard() → clears FTUEMatchManager.focusedCard so the tutorial advances.
    // The old direct AddToSelectedOptionsByEntityID poke did NONE of that — the searched card arrived
    // late and the FTUE never continued. ClickReleaseAutoPlacementInitiator auto-places onto the single
    // SelectedReceiver synchronously (no mouse). Falls back to the data poke if the card can't be resolved.
    private void MarkEntity(CardAddress addr)
    {
        if (_entityGroup == null || _entityOverlay == null) return;
        var mgr = _mgr;
        var th = mgr != null ? mgr.TargetingHandler : null;
        var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;

        // Re-evaluate targetability for the active overlay context — the carousel/hand cards become
        // placeable only after they settle into their slots, and the game refreshes on those events;
        // doing it here guarantees the state is current at the moment we select.
        bool anyBefore = th != null && th.AreAnyCardsTargetable;
        try { th?.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        Card3D? card = ResolveSelectionCard(addr, focused);
        bool selected = false;
        bool targetable = card != null && th != null && th.IsCardTargetable(card);
        if (card != null && th != null && targetable)
        {
            try { selected = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt: native select threw: {ex.Message}"); }
        }

        Log.Info($"BattlePrompt select eid={addr.entityID} selected={selected} owner={card?.owner?.GetType().Name ?? "null"} " +
                 $"isFocused={(card != null && ReferenceEquals(card, focused))} targetable={targetable} anyBefore={anyBefore} ctx={(th != null ? th.CurrentContextLabel : "?")}");

        if (!selected)
        {
            // Data fallback: add to the group that actually OWNS this option (multi-group prompts have
            // a group per kind), not always group 0.
            var grp = _entitySel?.selectionGroups.FirstOrDefault(g => g.validOptions.Any(o => o.entityID == addr.entityID)) ?? _entityGroup;
            grp?.AddToSelectedOptionsByEntityID(addr.entityID);
        }

        // Count selections across ALL groups from the game's own data (a native select moves the card and
        // adds to its group's selectedOptions; the fallback adds directly) — not a private tally and not a
        // single group — so it's truthful for multi-group prompts AND when the FTUE forces a card. The
        // label shows ", selected" from the same data so the player sees what's chosen as they browse.
        _marked = TotalSelected();
        if (_marked >= _target)
        {
            _submitted = true;
            _entityOverlay.SubmitClicked();   // validates min/max + submits + closes (the confirm button's own method)
        }
        else
        {
            _router.Speech.Say($"Selected, {_marked} of {_target}; choose {_target - _marked} more", interrupt: true);
        }
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
        AddAttachEnergyOptions();
        if (string.IsNullOrWhiteSpace(_question))
            _question = "Attach energy to a Pokémon, or choose Done";
    }

    // The energy list: one "Attach <energy>" per not-yet-attached reparent option, plus Done.
    private void AddAttachEnergyOptions()
    {
        _options.Clear();
        if (_reparentSel != null)
            foreach (var ro in _reparentSel.reparentOptions)
            {
                if (ro == null || string.IsNullOrEmpty(ro.entityID)) continue;
                if (!string.IsNullOrEmpty(ro.newParentEntityID)) continue;   // already attached this step
                var eid = ro.entityID;
                _options.Add(new Option { Name = "Attach " + ResolveName(eid), Choose = () => MarkAttach(eid) });
            }
        _options.Add(new Option { Name = "Done", Choose = DoneAttach });
    }

    private void ShowAttachEnergyOptions()
    {
        AddAttachEnergyOptions();
        _idx = 0;
        _question = "Attach energy to a Pokémon, or choose Done";
        Announce();
    }

    // Player picked an energy. Resolve its valid target Pokémon (narrowed to FTUE-permitted ones during
    // the tutorial). One target → attach straight away; several → offer a target picker.
    private void MarkAttach(string entityID)
    {
        if (_attachMenu == null || _mgr == null) return;
        var th = _mgr.TargetingHandler;
        var focused = FTUEMatchManager.Instance != null ? FTUEMatchManager.Instance.focusedCard : null;
        Card3D? energy = focused ?? _mgr.GetCard(entityID);
        if (energy == null) { _router.Speech.Say("Couldn't find that energy", interrupt: true); return; }

        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt attach refresh threw: {ex.Message}"); }

        var receivers = _attachMenu.ValidPlacementReceivers(energy)
            .Select(r => r as Card3D).Where(c => c != null).Select(c => c!).ToList();
        if (FTUEHook.FtueIsRunning && FTUEMatchManager.Instance != null)
            receivers = receivers.Where(SafeFtuePermits).ToList();

        Log.Info($"BattlePrompt attach eid={entityID} energy={energy.entityID} receivers={receivers.Count}");

        if (receivers.Count == 0) { _router.Speech.Say("No valid Pokémon to attach this energy to", interrupt: true); return; }
        if (receivers.Count == 1) { StartAttachHold(energy, receivers[0]); return; }

        // Several valid targets — let the player choose which Pokémon.
        _attachEnergy = energy;
        _options.Clear();
        foreach (var r in receivers)
        {
            var target = r;
            _options.Add(new Option { Name = "To " + ResolveName(target.entityID), Choose = () => ChooseAttachTarget(target) });
        }
        _options.Add(new Option { Name = "Back", Choose = ShowAttachEnergyOptions });
        _idx = 0;
        _question = $"Attach {ResolveName(energy.entityID)} to which Pokémon?";
        Announce();
    }

    private bool SafeFtuePermits(Card3D c)
    {
        try { return FTUEMatchManager.Instance.DoesFTUEPermitCardReceivability(c); }
        catch { return true; }
    }

    private void ChooseAttachTarget(Card3D target)
    {
        if (_attachEnergy == null) { ShowAttachEnergyOptions(); return; }
        StartAttachHold(_attachEnergy, target);
    }

    // Drive the game's targeting handler to attach the energy onto THIS Pokémon — the same drag a
    // sighted player does. Our raycast-free initiator commits onto the chosen receiver; the loop needs
    // the receiver to have been "imminent" for a frame before Commit, so the actual commit is in
    // TickAttachCommit (2 ticks later), mirroring the hand-placement hold-and-drop.
    private void StartAttachHold(Card3D energy, Card3D target)
    {
        if (_mgr == null) return;
        var th = _mgr.TargetingHandler;
        if (!th.IsCardTargetable(energy)) { _router.Speech.Say("Can't attach that energy now", interrupt: true); ShowAttachEnergyOptions(); return; }
        _attachInit.Reset();
        if (!th.TryTargetCard(energy, _attachInit)) { _router.Speech.Say("Couldn't pick up that energy", interrupt: true); ShowAttachEnergyOptions(); return; }
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
            _attachInit.Commit = true;   // loop has seen the receiver ≥1 frame → commits next frame
            _attachHolding = false;
            _attachReceiver = null;
            _attachEnergy = null;
            // Attach commits next frame; menu stays open for more (or auto-submits). Re-offer the energy
            // list — if it auto-submitted, the next Tick's IsPromptOpen pops us instead.
            if (IsPromptOpen()) ShowAttachEnergyOptions();
        }
    }

    private void CancelAttachHold()
    {
        try { _mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt attach cancel threw: {ex.Message}"); }
        _attachInit.Reset();
        _attachHolding = false;
        _attachReceiver = null;
        _attachEnergy = null;
        if (IsPromptOpen()) ShowAttachEnergyOptions();
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

    public void OnEnter() => Announce();
    public void OnResume() => Announce();
    public void OnExit() { }
    public void RespeakAll() => Announce();
    public string StateSignature() => $"Prompt|{_question}|{_idx}|{IsPromptOpen()}";

    private void Announce()
    {
        if (!string.IsNullOrWhiteSpace(_question)) _router.Speech.Say(_question, interrupt: true);
        if (_options.Count > 0) _router.Speech.Say(OptionLabel(_idx), interrupt: false);
    }

    // Append ", selected" when the game's own selection data says this card is currently chosen — so
    // the player can tell what's already selected as they browse (the game shows it by moving the card
    // to its "selected" area). Reads SelectionGroup.selectedOptions; no separate tracking of our own.
    private string OptionLabel(int i)
    {
        var o = _options[i];
        string sel = (o.Eid != null && IsOptionSelected(o.Eid)) ? ", selected" : "";
        return $"{o.Name}{sel}, {i + 1} of {_options.Count}";
    }

    // Is this card currently selected, per the game's own selection data? Checks ALL entity groups
    // (multi-group prompts), then the board / drag group.
    private bool IsOptionSelected(string eid)
    {
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

        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.RightArrow)) { Move(1); return; }
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.LeftArrow)) { Move(-1); return; }
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
        _router.Speech.Say(OptionLabel(_idx), interrupt: true);
    }

    private void JumpTo(int i)
    {
        if (i < 0 || i >= _options.Count) return;
        _idx = i;
        _router.Speech.Say(OptionLabel(_idx), interrupt: true);
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
        _options.Clear();
        _router.Deactivate(this);
    }
}
