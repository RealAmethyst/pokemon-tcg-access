using System.Collections.Generic;
using System.Text.RegularExpressions;
using CardDatabase.DataAccess;
using TPCI.Localization;

namespace PokemonTCGLiveAccessibility.Util;

// Formats a CardDataRow — the printed card data, resolved out of battle via
// CardDatabaseManager.TryGetCardFromDatabase(clientId) — into a spoken name + detail lines.
// This is the out-of-battle counterpart to BattleBoardReader, which reads the SAME data off a
// battle Card3D (c.cardDataRow). Used by the reward / card-reveal handler so a card shown in a
// pack/grant reveal reads exactly like one inspected in a match.
internal static class CardText
{
    // "name, type" — e.g. "Carnivine, Basic Pokémon", "Ultra Ball, Trainer", "Fire Energy, Energy".
    public static string NameWithType(CardDataRow row)
    {
        if (row == null) return "card";
        var name = row.LocalizedCardName;
        if (string.IsNullOrEmpty(name)) name = "card";
        var type = TypeLabel(row);
        return type.Length > 0 ? $"{name}, {type}" : name;
    }

    // The card's rarity name (e.g. "Common", "Double Rare", "Reverse Holo Rare") from its loc code.
    // Ownership and deck legality are per-rarity (a card's variants are distinct CardIDs with separate
    // owned counts), so the deck editor speaks this after the type so a player can tell which printing
    // they're on. Null when the card has no rarity / the loc key is missing (we never speak a raw code).
    //
    // Rarity names live under the same "tcg_" loc family the Card Dex uses for set/series names
    // (CardDexSeriesSelector: SetTextByID("tcg_" + seriesId)). The key is "tcg_rarity_" + the card's
    // Loc Rarity Code — the bare code ("rr", "r_ph", "2r-c") is NOT a loc key on its own (that was the
    // "version 1, version 2…" bug). Some data may store the full id, so we try it raw as a fallback.
    public static string? RarityName(ICardDataRow? row)
    {
        try
        {
            var code = row?.Rarity?.LocRarityCode;
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code!.Trim();
            var keys = code.StartsWith("tcg_")
                ? new[] { code }
                : new[] { "tcg_rarity_" + code.ToLowerInvariant(), code };
            foreach (var k in keys)
            {
                var s = LocalizationManager.Instance?.GetString(k);
                if (string.IsNullOrWhiteSpace(s) || s!.Contains("(Missing)")) continue;
                var clean = StripFormatting(s);
                if (clean.Length > 0) return clean;
            }
        }
        catch { }
        return null;
    }

