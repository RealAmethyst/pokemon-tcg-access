using System;
using System.Runtime.InteropServices;

namespace PokemonTCGLiveAccessibility.Speech;

internal static class Prism
{
    private const string Dll = "prism";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    [StructLayout(LayoutKind.Sequential)]
    public struct PrismConfig
    {
        public byte version;
    }

    public enum PrismError
    {
        Ok = 0,
        NotInitialized,
        InvalidParam,
        NotImplemented,
        NoVoices,
        VoiceNotFound,
        SpeakFailure,
        MemoryFailure,
        RangeOutOfBounds,
        Internal,
        NotSpeaking,
        NotPaused,
        AlreadyPaused,
        InvalidUtf8,
        InvalidOperation,
        AlreadyInitialized,
        BackendNotAvailable,
        Unknown,
        InvalidAudioFormat,
        InternalBackendLimitExceeded,
        BackendEnteredUndefinedState,
    }

    [Flags]
    public enum Feature : ulong
    {
        SupportedAtRuntime = 1UL << 0,
        Speak = 1UL << 2,
        SpeakToMemory = 1UL << 3,
        Braille = 1UL << 4,
        Output = 1UL << 5,
        IsSpeaking = 1UL << 6,
        Stop = 1UL << 7,
        Pause = 1UL << 8,
        Resume = 1UL << 9,
        SetVolume = 1UL << 10,
        GetVolume = 1UL << 11,
        SetRate = 1UL << 12,
        GetRate = 1UL << 13,
        SetPitch = 1UL << 14,
        GetPitch = 1UL << 15,
    }

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_config_init")]
    public static extern PrismConfig ConfigInit();

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_init")]
    public static extern IntPtr Init(ref PrismConfig cfg);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_shutdown")]
    public static extern void Shutdown(IntPtr ctx);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_registry_create_best")]
    public static extern IntPtr RegistryCreateBest(IntPtr ctx);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_free")]
    public static extern void BackendFree(IntPtr backend);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_name")]
    public static extern IntPtr BackendNameRaw(IntPtr backend);

    public static string? BackendName(IntPtr backend)
    {
        var p = BackendNameRaw(backend);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_get_features")]
    public static extern ulong BackendGetFeatures(IntPtr backend);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_initialize")]
    public static extern PrismError BackendInitialize(IntPtr backend);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_speak", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = false)]
    public static extern PrismError BackendSpeak(IntPtr backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_output", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = false)]
    public static extern PrismError BackendOutput(IntPtr backend,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        [MarshalAs(UnmanagedType.I1)] bool interrupt);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_stop")]
    public static extern PrismError BackendStop(IntPtr backend);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_set_rate")]
    public static extern PrismError BackendSetRate(IntPtr backend, float rate);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_backend_set_volume")]
    public static extern PrismError BackendSetVolume(IntPtr backend, float volume);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "prism_error_string")]
    public static extern IntPtr ErrorStringRaw(PrismError error);

    public static string? ErrorString(PrismError error)
    {
        var p = ErrorStringRaw(error);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }
}
