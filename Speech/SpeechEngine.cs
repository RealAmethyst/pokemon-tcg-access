using System;
using PokemonTCGLiveAccessibility.Util;
using UnityEngine;

namespace PokemonTCGLiveAccessibility.Speech;

internal sealed class SpeechEngine
{
    private IntPtr _ctx;
    private IntPtr _backend;

    public bool Ready => _ctx != IntPtr.Zero && _backend != IntPtr.Zero;

    public void Start()
    {
        try
        {
            var cfg = Prism.ConfigInit();
            _ctx = Prism.Init(ref cfg);
            if (_ctx == IntPtr.Zero)
            {
                Log.Error("Prism.Init returned null context");
                return;
            }

            _backend = Prism.RegistryCreateBest(_ctx);
            if (_backend == IntPtr.Zero)
            {
                Log.Error("Prism.RegistryCreateBest returned null — no backend available");
                return;
            }

            var initErr = Prism.BackendInitialize(_backend);
            if (initErr != Prism.PrismError.Ok && initErr != Prism.PrismError.AlreadyInitialized)
            {
                Log.Error($"Prism backend init failed: {initErr} ({Prism.ErrorString(initErr)})");
                return;
            }

            Log.Info($"Prism ready — backend: {Prism.BackendName(_backend)}");
            Say($"{BuildInfo.Name} loaded", interrupt: true);
        }
        catch (DllNotFoundException)
        {
            Log.Error("prism.dll not found next to the game executable. Speech is disabled.");
        }
        catch (Exception ex)
        {
            Log.Error($"Prism startup threw: {ex.Message}");
        }
    }

    public void Say(string text, bool interrupt)
    {
        if (!Ready) return;
        // Silent while the game window isn't focused. The user is presumably reading another
        // window's content (browser login, IDE, etc.) and doesn't want game speech interleaved.
        // On focus regain, Mod.OnUpdate asks the active handler to RespeakAll() so nothing is lost.
        if (!Application.isFocused) return;
        var processed = TextProcessor.Process(text);
        if (string.IsNullOrEmpty(processed)) return;
        var err = Prism.BackendOutput(_backend, processed!, interrupt);
        if (err != Prism.PrismError.Ok)
            Log.Warn($"Speak failed: {err} ({Prism.ErrorString(err)})");
    }

    public void Stop()
    {
        if (Ready) Prism.BackendStop(_backend);
    }

    public void Shutdown()
    {
        if (_backend != IntPtr.Zero)
        {
            Prism.BackendFree(_backend);
            _backend = IntPtr.Zero;
        }
        if (_ctx != IntPtr.Zero)
        {
            Prism.Shutdown(_ctx);
            _ctx = IntPtr.Zero;
        }
    }
}
