using MelonLoader;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

[assembly: MelonInfo(typeof(PokemonTCGLiveAccessibility.UpdaterMod),
    PokemonTCGLiveAccessibility.BuildInfo.Name,
    PokemonTCGLiveAccessibility.BuildInfo.Version,
    PokemonTCGLiveAccessibility.BuildInfo.Author)]
// The standalone updater's app.info reads `pokemon` / `Updater` (verified
// Updater\<ver>\Updater_Data\app.info) — must match exactly or MelonLoader rejects the mod as
// incompatible, same rule as the main game's MelonGame attribute.
[assembly: MelonGame("pokemon", "Updater")]

namespace PokemonTCGLiveAccessibility;

public static class BuildInfo
{
    public const string Name = "PokemonTCGLiveUpdaterAccessibility";
    public const string Author = "RealAmethyst";
    public const string Version = "0.1.0";
}

// Entry point for the Updater.exe accessibility mod. Mirrors the main mod's Mod class but far
// smaller: the updater is a single screen, so there's no ScreenRouter / per-screen handlers — just
// speech, the window fix, and one polling handler (UpdaterScreen). See UpdaterScreen for the why.
public class UpdaterMod : MelonMod
{
    private SpeechEngine? _speech;
    private WindowFix? _windowFix;
    private UpdaterScreen? _screen;

    public override void OnInitializeMelon()
    {
        Log.Init(LoggerInstance);
        Log.Info($"{BuildInfo.Name} v{BuildInfo.Version} starting");

        _speech = new SpeechEngine();
        _speech.Start();

        _windowFix = new WindowFix();
        _screen = new UpdaterScreen(_speech);
    }

    public override void OnUpdate()
    {
        _windowFix?.Tick();
        _screen?.Tick();
    }

    public override void OnApplicationQuit()
    {
        // Same order as the main mod: drop the WndProc subclass before the managed domain goes away,
        // or Windows dispatches teardown messages into dead managed code and the process crashes on
        // exit. See WindowFix.Shutdown.
        Cleanup();
    }

    // Second chance for the unregister / definite-quit paths that skip OnApplicationQuit. Idempotent.
    public override void OnDeinitializeMelon() => Cleanup();

    private void Cleanup()
    {
        _windowFix?.Shutdown();
        _speech?.Shutdown();
    }
}
