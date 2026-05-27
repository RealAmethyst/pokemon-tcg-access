using System.Text.RegularExpressions;

namespace PokemonTCGLiveAccessibility.Speech;

// Central text sanitisation for everything spoken. SpeechEngine.Say() runs every string
// through Process() before handing it to Prism, so callers can pass raw game text without
// worrying about debug markers or rich-text noise.
//
// PTCGL-specific things we strip:
//   - "(Missing)<resourceId>" — LocalizationManager emits this when a key isn't in the
//     loaded or fallback loc tables (some screens use their own _startupLoc table that
//     LocalizationManager.Instance doesn't see).
//   - "(Fallback)" — prepended when a string resolved from the fallback locale.
//   - "(<id>… )" / "(en)…" — emitted when debug flags displayStringIds / displayLanguageTagsInText are on.
//   - TMP / HTML-like rich-text tags: <color=…>, <b>, <i>, <size=…>, <sprite=…>, etc.
internal static class TextProcessor
{
    private static readonly Regex MissingMarker = new(@"\(Missing\)<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex FallbackMarker = new(@"\(Fallback\)", RegexOptions.Compiled);
    private static readonly Regex DebugIdWrap = new(@"\(<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex LeadingLangTag = new(@"^\([a-z]{2,5}\)", RegexOptions.Compiled);
    private static readonly Regex RichText = new(@"<[^<>]+>", RegexOptions.Compiled);

    public static string? Process(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var result = text!;

        // Treat (Missing)<id> as "nothing speakable" — but only when it's the *only* content.
        // If something useful sits alongside it, strip just the marker and keep the rest.
        if (MissingMarker.IsMatch(result))
        {
            var stripped = MissingMarker.Replace(result, "").Trim();
            if (string.IsNullOrWhiteSpace(stripped)) return null;
            result = stripped;
        }

        result = FallbackMarker.Replace(result, "");
        result = DebugIdWrap.Replace(result, "");
        result = LeadingLangTag.Replace(result, "");
        result = RichText.Replace(result, "");

        result = result.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
