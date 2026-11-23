using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Sada;

internal static class DesktopInterop
{
    internal const int HotkeyMessage = 0x0312;
    const int StyleIndex = -16;
    const int ExtendedStyleIndex = -20;
    const long Child = 0x40000000;
    const long Popup = 0x80000000;
    const long Tool = 0x80, NoActivate = 0x08000000, Transparent = 0x20;
    const long Layered = 0x00080000, NoRedirectionBitmap = 0x00200000;
    const uint NoMove = 0x0002, NoSize = 0x0001, NoFocus = 0x0010, FrameChanged = 0x0020, NoZOrder = 0x0004;
    const uint NoOwnerZOrder = 0x0200, PreviousWindow = 3;
    static readonly nint Bottom = 1;
    static nint lastShell;
    static nint lastHost;
    static nint raisedIconLayer;

    public static nint WindowHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();
    internal static void UseDarkCaption(nint hwnd)
    {
        var enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
    }
    public static bool IsDesktopAttached(Window window)
    {
        var host = FindDesktop();
        if (host == 0) return false;
        var parent = GetChildParent(WindowHandle(window));
        return raisedIconLayer != 0 ? parent == 0 : parent == host;
    }

    public static bool SetPlacement(Window window, Placement placement, bool locked)
    {
        var hwnd = WindowHandle(window);
        var desktop = placement == Placement.Desktop;
        ChangeExStyle(hwnd, Tool, true);
        ChangeExStyle(hwnd, NoActivate, locked || desktop);
        ChangeExStyle(hwnd, Transparent, locked);
        var parent = GetChildParent(hwnd);
        if (desktop)
        {
            var host = FindDesktop();
            if (host != 0)
            {
                if (raisedIconLayer != 0)
                {
                    // Some Explorer compositors omit per-pixel WPF children. Keep this
                    // window in the ordinary desktop stack just above Progman instead.
                    if (parent != 0) Detach(hwnd);
                    window.Topmost = false;
                    return true;
                }
                if (parent != host)
                {
                    if (parent != 0) Detach(hwnd);
                    PlaceOnDesktop(hwnd, host);
                }
                if (GetParent(hwnd) == host)
                {
                    // Keep below the icon host and other windows in the desktop stack.
                    return true;
                }
            }
            if (GetChildParent(hwnd) != 0) Detach(hwnd);
            return false;
        }
        if (parent != 0) Detach(hwnd);
        window.Topmost = placement == Placement.OnTop;
        return true;
    }

    static void PlaceOnDesktop(nint hwnd, nint host)
    {
        var hasRect = GetWindowRect(hwnd, out var bounds);
        var style = GetWindowLongPtr(hwnd, StyleIndex).ToInt64();
        SetWindowLongPtr(hwnd, StyleIndex, (nint)((style | Child) & ~Popup));
        SetParent(hwnd, host);
        if (GetParent(hwnd) != host)
        {
            SetWindowLongPtr(hwnd, StyleIndex, (nint)style);
            return;
        }
        if (hasRect)
        {
            var position = new NativePoint { X = bounds.Left, Y = bounds.Top };
            ScreenToClient(host, ref position);
            SetWindowPos(hwnd, Bottom, position.X, position.Y, 0, 0, NoSize | NoFocus | FrameChanged);
        }
    }

    static void Detach(nint hwnd)
    {
        var hasRect = GetWindowRect(hwnd, out var rect);
        SetParent(hwnd, 0);
        var style = GetWindowLongPtr(hwnd, StyleIndex).ToInt64();
        SetWindowLongPtr(hwnd, StyleIndex, (nint)((style & ~Child) | Popup));
        if (hasRect) SetWindowPos(hwnd, 0, rect.Left, rect.Top, 0, 0, NoSize | NoFocus | FrameChanged | NoZOrder);
    }

