// Copyright (c) Bili Copilot. All rights reserved.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BiliFluent.Mpv.RelayCheck;

internal static partial class PlaybackSmokeSupport
{
    internal sealed unsafe class OffscreenWindow : IDisposable
    {
        private const uint Popup = 0x80000000;
        private const uint ToolWindow = 0x80;
        private const uint NoActivate = 0x08000000;
        private const uint CloseMessage = 0x10;
        private const string ClassName = "BiliFluentPlaybackSmoke";
        private static OffscreenWindow? _active;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private Exception? _failure;
        private bool _disposed;

        internal OffscreenWindow()
        {
            // libmpv's native window thread must inherit physical-pixel DPI awareness.
            if (SetProcessDpiAwarenessContext(-4) == 0)
                throw new InvalidOperationException("Per-monitor-v2 process DPI awareness could not be enabled before libmpv initialization.");
            _active = this;
            _thread = new Thread(RunWindow) { IsBackground = true, Name = "playback-smoke-offscreen-window" };
            _thread.Start();
            _ready.Wait();
            if (_failure is not null)
            {
                _thread.Join();
                _ready.Dispose();
                throw new InvalidOperationException("Offscreen window creation failed: " + _failure.GetType().Name, _failure);
            }
        }

        internal nint Handle { get; private set; }
        internal Rectangle WindowRectangle { get; private set; }
        internal Rectangle DesktopRectangle { get; private set; }
        internal double? DesktopHertz { get; private set; }
        internal int MonitorCount { get; private set; }

        internal void Verify()
        {
            var previous = SetThreadDpiAwarenessContext(-4);
            if (previous == 0) throw new InvalidOperationException("Physical offscreen coordinates cannot be verified.");
            try { VerifyCoordinates(); }
            finally { _ = SetThreadDpiAwarenessContext(previous); }
        }

        private void VerifyCoordinates()
        {
            if (_failure is not null || Handle == 0 || IsWindowVisible(Handle) == 0)
                throw new InvalidOperationException("Offscreen window is no longer visible and safe.");
            if (GetWindowRect(Handle, out var window) == 0 || GetClientRect(Handle, out var client) == 0)
                throw new InvalidOperationException("Offscreen window rectangle is unavailable.");
            var desktop = ReadDesktop();
            if (Intersects(window, desktop))
            {
                _ = PostMessageW(Handle, CloseMessage, 0, 0);
                _thread.Join();
                throw new InvalidOperationException("Offscreen window intersects the virtual desktop.");
            }

            var style = (uint)GetWindowLongPtrW(Handle, -16);
            var extended = (uint)GetWindowLongPtrW(Handle, -20);
            if ((style & Popup) == 0 || (extended & (ToolWindow | NoActivate)) != (ToolWindow | NoActivate)
                || (extended & 0x40000) != 0 || GetForegroundWindow() == Handle || client.Right - client.Left != 2560 || client.Bottom - client.Top != 1440)
                throw new InvalidOperationException("Offscreen window styles, focus, or client size do not meet the contract.");
            WindowRectangle = ToRectangle(window);
            DesktopRectangle = ToRectangle(desktop);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Handle != 0) _ = PostMessageW(Handle, CloseMessage, 0, 0);
            _thread.Join();
            _ready.Dispose();
            _active = null;
        }

