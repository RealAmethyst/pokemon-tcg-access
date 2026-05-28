using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using TMPro;

namespace PokemonTCGLiveAccessibility.Screens;

// Startup loading screen ("Catching wild Pokémon 49/53"). Driver: StartupScreenText.
//
// StartupScreenText.UpdateLoadString(step, final) is called once per game-manager
// initialization completion (~53 ticks total for first-launch). The game's text rotates
// between phase-specific localized templates (loading_screen_1 through loading_screen_8)
// formatted as "<phrase> {0}/{1}".
//
// Speech behavior: announce only when the localized *phrase* changes (i.e. on phase
// transition). Progress numbers tick too fast to be useful. All speech here queues
// (interrupt: false) — phase transitions fire close together and would otherwise cut
// each other off mid-word.
internal sealed class LoadingScreen : IScreenHandler
{
    private static LoadingScreen? _instance;

    private readonly ScreenRouter _router;
    private StartupScreenText? _sst;
    private string? _lastPhrase;
    private bool _active;

    private static readonly Regex TrailingNumbers = new(@"\s*\d+\s*[/\\]\s*\d+\s*$", RegexOptions.Compiled);

    public string Name => "Loading";

    public LoadingScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new LoadingScreen(router);

        var target = AccessTools.Method(typeof(StartupScreenText), "UpdateLoadString");
        if (target == null)
        {
            Log.Error("LoadingScreen: could not resolve StartupScreenText.UpdateLoadString");
            return;
        }
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(LoadingScreen), nameof(UpdateLoadString_Postfix)));
        harmony.Patch(target, postfix: postfix);
        Log.Info("LoadingScreen installed (patched StartupScreenText.UpdateLoadString)");
    }

    private static void UpdateLoadString_Postfix(StartupScreenText __instance, int step, bool final)
    {
        if (_instance == null) return;
        _instance._sst = __instance;
        _instance.OnProgress(step, final);
    }

    private void OnProgress(int step, bool final)
    {
        if (_sst == null) return;

        var phrase = ReadPhrase(_sst);

        // Lazy activation: only push on first real tick.
        if (!_active && (step > 0 || final))
        {
            _active = true;
            _router.Activate(this);
        }

        if (!_active) return;

        if (!string.IsNullOrEmpty(phrase) && phrase != _lastPhrase)
        {
            _lastPhrase = phrase;
            _router.Speech.Say(phrase!, interrupt: false);
        }

        if (final)
        {
            _active = false;
            _router.Deactivate(this);
            _lastPhrase = null;
        }
    }

    public void OnEnter()
    {
        // Speech is driven by the UpdateLoadString postfix; first phrase will speak through there.
    }

    public void OnResume()
    {
        // A modal closed over us (unusual during loading, but possible). Re-announce current phrase.
        if (!string.IsNullOrEmpty(_lastPhrase))
            _router.Speech.Say(_lastPhrase!, interrupt: false);
    }

    public void OnExit()
    {
        // Don't clear state — OnProgress is the only place that resets, and only on final.
    }

    public void RespeakAll()
    {
        if (!string.IsNullOrEmpty(_lastPhrase))
            _router.Speech.Say(_lastPhrase!, interrupt: false);
    }

    public string StateSignature() => $"Loading|{_lastPhrase}";

    public void Tick()
    {
        // No-op; updates are event-driven via the Harmony postfix.
    }

    private static string ReadPhrase(StartupScreenText sst)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var textField = typeof(StartupScreenText).GetField("loadingText", flags);
        var rendered = (textField?.GetValue(sst) as TMP_Text)?.text ?? "";
        return TrailingNumbers.Replace(rendered, "").Trim();
    }
}