    static nint FindDesktop()
    {
        var shell = FindWindow("Progman", null);
        if (shell == 0) return 0;
        if (lastShell == shell && lastHost != 0 && IsWindow(lastHost) &&
            (raisedIconLayer == 0 || IsWindow(raisedIconLayer))) return lastHost;
        lastShell = shell;
        lastHost = 0;
        raisedIconLayer = 0;
        SendMessageTimeout(shell, 0x052C, 0xD, 0, 2, 350, out _);
        SendMessageTimeout(shell, 0x052C, 0xD, 1, 2, 350, out _);
        var icons = FindWindowEx(shell, 0, "SHELLDLL_DefView", null);
        var wallpaper = FindWindowEx(shell, 0, "WorkerW", null);
        var shellStyle = GetWindowLongPtr(shell, ExtendedStyleIndex).ToInt64();
        var iconStyle = icons == 0 ? 0 : GetWindowLongPtr(icons, ExtendedStyleIndex).ToInt64();
        if (icons != 0 && wallpaper != 0 && ((shellStyle & NoRedirectionBitmap) != 0 || (iconStyle & Layered) != 0))
        {
            raisedIconLayer = icons;
            return lastHost = shell;
        }
        if (icons != 0 && wallpaper != 0) return lastHost = wallpaper;
        EnumWindows((candidate, _) =>
        {
            if (FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) == 0) return true;
            lastHost = FindWindowEx(0, candidate, "WorkerW", null);
            return lastHost == 0;
        }, 0);
        return lastHost;
    }

    static void ChangeExStyle(nint window, long mask, bool enabled)
    {
        var previous = GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64();
        var next = enabled ? previous | mask : previous & ~mask;
        if (next != previous) SetWindowLongPtr(window, ExtendedStyleIndex, (nint)next);
    }

    static nint GetChildParent(nint window) => (GetWindowLongPtr(window, StyleIndex).ToInt64() & Child) != 0 ? GetParent(window) : 0;

    public static void SetScreenBounds(Window window, System.Drawing.Rectangle bounds)
    {
        var hwnd = WindowHandle(window);
        var parent = GetChildParent(hwnd);
        var point = new NativePoint { X = bounds.X, Y = bounds.Y };
        if (parent != 0) ScreenToClient(parent, ref point);
        SetWindowPos(hwnd, 0, point.X, point.Y, bounds.Width, bounds.Height, NoFocus | NoZOrder);
    }

    public static void OrderDesktopWindows(IEnumerable<Window> frontToBack)
    {
        var host = FindDesktop();
        if (host == 0) return;
        if (raisedIconLayer != 0)
        {
            var handles = frontToBack.Select(WindowHandle).Where(handle => GetChildParent(handle) == 0).ToArray();
            var after = GetWindow(host, PreviousWindow);
            while (after != 0 && handles.Contains(after)) after = GetWindow(after, PreviousWindow);
            foreach (var handle in handles)
            {
                if (IsIconic(handle) || !IsWindowVisible(handle)) ShowWindow(handle, 4);
                if (SetWindowPos(handle, after, 0, 0, 0, 0, NoMove | NoSize | NoFocus | NoOwnerZOrder))
                    after = handle;
            }
            return;
        }
        foreach (var handle in frontToBack.Select(WindowHandle))
        {
            if (GetChildParent(handle) == host)
                SetWindowPos(handle, Bottom, 0, 0, 0, 0, NoMove | NoSize | NoFocus);
        }
    }

    public static Point Pointer(Window window)
    {
        var position = Forms.Cursor.Position;
        return window.PointFromScreen(new Point(position.X, position.Y));
    }

    public static bool Covered(Window window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || !GetWindowRect(foreground, out var active)) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == Environment.ProcessId || foreground == lastShell || foreground == lastHost) return false;
        if (!GetWindowRect(WindowHandle(window), out var bounds)) return false;
        return active.Left <= bounds.Left && active.Top <= bounds.Top && active.Right >= bounds.Right && active.Bottom >= bounds.Bottom;
    }

    public static string ActiveProcess()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] static extern nint GetParent(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint handle, out NativeRect rectangle);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] static extern nint SendMessageTimeout(nint hwnd, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    delegate bool EnumProc(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
}
