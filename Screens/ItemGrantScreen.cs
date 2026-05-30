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
    private int _focus;                             // 0 = the reward item, 1 = the Collect All button

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
            if (_active) { _active = false; _router.Deactivate(this); }
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
        if (item != _shown)
        {
            _shown = item;
            _lineIndex = -1;
            _focus = 0;
            if (item != null) AnnounceItem(item, interrupt: true);
        }

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

    private string CollectAllLabel()
    {
        var btn = _overlay?.collectAllButton;
        var t = btn != null ? btn.GetComponentInChildren<TMPro.TMP_Text>(includeInactive: false) : null;
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text : "Collect All";
    }

    private void CollectAll()
    {
        var btn = _overlay?.collectAllButton;
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
            btn.onClick?.Invoke();
    }

    private void AnnounceItem(ContentItemRewardController? item, bool interrupt)
    {
        if (item == null) return;
        _finalAnnounced = false;
        _lines = new List<string>();

        var granted = ReadField<GrantedItem>(item, "itemData");
        var clientId = granted?.clientId;

        CardDataRow? row = string.IsNullOrEmpty(clientId) ? null : CardDatabaseManager.instance?.TryGetCardFromDatabase(clientId);
        if (row != null)
        {
            _lines = CardText.DetailLines(row);
            var prefix = (granted != null && granted.isNewCard) ? "New, " : "";
            _router.Speech.Say(prefix + CardText.NameWithType(row), interrupt);
        }
        else
        {
            // Non-card reward (currency, token, sleeve, deck box, …) — read the overlay's OWN
            // displayed name + amount (ContentItemRewardUI.titleTxt e.g. "Event Token" + footerTxt
            // "Amount 10"), so it reads like what's on screen instead of a raw item id.
            var ui = ReadField<Component>(item, "ui");
            var name = ui != null ? TmpOf(ReadField<Component>(ui, "titleTxt")) : null;
            var amount = ui != null ? TmpOf(ReadField<Component>(ui, "footerTxt")) : null;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(name!);
            if (!string.IsNullOrWhiteSpace(amount)) parts.Add(amount!);
            if (parts.Count == 0 && !string.IsNullOrEmpty(clientId)) parts.Add(clientId!);
            _router.Speech.Say(parts.Count > 0 ? string.Join(", ", parts) : "Reward", interrupt);
        }
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
