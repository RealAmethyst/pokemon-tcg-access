using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RainierClientSDK.source.Commerce;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// Spoken labels for shop product tiles and currency amounts. A shop tile (ShopProductController)
// is a composite widget — image + name + price + "AVAILABLE: N" + buy button — so the focused
// control's own text is just a price/icon. We read the OWNING controller and speak
// "name, price, N available", mapping the currency icon (a TMP sprite, not a word) to a spoken
// currency name via the game's CurrencyType.
//
// UiLabels.Describe delegates product labelling here, so both free-browsing nav groups and an
// FTUE-reparented product focus get the same rich label.
internal static class ShopLabels
{
    // Rich label for a shop product, given any Selectable inside its tile. Null when the
    // Selectable isn't part of a ShopProductController (caller falls back to the default label).
    public static string? ProductLabel(Selectable s)
    {
        if (s == null) return null;
        var product = s.GetComponentInParent<ShopProductController>();
        return product != null ? Product(product) : null;
    }

    public static string Product(ShopProductController product)
    {
        var parts = new List<string>(3);
        var name = ProductName(product);
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name!);
        var price = ProductPrice(product);
        if (!string.IsNullOrWhiteSpace(price)) parts.Add(price!);
        var avail = ProductAvailability(product);
        if (!string.IsNullOrWhiteSpace(avail)) parts.Add(avail);
        return parts.Count > 0 ? string.Join(", ", parts) : "Product";
    }

    // Localized name for a shop expansion tile (e.g. "Mega Evolution Chaos Rising"). The on-screen
    // name is only a logo asset, but the game locs every set/series name under the key "tcg_<setCode>"
    // (the Card Dex resolves series headers the same way: SetTextByID("tcg_" + seriesId)). So we loc
    // the expansionId under that key — no hardcoded map. Returns null if it doesn't resolve (caller
    // then speaks the collection count alone).
    public static string? ExpansionName(ExpansionSelection expansion)
    {
        string id;
        try { id = expansion.expansionId; } catch { return null; }
        if (string.IsNullOrEmpty(id)) return null;
        var lower = id.ToLowerInvariant();
        foreach (var key in new[] { "tcg_" + lower, "tcg_" + id })
        {
            var loc = Loc(key);
            if (loc != null && HasLetter(loc) && !loc.Equals(id, System.StringComparison.OrdinalIgnoreCase))
                return loc;
        }
        return null;
    }

    // Collection / section header title ("RECOMMENDED", "NEW ARRIVALS"). The collection's
    // private titleText LocText carries the on-screen header; fall back to a generic word.
    public static string CollectionTitle(UnityEngine.Component collection)
    {
        var lt = ReadField<LocText>(collection, "titleText");
        var t = Clean(lt);
        return HasLetter(t) ? t : "Products";
    }

    // ---- product fields ----

    private static string? ProductName(ShopProductController product)
    {
        // The tile's own title is usually the localized product name; some templates (amount-
        // showing collections) blank it, so fall back to the ItemSet's name loc / id.
        var t = Clean(product.titleText);
        if (HasLetter(t)) return t;

        var id = SafeItemSetId(product);
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var db = ManagerSingleton<ItemSetDatabaseManager>.instance;
            if (db != null && db.ItemSets.TryGetValue(id, out var itemSet))
            {
                var byName = Loc(itemSet.nameLocId);
                if (byName != null) return byName;
            }
        }
        catch { }
        return Loc(id) ?? id;
    }

    private static string? ProductPrice(ShopProductController product)
    {
        var displayed = Clean(product.costTxt);
        if (string.IsNullOrEmpty(displayed)) return null;
        // Non-numeric → real-money IAP ("$4.99") or a status string ("MISSING"/"NO PRICE"): speak as-is.
        if (!LooksLikePlainNumber(displayed)) return displayed;
        // Plain number → a sprite-currency price; the currency itself is an icon, so name it
        // from the cached offering's price entry.
        var word = CurrencyWordForOffering(SafeItemSetId(product));
        return string.IsNullOrEmpty(word) ? displayed : $"{displayed} {word}";
    }

    private static string ProductAvailability(ShopProductController product)
    {
        try
        {
            var isr = ReadField<ItemSetReference>(product, "_itemSetReference");
            if (isr == null || isr.IsUnlimited()) return "";
            int n = isr.GetAvailableCount();
            return n <= 0 ? "sold out" : $"{n} available";
        }
        catch { return ""; }
    }

    // ---- currency ----

    private static string CurrencyWordForOffering(string itemSetId)
    {
        if (string.IsNullOrEmpty(itemSetId)) return "";
        try
        {
            if (!Commerce.TryGetCachedShopOffering(itemSetId, out var offering)) return "";
            var price = offering.PriceEntry;
            if (price == null || price.Count == 0) return "";
            var kv = price.First();
            // The game skips a Key price when a real currency price is also present.
            if (kv.Key == CurrencyType.Key && price.Count > 1) kv = price.ElementAt(1);
            return CurrencyWord(kv.Key);
        }
        catch { return ""; }
    }

    public static string CurrencyWord(CurrencyType type)
    {
        return type switch
        {
            CurrencyType.HardCurrency => Loc("currency_crystals") ?? "Crystals",
            CurrencyType.TradeCurrency => Loc("currency_credits") ?? "Credits",
            CurrencyType.TokenCurrency => Loc("currency_tokens") ?? "Tokens",
            CurrencyType.Key => Loc("currency_keys") ?? "Keys",
            CurrencyType.SoftCurrency => Loc("currency_coins") ?? "Coins",
            _ => "",
        };
    }

    // ---- text helpers ----

    private static string SafeItemSetId(ShopProductController p)
    {
        try { return p.GetItemSetID() ?? ""; } catch { return ""; }
    }

    private static string? Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) return null;
        return StripTags(s).Trim();
    }

    private static string Clean(LocText? t)
    {
        if (t == null) return "";
        var s = t.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? "" : StripTags(s!).Trim();
    }

    // True when the string is only digits and number formatting (comma / dot / spaces) — i.e. a
    // bare currency amount whose icon was stripped, not a real-money price or status word.
    private static bool LooksLikePlainNumber(string s)
    {
        bool digit = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) { digit = true; continue; }
            if (c == ',' || c == '.' || c == ' ' || c == ' ') continue;
            return false;
        }
        return digit;
    }

    private static bool HasLetter(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s!) if (char.IsLetter(c)) return true;
        return false;
    }

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
