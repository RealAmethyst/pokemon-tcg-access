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
        // Already somewhere in the stack but not on top: re-pushing would leave a duplicate that can
        // never fully unwind (its first copy is stranded under the rest). This happens if an overlay
        // re-activates while it's paused beneath a nested overlay. Don't duplicate — the existing entry
        // already owns its slot; just refresh it in place so a re-announce still happens.
        if (_stack.Contains(handler)) { Log.Info($"Screen re-activate (in stack): {handler.Name}"); handler.OnResume(); return; }
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

    // Activate a background/base handler so it sits BELOW the current top handler instead of over it.
    // Used when a base screen "starts" while a modal is already showing — e.g. Battle's StartGame fires
    // while a tutorial Dialogue bubble is up: the dialogue must stay the focused, only-tickable thing so
    // the player can advance it, and the base becomes active only when the modal closes/pops.
    public void ActivateBackground(IScreenHandler handler)
    {
        if (_stack.Contains(handler)) return;
        if (_stack.Count == 0) { Activate(handler); return; }
        var arr = _stack.ToArray();        // top-first
        _stack.Clear();
        for (int i = arr.Length - 1; i >= 1; i--) _stack.Push(arr[i]);   // everything below the top, bottom-first
        _stack.Push(handler);              // the new background handler, just below the top
        _stack.Push(arr[0]);               // restore the top (modal) — it keeps focus
        Log.Info($"Screen → {handler.Name} (below {arr[0].Name})");
        handler.OnEnter();                 // sets up its state; any announce queues behind the modal's speech
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
