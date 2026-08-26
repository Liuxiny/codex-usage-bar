using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageBar
{
    internal static class StartupRegistration
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Codex Usage Bar";

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    string value = key == null ? null : Convert.ToString(key.GetValue(ValueName));
                    return !String.IsNullOrWhiteSpace(value) && String.Equals(
                        value.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        internal static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    internal static class NativeMethods
    {
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        internal const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        internal const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        internal const uint EVENT_OBJECT_DESTROY = 0x8001;
        internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        internal const int OBJID_WINDOW = 0;
        internal const int GWL_EXSTYLE = -20;
        internal const int GWLP_HWNDPARENT = -8;
        internal const uint GW_OWNER = 4;
        internal const long WS_EX_TOOLWINDOW = 0x00000080L;
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal const int DWMWA_CLOAKED = 14;
        internal const int WM_NCLBUTTONDOWN = 0x00A1;
        internal const int HTCAPTION = 2;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);
        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hwnd, out RECT rectangle);
        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal static IntPtr SetWindowOwner(IntPtr hwnd, IntPtr owner)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, GWLP_HWNDPARENT, owner)
                : new IntPtr(SetWindowLong32(hwnd, GWLP_HWNDPARENT, owner.ToInt32()));
        }
    }

    internal static class CodexLocator
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<uint> Verified = new HashSet<uint>();

        internal static bool HasRunningProcess()
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("ChatGPT"); }
            catch { return false; }
            foreach (Process process in processes)
            {
                try
                {
                    if (IsCodexProcess((uint)process.Id)) return true;
                }
                finally { process.Dispose(); }
            }
            return false;
        }

        internal static bool IsCodexProcess(uint processId)
        {
            lock (Gate)
            {
                if (Verified.Contains(processId)) return true;
            }
            string path = QueryProcessPath(processId);
            string normalized = (path ?? String.Empty).Replace('/', '\\');
            bool valid = normalized.EndsWith("\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                (normalized.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 normalized.IndexOf("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase) >= 0);
            // Cache positive matches only. A transient access failure must not hide
            // a still-running Codex process until it receives a new PID.
            if (valid)
            {
                lock (Gate) Verified.Add(processId);
            }
            return valid;
        }

        internal static IntPtr FindBestWindow()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            NativeMethods.EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                uint processId;
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
                if (!IsCodexProcess(processId)) return true;
                int cloaked;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                NativeMethods.RECT rectangle;
                if (!NativeMethods.GetWindowRect(hwnd, out rectangle)) return true;
                long width = Math.Max(0, rectangle.Right - rectangle.Left);
                long height = Math.Max(0, rectangle.Bottom - rectangle.Top);
                if (width < 320 || height < 240) return true;
                if (hwnd == foreground)
                {
                    best = hwnd;
                    bestArea = Int64.MaxValue;
                    return false;
                }
                long area = width * height;
                if (area > bestArea)
                {
                    best = hwnd;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        internal static bool IsForegroundCodex()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(foreground, out processId);
            return IsCodexProcess(processId);
        }

        internal static bool TryClientBounds(IntPtr hwnd, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
            NativeMethods.RECT client;
            if (!NativeMethods.GetClientRect(hwnd, out client)) return false;
            var topLeft = new NativeMethods.POINT();
            topLeft.X = client.Left;
            topLeft.Y = client.Top;
            if (!NativeMethods.ClientToScreen(hwnd, ref topLeft)) return false;
            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;
            if (width < 320 || height < 240) return false;
            bounds = new Rectangle(topLeft.X, topLeft.Y, width, height);
            return true;
        }

        private static string QueryProcessPath(uint processId)
        {
            IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return String.Empty;
            try
            {
                var builder = new StringBuilder(32768);
                int size = builder.Capacity;
                return NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : String.Empty;
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
    }

    internal sealed class WindowEventMonitor : IDisposable
    {
        private readonly NativeMethods.WinEventDelegate _callback;
        private readonly Action _changed;
        private readonly List<IntPtr> _hooks = new List<IntPtr>();

        internal WindowEventMonitor(Action changed)
        {
            _changed = changed;
            _callback = OnEvent;
            Add(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
            Add(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
            Add(NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_DESTROY);
            Add(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE);
        }

        private void Add(uint minimum, uint maximum)
        {
            IntPtr hook = NativeMethods.SetWinEventHook(minimum, maximum, IntPtr.Zero, _callback, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }

        private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (eventType >= NativeMethods.EVENT_OBJECT_DESTROY && objectId != NativeMethods.OBJID_WINDOW) return;
            try { if (_changed != null) _changed(); } catch { }
        }

        public void Dispose()
        {
            foreach (IntPtr hook in _hooks) try { NativeMethods.UnhookWinEvent(hook); } catch { }
            _hooks.Clear();
        }
    }
}
