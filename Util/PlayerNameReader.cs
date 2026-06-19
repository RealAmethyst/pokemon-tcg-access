using System.Collections.Generic;
using PokemonTCGLiveAccessibility.Screens;
using PokemonTCGLiveAccessibility.Speech;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// "Read / copy my screen name" key (N), scoped to the Profile + friends screens. The player's name
// sits on the Profile nameplate, but it's an info readout, not a focusable control — and a blind
// player needs it to give to others (it's exactly what someone types into Add Friend). N speaks it
// AND copies it to the OS clipboard, so it can be pasted straight into Discord/chat. Read from the
// game's own NetworkProfile.GetPlayerName() (the same source the nameplate uses). Gated to where the
// name is actually relevant — Profile and the friends flow — not every screen (Amethyst, 2026-06-20).
// Skipped while a text field is focused so it never hijacks a typed "N" (e.g. a friend's name).
internal static class PlayerNameReader
{
    private static SpeechEngine? _speech;
    private static ScreenRouter? _router;

    // The handlers where reading/sharing your own name makes sense — Profile and the friends stuff.
    // Matches each handler's Name property. (ConfirmOverlay is generic / used elsewhere, so excluded.)
    private static readonly HashSet<string> AllowedScreens = new()
    {
        "Profile", "Friends", "Add friend", "Friend options", "Battle request",
    };

    public static void Install(SpeechEngine speech, ScreenRouter router)
    {
        _speech = speech;
        _router = router;
    }

    public static void Tick()
    {
        if (_speech == null || !Application.isFocused) return;
        if (!UnityEngine.Input.GetKeyDown(KeyCode.N)) return;
        if (IsTextInputFocused()) return;
        var active = _router?.Active?.Name;
        if (active == null || !AllowedScreens.Contains(active)) return;

        string name;
        try { name = NetworkProfile.GetPlayerName(); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(name)) return;   // not logged in yet → stay silent
        name = name.Trim();

        // Copy to the OS clipboard so the user can paste their name to share it.
        bool copied = false;
        try { GUIUtility.systemCopyBuffer = name; copied = true; } catch { }

        _speech.Say(copied ? $"{name}, copied to clipboard" : name, interrupt: true);
    }

    private static bool IsTextInputFocused()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
    }
}
