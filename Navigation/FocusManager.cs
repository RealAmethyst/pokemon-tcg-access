using System.Collections.Generic;
using PokemonTCGLiveAccessibility.Speech;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Navigation;

internal sealed class FocusManager
{
    private readonly SpeechEngine _speech;
    private readonly List<GameObject> _focusables = new();
    private int _index = -1;
    private GameObject? _lastEventSystemSelected;

    public FocusManager(SpeechEngine speech)
    {
        _speech = speech;
    }

    public int Count => _focusables.Count;
    public int Index => _index;
    public GameObject? Current => _index >= 0 && _index < _focusables.Count ? _focusables[_index] : null;

    public void Tick()
    {
        // TODO(phase-0): mirror EventSystem.current.currentSelectedGameObject into our index so native nav and synthetic nav stay in sync.
        var es = EventSystem.current;
        if (es == null) return;
        var sel = es.currentSelectedGameObject;
        if (sel != _lastEventSystemSelected)
        {
            _lastEventSystemSelected = sel;
            if (sel != null) AnnounceSelected(sel);
        }
    }

    public void MoveNext()
    {
        if (_focusables.Count == 0) return;
        _index = (_index + 1) % _focusables.Count;
        AnnounceIndex();
    }

    public void MovePrev()
    {
        if (_focusables.Count == 0) return;
        _index = (_index - 1 + _focusables.Count) % _focusables.Count;
        AnnounceIndex();
    }

    public void Reset(IEnumerable<GameObject> focusables)
    {
        _focusables.Clear();
        _focusables.AddRange(focusables);
        _index = _focusables.Count > 0 ? 0 : -1;
        if (_index >= 0) AnnounceIndex();
    }

    private void AnnounceIndex()
    {
        var go = Current;
        if (go == null) return;
        // TODO(phase-0): pull a real label from a TextMeshProUGUI / Text child of `go` instead of using GameObject.name.
        var label = go.name;
        _speech.Say(Announce.Item(label, description: null, index: _index + 1, total: _focusables.Count), interrupt: true);
    }

    private void AnnounceSelected(GameObject go)
    {
        // TODO(phase-0): same as AnnounceIndex but for native EventSystem-driven focus changes.
        _speech.Say(go.name, interrupt: true);
    }
}
