using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Professor speech-bubble dialogue, driven by DialogueManager (TPCI.RainierClient/DialogueManager.cs).
// This is the actual driver of the "Welcome to Pokémon Trading Card Game Live!" FTUE bubble on
// the Profile screen — not ProfessorTooltip (that's a simpler click-an-icon-to-show-help tooltip).
//
// DialogueManager.StartDialogue(List<Dialogue>) kicks off a chain. Each Dialogue in the chain
// triggers SetSpeaker() (private) which formats the localised text into `currentText` and
// starts a typewriter animation coroutine. NextSpeaker() advances: first press cancels the
// animation and shows full text, second press moves to the next dialogue. After the last
// dialogue, NextSpeaker() calls HideDialogue() to tear everything down.
//
// We patch SetSpeaker (postfix) to capture each dialogue's full text from `currentText` and
// speak it — works for every link in the chain. We patch HideDialogue (postfix) to deactivate.
//
// For input we call NextSpeaker() twice per user keypress: that reliably skips the typewriter
// animation AND advances to the next dialogue in one user action. The double-call is safe on
// the final dialogue because HideDialogue sets canClick=false and NextSpeaker no-ops then.
internal sealed class DialogueScreen : IScreenHandler
{
    private static DialogueScreen? _instance;

    private readonly ScreenRouter _router;
    private DialogueManager? _manager;
    private bool _active;
    private string _lastSpoken = string.Empty;

    public string Name => "Dialogue";

    public DialogueScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new DialogueScreen(router);

        var setSpeaker = AccessTools.Method(typeof(DialogueManager), "SetSpeaker");
        if (setSpeaker == null)
        {
            Log.Error("DialogueScreen: could not resolve DialogueManager.SetSpeaker");
            return;
        }
        harmony.Patch(setSpeaker, postfix: new HarmonyMethod(AccessTools.Method(typeof(DialogueScreen), nameof(SetSpeaker_Postfix))));

        var hideDialogue = AccessTools.Method(typeof(DialogueManager), "HideDialogue");
        if (hideDialogue != null)
            harmony.Patch(hideDialogue, postfix: new HarmonyMethod(AccessTools.Method(typeof(DialogueScreen), nameof(HideDialogue_Postfix))));

        Log.Info("DialogueScreen installed");
    }

    private static void SetSpeaker_Postfix(DialogueManager __instance)
    {
        if (_instance == null) return;
        _instance._manager = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
        else
        {
            _instance.AnnounceCurrent();
        }
    }

    private static void HideDialogue_Postfix(DialogueManager __instance)
    {
        if (_instance == null || _instance._manager != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._manager = null;
        _instance._lastSpoken = string.Empty;
    }

    public void OnEnter() => AnnounceCurrent();
    public void OnResume() => AnnounceCurrent();
    public void OnExit() { /* preserve _manager */ }
    public void RespeakAll() => AnnounceCurrent();

    public string StateSignature()
    {
        if (_manager == null) return "Dialogue|dead";
        return $"Dialogue|{ReadCurrentText() ?? string.Empty}";
    }

    public void Tick()
    {
        if (_manager == null)
        {
            if (_active)
            {
                _active = false;
                _router.Deactivate(this);
            }
            return;
        }
        if (!Application.isFocused) return;

        // Any of Enter / Space / Esc / Backspace advances. Double-call NextSpeaker: the first
        // call cancels the typewriter animation (or advances if no animation), the second
        // advances cleanly. Safe to call twice — canClick gates the second call when needed.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
         || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
         || UnityEngine.Input.GetKeyDown(KeyCode.Space)
         || UnityEngine.Input.GetKeyDown(KeyCode.Escape)
         || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            // Null-conditional both calls: the first NextSpeaker can trigger HideDialogue at
            // the end of a chain, which fires our HideDialogue_Postfix and nulls _manager.
            // Without the guard the second call would NRE.
            _manager?.NextSpeaker();
            _manager?.NextSpeaker();
        }
    }

    private void AnnounceCurrent()
    {
        var text = ReadCurrentText();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _lastSpoken) return;
        _lastSpoken = text!;
        _router.Speech.Say(text!, interrupt: true);
    }

    private string? ReadCurrentText()
    {
        if (_manager == null) return null;
        var field = typeof(DialogueManager).GetField("currentText", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(_manager) as string;
    }
}
