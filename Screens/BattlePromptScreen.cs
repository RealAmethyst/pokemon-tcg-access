using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MatchLogic;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Rainier.Match.Cards;
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

    private ISelectionRequestHandler? _handler;   // the open overlay (also a BattleMenu)
    private string _question = "";
    private readonly List<(string label, Action choose)> _options = new();
    private int _idx;
    private bool _submitted;
    private int _marked;
    private int _target;
    private SelectionGroup? _entityGroup;
    private EntitySelectionOverlay? _entityOverlay;

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
            // Family we can't drive yet — at least tell the player a decision is being asked.
            if (!string.IsNullOrWhiteSpace(_question))
                _router.Speech.Say(_question, interrupt: false);
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
        _entityOverlay = null;
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
            _options.Add(($"Prize card {idx + 1} of {n}", () => MarkPrize(prize, idx)));
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
        var group = esel.selectionGroups.Count > 0 ? esel.selectionGroups[0] : null;
        if (group == null) return;
        _entityOverlay = overlay;
        _entityGroup = group;
        _target = group.maxAmount < 1 ? 1 : group.maxAmount;
        foreach (var opt in group.validOptions)
        {
            string eid = opt.cardAddress.entityID;
            _options.Add((ResolveName(eid), () => MarkEntity(eid)));
        }
        if (string.IsNullOrWhiteSpace(_question))
            _question = $"Choose {_target} card" + (_target > 1 ? "s" : "");
    }

    private void MarkEntity(string entityID)
    {
        if (_entityGroup == null || _entityOverlay == null) return;
        _entityGroup.AddToSelectedOptionsByEntityID(entityID);   // populates the group's response
        _marked++;
        if (_marked >= _target)
        {
            _submitted = true;
            _entityOverlay.SubmitClicked();   // validates min/max + submits + closes
        }
        else
        {
            _router.Speech.Say($"Selected, {_marked} of {_target}; choose {_target - _marked} more", interrupt: true);
        }
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
            _options.Add((label, () => { _submitted = true; text.SelectOption(idx); }));
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

    private string OptionLabel(int i) => $"{_options[i].label}, {i + 1} of {_options.Count}";

    public void Tick()
    {
        if (!IsPromptOpen()) { Close(); return; }          // submitted/dismissed → pop
        if (!Application.isFocused || _submitted) return;  // wait silently for the close after submit

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
        try { _options[_idx].choose(); }
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
