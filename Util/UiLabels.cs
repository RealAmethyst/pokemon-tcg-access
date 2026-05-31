using System.Collections.Generic;
using System.Reflection;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// Game-specific spoken labels for controls the generic text-scrape can't name well.
//
// Two layers:
//   • Friendly(goName) — a name map for icon/badge-only buttons (their only text is an icon or a
//     notification badge), keyed by the prefab GameObject name (stable across locales).
//   • Describe(selectable) — composite widgets whose meaning is more than one text child (a shop
//     product tile = name + price). We read the OWNING controller, not the button's own text.
//
// KeyboardNavigator.ResolveBaseLabel consults Describe first, then Friendly, then text scraping.
internal static class UiLabels
{
    private static readonly Dictionary<string, string> Names = new()
    {
        { "ExpansionChallengeButton", "Battle Pass" },  // HOME shortcut: only text child is a "1" reward badge
        { "QuestBtn", "Quests" },                        // Battle Pass: the daily-quest / collect button
        { "SettingsButton", "Menu" },                    // top bar "⋮" — opens the overflow menu
        { "Btn_Rank", "Rank" },                          // HOME: rank hex, text child is the points count
        { "Btn_ProductDetailsPurchase", "Confirm purchase" },  // shop product-details overlay buy button
    };

    public static string? Friendly(string? goName)
        => goName != null && Names.TryGetValue(goName, out var n) ? n : null;

    // HUB top-strip tab label with the game's selected state appended ("HOME, selected"). The
    // selected tab is the one whose PersistentNavButtonGroup animator is in its "On" state — the
    // same signal the game uses to highlight the current tab. Other strips already say
    // selected/not-selected for their toggles; this gives the top strip parity.
    public static string TopNavLabel(Selectable s)
    {
        var label = Navigation.KeyboardNavigator.DefaultLabel(s);
        try
        {
            var grp = s.GetComponentInParent<PersistentNavButtonGroup>();
            if (grp != null && grp.IsOn()) return $"{label}, selected";
        }
        catch { }
        return label;
    }

    // Rich label for composite widgets. Returns null when not applicable (caller falls back to
    // text-scraping / humanized name).
    public static string? Describe(Selectable s)
    {
        if (s == null) return null;

        // Shop product tile (booster pack, bundle, collection, …) → "name, price, N available".
        // The focused control sits INSIDE the tile (its own text is just a price/icon), so we read
        // the owning ShopProductController. See ShopLabels.
        var product = s.GetComponentInParent<ShopProductController>();
        if (product != null) return ShopLabels.Product(product);

        // Shop expansion tile (selects a set to shop; shows collection progress as "11/122") →
        // "Expansion, 11 of 122 cards collected". The button lives inside an ExpansionSelection;
        // owned/max are private ints behind that "11/122" text.
        var expansion = s.GetComponentInParent<ExpansionSelection>();
        if (expansion != null)
        {
            int owned = ReadInt(expansion, "ownedCollectionCount");
            int max = ReadInt(expansion, "maxCollectionCount");
            var name = ShopLabels.ExpansionName(expansion);
            var head = string.IsNullOrEmpty(name) ? "Expansion" : name!;
            return (max > 0) ? $"{head}, {owned} of {max} cards collected" : head;
        }

        // Shop collection-header tooltip "(i)" button (icon-only, on RECOMMENDED / NEW ARRIVALS):
        // it sits under the ShopProductCollection but outside any product tile and has no worded
        // text, so name it by its section instead of reading a bare "info".
        var collection = s.GetComponentInParent<ShopProductCollection>();
        if (collection != null && s.GetComponentInParent<ShopProductController>() == null && !HasLetterTextChild(s))
            return ShopLabels.CollectionTitle(collection) + ", information";

        // Icon-only control in the expansion view — chiefly the drawer button that opens the set
        // picker. Text-bearing controls there (series-filter buttons) keep their own label.
        if (s.GetComponentInParent<ShopPageExpansionsScreen>() != null && !HasLetterTextChild(s))
            return "Choose expansion";

        return null;
    }

    private static bool HasLetterTextChild(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
            if (HasLetter(t.text)) return true;
        return false;
    }

    private static bool HasLetter(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s!) if (char.IsLetter(c)) return true;
        return false;
    }

    private static int ReadInt(object o, string field)
    {
        var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(o) is int n ? n : -1;
    }
}
