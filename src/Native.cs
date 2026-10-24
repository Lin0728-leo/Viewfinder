using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshShot
{
    internal static class Native
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int WM_HOTKEY = 0x0312;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    string path = Path.Combine(Path.GetTempPath(), "DshShot-debug.log");
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    internal static class Ui
    {
        public static Screen ScreenUnderCursor()
        {
            Point p = Cursor.Position;
            foreach (Screen s in Screen.AllScreens)
                if (s.Bounds.Contains(p)) return s;
            return Screen.PrimaryScreen;
        }

        public static float DpiScale(Screen scr)
        {
            try
            {
                Native.POINT pt = new Native.POINT();
                pt.X = scr.Bounds.Left + scr.Bounds.Width / 2;
                pt.Y = scr.Bounds.Top + scr.Bounds.Height / 2;
                IntPtr mon = Native.MonitorFromPoint(pt, 2);
                uint dx, dy;
                if (mon != IntPtr.Zero && Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0)
                    return dx / 96f;
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { }
            return 1f;
        }

        public static void ScaleForm(Form form, float f)
        {
            if (f <= 1.001f) return;
            form.SuspendLayout();
            form.ClientSize = new Size(
                (int)Math.Round(form.ClientSize.Width * f),
                (int)Math.Round(form.ClientSize.Height * f));
            foreach (Control c in form.Controls) ScaleTree(c, f);
            form.ResumeLayout(true);
        }

        private static void ScaleTree(Control c, float f)
        {
            c.Bounds = new Rectangle(
                (int)Math.Round(c.Left * f), (int)Math.Round(c.Top * f),
                (int)Math.Round(c.Width * f), (int)Math.Round(c.Height * f));
            // 字号用 point，物理大小已经随 DPI 变化，这里只按比例放大控件尺寸，避免字被放大两次
            foreach (Control child in c.Controls) ScaleTree(child, f);
        }
    }


    internal static class Clicker
    {
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Native.POINT p);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref Native.POINT p);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // 鼠标三个键有没有按着（用来判断人还在操作）
        public static bool AnyMouseButtonDown()
        {
            try
            {
                return (GetAsyncKeyState(0x01) & 0x8000) != 0
                    || (GetAsyncKeyState(0x02) & 0x8000) != 0
                    || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        // 系统级「距离上一次输入多久」：键盘、鼠标移动、点击、滚轮都算
        public static long SystemIdleMs()
        {
            try
            {
                LASTINPUTINFO info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref info)) return -1;
                uint now = GetTickCount();
                return (long)(now - info.dwTime);   // 无符号相减，天然处理回绕
            }
            catch { return -1; }
        }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;

        public static IntPtr Foreground()
        {
            return GetForegroundWindow();
        }

        // 前台点击：真的移动光标；restore=true 时点完把光标放回原处
        public static void LeftClick(int x, int y, bool restore)
        {
            Native.POINT old = new Native.POINT();
            bool haveOld = restore && GetCursorPos(out old);
            SetCursorPos(x, y);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            if (haveOld)
            {
                Thread.Sleep(120);
                SetCursorPos(old.X, old.Y);
            }
        }

        // 后台点击：不移动光标，直接把消息投给窗口
        public static void PostLeftClick(IntPtr hwnd, int x, int y)
        {
            if (hwnd == IntPtr.Zero) return;
            if (GetForegroundWindow() != hwnd)
            {
                PostMessage(hwnd, WM_ACTIVATE, (IntPtr)1, IntPtr.Zero);
                Thread.Sleep(20);
            }
            Native.POINT p = new Native.POINT();
            p.X = x;
            p.Y = y;
            if (!ScreenToClient(hwnd, ref p)) return;
            IntPtr lp = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, lp);
            Thread.Sleep(40);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        // 用剪贴板粘贴，避开中文输入法；结束恢复原剪贴板
        public static void PasteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string backup = null;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText()) { backup = Clipboard.GetText(); hadText = true; }
                Clipboard.SetText(text);
                Thread.Sleep(60);
                SendKeys.SendWait("^v");
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                Log.Write("paste failed: " + ex.Message);
            }
            finally
            {
                try
                {
                    if (hadText) Clipboard.SetText(backup);
                }
                catch { }
            }
        }
    }
}
