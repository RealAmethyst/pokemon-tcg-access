using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Hooks;

internal static class ScreenHook
{
    private static SpeechEngine? _speech;
    private static FocusManager? _focus;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech, FocusManager focus)
    {
        _speech = speech;
        _focus = focus;

        // TODO(phase-0): Harmony-patch MainMenuNavigation.GoToScreen (postfix).
        // On transition: announce screen name (interrupt), then reset FocusManager to the new screen's focusables.
        Log.Info("ScreenHook installed (skeleton)");
    }
}