        private void RunWindow()
        {
            var instance = GetModuleHandleW(null);
            ushort atom = 0;
            try
            {
                if (SetThreadDpiAwarenessContext(-4) == 0) throw new InvalidOperationException("Per-monitor physical pixel coordinates are unavailable.");
                fixed (char* name = ClassName)
                {
                    var windowClass = new WindowClass
                    {
                        Size = (uint)sizeof(WindowClass),
                        Instance = instance,
                        Name = name,
                        Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProcedure,
                    };
                    atom = RegisterClassExW(ref windowClass);
                }

                if (atom == 0) throw new InvalidOperationException("RegisterClassExW failed.");
                var desktop = ReadDesktop();
                Handle = CreateWindowExW(ToolWindow | NoActivate, ClassName, string.Empty, Popup,
                    checked(desktop.Left - 2560 - 256), checked(desktop.Top - 1440 - 256), 2560, 1440, 0, 0, instance, 0);
                if (Handle == 0) throw new InvalidOperationException("CreateWindowExW failed.");
                _ = ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE; never activate or show on a monitor.
                if (GetWindowRect(Handle, out var actual) == 0 || Intersects(actual, ReadDesktop()))
                {
                    _ = DestroyWindow(Handle);
                    Handle = 0;
                    throw new InvalidOperationException("The shown window is not outside every monitor.");
                }

                Verify();
                DesktopHertz = ReadDesktopHertz(Handle);
                MonitorCount = GetSystemMetrics(80);
                _ready.Set();
                while (GetMessageW(out var message, 0, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref message);
                    _ = DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                if (Handle != 0) _ = DestroyWindow(Handle);
                Handle = 0;
                if (atom != 0) _ = UnregisterClassW(ClassName, instance);
                _ready.Set();
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case 0x21: return 3; // WM_MOUSEACTIVATE -> MA_NOACTIVATE.
                case CloseMessage: _ = DestroyWindow(window); return 0;
                case 0x2: PostQuitMessage(0); return 0;
                case 0x7E:
                    if (GetWindowRect(window, out var rectangle) == 0 || Intersects(rectangle, ReadDesktop()))
                    {
                        if (_active is not null) _active._failure = new InvalidOperationException("Display topology exposed the offscreen rectangle.");
                        _ = DestroyWindow(window);
                        return 0;
                    }

                    break;
            }

            return DefWindowProcW(window, message, wParam, lParam);
        }

        private static NativeRect ReadDesktop()
        {
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78);
            var height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Virtual desktop bounds are unavailable.");
            return new() { Left = left, Top = top, Right = checked(left + width), Bottom = checked(top + height) };
        }

        private static bool Intersects(NativeRect a, NativeRect b)
            => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

        private static Rectangle ToRectangle(NativeRect rectangle) => new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

        private static double? ReadDesktopHertz(nint window)
        {
            var monitor = MonitorFromWindow(window, 2);
            var info = stackalloc byte[104];
            new Span<byte>(info, 104).Clear();
            *(uint*)info = 104;
            if (monitor == 0 || GetMonitorInfoW(monitor, info) == 0) return null;
            var mode = stackalloc byte[220];
            new Span<byte>(mode, 220).Clear();
            *(ushort*)(mode + 68) = 220;
            if (EnumDisplaySettingsW((char*)(info + 40), -1, mode) == 0) return null;
            var frequency = *(uint*)(mode + 184);
            return frequency > 1 ? frequency : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal nint Procedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint Background;
            internal char* Menu;
            internal char* Name;
            internal nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            internal nint Window;
            internal uint Id;
            internal nint WParam;
            internal nint LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern nint GetModuleHandleW(string? module);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int SetProcessDpiAwarenessContext(nint context);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint SetThreadDpiAwarenessContext(nint context);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern ushort RegisterClassExW(ref WindowClass windowClass);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int UnregisterClassW(string name, nint instance);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern nint CreateWindowExW(uint extended, string className, string title, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int ShowWindow(nint window, int command);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int IsWindowVisible(nint window);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetWindowRect(nint window, out NativeRect rectangle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetClientRect(nint window, out NativeRect rectangle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint GetWindowLongPtrW(nint window, int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint GetForegroundWindow();
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetrics(int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int TranslateMessage(ref Message message);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint DispatchMessageW(ref Message message);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int DestroyWindow(nint window);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int PostMessageW(nint window, uint message, nint wParam, nint lParam);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern void PostQuitMessage(int code);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern nint MonitorFromWindow(nint window, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetMonitorInfoW(nint monitor, void* info);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int EnumDisplaySettingsW(char* device, int mode, void* settings);
    }

    internal readonly record struct Rectangle(int Left, int Top, int Right, int Bottom);
}
