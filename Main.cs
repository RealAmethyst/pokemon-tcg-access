using MelonLoader;
using PokemonTCGLiveAccessibility;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Input;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

[assembly: MelonInfo(typeof(Mod), BuildInfo.Name, BuildInfo.Version, BuildInfo.Author)]
[assembly: MelonGame("The Pokémon Company International", "Pokémon Trading Card Game Live")]

namespace PokemonTCGLiveAccessibility;

public static class BuildInfo
{
    public const string Name = "PokemonTCGLiveAccessibility";
    public const string Author = "RealAmethyst";
    public const string Version = "0.1.0";
}

public class Mod : MelonMod
{
    private SpeechEngine? _speech;
    private FocusManager? _focus;
    private KeyBindings? _keys;

    public override void OnInitializeMelon()
    {
        Log.Init(LoggerInstance);
        Log.Info($"{BuildInfo.Name} v{BuildInfo.Version} starting");

        _speech = new SpeechEngine();
        _speech.Start();

        _focus = new FocusManager(_speech);
        _keys = new KeyBindings(_focus);

        LocalizationHook.Install(HarmonyInstance, _speech);
        ScreenHook.Install(HarmonyInstance, _speech, _focus);
        BootstrapHook.Install(HarmonyInstance, _speech, _focus);
    }

    public override void OnUpdate()
    {
        _keys?.Tick();
        _focus?.Tick();
    }

    public override void OnApplicationQuit()
    {
        _speech?.Shutdown();
    }
}
