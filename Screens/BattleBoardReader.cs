using System.Collections.Generic;
using System.Linq;
using CardDatabase.DataAccess;
using MatchLogic;
using RainierClientSDK;
using TPCI.Rainier.Match.Cards;

namespace PokemonTCGLiveAccessibility.Screens;

// Read-only view of the battle board for the screen-reader layer. Enumerates each zone and
// formats a card's spoken name + detail lines. Everything here is identity-gated on
// Card3D.IsPublic so we never voice a card a sighted player couldn't see; hidden piles report a
// count only (from the logical layer) and a generic "face-down card".
//
// Zone spine: MatchManager.GetOwnerForBoardPos(Board.GetBoardPos(playerPos, playerID)) resolves
// the right PlayerCardOwner for either player (the isPlayer1 flip is handled inside the game), and
// GetOwnedCards() gives its Card3D list. Counts for face-down piles come from the logical board
// (NetworkMatchController.currentMatchInfo.GetMatchEntities) because the deck/prize render as a
// short visual stack, not one Card3D per card.
internal static class BattleBoardReader
{
    internal enum Zone
    {
        MyActive, MyBench, MyHand, MyDiscard, MyDeck, MyPrize, MyLostZone,
        OppActive, OppBench, OppHand, OppDiscard, OppDeck, OppPrize, Stadium,
    }

    // Piles whose card identities are hidden (or that render as a stack) — report a count only.
    internal static bool IsCountOnly(Zone z) =>
        z is Zone.MyDeck or Zone.OppDeck or Zone.MyPrize or Zone.OppPrize or Zone.OppHand;

    internal static string ZoneName(Zone z) => z switch
    {
        Zone.MyActive => "your active",
        Zone.MyBench => "your bench",
        Zone.MyHand => "your hand",
        Zone.MyDiscard => "your discard pile",
        Zone.MyDeck => "your deck",
        Zone.MyPrize => "your prizes",
        Zone.MyLostZone => "your lost zone",
        Zone.OppActive => "opponent's active",
        Zone.OppBench => "opponent's bench",
        Zone.OppHand => "opponent's hand",
        Zone.OppDiscard => "opponent's discard pile",
        Zone.OppDeck => "opponent's deck",
        Zone.OppPrize => "opponent's prizes",
        Zone.Stadium => "stadium",
        _ => "zone",
    };

    private static (PlayerPos pos, PlayerID pid) Map(Zone z) => z switch
    {
        Zone.MyActive => (PlayerPos.ACTIVE, PlayerID.LOCAL),
        Zone.MyBench => (PlayerPos.BENCH, PlayerID.LOCAL),
        Zone.MyHand => (PlayerPos.HAND, PlayerID.LOCAL),
        Zone.MyDiscard => (PlayerPos.DISCARD, PlayerID.LOCAL),
        Zone.MyDeck => (PlayerPos.DECK, PlayerID.LOCAL),
        Zone.MyPrize => (PlayerPos.PRIZE, PlayerID.LOCAL),
        Zone.MyLostZone => (PlayerPos.LOSTZONE, PlayerID.LOCAL),
        Zone.OppActive => (PlayerPos.ACTIVE, PlayerID.OPPONENT),
        Zone.OppBench => (PlayerPos.BENCH, PlayerID.OPPONENT),
        Zone.OppHand => (PlayerPos.HAND, PlayerID.OPPONENT),
        Zone.OppDiscard => (PlayerPos.DISCARD, PlayerID.OPPONENT),
        Zone.OppDeck => (PlayerPos.DECK, PlayerID.OPPONENT),
        Zone.OppPrize => (PlayerPos.PRIZE, PlayerID.OPPONENT),
        Zone.Stadium => (PlayerPos.STADIUM, PlayerID.LOCAL),
        _ => (PlayerPos.NONE, PlayerID.LOCAL),
    };

    // Browsable card list for a zone (active/bench/hand/discard/lost zone/stadium). Empty if none.
    internal static List<Card3D> GetCards(MatchManager mgr, Zone z)
    {
        var (pp, pid) = Map(z);
        var owner = mgr.GetOwnerForBoardPos(Board.GetBoardPos(pp, pid));
        if (owner == null) return new List<Card3D>();
        // The local hand STACKS identical cards (same cardDataRow.ArchetypeID) into one HandCardSlot,
        // shown as a single pile with a count badge — and only the slot's TOP card is exposed for play
        // (GetCardsAccessibleForTargeting = slot top-cards, what the placement evaluator keys on). So we
        // browse the hand by SLOT (one entry per pile), matching exactly what a sighted player sees and
        // can click — instead of GetOwnedCards (every individual card), which listed buried duplicates
        // as separate, un-targetable entries. HandStackCount adds the "N copies" to the spoken label.
        if (z == Zone.MyHand && owner is HandController hand)
            return hand.GetCardsAccessibleForTargeting().Where(c => c != null).ToList();
        return owner.GetOwnedCards().Where(c => c != null).ToList();
    }

