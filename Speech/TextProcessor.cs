using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PokemonTCGLiveAccessibility.Speech;

// Central text sanitisation for everything spoken. SpeechEngine.Say() runs every string
// through Process() before handing it to Prism, so callers can pass raw game text without
// worrying about debug markers or rich-text noise.
//
// PTCGL-specific things we handle:
//   - Energy / card-marker SYMBOLS. Card effect text, attack costs, stadium rules and prompt
//     questions carry the type as a TMP sprite — "your <sprite name=\"water\"> Pokémon", "discard a
//     <sprite name=\"fire\"> Energy" — or, before the icon pass runs, as a token ("{W}", "{EX}").
//     Stripped blind these read as "your Pokémon" / "discard a Energy", which is why a Water-only
//     stadium sounded like it helped any Pokémon and Lillie's Clefairy's text made no sense. We
//     translate the symbol to its spoken word ("Water", "Fire", "ex", "Mega") BEFORE stripping tags.
//     Names come from the game's own IconReplacer map (energySpriteNameMappings / cardPropertyIcons).
//   - "(Missing)<resourceId>" — LocalizationManager emits this when a key isn't in the loaded or
//     fallback loc tables.
//   - "(Fallback)" / "(<id>… )" / "(en)…" — debug-flag markers (displayStringIds / fallback locale).
//   - Remaining TMP / HTML-like rich-text tags: <color=…>, <b>, <size=…>, leftover <sprite=…>, etc.
internal static class TextProcessor
{
    private static readonly Regex MissingMarker = new(@"\(Missing\)<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex FallbackMarker = new(@"\(Fallback\)", RegexOptions.Compiled);
    private static readonly Regex DebugIdWrap = new(@"\(<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex LeadingLangTag = new(@"^\([a-z]{2,5}\)", RegexOptions.Compiled);
    private static readonly Regex RichText = new(@"<[^<>]+>", RegexOptions.Compiled);

    // A TMP sprite tag carrying its atlas name, e.g. <sprite name="water" tint=1> or <sprite="Atlas" name="ex">.
    private static readonly Regex SpriteByName = new(@"<sprite\b[^>]*?\bname=""(?<n>[^""]+)""[^>]*>", RegexOptions.Compiled);
    // Pre-icon-pass energy / marker tokens: {W}{R}{G}…, {EX}{GX}{V}{VMAX}{MEGA}{*}.
    private static readonly Regex SymbolToken = new(@"\{(EX|GX|VMAX|VSTAR|VUNION|MEGA|V|C|R|G|W|F|Y|D|L|N|M|P|\*)\}", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // The 11 energy-type sprite names (exact), from the game's IconReplacer.energySpriteNameMappings.
    private static readonly Dictionary<string, string> EnergyWords = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["colorless"] = "Colorless", ["fire"] = "Fire", ["grass"] = "Grass", ["water"] = "Water",
        ["fighting"] = "Fighting", ["fairy"] = "Fairy", ["darkness"] = "Darkness", ["lightning"] = "Lightning",
        ["dragon"] = "Dragon", ["metal"] = "Metal", ["psychic"] = "Psychic",
    };

    // {X} token form → spoken word (energy letters + the card-property markers).
    private static readonly Dictionary<string, string> TokenWords = new()
    {
        ["C"] = "Colorless", ["R"] = "Fire", ["G"] = "Grass", ["W"] = "Water", ["F"] = "Fighting",
        ["Y"] = "Fairy", ["D"] = "Darkness", ["L"] = "Lightning", ["N"] = "Dragon", ["M"] = "Metal",
        ["P"] = "Psychic", ["EX"] = "ex", ["GX"] = "GX", ["V"] = "V", ["VMAX"] = "V Max",
        ["VSTAR"] = "V Star", ["VUNION"] = "V Union", ["MEGA"] = "Mega", ["*"] = "Prism Star",
    };

    // Card-property MARKER sprite → spoken word. These ship with a long tail of positional + per-locale
    // variants (ex / ex_lower / ex_lower_mega / ex_lower_tera, vstar_atk_it, acespec_de, tag_team_atk_fr,
    // gx0..gx3, break_en_atk, …), so we prefix-match the family rather than list every one. The card NAME
    // text already carries the words ("Mega Lucario"), so the marker just confirms the suffix ("ex").
    // Order matters: the longer vMax/vStar/vUnion prefixes are tested before the bare "v".
    private static string? MarkerWord(string lower) => lower switch
    {
        _ when lower.StartsWith("gx") => "GX",
        _ when lower.StartsWith("ex") => "ex",                 // ex, ex_lower, ex_upper, ex_lower_mega/tera
        _ when lower.StartsWith("mega") => "Mega",
        _ when lower.StartsWith("vmax") => "V Max",
        _ when lower.StartsWith("vstar") => "V Star",
        _ when lower.StartsWith("vunion") => "V Union",
        _ when lower.StartsWith("v") => "V",                   // v, v_atk
        _ when lower.StartsWith("prism") => "Prism Star",      // prism_star, prism_white
        _ when lower.StartsWith("tag_team") => "Tag Team",
        _ when lower.StartsWith("acespec") => "ACE SPEC",
        _ when lower.StartsWith("break") => "BREAK",
        _ => null,                                             // unknown sprite → dropped
    };

    private static string ResolveSprite(string name)
        => EnergyWords.TryGetValue(name, out var e) ? e : (MarkerWord(name.ToLowerInvariant()) ?? "");

    public static string? Process(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var result = text!;

        // Treat (Missing)<id> as "nothing speakable" — but only when it's the *only* content.
        if (MissingMarker.IsMatch(result))
        {
            var stripped = MissingMarker.Replace(result, "").Trim();
            if (string.IsNullOrWhiteSpace(stripped)) return null;
            result = stripped;
        }

        result = FallbackMarker.Replace(result, "");
        result = DebugIdWrap.Replace(result, "");
        result = LeadingLangTag.Replace(result, "");

        // Symbols → words, BEFORE the catch-all tag strip swallows the sprite tags. Pad with spaces so
        // a symbol butted against text ("30<sprite…>") doesn't glue into the neighbouring word.
        result = SpriteByName.Replace(result, m => Pad(ResolveSprite(m.Groups["n"].Value)));
        result = SymbolToken.Replace(result, m => Pad(TokenWords.TryGetValue(m.Groups[1].Value, out var w) ? w : ""));

        result = RichText.Replace(result, "");

        result = MultiSpace.Replace(result, " ");
        result = result.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string Pad(string word) => word.Length == 0 ? " " : " " + word + " ";
}
