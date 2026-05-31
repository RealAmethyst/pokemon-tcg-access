using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using PokemonTCGLiveAccessibility.Speech;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PokemonTCGLiveAccessibility.Util;

// Global "read my balances" key (P). The HUB top bar shows the player's currency totals
// (Crystals / Credits / a token), needed to know what you can afford in the shop — but those are
// info readouts, not focusable controls. P speaks them on demand from the live CurrencyDisplay
// group, ordered left-to-right to match the bar. Gated to screens that actually show the bar (at
// least one mainHub display active + visible), so it stays silent in battle / on the title screen.
// Skipped while a text field is focused so it doesn't hijack a typed "P" (e.g. a redeem code).
internal static class CurrencyReader
{
    private const string GroupName = "mainHub";   // CurrencyDisplay.GROUP_NAME_MAIN_HUB

    private static SpeechEngine? _speech;

    public static void Install(SpeechEngine speech) => _speech = speech;

    public static void Tick()
    {
        if (_speech == null || !Application.isFocused) return;
        if (!UnityEngine.Input.GetKeyDown(KeyCode.P)) return;
        if (IsTextInputFocused()) return;

        var msg = ReadBalances();
        if (!string.IsNullOrWhiteSpace(msg)) _speech.Say(msg!, interrupt: true);
        // No visible currency bar → stay silent: P only acts on screens that show it.
    }

    private static string? ReadBalances()
    {
        List<CurrencyDisplay> displays;
        try { displays = CurrencyDisplay.GetCurrencyDisplaysByGroup(GroupName); }
        catch { return null; }
        if (displays == null || displays.Count == 0) return null;

        var parts = new List<(float x, string text)>();
        var seenTypes = new HashSet<SharedLogicUtils.source.DataTypes.CurrencyType>();
        foreach (var d in displays)
        {
            if (d == null || !d.isActiveAndEnabled || !d.IsVisible) continue;
            if (!seenTypes.Add(d.currencyType)) continue;   // one readout per currency type
            var amount = AmountOf(d);
            if (string.IsNullOrWhiteSpace(amount)) continue;
            var word = ShopLabels.CurrencyWord(d.currencyType);
            var text = string.IsNullOrEmpty(word) ? amount! : $"{amount} {word}";
            parts.Add((d.transform.position.x, text));
        }
        if (parts.Count == 0) return null;
        return string.Join(", ", parts.OrderBy(p => p.x).Select(p => p.text));
    }

    private static string? AmountOf(CurrencyDisplay d)
    {
        var lt = ReadField<LocText>(d, "currencyText");
        var s = lt?.CurrentText;
        if (string.IsNullOrWhiteSpace(s)) return null;
        return StripTags(s!).Trim();
    }

    private static bool IsTextInputFocused()
    {
        var es = EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        return sel.GetComponent<InputField>() != null || sel.GetComponent<TMPro.TMP_InputField>() != null;
    }

    private static string StripTags(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        {
            var field = t.GetField(name, flags);
            if (field != null) return field.GetValue(instance) as T;
        }
        return null;
    }
}
