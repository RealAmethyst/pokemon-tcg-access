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
// We read each reward as the player flips through it: for a card we resolve its CardDataRow via
// CardDatabaseManager.TryGetCardFromDatabase(clientId) — the same printed data the battle reader
// uses — and speak name + type, with Down/Up walking the detail lines (HP, weakness, attacks).
// Enter / Backspace advances to the next reward (currentItem.OnClick); C collects all; on the
// final results display Enter closes (OnFinalDisplayClick).
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

        var item = _overlay.currentItem;
        if (item != _shown)
        {
            _shown = item;
            _lineIndex = -1;
            AnnounceItem(item, interrupt: true);
        }

        if (item != null)
        {
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
            else if (UnityEngine.Input.GetKeyDown(KeyCode.C))
            {
                CollectAll();
            }
        }
        else
        {
            // Final results display: everything revealed; Enter / Backspace closes the overlay.
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
             || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
             || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
            {
                _overlay.OnFinalDisplayClick();
            }
        }
    }

    private void CollectAll()
    {
        var btn = _overlay?.collectAllButton;
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
            btn.onClick?.Invoke();
    }

    private void AnnounceItem(ContentItemRewardController? item, bool interrupt)
    {
        if (item == null)
        {
            if (!_finalAnnounced)
            {
                _finalAnnounced = true;
                _router.Speech.Say("All rewards revealed.", interrupt);
            }
            return;
        }
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
            // Non-card reward (currency, sleeve, deck box, …) — we don't format these yet; speak a
            // best-effort label so the player at least knows a reward is here and can advance past it.
            _router.Speech.Say(string.IsNullOrEmpty(clientId) ? "Reward" : clientId!, interrupt);
        }
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