    // EVERY card in the local hand (each copy, including duplicates stacked behind a pile's top card) —
    // for resolving a prompt option's card to read its lines, where the option may reference a buried
    // copy that GetCards(MyHand) (slot top-cards only) wouldn't return.
    internal static List<Card3D> GetHandCardsAll(MatchManager mgr)
    {
        if (mgr == null) return new List<Card3D>();
        var owner = mgr.GetOwnerForBoardPos(Board.GetBoardPos(PlayerPos.HAND, PlayerID.LOCAL));
        return owner == null ? new List<Card3D>() : owner.GetOwnedCards().Where(c => c != null).ToList();
    }

    // How many cards sit in the hand pile (HandCardSlot) this card belongs to — i.e. the count badge a
    // sighted player sees on a stack of duplicates. 1 when it's a lone card or not a hand card.
    internal static int HandStackCount(MatchManager mgr, Card3D card)
    {
        if (card == null || mgr == null) return 1;
        var owner = mgr.GetOwnerForBoardPos(Board.GetBoardPos(PlayerPos.HAND, PlayerID.LOCAL));
        if (owner is HandController hand)
            foreach (var slot in hand.HandSlots)
                if (slot != null && slot.DoesSlotContainCard(card))
                    return slot.CardsInSlot.Count();
        return 1;
    }

    // Logical count — safe for hidden piles (identities not revealed, just the number).
    internal static int GetCount(Zone z)
    {
        var (pp, pid) = Map(z);
        var ents = NetworkMatchController.currentMatchInfo?.GetMatchEntities(Board.GetBoardPos(pp, pid));
        return ents?.Count ?? 0;
    }

    // Spoken name — localized when the card is public, generic when face-down. Never leaks identity.
    internal static string CardName(Card3D c)
    {
        if (c == null) return "empty";
        if (!c.IsPublic) return "face-down card";
        var name = c.cardDataRow?.LocalizedCardName;
        if (string.IsNullOrEmpty(name)) name = c.cardInfo?.cardName;
        return string.IsNullOrEmpty(name) ? "card" : name!;
    }

    // One-line "Charmander, 80 of 80 hit points" style summary used for the status overview.
    internal static string ShortSummary(Card3D c)
    {
        if (c == null) return "none";
        var info = c.cardInfo;
        if (!c.IsPublic || info == null) return "face-down card";
        if (info.isPokemon)
        {
            int remaining = info.totalHP - info.damageCounters * 10;
            return $"{CardName(c)}, {remaining} of {info.totalHP} hit points";
        }
        return CardName(c);
    }

    // Detail lines walked with Down arrow after the name is spoken on focus — the full printed
    // card: stats for Pokémon, then every attack/ability/trainer-effect (localized name + cost +
    // damage + rules text), then the flavor/notes. Covers Pokémon, Energy, Trainer/Item/Supporter.
    internal static List<string> DetailLines(Card3D c)
    {
        var lines = new List<string>();
        if (c == null || !c.IsPublic) { lines.Add("Face-down card, no details"); return lines; }
        var info = c.cardInfo;
        var row = c.cardDataRow;

        if (info != null && info.isPokemon)
        {
            int remaining = info.totalHP - info.damageCounters * 10;
            lines.Add($"{remaining} of {info.totalHP} hit points");

            string types = info.cardTyping.Count > 0 ? string.Join(", ", info.cardTyping.Select(t => t.ToString())) : "none";
            string weak = info.weaknessTyping.Count > 0 ? string.Join(", ", info.weaknessTyping.Select(t => t.ToString())) + $" times {info.weaknessAmount}" : "none";
            string resist = info.resistanceTyping.Count > 0 ? string.Join(", ", info.resistanceTyping.Select(t => t.ToString())) + $" minus {info.resistanceAmount}" : "none";
            lines.Add($"Type {types}. Weakness {weak}. Resistance {resist}. Retreat cost {info.retreatCost}");

            int energy = info.attachedEnergy?.Count ?? 0;
            int tools = info.attachedTools?.Count ?? 0;
            string attach = energy == 0 ? "No energy attached" : $"{energy} energy attached";
            if (tools > 0) attach += $", {tools} tool" + (tools > 1 ? "s" : "");
            lines.Add(attach);
        }
        // Every attack / ability / trainer-effect, localized — name, cost, damage, then rules text.
        // The card type is announced with the NAME (CardNameWithType); the printed flavor/metadata
        // "notes" (archetypeID, regulation mark, release date, etc.) are skipped as noise.
        if (row != null)
        {
            foreach (var a in row.GetActionInfos())
            {
                if (a == null) continue;
                var head = new List<string>();
                if (!string.IsNullOrEmpty(a.LocalizedName)) head.Add(a.LocalizedName);
                string cost = EnergyCostWords(a.Cost);
                if (cost.Length > 0) head.Add(cost);
                if (!string.IsNullOrEmpty(a.Damage)) head.Add($"{a.Damage} damage");
                string line = string.Join(", ", head);
                if (!string.IsNullOrEmpty(a.LocalizedDescription))
                    line = line.Length > 0 ? $"{line}. {a.LocalizedDescription}" : a.LocalizedDescription;
                if (line.Length > 0) lines.Add(line);
            }
        }
        return lines;
    }

