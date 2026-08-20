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

    // Icon-only controls named by the game method their onClick is wired to in the prefab. The
    // GameObject name of an icon button is a prefab detail that changes between art passes; the
    // method its UnityEvent calls is real game code we've read, so it's the stabler anchor — and it
    // says exactly what the button does. Only consulted when the control carries no worded text of
    // its own (KeyboardNavigator.ResolveBaseLabel), so a labelled button always keeps the game's own
    // localized text.
    //
    // Each entry pairs the game's OWN localization id for that control with an English fallback, so
    // the spoken label follows the client language rather than being baked in ([[feedback-no-hardcoded-text]]).
    // ONLY methods the prefab wires PERSISTENTLY belong here — GetPersistentMethodName sees prefab
    // (inspector) onClick entries, never runtime AddListener ones. The DECKS-panel pencil is wired at
    // runtime (DeckPanel_L.OnEnable → AddListener(OpenDeckRenameMenu)), so it is invisible to this map
    // and is labelled by identity in HUBDeckSelectScreen instead.
    private static readonly Dictionary<string, (string loc, string english)> Actions = new()
    {
        // The ✎ pencil beside the deck name in the deck EDITOR (prefab-wired to Settings_BeginNameChange).
        { "Settings_BeginNameChange", ("ui_deck_options_rename", "Rename deck") },
        // The ⋮ that opens the deck-options menu from inside the editor.
        { "OpenLandscapeDeckOptions", ("deck_options", "Deck options") },
    };

    public static string? ByAction(Selectable s)
    {
        if (s is not Button b) return null;
        try
        {
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                if (Actions.TryGetValue(b.onClick.GetPersistentMethodName(i) ?? "", out var a)) return Localize(a.loc, a.english);
        }
        catch { }
        return null;
    }

    // Resolve a game loc id, falling back to English when the id is missing from the loaded table
    // (GetString hands back a "(Missing)…" marker for an unknown key, which must not be spoken).
    public static string Localize(string locId, string english)
    {
        try
        {
            var s = Hooks.LocalizationHook.Get(locId);
            if (!string.IsNullOrWhiteSpace(s) && !s!.Contains("(Missing)")) return s!;
        }
        catch { }
        return english;
    }

    // Is this button wired, in the prefab, to a particular game method? The stable way to identify an
    // icon-only control: its GameObject name is art, the method it calls is game code we've read.
    public static bool HasHandler(Button? b, string method)
    {
        if (b == null) return false;
        try
        {
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                if (b.onClick.GetPersistentMethodName(i) == method) return true;
        }
        catch { }
        return false;
    }

    // The persistent (prefab-wired) onClick method names of a button, for diagnostics. An icon-only
    // control tells us nothing by its GameObject name; this says what it actually does.
    public static string Handlers(Button? b)
    {
        if (b == null) return "";
        try
        {
            int n = b.onClick.GetPersistentEventCount();
            if (n == 0) return "";
            var parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = b.onClick.GetPersistentMethodName(i) ?? "?";
            return string.Join("+", parts);
        }
        catch { return ""; }
    }

    // HUB top-strip tab label with the game's selected state appended ("HOME, selected"). The
    // selected tab is the one whose PersistentNavButtonGroup animator is in its "On" state — the
    // same signal the game uses to highlight the current tab. Other strips already say
    // selected/not-selected for their toggles; this gives the top strip parity.
    public static string TopNavLabel(Selectable s)
    {
        try
        {
            var grp = s.GetComponentInParent<PersistentNavButtonGroup>();
            // The current tab's button is disabled by the game so it can't be re-pressed — that's
            // "selected", not "unavailable", so build from the base name rather than DefaultLabel.
            if (grp != null && grp.IsOn()) return $"{Navigation.KeyboardNavigator.ResolveBaseLabel(s)}, selected";
        }
        catch { }
        return Navigation.KeyboardNavigator.DefaultLabel(s);
    }

    // Rich label for composite widgets. Returns null when not applicable (caller falls back to
    // text-scraping / humanized name).
    public static string? Describe(Selectable s)
    {
        if (s == null) return null;

        // HOME rank hex (Btn_Rank): its only text child is the bare rank-points number, so name it with
        // the full ranked status — league, rank tier, points, and points-to-next — read from the live
        // season config. Falls through to the "Rank" friendly name (below) until the season data loads.
        if (s.gameObject.name == "Btn_Rank")
        {
            var rank = RankReader.Status();
            if (!string.IsNullOrEmpty(rank)) return rank;
        }

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

        // Format toggle (ChangeGameModeButton — DECKS header, and inside the deck editor since the
        // Milestone3 update): its only text child is the bare format name ("Standard"), which alone
        // doesn't say what pressing it does. Say the action, then the game's own localized format.
        if (s.GetComponentInParent<ChangeGameModeButton>() != null)
        {
            var current = FirstLetterText(s);
            return string.IsNullOrEmpty(current) ? "Change format" : $"Change format, {current}";
        }

        return null;
    }

    // The first letter-bearing text under the control (skips icon glyphs / bare numbers).
    private static string? FirstLetterText(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
            if (HasLetter(t.text)) return t.text.Trim();
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
