using MelonLoader;

namespace PokemonTCGLiveAccessibility.Util;

internal static class Log
{
    private static MelonLogger.Instance? _logger;

    public static void Init(MelonLogger.Instance logger) => _logger = logger;

    public static void Info(string msg) => _logger?.Msg(msg);
    public static void Warn(string msg) => _logger?.Warning(msg);
    public static void Error(string msg) => _logger?.Error(msg);
}
