using System;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;

namespace PokemonTCGLiveAccessibility.Util;

// Dual-sink logger: every call goes to the MelonLoader console *and* to a private file in the
// Mods folder. The file is cleared on every game launch so each session starts clean — that
// makes it easy to ship a single fresh `PokemonTCGLiveAccessibility.log` after reproducing a
// problem, instead of grepping through Latest.log (which mixes loader noise with mod output).
internal static class Log
{
    private static MelonLogger.Instance? _logger;
    private static string? _filePath;
    private static readonly object _fileLock = new();

    public static void Init(MelonLogger.Instance logger)
    {
        _logger = logger;
        InitFile();
    }

    public static void Info(string msg)
    {
        _logger?.Msg(msg);
        WriteFile("INFO ", msg);
    }

    public static void Warn(string msg)
    {
        _logger?.Warning(msg);
        WriteFile("WARN ", msg);
    }

    public static void Error(string msg)
    {
        _logger?.Error(msg);
        WriteFile("ERROR", msg);
    }

    private static void InitFile()
    {
        try
        {
            var modsDir = Path.GetDirectoryName(typeof(Log).Assembly.Location);
            if (string.IsNullOrEmpty(modsDir)) return;
            _filePath = Path.Combine(modsDir!, "PokemonTCGLiveAccessibility.log");

            if (File.Exists(_filePath)) File.Delete(_filePath);

            var header = new StringBuilder();
            header.AppendLine($"=== PokemonTCGLiveAccessibility log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            var asm = typeof(Log).Assembly.GetName();
            header.AppendLine($"Assembly: {asm.Name} v{asm.Version}");
            File.WriteAllText(_filePath, header.ToString());
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Log: file init failed — {ex.Message}");
            _filePath = null;
        }
    }

    private static void WriteFile(string level, string msg)
    {
        if (_filePath == null) return;
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_filePath, $"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* swallow — logging must never break the mod */ }
    }
}
