using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe partial class TrackerTray
{
    private const uint TrayMessage = 0x8001;
    private const uint Close = 0x10;
    private const uint Destroy = 2;
    private const uint RightUp = 0x205;
    private const uint DoubleClick = 0x203;
    private static IHostApplicationLifetime? lifetime;
    private static ILogger? logger;
    private static int port;
    private static Process? dashboard;

    public static void Start(IHostApplicationLifetime appLifetime, ILogger appLogger, int bridgePort)
    {
        lifetime = appLifetime;
        logger = appLogger;
        port = bridgePort;
        new Thread(Run) { IsBackground = true, Name = "BDO Companion tray" }.Start();
    }

    private static void Run()
    {
        var module = GetModuleHandle(null);
        const string className = "BdoCompanionTrackerTray";
        fixed (char* name = className)
        {
            var windowClass = new WindowClass { Instance = module, Procedure = &WindowProc, Name = name };
            if (RegisterClass(ref windowClass) == 0) return;
        }

        var window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (window == 0) return;
        var icon = new TrayIcon
        {
            Size = (uint)sizeof(TrayIcon), Window = window, Id = 1, Flags = 7,
            Callback = TrayMessage, Icon = LoadIcon(0, 32512)
        };
        "BDO Companion".AsSpan().CopyTo(new Span<char>(icon.Tip, 128));
        if (Shell_NotifyIcon(0, ref icon) == 0)
        {
            DestroyWindow(window);
            return;
        }

        using var stop = lifetime!.ApplicationStopping.Register(() => PostMessage(window, Close, 0, 0));
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == TrayMessage)
        {
            if (lParam == DoubleClick) LaunchDashboard();
            if (lParam == RightUp) ShowMenu(window);
            return 0;
        }
        if (message == Destroy)
        {
            var icon = new TrayIcon { Size = (uint)sizeof(TrayIcon), Window = window, Id = 1 };
            Shell_NotifyIcon(2, ref icon);
            dashboard?.Dispose();
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void ShowMenu(nint window)
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, "Open desktop companion");
            AppendMenu(menu, 0, 2, "Open browser planner");
            AppendMenu(menu, 0, 3, "Exit BDO Companion");
            if (GetCursorPos(out var point) == 0) return;
            SetForegroundWindow(window);
            var choice = TrackPopupMenu(menu, 0x102, point.X, point.Y, 0, window, 0);
            if (choice == 1) LaunchDashboard();
            if (choice == 2) StartProcess(new ProcessStartInfo($"http://127.0.0.1:{port}/planner#craft") { UseShellExecute = true })?.Dispose();
            if (choice == 3) lifetime!.StopApplication();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void LaunchDashboard()
    {
        if (dashboard is { HasExited: false })
        {
            dashboard.Refresh();
            if (dashboard.MainWindowHandle is var window && window != 0)
            {
                ShowWindow(window, 9);
                SetForegroundWindow(window);
            }
            return;
        }

        dashboard?.Dispose();
        var path = Path.Combine(AppContext.BaseDirectory, "desktop", "Companion.Desktop.exe");
        if (!File.Exists(path))
        {
            logger!.LogWarning("Desktop companion not found at {Path}", path);
            dashboard = null;
            return;
        }
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        dashboard = StartProcess(start);
    }

    private static Process? StartProcess(ProcessStartInfo start)
    {
        try { return Process.Start(start); }
        catch (System.ComponentModel.Win32Exception exception)
        {
            logger!.LogWarning("Could not open companion window: {Reason}", exception.Message);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, Menu;
        public char* Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayIcon
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        public fixed char Tip[128];
        public uint State, StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char Title[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? module);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW")] private static partial ushort RegisterClass(ref WindowClass windowClass);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint extra, string kind, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] private static partial nint LoadIcon(nint instance, nint name);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static partial int Shell_NotifyIcon(uint operation, ref TrayIcon data);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] private static partial int GetMessage(out Message message, nint window, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(ref Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static partial nint DispatchMessage(ref Message message);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] private static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int status);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AppendMenu(nint menu, uint flags, nuint id, string label);
    [LibraryImport("user32.dll")] private static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [LibraryImport("user32.dll")] private static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] private static partial int GetCursorPos(out Point point);
    [LibraryImport("user32.dll")] private static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint window, int command);
}
