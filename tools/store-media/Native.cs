// Windows helpers for capture.ps1 (compiled by Windows PowerShell's Add-Type, so C# 5 only).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StoreMedia
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT rect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
    }
    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] class DesktopWallpaperClass { }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public class WindowInfo
    {
        public long Handle; public string Title; public string Class; public int Left, Top, Right, Bottom;
    }

    public static class Native
    {
        // ----- Wallpaper: Explorer applies and remembers it, unlike a registry write from this process.
        static IDesktopWallpaper Wallpaper() { return (IDesktopWallpaper)new DesktopWallpaperClass(); }
        public static string GetWallpaper()
        {
            var w = Wallpaper();
            return w.GetWallpaper(w.GetMonitorDevicePathAt(0)) ?? "";
        }
        public static int GetWallpaperPosition() { return Wallpaper().GetPosition(); }
        /// <summary>Sets one picture on every monitor; position 4 = fill.</summary>
        public static void SetWallpaper(string path, int position)
        {
            var w = Wallpaper();
            w.SetPosition(position);
            w.SetWallpaper(null, path);
        }

        // ----- Desktop icons: the same toggle as View → Show desktop icons.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        static IntPtr DefView()
        {
            var view = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            var worker = IntPtr.Zero;
            while (view == IntPtr.Zero && (worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            return view;
        }
        public static bool DesktopIconsVisible()
        {
            var list = FindWindowEx(DefView(), IntPtr.Zero, "SysListView32", null);
            return list != IntPtr.Zero && IsWindowVisible(list);
        }
        public static void SetDesktopIcons(bool visible)
        {
            if (DesktopIconsVisible() == visible) return;
            SendMessage(DefView(), 0x111, (IntPtr)0x7402, IntPtr.Zero);
            for (var i = 0; i < 40 && DesktopIconsVisible() != visible; i++) Thread.Sleep(50);
        }

        // ----- Screen.
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        public static void DpiAware() { try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch (EntryPointNotFoundException) { } }
        public static void CaptureScreen(string path, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        // ----- Windows of a process, with their visible bounds (without the invisible resize border).
        delegate bool EnumProc(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        public static WindowInfo[] Windows(int processId)
        {
            var result = new List<WindowInfo>();
            EnumWindows((hwnd, _) =>
            {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid != processId || !IsWindowVisible(hwnd)) return true;
                RECT r;
                if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(hwnd, out r);
                if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
                var title = new StringBuilder(512); GetWindowText(hwnd, title, title.Capacity);
                var cls = new StringBuilder(256); GetClassName(hwnd, cls, cls.Capacity);
                result.Add(new WindowInfo { Handle = hwnd.ToInt64(), Title = title.ToString(), Class = cls.ToString(), Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom });
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        public static void Place(long hwnd, int x, int y, int width, int height) { SetWindowPos((IntPtr)hwnd, IntPtr.Zero, x, y, width, height, 0x4 | 0x10); }
        public static void Front(long hwnd) { SetForegroundWindow((IntPtr)hwnd); }

        // ----- Input: real mouse and keyboard events, so hover states and the taskbar react as for a person.
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        static bool Mouse(uint flags, int x, int y, int data)
        {
            var input = new INPUT { type = 0 };
            input.u.mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }
        static void MoveTo(int x, int y)
        {
            var w = GetSystemMetrics(0) - 1; var h = GetSystemMetrics(1) - 1;
            Mouse(0x8001, (int)Math.Round(x * 65535.0 / w), (int)Math.Round(y * 65535.0 / h), 0);
            // Windows may drop injected input (an elevated window in front, a menu holding the mouse);
            // the pointer can still be placed directly, so every step is checked.
            POINT p; GetCursorPos(out p);
            if (Math.Abs(p.X - x) > 1 || Math.Abs(p.Y - y) > 1) SetCursorPos(x, y);
        }
        /// <summary>Glides the pointer with an ease-in-out curve, like a hand on a mouse.</summary>
        public static void Glide(int x, int y, int milliseconds)
        {
            POINT start; GetCursorPos(out start);
            var steps = Math.Max(1, milliseconds / 10);
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps; var e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                MoveTo((int)Math.Round(start.X + (x - start.X) * e), (int)Math.Round(start.Y + (y - start.Y) * e));
                Thread.Sleep(10);
            }
        }
        public static void Click()
        {
            if (!Mouse(0x2, 0, 0, 0)) throw new InvalidOperationException("Windows blocked the click: an elevated window is in front.");
            Thread.Sleep(60); Mouse(0x4, 0, 0, 0);
        }
        public static void Wheel(int notches) { Mouse(0x800, 0, 0, -120 * notches); }
        public static void Key(ushort vk) { KeyEvent(vk, 0); Thread.Sleep(30); KeyEvent(vk, 2); }
        static void KeyEvent(ushort vk, uint flags)
        {
            var input = new INPUT { type = 1 };
            input.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = flags };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }
        public static int[] Cursor() { POINT p; GetCursorPos(out p); return new[] { p.X, p.Y }; }
    }
}
