using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Hooks;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;
using UnityEngine.UI;

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
    private string _lastSpoken = string.Empty;
    private Selectable? _lastFocusAnnounced;

    // The game's own "a dialogue is showing" flag. StartDialogue sets currentDialogue; HideDialogue
    // nulls it. No mirror of our own — an `_active` bool that says "I'm on the router stack" WILL
    // desync, because ScreenRouter.Deactivate only pops the TOP handler. HideDialogue fires whenever
    // the game likes, including while we're paused beneath an overlay (measured: the tutorial closed
    // its bubble at 12:47:49.618 with the reward popup on top of us). The old code cleared `_active`
    // and `_manager` anyway, so the refused Deactivate left this handler on the stack with
    // `_manager == null && !_active` — and Tick's `if (_manager == null) { if (_active) …; return; }`
    // then returned without ever popping. Once the overlay closed we resumed onto a dead dialogue,
    // owned the stack, swallowed every key, and the screen underneath never came back.
    private static readonly FieldInfo? CurrentDialogueField =
        AccessTools.Field(typeof(DialogueManager), "currentDialogue");

    private static DialogueManager? Manager => DialogueManager.instance;

    private static bool IsOpen
    {
        get
        {
            var m = Manager;
            return m != null && CurrentDialogueField?.GetValue(m) != null;
        }
    }

    private bool IsActiveHandler => ReferenceEquals(_router.Active, this);

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

    private static void SetSpeaker_Postfix()
    {
        if (_instance == null || !IsOpen) return;
        if (_instance.IsActiveHandler) _instance.AnnounceCurrent();
        else _instance._router.Activate(_instance);   // → OnEnter, or OnResume if we're already in the stack
    }

    private static void HideDialogue_Postfix() => _instance?.Close();

    // Deactivate no-ops unless we're the handler on top, so this is safe from any hide path. When the
    // game hides the bubble underneath an overlay, Tick's backstop pops us the moment we're on top again.
    private void Close()
    {
        _lastSpoken = string.Empty;
        _lastFocusAnnounced = null;
        _router.Deactivate(this);
    }

    public void OnEnter() => AnnounceCurrent();
    public void OnResume() { if (IsOpen) AnnounceCurrent(); }   // resumed onto a dead bubble → Tick pops us
    public void OnExit() { }
    public void RespeakAll() => AnnounceCurrent();

    public string StateSignature()
    {
        if (!IsOpen) return "Dialogue|dead";
        return $"Dialogue|{ReadCurrentText() ?? string.Empty}";
    }

    public void Tick()
    {
        // The bubble is gone — possibly hidden long ago, while an overlay sat on top of us and our
        // Deactivate was refused. Let go now that we're the top handler.
        if (!IsOpen) { Close(); return; }
        if (!Application.isFocused) return;

        // Coaching-bubble case: an FTUE bubble shown ALONGSIDE a highlighted action button
        // (e.g. "you can redeem the first tier reward… let's see what you get!" → COLLECT ALL).
        // The game starts these with exitCondition=false, so DialogueManager.NextSpeaker() is a
        // no-op — the only way forward is to ACTIVATE the highlighted button. So when the FTUE
        // has a usable focused element, route Enter/Space to it (and announce it once) instead
        // of trying to advance the text.
        //
        // CRUCIAL: gate this on hasExitCondition == false. A normal *advanceable* welcome/info
        // bubble (hasExitCondition == true) often has an indicator already pre-focusing the NEXT
        // step's target; routing Enter to that target would activate it early and re-show the
        // bubble in a loop. Advanceable bubbles must always advance via NextSpeaker below.
        var focus = (FTUEHook.FtueIsRunning && !DialogueAdvanceable()) ? FTUEHook.FocusedElement : null;
        if (IsUsableAction(focus))
        {
            if (focus != _lastFocusAnnounced)
            {
                _lastFocusAnnounced = focus;
                _router.Speech.Say(KeyboardNavigator.DefaultLabel(focus!), interrupt: false);
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
             || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
             || UnityEngine.Input.GetKeyDown(KeyCode.Space))
            {
                focus!.Select();
                // Fire what the tutorial listens on — see FTUEHook.FocusedElement. A coached Toggle is a
                // tab: select it, never flip it back off.
                if (focus is Button btn) btn.onClick?.Invoke();
                else if (focus is Toggle tog && !tog.isOn) tog.isOn = true;
            }
            return;
        }
        _lastFocusAnnounced = null;

        // Plain dialogue: Enter / Space / Esc / Backspace advances. Double-call NextSpeaker: the
        // first call cancels the typewriter animation (or advances if no animation), the second
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
            Manager?.NextSpeaker();
            Manager?.NextSpeaker();
        }
    }

    // A focused element we can drive: active, interactable, and a Button/Toggle (so Enter has a
    // meaningful effect). focusOnly highlights and other non-interactive targets are excluded.
    private static bool IsUsableAction(Selectable? s)
        => s != null && s.isActiveAndEnabled && s.IsInteractable() && (s is Button || s is Toggle);

    // True when the current dialogue advances on click (started with exitCondition=true — the
    // common welcome/info bubbles, whose NextSpeaker fires the FTUE animator's NextState). False
    // for coaching bubbles (exitCondition=false) that stay until the player performs a highlighted
    // action. Read from the game's own flag. Defaults to true if unreadable, so we never hijack
    // Enter away from a normal advanceable dialogue.
    private static bool DialogueAdvanceable()
    {
        var m = Manager;
        if (m == null) return true;
        var f = typeof(DialogueManager).GetField("hasExitCondition", BindingFlags.Instance | BindingFlags.NonPublic);
        return f?.GetValue(m) is bool b ? b : true;
    }

    private void AnnounceCurrent()
    {
        var text = ReadCurrentText();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text == _lastSpoken) return;
        _lastSpoken = text!;
        _router.Speech.Say(text!, interrupt: true);
    }

    private static string? ReadCurrentText()
    {
        var m = Manager;
        if (m == null) return null;
        var field = typeof(DialogueManager).GetField("currentText", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(m) as string;
    }
}
