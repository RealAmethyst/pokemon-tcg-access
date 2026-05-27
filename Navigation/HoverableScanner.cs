using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Navigation;

internal static class HoverableScanner
{
    // Anything the game treats as clickable is a candidate focusable.
    // The "hoverable = focusable" heuristic — see memory: feedback-hoverable-equals-focusable.
    public static IEnumerable<GameObject> ScanActiveCanvases()
    {
        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        foreach (var canvas in canvases)
        {
            if (!canvas.isActiveAndEnabled) continue;
            foreach (var go in CollectInteractables(canvas.gameObject))
                yield return go;
        }
    }

    private static IEnumerable<GameObject> CollectInteractables(GameObject root)
    {
        var seen = new HashSet<GameObject>();
        foreach (var sel in root.GetComponentsInChildren<Selectable>(includeInactive: false))
        {
            if (sel.interactable && seen.Add(sel.gameObject))
                yield return sel.gameObject;
        }
        foreach (var h in root.GetComponentsInChildren<MonoBehaviour>(includeInactive: false))
        {
            if (h is IPointerClickHandler && seen.Add(h.gameObject))
                yield return h.gameObject;
        }
    }
}
