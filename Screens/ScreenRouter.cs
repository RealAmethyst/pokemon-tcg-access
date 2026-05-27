using System.Collections.Generic;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;

namespace PokemonTCGLiveAccessibility.Screens;

// Stack of active screen handlers. Only the top of the stack receives Tick — when a modal
// activates over a menu, the menu is paused (OnExit) and the modal's OnEnter runs. When the
// modal deactivates, it pops and the menu's OnEnter runs again. This is what stops kb nav
// from cycling through the language list while the confirmation modal is up.
//
// Handlers must keep state intact across OnExit/OnEnter pairs (i.e. don't null out the
// MonoBehaviour reference in OnExit — Tick can check Unity-null instead).
//
// Title tracking: each handler registers its current screen title via SetCurrentTitle so
// child handlers (modals) can suppress redundant readbacks. PTCGL reuses resourceIDs between
// screen headers and their popup titles (e.g. "startup_select_language" appears on both the
// language picker header and its confirmation modal), so a child whose title matches the
// parent's just skips speaking the title and goes straight to the body.
internal sealed class ScreenRouter
{
    public SpeechEngine Speech { get; }

    private readonly Stack<IScreenHandler> _stack = new();
    private readonly Dictionary<IScreenHandler, string?> _titles = new();

    public ScreenRouter(SpeechEngine speech)
    {
        Speech = speech;
    }

    public IScreenHandler? Active => _stack.Count > 0 ? _stack.Peek() : null;
    public bool HasActive => _stack.Count > 0;

    public IScreenHandler? Paused
    {
        get
        {
            if (_stack.Count < 2) return null;
            int i = 0;
            foreach (var h in _stack)
            {
                if (i++ == 1) return h;
            }
            return null;
        }
    }

    public string? PausedTitle => Paused is { } p && _titles.TryGetValue(p, out var t) ? t : null;

    // Handlers call this from OnEnter (or whenever the screen's title text changes) so the
    // router can offer it to child handlers for redundancy checks.
    public void SetCurrentTitle(IScreenHandler handler, string? title) => _titles[handler] = title;

    public void Activate(IScreenHandler handler)
    {
        if (_stack.Count > 0 && _stack.Peek() == handler) return;
        if (_stack.Count > 0)
        {
            var paused = _stack.Peek();
            Log.Info($"Screen pause: {paused.Name}");
            paused.OnExit();
        }
        _stack.Push(handler);
        Log.Info($"Screen → {handler.Name}");
        handler.OnEnter();
    }

    public void Deactivate(IScreenHandler handler)
    {
        // Only pop the top — refuse silent reordering of the stack.
        if (_stack.Count == 0 || _stack.Peek() != handler) return;
        Log.Info($"Screen ← {handler.Name}");
        handler.OnExit();
        _stack.Pop();
        _titles.Remove(handler);
        if (_stack.Count > 0)
        {
            var resumed = _stack.Peek();
            Log.Info($"Screen resume: {resumed.Name}");
            resumed.OnResume();
        }
    }

    public void Tick()
    {
        if (_stack.Count == 0) return;
        _stack.Peek().Tick();
    }
}
