using System;
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
// Deactivate works from ANYWHERE in the stack, not just the top. The game tears a screen down when it
// pleases, including while another handler sits above it, and a dead handler left buried will later
// surface owning the keyboard with nothing to drive. Popping the top resumes the next handler; removing
// a buried one leaves the top untouched.
//
// Title tracking: each handler registers its current screen title via SetCurrentTitle so
// child handlers (modals) can suppress redundant readbacks. PTCGL reuses resourceIDs between
// screen headers and their popup titles (e.g. "startup_select_language" appears on both the
// language picker header and its confirmation modal), so a child whose title matches the
// parent's just skips speaking the title and goes straight to the body.
//
// EVERY handler callback is invoked through Safe(). Handlers are almost always entered from a Harmony
// postfix, which means we are running inside the game's own call stack — and an exception thrown here
// unwinds into THAT, not into MelonLoader. On 2026-07-10 a NullReferenceException in
// ModalContainerScreen.OnEnter escaped through ModalElementContainer.Open, up through
// PlayerSelectionOverlay.OpenWithSelectionRequest, and killed InputRequestOperation.RunOperation()'s
// coroutine mid-yield: `currentlyRequesting` never cleared, the operation never completed, and
// MatchNetworkParser stopped advancing. The whole online match hard-froze — from a speech bug. A broken
// handler that stays on the stack is bad; a dead match is unrecoverable, so the boundary is guarded here
// rather than at each of the ~40 call sites.
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
        if (_stack.Contains(handler)) { Log.Info($"Screen re-activate (in stack): {handler.Name}"); Safe(handler, "OnResume", handler.OnResume); return; }
        if (_stack.Count > 0)
        {
            var paused = _stack.Peek();
            Log.Info($"Screen pause: {paused.Name}");
            Safe(paused, "OnExit", paused.OnExit);
        }
        _stack.Push(handler);
        Log.Info($"Screen → {handler.Name}");
        Safe(handler, "OnEnter", handler.OnEnter);
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
        Safe(handler, "OnEnter", handler.OnEnter);   // state setup; any announce queues behind the modal's speech
    }

    public void Deactivate(IScreenHandler handler)
    {
        if (_stack.Count == 0) return;

        if (_stack.Peek() == handler)
        {
            Log.Info($"Screen ← {handler.Name}");
            Safe(handler, "OnExit", handler.OnExit);
            _stack.Pop();
            _titles.Remove(handler);
            if (_stack.Count > 0)
            {
                var resumed = _stack.Peek();
                Log.Info($"Screen resume: {resumed.Name}");
                Safe(resumed, "OnResume", resumed.OnResume);
            }
            return;
        }

        // BURIED, and its screen is gone. Excise it in place.
        //
        // A handler deactivates when the thing it drives disappears, and the game does not wait for us
        // to be on top: the FTUE hid its dialogue bubble while HOME sat above it, so this call was
        // refused and the dead handler stayed on the stack. It then can only leave by everything above
        // it closing — at which point it owns the keyboard with nothing to drive. Worse, Activate() sees
        // it via _stack.Contains and merely re-announces ("Screen re-activate (in stack)"), so the NEXT
        // dialogue spoke from underneath SHOP, which kept the keyboard and let the player roam a screen
        // the tutorial had locked. A per-handler Tick backstop can't save this: HOME popped and SHOP
        // pushed 16 ms later, before the backstop's frame ever ran.
        //
        // Removing a buried entry is not the "silent reordering" this stack refuses — the top keeps
        // focus, nothing above or below changes order, and only the removed handler sees OnExit. It's
        // the same excision ActivateBackground already performs to slot a handler underneath the top.
        if (!_stack.Contains(handler)) return;
        Log.Info($"Screen ✕ {handler.Name} (removed from under {_stack.Peek().Name})");
        Safe(handler, "OnExit", handler.OnExit);
        _titles.Remove(handler);
        var arr = _stack.ToArray();          // top-first
        _stack.Clear();
        for (int i = arr.Length - 1; i >= 0; i--)   // rebuild bottom-first, minus the handler
            if (!ReferenceEquals(arr[i], handler)) _stack.Push(arr[i]);
    }

    public void Tick()
    {
        if (_stack.Count == 0) return;
        var top = _stack.Peek();
        Safe(top, "Tick", top.Tick);
    }

    // Run one handler callback without ever letting it escape into the caller — see the class comment.
    private static void Safe(IScreenHandler handler, string what, Action call)
    {
        try { call(); }
        catch (Exception ex) { Log.Error($"ScreenRouter: {handler.Name}.{what} threw: {ex}"); }
    }
}
