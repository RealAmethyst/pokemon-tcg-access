using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Item Grant / reward card reveal — driver: ContentItemGrantingOverlay (OverlayScreenGroup),
// the screen that flips through cards/items you just earned (battle pass tiers, packs, shop, …).
//
// It shows ONE reward at a time as `currentItem` (ContentItemRewardController). Clicking it
// (OnClick → AnimateOut → ShowNextItem) reveals the next; `collectAllButton` skips to the end;
// once everything is shown `currentItem` goes null and a results display appears.
//
// We read each reward as the player flips through it: a card resolves its CardDataRow via
// CardDatabaseManager.TryGetCardFromDatabase(clientId) (name + type, Down/Up walk the detail lines);
// a non-card reward (currency/token/sleeve) reads the overlay's own displayed name + amount
// (ContentItemRewardUI.titleTxt + footerTxt — "Event Token, Amount 10"). Enter / Backspace advances
// (currentItem.OnClick); Left/Right move between the reward and the Collect All button (so it's
// reachable and read by name); on the final results display Enter closes (OnFinalDisplayClick).
internal sealed class ItemGrantScreen : IScreenHandler
{
    private static ItemGrantScreen? _instance;

    private readonly ScreenRouter _router;
    private ContentItemGrantingOverlay? _overlay;
    private bool _active;

    private ContentItemRewardController? _shown;   // reward currently announced
    private List<string> _lines = new();
    private int _lineIndex = -1;                    // -1 = on the name/header; 0..n-1 = detail lines
    private bool _finalAnnounced;
    private bool _namePending;                       // a non-card reward whose title wasn't ready yet
    private int _focus;                             // 0 = the reward item, 1 = the Collect All button

    private ContentItemRewardController_Booster? _booster;   // a pack being revealed card-by-card
    private int _boosterState = int.MinValue;
    private static MethodInfo? _onActiveClick;

    public string Name => "Rewards";

    public ItemGrantScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ItemGrantScreen(router);