    // Some loc strings are format/decorated, e.g. the rarity name "{0} Common" (the {0} is the game's
    // rarity-symbol sprite slot, filled at render time). Drop format placeholders and any rich-text/
    // sprite tags and collapse whitespace so speech says the words only ("Common", not "{0} Common").
    private static string StripFormatting(string s)
    {
        s = Regex.Replace(s, @"\{\d+\}", " ");   // {0}, {1}, …
        s = Regex.Replace(s, @"<[^>]+>", " ");   // <sprite=…>, <color=…>, etc.
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // The card's expansion/set name — what tells apart two printings that share a rarity AND treatment
    // but come from different sets (the same card reprinted). Friendly localized name via the same "tcg_"
    // loc family the Card Dex uses for series/set names ("tcg_" + set code, e.g. tcg_swsh1). Falls back
    // to the raw set code the game itself prints on the card stub (CardStubParts: CardSetID + number),
    // which is still a fine disambiguator. Null when the card has no set id.
    public static string? SetName(ICardDataRow? row)
    {
        try
        {
            var code = row?.CardSetID;
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code!.Trim();
            var s = LocalizationManager.Instance?.GetString("tcg_" + code.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(s) && !s!.Contains("(Missing)"))
            {
                var clean = StripFormatting(s!);
                if (clean.Length > 0) return clean;
            }
            return code;   // raw set code (e.g. "MEG") — what the game shows on the stub
        }
        catch { }
        return null;
    }

    // The card's foil / print TREATMENT — what tells same-rarity printings of a card apart (e.g. two
    // "Common" Bulbasaur = normal vs reverse holo). Read from the variant flags (CardDataRow.CardVariant
    // = the "variant" column). Supplied in English: there's no loc key for these treatments, same as the
    // game-fundamental wording already in this file. Null for a standard (non-foil-variant) printing.
    public static string? VariantName(ICardDataRow? row)
    {
        if (row == null) return null;
        try
        {
            var v = row.CardVariant;
            if (v.HasFlag(CardVariant.MasterBallParallelHolo)) return "Master Ball pattern";
            if (v.HasFlag(CardVariant.PokeBallParallelHolo)) return "Poké Ball pattern";
            if (v.HasFlag(CardVariant.ParallelHolo)) return "reverse holo";
            if (v.HasFlag(CardVariant.Alternate)) return "alternate art";
        }
        catch { }
        return null;
    }

    // "Common" / "Common, reverse holo" / "Illustration Rare, Poké Ball pattern" — rarity plus the
    // print treatment, so two printings that share a rarity name are still distinguishable. Falls back
    // to the treatment alone when the rarity loc is missing; null when neither resolves.
    public static string? RarityWithVariant(ICardDataRow? row)
    {
        var rarity = RarityName(row);
        var variant = VariantName(row);
        if (string.IsNullOrEmpty(rarity)) return variant;
        return string.IsNullOrEmpty(variant) ? rarity : $"{rarity}, {variant}";
    }

    // "Charizard ex, Stage 2 Pokémon, Double Rare" — name+type plus rarity/variant. Used where a card is
    // revealed (pack openings, reward grants) so the player hears exactly which printing they pulled.
    public static string NameTypeRarity(CardDataRow row)
    {
        var s = NameWithType(row);
        var rv = RarityWithVariant(row);
        return string.IsNullOrEmpty(rv) ? s : $"{s}, {rv}";
    }

    // Lines walked with Down / Up after the name: Pokémon stats first, then each attack / ability
    // (localized name + energy cost + damage + rules text). Mirrors BattleBoardReader.DetailLines.
    public static List<string> DetailLines(CardDataRow row)
    {
        var lines = new List<string>();
        if (row == null) return lines;

        if (row.IsPokemonCard())
        {
            lines.Add($"{row.HP} hit points");
            string types = string.IsNullOrEmpty(row.CardTyping) ? "none" : row.CardTyping;
            string weak = string.IsNullOrEmpty(row.WeaknessType) ? "none" : $"{row.WeaknessType} times {row.WeaknessAmount}";
            string resist = string.IsNullOrEmpty(row.ResistanceType) ? "none" : $"{row.ResistanceType} minus {row.ResistanceAmount}";
            lines.Add($"Type {types}. Weakness {weak}. Resistance {resist}. Retreat cost {row.RetreatCost}");
        }

        lines.AddRange(SpecialRuleLines(row));

        foreach (var a in row.GetActionInfos())
        {
            if (a == null) continue;
            var head = new List<string>();
            if (!string.IsNullOrEmpty(a.LocalizedName)) head.Add(a.LocalizedName);
            var cost = EnergyCostWords(a.Cost);
            if (cost.Length > 0) head.Add(cost);
            if (!string.IsNullOrEmpty(a.Damage)) head.Add($"{a.Damage} damage");
            var line = string.Join(", ", head);
            if (!string.IsNullOrEmpty(a.LocalizedDescription))
                line = line.Length > 0 ? $"{line}. {a.LocalizedDescription}" : a.LocalizedDescription;
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    // The card's special-rule box — the printed rule a sighted player reads off the bottom of the card
    // but which lives only on the card IMAGE, not in any loc table, so we supply it (game-fundamental
    // text, like "Basic Pokémon" / "Retreat cost" already are). Detected from the card's format flags:
    //   • ACE SPEC — the deckbuilding limit (one per deck across the whole ACE SPEC pool).
    //   • Rule-box Pokémon (ex / EX / Mega Evolution ex / V / VSTAR / GX → 2 prizes; VMAX / Tag Team /
    //     V-UNION → 3) — the opponent takes extra Prize cards when it's Knocked Out.
    public static List<string> SpecialRuleLines(ICardDataRow? row)
    {
        var lines = new List<string>();
        if (row == null) return lines;
        try
        {
            var ff = ManagerSingleton<CardFormatManager>.instance?.FormatFlagsProvider;
            if (ff == null) return lines;
            var flags = row.CardFormatFlags;

            if (flags.HasFlag(ff.ACESPEC))
                lines.Add("ACE SPEC. You can have only 1 ACE SPEC card in your deck.");

            int prizes = flags.HasFlag(ff.VMAX) || flags.HasFlag(ff.TagTeam) || flags.HasFlag(ff.VUNION) ? 3
                       : flags.HasFlag(ff.ex) || flags.HasFlag(ff.EX) || flags.HasFlag(ff.MegaEvolutionEx)
                         || flags.HasFlag(ff.V) || flags.HasFlag(ff.VSTAR) || flags.HasFlag(ff.GX) ? 2 : 1;
            if (prizes >= 2)
                lines.Add($"When this Pokémon is Knocked Out, your opponent takes {prizes} Prize cards.");
        }
        catch { }
        return lines;
    }

    private static string TypeLabel(CardDataRow row)
    {
        if (row.IsPokemonCard())
        {
            string stage = row.CardStage.ToString() switch
            {
                "Basic" => "Basic ",
                "Stage1" => "Stage 1 ",
                "Stage2" => "Stage 2 ",
                _ => "",
            };
            return stage + "Pokémon";
        }
        return row.Category switch
        {
            CardCategory.Trainer => "Trainer",
            CardCategory.Energy => "Energy",
            _ => "",
        };
    }

    // Translate an energy-cost letter string (e.g. "GGC") into words ("cost 2 Grass, 1 Colorless").
    private static string EnergyCostWords(string? cost)
    {
        if (string.IsNullOrEmpty(cost)) return "";
        const string letters = "RGWLPFDMYNC";
        var counts = new Dictionary<char, int>();
        foreach (var ch in cost!)
            if (letters.IndexOf(ch) >= 0) counts[ch] = counts.TryGetValue(ch, out var n) ? n + 1 : 1;
        if (counts.Count == 0) return "";
        var parts = new List<string>();
        foreach (var kv in counts) parts.Add($"{kv.Value} {EnergyName(kv.Key)}");
        return "cost " + string.Join(", ", parts);
    }

    private static string EnergyName(char c) => c switch
    {
        'R' => "Fire", 'G' => "Grass", 'W' => "Water", 'L' => "Lightning", 'P' => "Psychic",
        'F' => "Fighting", 'D' => "Darkness", 'M' => "Metal", 'Y' => "Fairy", 'N' => "Dragon",
        'C' => "Colorless", _ => "energy",
    };
}
