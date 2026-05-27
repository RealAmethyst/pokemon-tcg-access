using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PokemonTCGLiveAccessibility.Util;

internal sealed class WindowFix
{
    private bool _diagnosed;
    private bool _styleAppliedOnce;
    private bool _subclassed;
    private int _revertCount;
    private int _ticksSinceLastRevertLog;
    private IntPtr _gameHwnd = IntPtr.Zero;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Pinned via static reference so the GC doesn't collect it while
    // Windows is calling back into native code.
    private static WndProcDelegate? _wndProcDelegate;
    private static IntPtr _originalWndProc = IntPtr.Zero;
    private static int _getObjectLogCount;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_WNDPROC = -4;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_OVERLAPPED_DECOR = WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint WM_GETOBJECT = 0x003D;
    private const long OBJID_WINDOW = 0;
    private const long OBJID_CLIENT = -4;

    private const int RevertLogIntervalTicks = 120;

    public void Tick()
    {
        if (_gameHwnd == IntPtr.Zero) _gameHwnd = FindUnityWindow();
        if (_gameHwnd == IntPtr.Zero) return;

        if (!_diagnosed) { Diagnose(); _diagnosed = true; }

        // 1. Make sure window style stays normal-overlapped.
        EnsureStyle();

        // 2. Subclass WndProc so we can hand WM_GETOBJECT to DefWindowProc
        //    (Unity's handler suppresses it).
        if (!_subclassed) { InstallSubclass(_gameHwnd); _subclassed = true; }
    }

    private void EnsureStyle()
    {
        var style = (uint)GetWindowLong(_gameHwnd, GWL_STYLE);
        var exStyle = (uint)GetWindowLong(_gameHwnd, GWL_EXSTYLE);

        bool styleOk = (style & WS_POPUP) == 0 && (style & WS_OVERLAPPED_DECOR) == WS_OVERLAPPED_DECOR;
        bool exOk = (exStyle & WS_EX_APPWINDOW) != 0
                    && (exStyle & WS_EX_NOACTIVATE) == 0
                    && (exStyle & WS_EX_TOOLWINDOW) == 0;

        _ticksSinceLastRevertLog++;

        if (styleOk && exOk) return;

        var newStyle = (style & ~WS_POPUP) | WS_OVERLAPPED_DECOR;
        var newEx = (exStyle & ~WS_EX_NOACTIVATE & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;

        if (!_styleAppliedOnce)
        {
            Log.Info($"WindowFix: first apply — style 0x{style:X8} -> 0x{newStyle:X8} exStyle 0x{exStyle:X8} -> 0x{newEx:X8}");
        }
        else
        {
            _revertCount++;
            if (_ticksSinceLastRevertLog >= RevertLogIntervalTicks)
            {
                Log.Warn($"WindowFix: style reverted (#{_revertCount}) — observed style 0x{style:X8} exStyle 0x{exStyle:X8} — re-applying");
                _ticksSinceLastRevertLog = 0;
            }
        }

        SetWindowLong(_gameHwnd, GWL_STYLE, unchecked((int)newStyle));
        SetWindowLong(_gameHwnd, GWL_EXSTYLE, unchecked((int)newEx));
        SetWindowPos(_gameHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        if (!_styleAppliedOnce)
        {
            var verifyStyle = (uint)GetWindowLong(_gameHwnd, GWL_STYLE);
            var verifyEx = (uint)GetWindowLong(_gameHwnd, GWL_EXSTYLE);
            Log.Info($"WindowFix: verify after apply — style 0x{verifyStyle:X8} exStyle 0x{verifyEx:X8}");
            _styleAppliedOnce = true;
        }
    }

    private static IntPtr FindUnityWindow()
    {
        var pid = (uint)Process.GetCurrentProcess().Id;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var winPid);
            if (winPid != pid) return true;
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString().StartsWith("Unity", StringComparison.Ordinal))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private void Diagnose()
    {
        var proc = Process.GetCurrentProcess();
        var pid = (uint)proc.Id;
        Log.Info($"WindowFix: PID={pid} MainWindowHandle=0x{proc.MainWindowHandle.ToInt64():X} — enumerating windows:");
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var winPid);
            if (winPid != pid) return true;
            var cls = new StringBuilder(256); GetClassName(h, cls, cls.Capacity);
            var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity);
            var style = (uint)GetWindowLong(h, GWL_STYLE);
            var ex = (uint)GetWindowLong(h, GWL_EXSTYLE);
            var visible = IsWindowVisible(h);
            Log.Info($"  hwnd=0x{h.ToInt64():X8} visible={visible} class='{cls}' title='{title}' style=0x{style:X8} exStyle=0x{ex:X8}");
            return true;
        }, IntPtr.Zero);

        // Probe Unity's current WM_GETOBJECT behaviour BEFORE we subclass.
        var clientResp = SendMessage(_gameHwnd, WM_GETOBJECT, IntPtr.Zero, (IntPtr)OBJID_CLIENT);
        var windowResp = SendMessage(_gameHwnd, WM_GETOBJECT, IntPtr.Zero, (IntPtr)OBJID_WINDOW);
        Log.Info($"WindowFix: Unity WM_GETOBJECT response — client=0x{clientResp.ToInt64():X16} window=0x{windowResp.ToInt64():X16}");
    }

    private static IntPtr OurWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETOBJECT)
        {
            // Bypass Unity's handler — let DefWindowProc provide the standard
            // window-level accessibility object (window role, title, etc.).
            if (_getObjectLogCount < 8)
            {
                _getObjectLogCount++;
                Log.Info($"WndProc: WM_GETOBJECT lParam=0x{lParam.ToInt64():X16} (intercepted #{_getObjectLogCount})");
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private static void InstallSubclass(IntPtr hwnd)
    {
        _wndProcDelegate = OurWndProc;
        var newPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newPtr);
        Log.Info($"WindowFix: WndProc subclass installed. Original=0x{_originalWndProc.ToInt64():X16} New=0x{newPtr.ToInt64():X16}");
    }
}