        var activate = AccessTools.Method(typeof(ContentItemGrantingOverlay), "Activate");
        if (activate == null)
        {
            Log.Error("ItemGrantScreen: could not resolve ContentItemGrantingOverlay.Activate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemGrantScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(ContentItemGrantingOverlay), "OnDeActivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemGrantScreen), nameof(Deactivate_Postfix))));

        Log.Info("ItemGrantScreen installed");
    }

    private static void Activate_Postfix(ContentItemGrantingOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
    }

    private static void Deactivate_Postfix(ContentItemGrantingOverlay __instance)
    {
        if (_instance == null || _instance._overlay != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._overlay = null;
    }

    public void OnEnter() { _shown = null; _lineIndex = -1; _finalAnnounced = false; }
    public void OnResume() { _shown = null; _lineIndex = -1; }   // re-announce current reward on Tick
    public void OnExit() { }
    public void RespeakAll() { if (_shown != null) AnnounceItem(_shown, interrupt: true); }

    public string StateSignature()
        => _overlay == null ? "Rewards|dead" : $"Rewards|{(_shown != null)}|{_lineIndex}|{_finalAnnounced}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this);
            return;
        }
        if (!Application.isFocused) return;

        // Final "COLLECTED" summary (resultsDisplay active): read the totals + the DONE button.
        if (ResultsActive())
        {
            if (!_finalAnnounced) { _finalAnnounced = true; _shown = null; AnnounceCollected(interrupt: true); }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
             || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
                _overlay.OnFinalDisplayClick();
            return;
        }
        _finalAnnounced = false;

        var item = _overlay.currentItem;

        // A booster pack reveals its cards INSIDE one currentItem (ContentItemRewardController_Booster):
        // each tap steps the 3D pack to the next card. The normal per-currentItem path would read the
        // pack as a single item and Enter→OnClick would skip the whole pack, so drive the pack's own
        // reveal here instead — card by card.
        if (item is ContentItemRewardController_Booster booster) { TickBooster(booster); return; }
        _booster = null;

        if (item != _shown)
        {
            _shown = item;
            _lineIndex = -1;
            _focus = 0;
            if (item != null) AnnounceItem(item, interrupt: true);
        }
        else if (_namePending && item != null)
            AnnounceItem(item, interrupt: true);   // title wasn't ready on the first pass — retry until it lands

        if (item != null)
        {
            // Left/Right move between the reward (focus 0) and the Collect All button (focus 1),
            // so the button is reachable + read by name — no hidden shortcut, no hint.
            bool hasCollectAll = _overlay.collectAllButton != null && _overlay.collectAllButton.isActiveAndEnabled;
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (_focus != 0) { _focus = 0; _lineIndex = -1; AnnounceItem(item, interrupt: true); }
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
            {
                if (hasCollectAll && _focus != 1) { _focus = 1; _router.Speech.Say(CollectAllLabel(), interrupt: true); }
                return;
            }

            if (_focus == 1)
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
                    CollectAll();
                return;
            }

            // On the reward: Down/Up walk its detail lines (cards); Enter advances to the next reward.
            if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
            {
                if (_lines.Count > 0)
                {
                    _lineIndex = Mathf.Min(_lineIndex + 1, _lines.Count - 1);
                    _router.Speech.Say(_lines[_lineIndex], interrupt: true);
                }
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
            {
                if (_lineIndex <= 0) { _lineIndex = -1; AnnounceItem(item, interrupt: true); }
                else { _lineIndex--; _router.Speech.Say(_lines[_lineIndex], interrupt: true); }
            }
            else if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
                  || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
                  || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
            {
                item.OnClick();   // advance to the next reward (or the results display)
            }
        }
    }

    // The final "COLLECTED" summary: read the totals (cards + each currency) and the DONE button.
    private void AnnounceCollected(bool interrupt)
    {
        var stats = CollectedStats();
        var msg = string.IsNullOrWhiteSpace(stats) ? "Collected" : $"Collected. Total collected: {stats}";
        msg += ". Done";   // the only action; Enter / Backspace presses it
        _router.Speech.Say(msg, interrupt);
        Log.Info($"ItemGrant COLLECTED: {stats}");
    }

    private bool ResultsActive()
    {
        var rd = ReadField<Component>(_overlay!, "resultsDisplay");
        return rd != null && rd.gameObject.activeInHierarchy;
    }

    private string CollectedStats()
    {
        var rd = ReadField<Component>(_overlay!, "resultsDisplay");
        if (rd == null) return string.Empty;
        var parts = new List<string>();
        AddStat(rd, "cardsCollectedContainer", "cardCountTxt", "cards", parts);
        AddStat(rd, "hardCollectedContainer", "hardCountTxt", "Crystals", parts);
        AddStat(rd, "traderCollectedContainer", "traderCountTxt", "Credits", parts);
        AddStat(rd, "tokensCollectedContainer", "tokenCountTxt", "Event Tokens", parts);
        return string.Join(", ", parts);
    }

    // Read one TOTAL-COLLECTED stat if its container is shown; the count text is just the number, so
    // append the type label unless the loc already worded it.
    private static void AddStat(object rd, string containerField, string txtField, string label, List<string> parts)
    {
        var container = ReadField<GameObject>(rd, containerField);
        if (container == null || !container.activeInHierarchy) return;
        var txt = TmpOf(ReadField<Component>(rd, txtField))?.Trim();
        if (string.IsNullOrWhiteSpace(txt)) return;
        parts.Add(HasLetter(txt!) ? txt! : $"{txt} {label}");
    }

    private static bool HasLetter(string s) { foreach (var c in s) if (char.IsLetter(c)) return true; return false; }

    // The tutorial DISABLES this button. ItemGrantButtonController.WaitForOverlay sets
    // `grantOverlay.collectAllButton.interactable = false` as soon as any item-grant FTUE step arms, and
    // only its CollectAll step ever turns it back on — during the ClickItem step you are meant to reveal
    // each card yourself. So it stays reachable (you can tab to a dimmed control) but reads as
    // unavailable, and pressing it says so instead of doing nothing at all.
    private string CollectAllLabel()
    {
        var btn = _overlay?.collectAllButton;
        var t = btn != null ? btn.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false) : null;
        var label = t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : "Collect All";
        return btn != null && !btn.IsInteractable() ? $"{label}, unavailable" : label;
    }

    private void CollectAll()
    {
        var btn = _overlay?.collectAllButton;
        if (btn == null || !btn.isActiveAndEnabled) return;
        if (!btn.IsInteractable()) { _router.Speech.Say(CollectAllLabel(), interrupt: true); return; }
        btn.onClick?.Invoke();
    }

    private void AnnounceItem(ContentItemRewardController? item, bool interrupt)
    {
        if (item == null) return;
        _finalAnnounced = false;
        _namePending = false;
        _lines = new List<string>();

        var granted = ReadField<GrantedItem>(item, "itemData");
        var clientId = granted?.clientId;

        CardDataRow? row = string.IsNullOrEmpty(clientId) ? null : CardDatabaseManager.instance?.TryGetCardFromDatabase(clientId);
        if (row != null)
        {
            _lines = CardText.DetailLines(row);
            var prefix = (granted != null && granted.isNewCard) ? "New, " : "";
            _router.Speech.Say(prefix + CardText.NameTypeRarity(row), interrupt);
        }
        else
        {
            // Non-card reward (avatar item, currency, sleeve, …) — read the overlay's OWN displayed
            // name + amount (ContentItemRewardUI.titleTxt / footerTxt). The reveal sets the title a beat
            // AFTER the item appears, so on the first pass it can still be blank — never blurt the raw
            // item id ("upper_f_035-002") as a fallback; flag it pending and re-announce (Tick retries)
            // once the title lands.
            var ui = ReadField<Component>(item, "ui");
            var name = ui != null ? TmpOf(ReadField<Component>(ui, "titleTxt")) : null;
            if (string.IsNullOrWhiteSpace(name)) { _namePending = true; return; }
            _namePending = false;
            var amount = ui != null ? TmpOf(ReadField<Component>(ui, "footerTxt")) : null;
            var parts = new List<string> { name! };
            if (!string.IsNullOrWhiteSpace(amount)) parts.Add(amount!);
            _router.Speech.Say(string.Join(", ", parts), interrupt);
        }
    }

    // ---- booster pack reveal (card by card) ----

    private void TickBooster(ContentItemRewardController_Booster booster)
    {
        if (!ReferenceEquals(booster, _booster)) { _booster = booster; _boosterState = int.MinValue; _shown = booster; _lineIndex = -1; _lines = new List<string>(); _focus = 0; }

        int state = BoosterState(booster);
        if (state != _boosterState)
        {
            _boosterState = state;
            _lineIndex = -1;
            _focus = 0;
            if (state <= 0)
            {
                // Sealed pack: no card is on screen, so no detail lines. Without this clear, Down/Up
                // kept reading the PREVIOUS pack's last card — the card was gone but its stats weren't
                // (reported 2026-07-23: "a new pack was about to be opened but moving down would speak
                // the stats from the last pokemon").
                _lines = new List<string>();
                _router.Speech.Say(SealedPackText(booster), interrupt: true);
            }
            else AnnounceBoosterCard(booster, state - 1, interrupt: true);
        }

        // Same Left/Right focus model as the one-reward flow: the pack/card, or the Collect All
        // button. The game keeps Collect All active and clickable right through a pack reveal
        // (ContentItemGrantingOverlay.Activate shows it; OnClickCollectAll works until the final
        // tally) — a sighted player can bail out of a long reveal at any card, so we must too.
        bool hasCollectAll = _overlay!.collectAllButton != null && _overlay.collectAllButton.isActiveAndEnabled;
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow))
        {
            if (_focus != 0)
            {
                _focus = 0; _lineIndex = -1;
                if (_boosterState <= 0) _router.Speech.Say(SealedPackText(booster), interrupt: true);
                else AnnounceBoosterCard(booster, _boosterState - 1, interrupt: true);
            }
            return;
        }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow))
        {
            if (hasCollectAll && _focus != 1) { _focus = 1; _router.Speech.Say(CollectAllLabel(), interrupt: true); }
            return;
        }
        if (_focus == 1)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter))
                CollectAll();
            return;
        }

        // Down/Up walk the current card's detail lines; Enter reveals the next card (or finishes the pack).
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow))
        {
            if (_lines.Count > 0) { _lineIndex = Mathf.Min(_lineIndex + 1, _lines.Count - 1); _router.Speech.Say(_lines[_lineIndex], interrupt: true); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (_lineIndex <= 0) { _lineIndex = -1; AnnounceBoosterCard(booster, _boosterState - 1, interrupt: true); }
            else { _lineIndex--; _router.Speech.Say(_lines[_lineIndex], interrupt: true); }
        }
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
              || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            AdvanceBooster(booster);   // step the real 3D pack to the next card (its own tap handler)
        }
    }

    // The sealed-pack screen, spoken as it reads: "Mega Evolution—Pitch Black Booster Pack, 10 cards.
    // Packs left: 3." Title and packs-left are the game's own texts (no invented "Opening" — the
    // screen doesn't say it, and nothing has opened yet); the card count is the
    // one mod-added orientation fact.
    private string SealedPackText(ContentItemRewardController_Booster booster)
    {
        var parts = new List<string> { $"{PackName(booster) ?? "Pack"}, {booster.numberOfCards} cards" };
        var ui = ReadField<Component>(booster, "ui");
        var footer = ui != null ? TmpOf(ReadField<Component>(ui, "footerTxt")) : null;
        if (!string.IsNullOrWhiteSpace(footer) && !footer!.Contains("(Missing)")) parts.Add(footer.Trim());
        return string.Join(". ", parts);
    }

    private string? PackName(ContentItemRewardController_Booster booster)
    {
        var granted = ReadField<GrantedItem>(booster, "itemData");
        var name = LocOf(granted?.clientId);
        if (name != null) return name;
        var ui = ReadField<Component>(booster, "ui");
        var shown = ui != null ? TmpOf(ReadField<Component>(ui, "titleTxt")) : null;
        return !string.IsNullOrWhiteSpace(shown) && !shown!.Contains("(Missing)") ? shown.Trim() : null;
    }

    private static string? LocOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var s = TPCI.Localization.LocalizationManager.Instance?.GetString(id);
            return string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)") ? null : s!.Trim();
        }
        catch { return null; }
    }

    private void AnnounceBoosterCard(ContentItemRewardController_Booster booster, int index, bool interrupt)
    {
        _lines = new List<string>();
        var cards = ReadField<System.Collections.IList>(booster, "cardsData");
        if (cards == null || index < 0 || index >= cards.Count) { _router.Speech.Say("Pack", interrupt); return; }
        var granted = cards[index] as GrantedItem;
        var clientId = granted?.clientId;
        var row = string.IsNullOrEmpty(clientId) ? null : CardDatabaseManager.instance?.TryGetCardFromDatabase(clientId);
        if (row != null)
        {
            _lines = CardText.DetailLines(row);
            var prefix = (granted != null && granted.isNewCard) ? "New, " : "";
            _router.Speech.Say($"{prefix}{CardText.NameTypeRarity(row)}, {index + 1} of {booster.numberOfCards}", interrupt);
        }
        else _router.Speech.Say(string.IsNullOrEmpty(clientId) ? "Card" : clientId!, interrupt);
    }

    // The interaction's `state`: the revealed card index + 1 (0 = the unopened pack, before the first tap).
    private static int BoosterState(ContentItemRewardController_Booster booster)
    {
        var interaction = ReadField<Component>(booster, "interaction");
        if (interaction == null) return int.MinValue;
        var f = interaction.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(interaction) is int s ? s : int.MinValue;
    }

    // Step the pack to the next card via the interaction's own click handler (what a tap calls) — it
    // reveals the next card, or finishes the pack (→ the next reward) once all are shown.
    private static void AdvanceBooster(ContentItemRewardController_Booster booster)
    {
        try
        {
            var interaction = ReadField<Component>(booster, "interaction");
            if (interaction == null) return;
            _onActiveClick ??= interaction.GetType().GetMethod("OnActiveClick",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            _onActiveClick?.Invoke(interaction, null);
        }
        catch (System.Exception ex) { Log.Error($"ItemGrant: booster advance threw: {ex.Message}"); }
    }

    private static string? TmpOf(Component? c)
    {
        if (c == null) return null;
        var t = c.GetComponent<TMPro.TMP_Text>() ?? c.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false);
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : null;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }
}
