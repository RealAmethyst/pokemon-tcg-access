using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Screens;

// Product purchase overlay — driver: ShopProductOverlay (an OverlayScreenGroup), opened when a
// shop tile is activated (ShopProductController.OnClick). It shows the product header (name +
// price + availability, a ShopProductController = productHeader), the breakdown of what's inside
// (a row of ProductItemSummarySlot — cards / boosters / currency / a deck), and a CONFIRM
// PURCHASE button (+ booster-odds when the bundle includes a pack).
//
// We announce the header and a spoken "Contains …" summary on entry, then present the real action
// buttons for Enter; Escape closes the overlay. Purchase routes through the game's own
// OnClickPurchase (the button's wired onClick) — never a parallel purchase call. A completed
// purchase tears the overlay down and chains into the reward reveal (ItemGrantScreen), already
// handled. Hooking OnDeActivate (the common teardown for Escape / X / post-purchase) pops us.
internal sealed class ShopProductOverlayScreen : IScreenHandler
{
    private static ShopProductOverlayScreen? _instance;

    private readonly ScreenRouter _router;
    private readonly KeyboardNavigator _nav;
    private ShopProductOverlay? _overlay;
    private bool _active;
    private Button? _boosterOddsButton;

    public string Name => "Product";

    public ShopProductOverlayScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
        _nav = new KeyboardNavigator(router.Speech) { AllowTab = false };
        _nav.OnBack = Close;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ShopProductOverlayScreen(router);

        var activate = AccessTools.Method(typeof(ShopProductOverlay), "OnActivate", System.Type.EmptyTypes);
        if (activate == null)
        {
            Log.Error("ShopProductOverlayScreen: could not resolve ShopProductOverlay.OnActivate");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ShopProductOverlayScreen), nameof(Activate_Postfix))));

        // No OnDeActivate hook: ShopProductOverlay doesn't override OnDeActivate, so patching it
        // would instrument the SHARED OverlayScreenGroup base method and fire (with a bad cast)
        // for every other overlay's close. Dismissal is handled by the Tick backstop below
        // (the overlay GameObject goes inactive on close).

        Log.Info("ShopProductOverlayScreen installed");
    }

    private static void Activate_Postfix(ShopProductOverlay __instance)
    {
        if (_instance == null) return;
        _instance._overlay = __instance;
        if (!_instance._active) { _instance._active = true; _instance._router.Activate(_instance); }
        else _instance.Rebuild(announce: true);
    }

    public void OnEnter() => Rebuild(announce: true);
    public void OnResume() => Rebuild(announce: true);
    public void OnExit() { }
    public void RespeakAll() => Rebuild(announce: true);

    public string StateSignature() => _overlay == null ? "Product|dead" : $"Product|{_nav.Index}|{_nav.Count}";

    public void Tick()
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy)
        {
            _active = false; _router.Deactivate(this); _overlay = null;
            return;
        }
        if (!Application.isFocused) return;
        _nav.Tick();
    }

    private void Close()
    {
        if (_overlay != null) _overlay.Deactivate();
    }

    private void Rebuild(bool announce)
    {
        if (_overlay == null || !_overlay.gameObject.activeInHierarchy) return;

        var buttons = ActionButtons();
        _nav.SetItems(buttons, OverlayLabel);
        _nav.AllowHorizontal = true;
        _nav.AllowVertical = true;
        if (!announce) return;

        var header = HeaderLabel();
        _router.Speech.Say(string.IsNullOrWhiteSpace(header) ? Name : header!, interrupt: true);

        var contents = ContentsSummary();
        if (!string.IsNullOrWhiteSpace(contents)) _router.Speech.Say(contents!, interrupt: false);

        if (buttons.Count > 0) _nav.FocusFirst(announce: true, interruptPrior: false);
        Log.Info($"ShopProductOverlay: {header} | {contents} | {buttons.Count} buttons");
    }

    // CONFIRM PURCHASE first (the primary action), then booster-odds when present. The content-
    // item slots are clickable (card preview) but deliberately not in the action nav — their
    // contents are spoken in the summary instead.
    private List<Selectable> ActionButtons()
    {
        var result = new List<Selectable>();
        _boosterOddsButton = null;
        if (_overlay == null) return result;

        var header = _overlay.productHeader;
        var buy = header != null ? header.purchaseButton : null;
        if (buy != null && buy.isActiveAndEnabled) result.Add(buy);

        if (_overlay.boosterButton != null && _overlay.boosterButton.activeInHierarchy)
        {
            var odds = _overlay.boosterButton.GetComponent<Button>() ?? _overlay.boosterButton.GetComponentInChildren<Button>(includeInactive: false);
            if (odds != null && odds.isActiveAndEnabled) { _boosterOddsButton = odds; result.Add(odds); }
        }
        return result;
    }

    private string OverlayLabel(Selectable s)
    {
        if (_boosterOddsButton != null && s == _boosterOddsButton) return "Booster pack odds";
        return KeyboardNavigator.DefaultLabel(s);
    }

    private string? HeaderLabel()
    {
        var header = _overlay != null ? _overlay.productHeader : null;
        return header != null ? ShopLabels.Product(header) : null;
    }

    // "Contains <item>, <item> …" read from the spawned ProductItemSummarySlots — cards by name,
    // currencies by name, everything else by its content loc / item type.
    private string ContentsSummary()
    {
        if (_overlay == null || _overlay.productItemHolder == null) return "";
        var slots = _overlay.productItemHolder.GetComponentsInChildren<ProductItemSummarySlot>(includeInactive: false);
        if (slots == null || slots.Length == 0) return "";

        var parts = new List<string>();
        foreach (var slot in slots)
        {
            var item = ReadFieldObj(slot, "currentItem") is ItemData d ? d : default;
            if (item.itemType == ItemData.ItemType.None) continue;
            int n = ReadFieldObj(slot, "numberOfItems") is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
            var name = ItemName(item);
            if (string.IsNullOrWhiteSpace(name)) continue;
            parts.Add(n > 1 ? $"{name} times {n}" : name);
        }
        return parts.Count > 0 ? "Contains " + string.Join(", ", parts) : "";
    }

    private static string ItemName(ItemData item)
    {
        if (item.itemType == ItemData.ItemType.Card && !string.IsNullOrEmpty(item.clientId))
        {
            try
            {
                var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(item.clientId);
                if (row != null) return CardText.NameWithType(row);
            }
            catch { }
        }

        switch (item.itemType)
        {
            case ItemData.ItemType.HardCurrency: return ShopLabels.CurrencyWord(CurrencyType.HardCurrency);
            case ItemData.ItemType.TraderCurrency: return ShopLabels.CurrencyWord(CurrencyType.TradeCurrency);
            case ItemData.ItemType.TokenCurrency: return ShopLabels.CurrencyWord(CurrencyType.TokenCurrency);
            case ItemData.ItemType.SoftCurrency: return ShopLabels.CurrencyWord(CurrencyType.SoftCurrency);
        }

        var loc = Loc(item.contentLocId);
        if (loc != null) return loc;

        return item.itemType switch
        {
            ItemData.ItemType.Booster => "booster pack",
            ItemData.ItemType.Coin => "coin",
            ItemData.ItemType.Sleeve => "card sleeve",
            ItemData.ItemType.DeckBox => "deck box",
            ItemData.ItemType.Deck => "deck",
            ItemData.ItemType.Avatar => "avatar item",
            ItemData.ItemType.BuildBattleBox => "Build and Battle box",
            ItemData.ItemType.Feature => "feature",
            _ => "item",
        };
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) return null;
        return s;
    }

    private static object? ReadFieldObj(object instance, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }
}
