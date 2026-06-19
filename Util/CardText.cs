using System.Collections.Generic;
using CardDatabase.DataAccess;

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
