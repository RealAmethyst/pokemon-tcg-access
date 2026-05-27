using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Hooks;

internal static class LocalizationHook
{
    private static SpeechEngine? _speech;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech)
    {
        _speech = speech;

        // TODO(phase-0): Harmony-patch LocalizationManager.GetString (postfix) — universal text capture.
        // Target: TPCILocalization/TPCI/Localization/LocalizationManager.cs, method GetString(string resourceId)
        // The postfix shouldn't speak directly; it should push the resourceId + resolved text into a per-frame buffer
        // that the active screen handler reads. Otherwise we'll narrate every UI element regardless of focus.
        Log.Info("LocalizationHook installed (skeleton)");
    }
}
