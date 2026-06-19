using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// Stub of the main mod's Util.UiLabels for the Updater project. The real UiLabels maps game-specific
// composite widgets (shop product tiles, battle-pass badges, etc.) to friendly names — none of which
// exist in the Updater. Its buttons (PLAY / RETRY / SUPPORT / RELEASE NOTES) are plain labelled
// Buttons, so KeyboardNavigator's default "first text child with a letter" resolution is correct and
// both hooks return null here. Kept as a stub purely so the linked KeyboardNavigator.cs compiles.
internal static class UiLabels
{
    public static string? Friendly(string? goName) => null;

    public static string? Describe(Selectable s) => null;
}
