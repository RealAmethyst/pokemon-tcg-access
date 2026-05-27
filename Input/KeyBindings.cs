using PokemonTCGLiveAccessibility.Navigation;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Input;

internal sealed class KeyBindings
{
    private readonly FocusManager _focus;

    public KeyBindings(FocusManager focus)
    {
        _focus = focus;
    }

    public void Tick()
    {
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) || UnityEngine.Input.GetKeyDown(KeyCode.Tab))
            _focus.MoveNext();
        else if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow))
            _focus.MovePrev();
        else if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space))
        {
            // TODO(phase-0): synthesize a click on _focus.Current via ExecuteEvents.Execute(go, PointerEventData, ExecuteEvents.pointerClickHandler).
        }
    }
}
