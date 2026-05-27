using System.Collections.Concurrent;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using TPCI.Localization;

namespace PokemonTCGLiveAccessibility.Hooks;

internal static class LocalizationHook
{
    private static SpeechEngine? _speech;
    private static readonly ConcurrentDictionary<string, string> _cache = new();

    // Toggle to dump every resolved string id → text to the log. Useful for discovering new resourceIds.
    internal static bool DebugLogResolutions = false;

    public static void Install(HarmonyLib.Harmony harmony, SpeechEngine speech)
    {
        _speech = speech;

        var target = AccessTools.Method(typeof(LocalizationManager), nameof(LocalizationManager.GetString), new[] { typeof(string) });
        if (target == null)
        {
            Log.Error("LocalizationHook: could not resolve LocalizationManager.GetString(string)");
            return;
        }
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(LocalizationHook), nameof(GetString_Postfix)));
        harmony.Patch(target, postfix: postfix);
        Log.Info("LocalizationHook installed");
    }

    // Query the cache; fall back to a live LocalizationManager call so handlers can ask for an id we haven't seen yet.
    public static string? Get(string resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;
        if (_cache.TryGetValue(resourceId, out var cached)) return cached;
        var live = LocalizationManager.Instance?.GetString(resourceId);
        return string.IsNullOrEmpty(live) ? null : live;
    }

    private static void GetString_Postfix(string resourceID, string __result)
    {
        if (string.IsNullOrEmpty(resourceID) || string.IsNullOrEmpty(__result)) return;
        _cache[resourceID] = __result;
        if (DebugLogResolutions) Log.Info($"loc: {resourceID} = {__result}");
    }
}