    // Cost / damage / rules-text for the action with this (localized) name on the card — so the
    // attack/ability menu can speak WHAT an action does after its name. Empty when there's no match
    // (e.g. Retreat, which isn't a card action). Matches by localized name (exact, then a contains
    // fallback to tolerate a leading type tag).
    internal static string ActionDetail(Card3D c, string actionName)
    {
        var row = c?.cardDataRow;
        if (row == null || string.IsNullOrEmpty(actionName)) return "";
        string want = actionName.Trim();
        CardDataRow.ActionInfo? match = null;
        foreach (var a in row.GetActionInfos())
        {
            if (a == null || string.IsNullOrEmpty(a.LocalizedName)) continue;
            string ln = a.LocalizedName.Trim();
            if (ln == want) { match = a; break; }
            if (match == null && (want.Contains(ln) || ln.Contains(want))) match = a;
        }
        if (match == null) return "";

        var head = new List<string>();
        string cost = EnergyCostWords(match.Cost);
        if (cost.Length > 0) head.Add(cost);
        if (!string.IsNullOrEmpty(match.Damage)) head.Add($"{match.Damage} damage");
        string stats = string.Join(", ", head);
        string desc = match.LocalizedDescription ?? "";
        if (stats.Length > 0 && desc.Length > 0) return $"{stats}. {desc}";
        return stats.Length > 0 ? stats : desc;
    }

    // "name, type" — e.g. "Charmander, Basic Pokémon", "Ultra Ball, Item", "Lillie's Determination, Supporter".
    internal static string CardNameWithType(Card3D c)
    {
        if (c == null) return "empty";
        if (!c.IsPublic) return "face-down card";
        var type = CardType(c);
        return type.Length > 0 ? $"{CardName(c)}, {type}" : CardName(c);
    }

    private static string CardType(Card3D c)
    {
        var info = c?.cardInfo;
        if (info == null) return "";
        if (info.isPokemon)
        {
            string stage = info.cardStage.ToString() switch
            {
                "Basic" => "Basic ",
                "Stage1" => "Stage 1 ",
                "Stage2" => "Stage 2 ",
                _ => "",
            };
            return stage + "Pokémon";
        }
        if (info.isEnergy) return "Energy";
        return info.cardFormat switch
        {
            CardFormat.TrainerItem => "Item",
            CardFormat.TrainerItemPokemon => "Item",
            CardFormat.TrainerSupporter => "Supporter",
            CardFormat.TrainerStadium => "Stadium",
            CardFormat.TrainerTool => "Pokémon Tool",
            _ => "Trainer",
        };
    }

    // Translate an energy-cost letter string (e.g. "RRC") into words ("cost 2 Fire, 1 Colorless").
    private static string EnergyCostWords(string? cost)
    {
        if (string.IsNullOrEmpty(cost)) return "";
        const string letters = "RGWLPFDMYNC";
        var counts = new Dictionary<char, int>();
        foreach (var ch in cost!)
            if (letters.IndexOf(ch) >= 0) counts[ch] = counts.TryGetValue(ch, out var n) ? n + 1 : 1;
        if (counts.Count == 0) return "";
        return "cost " + string.Join(", ", counts.Select(kv => $"{kv.Value} {EnergyName(kv.Key)}"));
    }

    private static string EnergyName(char c) => c switch
    {
        'R' => "Fire", 'G' => "Grass", 'W' => "Water", 'L' => "Lightning", 'P' => "Psychic",
        'F' => "Fighting", 'D' => "Darkness", 'M' => "Metal", 'Y' => "Fairy", 'N' => "Dragon",
        'C' => "Colorless", _ => "energy",
    };
}
