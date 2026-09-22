using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class RetryProxyTrayVerification {
    private static IntPtr desktop = IntPtr.Zero;
    private static IntPtr shellWindow = IntPtr.Zero;
    private static Thread shellThread;
    private static Exception shellError;
    private static readonly ManualResetEvent shellReady = new ManualResetEvent(false);
    private static readonly WindowProcedure shellProcedure = HandleShellMessage;
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr value, IntPtr detail);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass {
        public uint Size;
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string MenuName;
        public string Name;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage {
        public IntPtr Window;
        public uint Message;
        public UIntPtr Value;
        public IntPtr Detail;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rectangle {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo {
        public uint Size;
        public Rectangle Monitor;
        public Rectangle Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo {
        public uint Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public uint Left;
        public uint Top;
        public uint Width;
        public uint Height;
        public uint CharacterWidth;
        public uint CharacterHeight;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr ReservedData;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int left, int top, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr value, IntPtr detail);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out WindowMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref WindowMessage message);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr handle, WindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity,
        IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr value, IntPtr detail);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int left, int top, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rectangle rectangle);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static Process StartPrivateProcess(string executable, string directory) {
        if (desktop != IntPtr.Zero) { throw new InvalidOperationException("Test desktop already exists"); }
        string desktopName = "RetryProxyVerification-" + Guid.NewGuid().ToString("N");
        desktop = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        shellReady.Reset();
        shellError = null;
        shellThread = new Thread(RunPrivateShell) { IsBackground = true };
        shellThread.Start();
        if (!shellReady.WaitOne(10000)) { throw new TimeoutException("Test tray host did not start"); }
        if (shellError != null) { throw new InvalidOperationException("Test tray host failed", shellError); }
        var startup = new StartupInfo {
            Size = (uint)Marshal.SizeOf(typeof(StartupInfo)),
            Desktop = desktopName,
            Flags = 0,
            ShowWindow = 1
        };
        ProcessInformation information;
        if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero,
            IntPtr.Zero, false, 0, IntPtr.Zero, directory, ref startup, out information)) {
            int error = Marshal.GetLastWin32Error();
            ClosePrivateDesktop();
            throw new Win32Exception(error);
        }
        try { return Process.GetProcessById((int)information.ProcessId); }
        finally {
            CloseHandle(information.Thread);
            CloseHandle(information.Process);
        }
    }

    public static void ClosePrivateDesktop() {
        if (shellThread != null) {
            if (shellWindow != IntPtr.Zero) { PostMessage(shellWindow, 0x0010, UIntPtr.Zero, IntPtr.Zero); }
            if (!shellThread.Join(5000)) { throw new TimeoutException("Test tray host did not stop"); }
            shellThread = null;
        }
        if (desktop == IntPtr.Zero) { return; }
        if (!CloseDesktop(desktop)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        desktop = IntPtr.Zero;
    }

    private static IntPtr HandleShellMessage(IntPtr window, uint message, UIntPtr value, IntPtr detail) {
        if (message == 0x004A) { return new IntPtr(1); }
        if (message == 0x0002) { PostQuitMessage(0); }
        return DefWindowProc(window, message, value, detail);
    }

    private static void RunPrivateShell() {
        IntPtr instance = GetModuleHandle(null);
        try {
            if (!SetThreadDesktop(desktop)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var windowClass = new WindowClass {
                Size = (uint)Marshal.SizeOf(typeof(WindowClass)),
                Procedure = shellProcedure,
                Instance = instance,
                Name = "Shell_TrayWnd"
            };
            if (RegisterClassEx(ref windowClass) == 0) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            shellWindow = CreateWindowEx(0, windowClass.Name, "Retry Proxy test tray", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (shellWindow == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            shellReady.Set();
            WindowMessage message;
            while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0) {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error) {
            shellError = error;
            shellReady.Set();
        }
        finally {
            if (shellWindow != IntPtr.Zero) { DestroyWindow(shellWindow); shellWindow = IntPtr.Zero; }
            UnregisterClass("Shell_TrayWnd", instance);
        }
    }

    public static IntPtr FindWindow(uint processId, string title, string className) {
        // A null desktop enumerates the caller's desktop; always restrict results to the test process.
        IntPtr result = IntPtr.Zero;
        EnumDesktopWindows(desktop, (window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId) { return true; }
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (!String.IsNullOrEmpty(title) && text.ToString() != title) { return true; }
            text.Clear();
            GetClassName(window, text, text.Capacity);
            if (!String.IsNullOrEmpty(className) && text.ToString() != className) { return true; }
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindWindowByTitlePrefix(uint processId, string prefix) {
        IntPtr result = IntPtr.Zero;
        EnumDesktopWindows(desktop, (window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId) { return true; }
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (!text.ToString().StartsWith(prefix, StringComparison.Ordinal)) { return true; }
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static Rectangle Bounds(IntPtr window) {
        Rectangle bounds;
        if (!GetWindowRect(window, out bounds)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return bounds;
    }

    public static bool IsUsable(IntPtr window) {
        if (!IsWindowVisible(window) || IsIconic(window)) { return false; }
        Rectangle bounds;
        Rectangle client;
        if (!GetWindowRect(window, out bounds) || !GetClientRect(window, out client)) { return false; }
        IntPtr monitor = MonitorFromWindow(window, 0);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) { return false; }
        int dpi = (int)GetDpiForWindow(window);
        if (dpi == 0) { return false; }
        int frameWidth = bounds.Right - bounds.Left - client.Right;
        int frameHeight = bounds.Bottom - bounds.Top - client.Bottom;
        int minimumWidth = Math.Min((720 * dpi + 95) / 96, info.Work.Right - info.Work.Left - frameWidth);
        int minimumHeight = Math.Min((480 * dpi + 95) / 96, info.Work.Bottom - info.Work.Top - frameHeight);
        return client.Right >= minimumWidth && client.Bottom >= minimumHeight &&
            bounds.Top < info.Work.Bottom && bounds.Top + (32 * dpi + 95) / 96 > info.Work.Top;
    }

    public static void MinimizeDirectly(IntPtr window) {
        if (!ShowWindowAsync(window, 6)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public static void SetBounds(IntPtr window, int left, int top, int width, int height) {
        if (!SetWindowPos(window, IntPtr.Zero, left, top, width, height, 0x4414)) {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
