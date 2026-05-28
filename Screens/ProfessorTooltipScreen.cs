using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Util;
using TMPro;
using TPCI.Localization;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Screens;

// Welcome / tutorial speech bubble shown by the Professor NPC (e.g. "Welcome to Pokémon Trading
// Card Game Live! Let's customize your look a little. I assigned you a random avatar...").
// Driver: ProfessorTooltip (TPCI.RainierClient/ProfessorTooltip.cs) — subclass of
// OverlayScreenGroup.
//
// Activation: ProfessorTooltip.ActivateWithText(string locID) sets the tooltipText LocText
// via SetTextByID and calls Activate(). We patch that postfix to push the handler and speak
// the now-rendered text.
//
// Deactivation: OverlayScreenGroup.OnDeActivate fires when the overlay closes (via Deactivate
// or any internal animation-driven close). We patch the base method and filter by instance.
//
// The bubble itself has no Selectable controls — there's just a small triangle indicator and
// the player taps anywhere to dismiss / advance. For keyboard, any of Enter / Space / Esc /
// Backspace dismisses (call ProfessorTooltip.Deactivate). If the FTUE chains messages (prof1
// → prof2 → ...) each new ActivateWithText call updates the text and we re-announce.
internal sealed class ProfessorTooltipScreen : IScreenHandler
{
    private static ProfessorTooltipScreen? _instance;

    private readonly ScreenRouter _router;
    private ProfessorTooltip? _tooltip;
    private bool _active;

    public string Name => "Tooltip";

    public ProfessorTooltipScreen(ScreenRouter router)
    {
        _router = router;
        _instance = this;
    }

    public static void Install(HarmonyLib.Harmony harmony, ScreenRouter router)
    {
        new ProfessorTooltipScreen(router);

        var activate = AccessTools.Method(typeof(ProfessorTooltip), nameof(ProfessorTooltip.ActivateWithText));
        if (activate == null)
        {
            Log.Error("ProfessorTooltipScreen: could not resolve ProfessorTooltip.ActivateWithText");
            return;
        }
        harmony.Patch(activate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ProfessorTooltipScreen), nameof(Activate_Postfix))));

        var deactivate = AccessTools.Method(typeof(OverlayScreenGroup), "OnDeActivate");
        if (deactivate != null)
            harmony.Patch(deactivate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ProfessorTooltipScreen), nameof(Deactivate_Postfix))));

        Log.Info("ProfessorTooltipScreen installed");
    }

    private static void Activate_Postfix(ProfessorTooltip __instance)
    {
        if (_instance == null) return;
        _instance._tooltip = __instance;
        if (!_instance._active)
        {
            _instance._active = true;
            _instance._router.Activate(_instance);
        }
        else
        {
            // Same tooltip, new message (FTUE step advanced) — re-announce without re-pushing.
            _instance.AnnounceText();
        }
    }

    private static void Deactivate_Postfix(OverlayScreenGroup __instance)
    {
        if (_instance == null) return;
        if (_instance._tooltip != __instance) return;
        if (!_instance._active) return;
        _instance._active = false;
        _instance._router.Deactivate(_instance);
        _instance._tooltip = null;
    }

    public void OnEnter() => AnnounceText();
    public void OnResume() => AnnounceText();
    public void OnExit() { /* preserve _tooltip for OnResume; cleared on real deactivate */ }
    public void RespeakAll() => AnnounceText();

    public string StateSignature()
    {
        if (_tooltip == null) return "Tooltip|dead";
        return $"Tooltip|{ReadText() ?? string.Empty}";
    }

    public void Tick()
    {
        if (_tooltip == null)
        {
            if (_active)
            {
                _active = false;
                _router.Deactivate(this);
            }
            return;
        }
        if (!Application.isFocused) return;

        // Any of Enter / Space / Esc / Backspace dismisses. The tooltip has no other controls.
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return)
         || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)
         || UnityEngine.Input.GetKeyDown(KeyCode.Space)
         || UnityEngine.Input.GetKeyDown(KeyCode.Escape)
         || UnityEngine.Input.GetKeyDown(KeyCode.Backspace))
        {
            _tooltip.Deactivate();
            // Deactivate_Postfix will pop the handler and clear _tooltip.
        }
    }

    private void AnnounceText()
    {
        var text = ReadText();
        if (!string.IsNullOrWhiteSpace(text))
            _router.Speech.Say(text!, interrupt: true);
    }

    private string? ReadText()
    {
        if (_tooltip == null) return null;
        var field = typeof(ProfessorTooltip).GetField("tooltipText", BindingFlags.Instance | BindingFlags.NonPublic);
        var locText = field?.GetValue(_tooltip) as Component;
        if (locText == null) return null;
        var tmp = locText.GetComponent<TMP_Text>() ?? locText.GetComponentInChildren<TMP_Text>();
        return tmp?.text;
    }
}
