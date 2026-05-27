using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Hooks;

internal static class BootstrapHook
{
    private static SpeechEngine? _speech;
    private static FocusManager? _focus;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech, FocusManager focus)
    {
        _speech = speech;
        _focus = focus;

        // TODO(phase-0): patch RainierManager.Awake (postfix) to install per-screen wiring once the game's singleton is up.
        // See research/architecture-notes.md — `RainierManager : ManagerSingleton<RainierManager>` bootstraps the app.
        Log.Info("BootstrapHook installed (skeleton)");
    }
}
