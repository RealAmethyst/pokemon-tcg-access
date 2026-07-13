using System.Collections.Generic;
using CardDatabase.DataAccess;
using TPCI.Localization;

namespace PokemonTCGLiveAccessibility.Util;

// Shared text helpers for the CARD DEX screen + its overlays: localized set/series names and the
// one-line label for a card stack (name + type, owned count, new flag, collector number). The card
// label mirrors the deck editor's proven CardLabel (HUBEditDeckScreen) — ownership is read per the
// exact displayed CardID, never the archetype-wide total (see [[project-card-rarity-variants]]).
internal static class CardDexText
{
    public static string? Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var s = LocalizationManager.Instance?.GetString(id);
        if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) return null;
        return s.Trim();
    }

    // "<name>, <type>, N owned / not owned[, new][, number 001]" for one collection tile.
    public static string CardStackLabel(ArchetypeDBCards stack)
    {
        ICardDataRow? card = null;
        try { card = stack.GetPreferredDisplayCard(); } catch { }

        CardDataRow? row = null;
        try { if (card != null) row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID); }
        catch { }

        string name;
        try { name = row != null ? CardText.NameWithType(row) : stack.LocalizedCardName(); }
        catch { name = card?.CardID ?? "Card"; }

        var parts = new List<string> { name };

        int owned = 0;
        try { if (card != null) owned = stack.TotalOwnedQuantityForSpecificCard(card.CardID); } catch { }
        parts.Add(owned > 0 ? $"{owned} owned" : "not owned");

        try { if (stack.NewFlagStatus()) parts.Add("new"); } catch { }

        try
        {
            var num = card?.DisplayCardNumber;
            if (!string.IsNullOrWhiteSpace(num)) parts.Add($"number {num}");
        }
        catch { }

        return string.Join(", ", parts);
    }
}
