using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TPCI.Localization;
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

    // Rich label for composite widgets. Currently: a shop product tile (ShopProductController) —
    // the FTUE focuses `product.GetComponentInChildren<Button>()`, so the focused control sits
    // INSIDE the product; its own text is just a price or icon ("Button"/"200"). Read the tile's
    // title + cost instead → "Chaos Rising Booster Pack, 200". Returns null when not applicable.
    public static string? Describe(Selectable s)
    {
        if (s == null) return null;

        // Shop product tile (booster pack, collection, …) → "name, price".
        var product = s.GetComponentInParent<ShopProductController>();
        if (product != null)
        {
            var name = Clean(product.titleText);
            var cost = Clean(product.costTxt);
            if (name.Length > 0 && cost.Length > 0) return $"{name}, {cost}";
            if (name.Length > 0) return name;
            if (cost.Length > 0) return cost;
        }

        // Shop expansion tile (selects a set to shop; shows collection progress as "11/122") →
        // "Expansion, 11 of 122 cards collected". The button lives inside an ExpansionSelection;
        // owned/max are private ints behind that "11/122" text.
        var expansion = s.GetComponentInParent<ExpansionSelection>();
        if (expansion != null)
        {
            int owned = ReadInt(expansion, "ownedCollectionCount");
            int max = ReadInt(expansion, "maxCollectionCount");
            return (max > 0) ? $"Expansion, {owned} of {max} cards collected" : "Expansion";
        }

        // Other controls in the expansion view — chiefly the drawer button that opens the set picker.
        if (s.GetComponentInParent<ShopPageExpansionsScreen>() != null)
            return "Choose expansion";

        return null;
    }

    private static int ReadInt(object o, string field)
    {
        var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(o) is int n ? n : -1;
    }

    private static string Clean(LocText? t)
    {
        if (t == null) return "";
        var s = t.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? "" : StripTags(s!).Trim();
    }

    // Strip TMP rich-text / sprite tags (<sprite=…>, <color>, …) so a price like "<sprite>200"
    // reads as "200" rather than leaking the markup.
    private static string StripTags(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}
