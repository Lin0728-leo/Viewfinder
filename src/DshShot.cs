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

    internal static class Program
    {
        public const string AppName = "DSH 取景器";
        public const string AppId = "DshShot";

        // 由启动器设置：程序所在目录（DLL 被 PowerShell 宿主加载时用）
        public static string BaseDir = "";

        // 第二个实例用它叫醒第一个实例弹窗
        public const string ShowEventName = "Global\\DshShot.ShowWindow";

        // 开机自启时由 launch.ps1 置为 true，不弹窗口，只留在托盘
        public static bool Silent;

        public static void Run()
        {
            Main(new string[0]);
        }

        public const string DefaultPrompt =
            "你是答题助手。看到题目就直接给答案，不要任何分析、推理、解释或知识点说明。\n" +
            "格式要求：\n" +
            "1. 单选题、多选题：只写题号和选项字母，字母后面跟选项内容，例如「3. C 國」；多选题字母连写，例如「5. ABD」；\n" +
            "2. 判断题：只写「对」或「错」；\n" +
            "3. 填空题：只写要填进去的内容，多个空用「；」隔开；\n" +
            "4. 计算题、编程题：只给最终结果或最终代码，不给推导过程；\n" +
            "5. 图里有多道题时，按题号一行一道，不要写多余的话；\n" +
            "6. 看不清或图里没有题目时，只回「看不清」。\n" +
            "不要寒暄，不要复述题干，不要加“答案：”“解析：”之类的标签，直接输出结果。";

        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch
            {
                try { Native.SetProcessDpiAwareness(2); } catch { }
            }

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768;
            ServicePointManager.Expect100Continue = false;

            Log.Write("main start, args=" + args.Length);
            if (args.Length > 0) return RunCli(args);

            bool created;
            using (Mutex mutex = new Mutex(true, "Global\\" + AppId, out created))
            {
                if (!created)
                {
                    // 已经在运行：通知已有实例把窗口弹到前台，然后自己退出
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            ev.Set();
                        }
                    }
                    catch
                    {
                        MessageBox.Show(AppName + " 已经在运行（看右下角托盘图标）。", AppName,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return 0;
                }

                AppDomain.CurrentDomain.SetData("DshShot.ShowEvent", null);
                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    TrayContext ctx = new TrayContext();
                    ctx.ListenShowSignal(showEvent);
                    Application.Run(ctx);
                    GC.KeepAlive(mutex);
                    GC.KeepAlive(showEvent);
                }
            }
            return 0;
        }

        private static int RunCli(string[] args)
        {
            if (args.Length >= 4 && args[0].ToLowerInvariant() == "--crop")
            {
                using (Bitmap src = new Bitmap(args[1]))
                {
                    string note;
                    Rectangle box = AutoCrop.Detect(src, out note);
                    using (Bitmap cut = src.Clone(box, PixelFormat.Format32bppArgb))
                    {
                        cut.Save(args[2], ImageFormat.Png);
                    }
                    File.WriteAllText(args[3], note + " | " + box, Encoding.UTF8);
                }
                return 0;
            }

            string mode = "";
            string outPath = "";
            Rectangle rect = Rectangle.Empty;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--shot" && i + 1 < args.Length) mode = args[++i].ToLowerInvariant();
                else if (a == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (a == "--rect" && i + 1 < args.Length)
                {
                    string[] p = args[++i].Split(',');
                    if (p.Length == 4)
                        rect = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                }
            }

            if (outPath.Length == 0)
                outPath = Path.Combine(Path.GetTempPath(), "dshshot-" + DateTime.Now.ToString("HHmmss") + ".png");

            Bitmap bmp;
            if (mode == "window") bmp = Capture.Window(Capture.ForegroundWindow());
            else if (mode == "region" && rect != Rectangle.Empty) bmp = Capture.ScreenRegion(rect);
            else bmp = Capture.FullScreen();

            if (bmp == null) return 2;
            using (bmp)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                bmp.Save(outPath, ImageFormat.Png);
            }
            return 0;
        }
    }

    internal static class Capture
    {
        public static IntPtr ForegroundWindow()
        {
            return Native.GetForegroundWindow();
        }

        public static Rectangle WindowBounds(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Rectangle.Empty;

            Native.RECT dwm = new Native.RECT();
            Native.RECT win = new Native.RECT();

            bool hasDwm = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out dwm,
                Marshal.SizeOf(typeof(Native.RECT))) == 0;
            bool hasWin = Native.GetWindowRect(hwnd, out win);

            Rectangle rect;
            if (hasDwm && hasWin)
            {
                // 个别无边框/分层窗口 DWM 会给出离谱的矩形，面积差太多就用 GetWindowRect
                Rectangle a = Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
                Rectangle b = Rectangle.FromLTRB(win.Left, win.Top, win.Right, win.Bottom);
                if (a.Width <= 0 || a.Height <= 0) rect = b;
                else if (b.Width <= 0 || b.Height <= 0) rect = a;
                else
                {
                    double ratio = (double)((long)a.Width * a.Height) / ((long)b.Width * b.Height);
                    rect = (ratio < 0.75 || ratio > 1.33) ? b : a;
                }
            }
            else if (hasDwm)
            {
                rect = Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
            }
            else if (hasWin)
            {
                rect = Rectangle.FromLTRB(win.Left, win.Top, win.Right, win.Bottom);
            }
            else
            {
                return Rectangle.Empty;
            }

            if (rect.Width <= 0 || rect.Height <= 0) return Rectangle.Empty;
            return rect;
        }

        public static Bitmap FullScreen()
        {
            return ScreenRegion(SystemInformation.VirtualScreen);
        }

        public static Bitmap ScreenRegion(Rectangle r)
        {
            r = Rectangle.Intersect(r, SystemInformation.VirtualScreen);
            if (r.Width <= 0 || r.Height <= 0) return null;

            Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        public static Bitmap Window(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            if (Native.IsIconic(hwnd)) return null;
            Rectangle r = WindowBounds(hwnd);
            if (r.Width < 8 || r.Height < 8) return null;
            return ScreenRegion(r);
        }
    }

    // 自动裁掉大片留白：先找"有内容"的行列，再挑出墨迹最集中的连续块
    internal static class AutoCrop
    {
        private const int SampleWidth = 420;
        private const int EdgeDiff = 45;

        public static Rectangle Detect(Bitmap src, out string note)
        {
            note = "";
            Rectangle full = new Rectangle(0, 0, src.Width, src.Height);
            try
            {
                double k = Math.Min(1.0, (double)SampleWidth / src.Width);
                int sw = Math.Max(1, (int)Math.Round(src.Width * k));
                int sh = Math.Max(1, (int)Math.Round(src.Height * k));
                if (sw < 60 || sh < 60) { note = "too small"; return full; }

                byte[] px;
                int stride;
                using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.None;
                        g.DrawImage(src, new Rectangle(0, 0, sw, sh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                    }
                    BitmapData bd = small.LockBits(new Rectangle(0, 0, sw, sh),
                        ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    stride = bd.Stride;
                    px = new byte[stride * sh];
                    Marshal.Copy(bd.Scan0, px, 0, px.Length);
                    small.UnlockBits(bd);
                }

                // 用局部梯度判断"有内容"：文字/控件边缘梯度大，纯色留白几乎为 0，
                // 不依赖背景色，深浅主题都适用
                bool[] ink = new bool[sw * sh];
                int[] rowInk = new int[sh];
                for (int y = 0; y < sh; y++)
                {
                    int row = y * stride;
                    int next = Math.Min(y + 1, sh - 1) * stride;
                    int cnt = 0;
                    for (int x = 0; x < sw; x++)
                    {
                        int i = row + x * 3;
                        int i2 = row + Math.Min(x + 1, sw - 1) * 3;
                        int i3 = next + x * 3;
                        int d = Math.Abs(px[i] - px[i2]) + Math.Abs(px[i + 1] - px[i2 + 1]) + Math.Abs(px[i + 2] - px[i2 + 2])
                              + Math.Abs(px[i] - px[i3]) + Math.Abs(px[i + 1] - px[i3 + 1]) + Math.Abs(px[i + 2] - px[i3 + 2]);
                        if (d > EdgeDiff && x > 0 && y > 0 && x < sw - 1 && y < sh - 1)
                        {
                            ink[y * sw + x] = true; cnt++;
                        }
                    }
                    rowInk[y] = cnt;
                }

                // 通栏的分隔线/黑条会让"每一列都有内容"，先把这类整行整列找出来，
                // 统计列时忽略整行线，统计行时忽略整列线
                int[] colRaw = new int[sw];
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        if (ink[y * sw + x]) colRaw[x]++;

                bool[] lineRow = new bool[sh];
                bool[] lineCol = new bool[sw];
                for (int y = 0; y < sh; y++) lineRow[y] = rowInk[y] > sw * 0.5;
                for (int x = 0; x < sw; x++) lineCol[x] = colRaw[x] > sh * 0.5;

                int[] rowCnt = new int[sh];
                int[] colCnt = new int[sw];
                for (int y = 0; y < sh; y++)
                {
                    for (int x = 0; x < sw; x++)
                    {
                        if (!ink[y * sw + x]) continue;
                        if (!lineCol[x]) rowCnt[y]++;
                        if (!lineRow[y]) colCnt[x]++;
                    }
                }

                int minPix = Math.Max(2, sw / 200);
                int top = 0, bottom = sh - 1;
                while (top < bottom && rowCnt[top] < minPix) top++;
                while (bottom > top && rowCnt[bottom] < minPix) bottom--;

                int left = 0, right = sw - 1;
                while (left < right && colCnt[left] < minPix) left++;
                while (right > left && colCnt[right] < minPix) right--;


                // 被大段空白隔开的内容块里，只保留最高的那块：
                // 浏览器工具栏、任务栏这类细条会被丢掉
                int bandH = bottom - top + 1;
                int gapMin = Math.Max(4, (int)(sh * 0.04));
                List<int[]> segs = new List<int[]>();
                int segStart = top;
                for (int y = top; y <= bottom; y++)
                {
                    if (rowCnt[y] >= minPix) continue;
                    int z = y;
                    while (z <= bottom && rowCnt[z] < minPix) z++;
                    if (z - y >= gapMin)
                    {
                        if (y - 1 >= segStart) segs.Add(new int[] { segStart, y - 1 });
                        segStart = z;
                    }
                    y = z - 1;
                }
                if (segStart <= bottom) segs.Add(new int[] { segStart, bottom });

                // 只有贴着画面最上/最下沿的细条才丢掉（浏览器工具栏、任务栏），
                // 中间的内容块无论多矮都不动
                if (segs.Count >= 2 && segs[0][0] <= sh * 0.02 && (segs[0][1] - segs[0][0] + 1) < bandH * 0.2)
                {
                    top = segs[1][0];
                    segs.RemoveAt(0);
                }
                if (segs.Count >= 2
                    && segs[segs.Count - 1][1] >= sh * 0.98
                    && (segs[segs.Count - 1][1] - segs[segs.Count - 1][0] + 1) < bandH * 0.2)
                {
                    bottom = segs[segs.Count - 2][1];
                }

                // 同理按列再切一次（右边栏这类）
                if (bottom > top)
                {
                    int[] colCnt2 = new int[sw];
                    for (int y = top; y <= bottom; y++)
                        for (int x = 0; x < sw; x++)
                            if (ink[y * sw + x] && !lineRow[y]) colCnt2[x]++;

                    // 丢掉顶部细条后再按列重新收边（否则工具栏的文字会把左边界撑开）
                    left = 0; right = sw - 1;
                    while (left < right && colCnt2[left] < minPix) left++;
                    while (right > left && colCnt2[right] < minPix) right--;

                    int bandW = right - left + 1;
                    int gapMinX = Math.Max(4, (int)(sw * 0.04));
                    List<int[]> csegs = new List<int[]>();
                    int cs = left;
                    for (int x = left; x <= right; x++)
                    {
                        if (colCnt2[x] >= minPix) continue;
                        int z = x;
                        while (z <= right && colCnt2[z] < minPix) z++;
                        if (z - x >= gapMinX)
                        {
                            if (x - 1 >= cs) csegs.Add(new int[] { cs, x - 1 });
                            cs = z;
                        }
                        x = z - 1;
                    }
                    if (cs <= right) csegs.Add(new int[] { cs, right });

                    if (csegs.Count >= 2 && csegs[0][0] <= sw * 0.02 && (csegs[0][1] - csegs[0][0] + 1) < bandW * 0.25)
                    {
                        left = csegs[1][0];
                        csegs.RemoveAt(0);
                    }
                    if (csegs.Count >= 2
                        && csegs[csegs.Count - 1][1] >= sw * 0.98
                        && (csegs[csegs.Count - 1][1] - csegs[csegs.Count - 1][0] + 1) < bandW * 0.25)
                    {
                        right = csegs[csegs.Count - 2][1];
                    }
                }

                // 映射回原图并留一点边
                int padX = Math.Max(10, (int)((right - left) * 0.02));
                int padY = Math.Max(10, (int)((bottom - top) * 0.02));
                int x0 = (int)Math.Floor(left / k) - padX;
                int y0 = (int)Math.Floor(top / k) - padY;
                int x1 = (int)Math.Ceiling(right / k) + padX;
                int y1 = (int)Math.Ceiling(bottom / k) + padY;

                Rectangle r = Rectangle.FromLTRB(x0, y0, x1, y1);
                r = Rectangle.Intersect(r, full);
                if (r.Width < 80 || r.Height < 80) { note = "tiny"; return full; }

                double ratio = (double)r.Width * r.Height / ((double)full.Width * full.Height);
                if (ratio < 0.12 || ratio > 0.96) { note = "skip " + ratio.ToString("0.00"); return full; }

                note = string.Format("{0}x{1} -> {2}x{3} ({4:0}%)", full.Width, full.Height, r.Width, r.Height, ratio * 100);
                return r;
            }
            catch (Exception ex)
            {
                note = "err " + ex.Message;
                return full;
            }
        }

        private static void BackgroundColor(byte[] px, int stride, int sw, int sh, out int r, out int g, out int b)
        {
            List<int> rs = new List<int>(), gs = new List<int>(), bs = new List<int>();
            int stepX = Math.Max(1, sw / 80), stepY = Math.Max(1, sh / 80);
            for (int x = 0; x < sw; x += stepX)
            {
                int i0 = x * 3, i1 = (sh - 1) * stride + x * 3;
                rs.Add(px[i0 + 2]); gs.Add(px[i0 + 1]); bs.Add(px[i0]);
                rs.Add(px[i1 + 2]); gs.Add(px[i1 + 1]); bs.Add(px[i1]);
            }
            for (int y = 0; y < sh; y += stepY)
            {
                int i0 = y * stride, i1 = y * stride + (sw - 1) * 3;
                rs.Add(px[i0 + 2]); gs.Add(px[i0 + 1]); bs.Add(px[i0]);
                rs.Add(px[i1 + 2]); gs.Add(px[i1 + 1]); bs.Add(px[i1]);
            }
            r = Median(rs); g = Median(gs); b = Median(bs);
        }

        private static int Median(List<int> v)
        {
            if (v.Count == 0) return 255;
            v.Sort();
            return v[v.Count / 2];
        }

        private static int[] Slice(int[] a, int from, int to)
        {
            int n = Math.Max(0, to - from + 1);
            int[] r = new int[n];
            for (int i = 0; i < n; i++) r[i] = a[from + i];
            return r;
        }

        // 返回最"重"的连续区段（允许小缺口），长度不足 minLen 的忽略
        private static int[] BestRun(int[] counts, int gapTol, int minLen)
        {
            int n = counts.Length;
            if (n == 0) return new int[] { 0, 0 };
            int bestStart = 0, bestEnd = n - 1;
            long bestSum = -1;
            int i = 0;
            while (i < n)
            {
                if (counts[i] <= 0) { i++; continue; }
                int start = i, last = i;
                long sum = 0;
                while (i < n)
                {
                    if (counts[i] > 0) { sum += counts[i]; last = i; i++; }
                    else
                    {
                        int j = i;
                        while (j < n && counts[j] <= 0) j++;
                        if (j - i > gapTol || j >= n) break;
                        i = j;
                    }
                }
                if (last - start + 1 >= minLen && sum > bestSum)
                {
                    bestSum = sum; bestStart = start; bestEnd = last;
                }
            }
            if (bestSum < 0) return new int[] { 0, n - 1 };
            return new int[] { bestStart, bestEnd };
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        private readonly List<int> _ids = new List<int>();

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(int id, uint mods, uint vk)
        {
            bool ok = Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, vk);
            if (ok) _ids.Add(id);
            return ok;
        }

        public void UnregisterAll()
        {
            foreach (int id in _ids) Native.UnregisterHotKey(Handle, id);
            _ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null)
            {
                try { Pressed(m.WParam.ToInt32()); }
                catch (Exception ex) { Log.Write("hotkey handler threw: " + ex); }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            try { DestroyHandle(); } catch { }
        }
    }

    // 多屏 DPI 不同，WinForms 不会自动跟着走，这里统一按目标屏幕缩放一遍
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

    internal sealed class UsageInfo
    {
        public int Prompt;
        public int Completion;
        public int CacheHit;
        public int CacheMiss;
        public int Reasoning;

        public int Total { get { return Prompt + Completion; } }
    }

    // 单价表：元 / 百万 tokens（高峰时段价）；空闲时段按官网规则减半。
    internal static class Pricing
    {
        private sealed class Rate
        {
            public readonly double Hit;
            public readonly double Miss;
            public readonly double Out;
            public Rate(double hit, double miss, double output) { Hit = hit; Miss = miss; Out = output; }
        }

        private static readonly Dictionary<string, Rate> Table = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            { "deepseek-flash", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-flash", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-flash-vision-exp", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-pro", new Rate(0.30, 9.00, 27.00) }
        };

        // 北京时间周一至周五 9:00-12:00、14:00-18:00 为高峰，其余时段减半（法定节假日未单独处理）
        public static bool IsPeak(DateTime t)
        {
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday) return false;
            double h = t.Hour + t.Minute / 60.0;
            return (h >= 9.0 && h < 12.0) || (h >= 14.0 && h < 18.0);
        }

        public static double Cost(string model, UsageInfo u)
        {
            Rate r;
            if (model == null || !Table.TryGetValue(model.Trim(), out r)) r = Table["deepseek-flash"];
            long hit = u.CacheHit;
            long miss = u.CacheMiss > 0 ? u.CacheMiss : Math.Max(0, u.Prompt - u.CacheHit);
            double peak = IsPeak(DateTime.Now) ? 1.0 : 0.5;
            return (hit * r.Hit + miss * r.Miss + u.Completion * r.Out) / 1000000.0 * peak;
        }

        public static string Money(double v)
        {
            if (v <= 0) return "¥0";
            if (v < 0.01) return "¥" + v.ToString("0.0000", CultureInfo.InvariantCulture);
            if (v < 1) return "¥" + v.ToString("0.000", CultureInfo.InvariantCulture);
            return "¥" + v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public static string Tok(long n)
        {
            if (n < 10000) return n.ToString("N0");
            return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }
    }

    internal sealed class AppConfig
    {
        public string ShotsDir;
        public string ApiBase;
        public string ApiModel;
        public string ApiKey;
        public bool AutoAnalyze;
        public string Prompt;
        public int MaxEdge;
        public int JpegQuality;
        public bool AutoCrop;
        public long StatPrompt;
        public long StatCompletion;
        public long StatCacheHit;
        public long StatCacheMiss;
        public double StatCost;

        // 自动作答
        public int AutoMode;          // 0 关 / 1 预览 / 2 自动点击
        public int AutoIntervalMs;    // 抓屏间隔
        public string AutoBackend;    // sendinput / postmessage
        public bool AutoVerify;       // 点击前复检选项文字
        public bool AutoType;         // 填空题自动粘贴
        public int AutoMaxClicks;     // 每轮最多点几次
        public bool AutoRetryThink;   // 答案不全时用思考模式重试
        public string HotkeyPreview;  // 预览热键 "mods,vk"
        public string HotkeyClick;    // 自动点击热键 "mods,vk"
        public int AutoMaxEdge;       // 自动作答专用：送模型的最长边
        public int AutoQuality;       // 自动作答专用：JPEG 质量
        public bool AutoSplitScan;    // 上下分两半各识别一次，减少底部漏题
        public int AutoIdleMs;        // 鼠标静止多久才开始识别
        public bool AutoDoubleCheck;  // 逐题裁剪重识别 + 两次答案比对
        public bool AutoThinkTie;     // 两次答案冲突时，用思考模式裁决第三次
        public bool AutoWebSearch;    // 裁决不了时联网搜一次
        public bool NoCompress;       // 不缩放、不转 JPEG，原图 PNG 送模型
        public int MaxShots;          // 截图最多保留多少张，0 = 不限制

        public AppConfig()
        {
            ShotsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DSH-Shots");
            ApiBase = "";
            ApiModel = "";
            ApiKey = "";
            AutoAnalyze = true;
            Prompt = Program.DefaultPrompt;
            MaxEdge = 1024;
            JpegQuality = 80;
            AutoCrop = true;
            AutoMode = 0;
            AutoIntervalMs = 3000;
            AutoBackend = "sendinput";
            AutoVerify = true;
            AutoType = false;
            AutoMaxClicks = 6;
            AutoRetryThink = true;
            HotkeyPreview = "3,81";   // Ctrl+Alt+Q
            HotkeyClick = "3,88";     // Ctrl+Alt+X
            AutoMaxEdge = 1600;
            AutoQuality = 88;
            AutoSplitScan = true;
            AutoIdleMs = 2000;
            AutoDoubleCheck = true;
            AutoThinkTie = true;
            AutoWebSearch = true;
            NoCompress = true;
            MaxShots = 200;
        }

        public bool HasApi
        {
            get { return ApiBase.Length > 0 && ApiModel.Length > 0 && ApiKey.Length > 0; }
        }

        private static string ConfigPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DshShot");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.ini");
        }

        public static AppConfig Load()
        {
            AppConfig c = new AppConfig();
            try
            {
                string path = ConfigPath();
                if (!File.Exists(path)) return c;

                string promptFromFile = null;
                string enc = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    int i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    string key = raw.Substring(0, i).Trim();
                    string val = raw.Substring(i + 1).Trim();

                    if (key == "ShotsDir" && val.Length > 0) c.ShotsDir = val;
                    else if (key == "ApiBase") c.ApiBase = val;
                    else if (key == "ApiModel") c.ApiModel = val;
                    else if (key == "ApiKey") c.ApiKey = val;
                    else if (key == "ApiKeyEnc") enc = val;
                    else if (key == "AutoAnalyze") c.AutoAnalyze = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "Prompt" && val.Length > 0) promptFromFile = val.Replace("\\n", "\n").Replace("\\r", "\r");
                    else if (key == "MaxEdge") { int n; if (int.TryParse(val, out n) && n >= 400) c.MaxEdge = n; }
                    else if (key == "JpegQuality") { int n; if (int.TryParse(val, out n) && n >= 40 && n <= 95) c.JpegQuality = n; }
                    else if (key == "StatPrompt") long.TryParse(val, out c.StatPrompt);
                    else if (key == "StatCompletion") long.TryParse(val, out c.StatCompletion);
                    else if (key == "StatCacheHit") long.TryParse(val, out c.StatCacheHit);
                    else if (key == "StatCacheMiss") long.TryParse(val, out c.StatCacheMiss);
                    else if (key == "StatCost") double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out c.StatCost);
                    else if (key == "AutoMode") { int n; if (int.TryParse(val, out n) && n >= 0 && n <= 2) c.AutoMode = n; }
                    else if (key == "AutoIntervalMs") { int n; if (int.TryParse(val, out n) && n >= 800 && n <= 60000) c.AutoIntervalMs = n; }
                    else if (key == "AutoBackend") c.AutoBackend = val;
                    else if (key == "AutoVerify") c.AutoVerify = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "AutoType") c.AutoType = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "AutoMaxClicks") { int n; if (int.TryParse(val, out n) && n >= 1 && n <= 40) c.AutoMaxClicks = n; }
                    else if (key == "AutoRetryThink") c.AutoRetryThink = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "HotkeyPreview" && val.Length > 0) c.HotkeyPreview = val;
                    else if (key == "HotkeyClick" && val.Length > 0) c.HotkeyClick = val;
                    else if (key == "AutoMaxEdge") { int n; if (int.TryParse(val, out n) && n >= 512 && n <= 4096) c.AutoMaxEdge = n; }
                    else if (key == "AutoQuality") { int n; if (int.TryParse(val, out n) && n >= 50 && n <= 98) c.AutoQuality = n; }
                    else if (key == "AutoSplitScan") c.AutoSplitScan = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "AutoIdleMs") { int n; if (int.TryParse(val, out n) && n >= 300 && n <= 20000) c.AutoIdleMs = n; }
                    else if (key == "AutoDoubleCheck") c.AutoDoubleCheck = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "AutoThinkTie") c.AutoThinkTie = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "AutoWebSearch") c.AutoWebSearch = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "NoCompress") c.NoCompress = (val == "1" || val.ToLowerInvariant() == "true");
                    else if (key == "MaxShots") { int n; if (int.TryParse(val, out n) && n >= 0 && n <= 100000) c.MaxShots = n; }
                }

                if (promptFromFile != null) c.Prompt = promptFromFile;
                if (enc != null && enc.Length > 0) c.ApiKey = Unprotect(enc);
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("ShotsDir=" + ShotsDir);
                sb.AppendLine("ApiBase=" + ApiBase);
                sb.AppendLine("ApiModel=" + ApiModel);
                sb.AppendLine("ApiKeyEnc=" + Protect(ApiKey));
                sb.AppendLine("AutoAnalyze=" + (AutoAnalyze ? "1" : "0"));
                sb.AppendLine("MaxEdge=" + MaxEdge);
                sb.AppendLine("JpegQuality=" + JpegQuality);
                sb.AppendLine("StatPrompt=" + StatPrompt);
                sb.AppendLine("StatCompletion=" + StatCompletion);
                sb.AppendLine("StatCacheHit=" + StatCacheHit);
                sb.AppendLine("StatCacheMiss=" + StatCacheMiss);
                sb.AppendLine("StatCost=" + StatCost.ToString("0.000000", CultureInfo.InvariantCulture));
                sb.AppendLine("AutoMode=" + AutoMode);
                sb.AppendLine("AutoIntervalMs=" + AutoIntervalMs);
                sb.AppendLine("AutoBackend=" + AutoBackend);
                sb.AppendLine("AutoVerify=" + (AutoVerify ? "1" : "0"));
                sb.AppendLine("AutoType=" + (AutoType ? "1" : "0"));
                sb.AppendLine("AutoMaxClicks=" + AutoMaxClicks);
                sb.AppendLine("AutoRetryThink=" + (AutoRetryThink ? "1" : "0"));
                sb.AppendLine("HotkeyPreview=" + HotkeyPreview);
                sb.AppendLine("HotkeyClick=" + HotkeyClick);
                sb.AppendLine("AutoMaxEdge=" + AutoMaxEdge);
                sb.AppendLine("AutoQuality=" + AutoQuality);
                sb.AppendLine("AutoSplitScan=" + (AutoSplitScan ? "1" : "0"));
                sb.AppendLine("AutoIdleMs=" + AutoIdleMs);
                sb.AppendLine("AutoDoubleCheck=" + (AutoDoubleCheck ? "1" : "0"));
                sb.AppendLine("AutoThinkTie=" + (AutoThinkTie ? "1" : "0"));
                sb.AppendLine("AutoWebSearch=" + (AutoWebSearch ? "1" : "0"));
                sb.AppendLine("NoCompress=" + (NoCompress ? "1" : "0"));
                sb.AppendLine("MaxShots=" + MaxShots);
                sb.AppendLine("Prompt=" + Prompt.Replace("\r", "\\r").Replace("\n", "\\n"));
                File.WriteAllText(ConfigPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        private static string Protect(string plain)
        {
            if (plain == null || plain.Length == 0) return "";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(plain);
                DATA_BLOB inBlob = new DATA_BLOB();
                DATA_BLOB outBlob = new DATA_BLOB();
                DATA_BLOB entropy = new DATA_BLOB();
                inBlob.cbData = bytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
                try
                {
                    if (!CryptProtectData(ref inBlob, "DshShot", ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                        return "plain:" + plain;
                    byte[] result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return Convert.ToBase64String(result);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBlob.pbData);
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            catch
            {
                return "plain:" + plain;
            }
        }

        private static string Unprotect(string stored)
        {
            if (stored == null || stored.Length == 0) return "";
            if (stored.StartsWith("plain:")) return stored.Substring(6);
            try
            {
                byte[] bytes = Convert.FromBase64String(stored);
                DATA_BLOB inBlob = new DATA_BLOB();
                DATA_BLOB outBlob = new DATA_BLOB();
                DATA_BLOB entropy = new DATA_BLOB();
                inBlob.cbData = bytes.Length;
                inBlob.pbData = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, inBlob.pbData, bytes.Length);
                try
                {
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                        return "";
                    byte[] result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(result);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBlob.pbData);
                    if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                }
            }
            catch
            {
                return "";
            }
        }
    }

    internal static class AiClient
    {
        public static string BuildEndpoint(string baseUrl)
        {
            string b = baseUrl.Trim();
            if (b.EndsWith("/chat/completions")) return b;
            if (b.EndsWith("/")) b = b.Substring(0, b.Length - 1);
            return b + "/chat/completions";
        }

        public static Dictionary<string, object> UserMessage(string text, string dataUrl)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["role"] = "user";

            if (dataUrl == null)
            {
                msg["content"] = text;
                return msg;
            }

            List<object> parts = new List<object>();
            Dictionary<string, object> tp = new Dictionary<string, object>();
            tp["type"] = "text";
            tp["text"] = text;
            parts.Add(tp);

            Dictionary<string, object> ip = new Dictionary<string, object>();
            ip["type"] = "image_url";
            Dictionary<string, object> url = new Dictionary<string, object>();
            url["url"] = dataUrl;
            ip["image_url"] = url;
            parts.Add(ip);

            msg["content"] = parts;
            return msg;
        }

        public static Size FitTo(int w, int h, int maxEdge)
        {
            int max = Math.Max(w, h);
            if (maxEdge > 0 && max > maxEdge)
            {
                double k = (double)maxEdge / max;
                return new Size(Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
            }
            return new Size(w, h);
        }

        // 粗略估算：主流视觉模型按 512 像素块计费，约 750 像素/ token
        public static int EstimateTokens(int w, int h, int maxEdge)
        {
            Size s = FitTo(w, h, maxEdge);
            return (int)Math.Round(s.Width * (double)s.Height / 750.0);
        }

        public static string ToDataUrl(Bitmap bmp, int maxEdge, int quality)
        {
            string stat;
            return ToDataUrl(bmp, maxEdge, quality, out stat);
        }

        // 由配置驱动：true 时不缩放、不转 JPEG，原图 PNG 发送
        public static bool Lossless = true;

        public static string ToDataUrl(Bitmap bmp, int maxEdge, int quality, out string stat)
        {
            stat = "";
            Stopwatch sw = Stopwatch.StartNew();
            Bitmap scaled = bmp;
            bool owned = false;
            try
            {
                Size fit = Lossless ? new Size(bmp.Width, bmp.Height) : FitTo(bmp.Width, bmp.Height, maxEdge);
                if (fit.Width != bmp.Width || fit.Height != bmp.Height)
                {
                    scaled = new Bitmap(fit.Width, fit.Height);
                    owned = true;
                    using (Graphics g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(bmp, 0, 0, scaled.Width, scaled.Height);
                    }
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    if (Lossless)
                    {
                        scaled.Save(ms, ImageFormat.Png);
                        int pbytes = (int)ms.Length;
                        stat = string.Format("img {0}x{1} png={2}KB est={3}tok enc={4}ms",
                            scaled.Width, scaled.Height, (pbytes + 1023) / 1024,
                            EstimateTokens(scaled.Width, scaled.Height, 0), sw.ElapsedMilliseconds);
                        Log.Write(stat);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }

                    ImageCodecInfo jpg = GetEncoder(ImageFormat.Jpeg);
                    EncoderParameters ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    scaled.Save(ms, jpg, ps);
                    int bytes = (int)ms.Length;
                    stat = string.Format("img {0}x{1} -> {2}x{3} jpeg={4}KB q={5} est={6}tok enc={7}ms",
                        bmp.Width, bmp.Height, scaled.Width, scaled.Height,
                        (bytes + 1023) / 1024, quality,
                        EstimateTokens(scaled.Width, scaled.Height, 0), sw.ElapsedMilliseconds);
                    Log.Write(stat);
                    return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            finally
            {
                if (owned) scaled.Dispose();
            }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo c in codecs)
                if (c.FormatID == format.Guid) return c;
            return null;
        }

        // 联网搜索（DeepSeek Search API）：返回拼好的资料文本
        public static string Search(AppConfig cfg, string query, int count, out string error)
        {
            error = null;
            try
            {
                string b = cfg.ApiBase.Trim().TrimEnd('/');
                if (b.EndsWith("/chat/completions")) b = b.Substring(0, b.Length - "/chat/completions".Length);
                string url = b.EndsWith("/v1") ? (b + "/tools/search") : (b + "/v1/tools/search");

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["query"] = query;
                payload["count"] = count;
                payload["need_content"] = false;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                byte[] body = Encoding.UTF8.GetBytes(ser.Serialize(payload));

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.Proxy = null;
                req.ContentLength = body.Length;
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);

                string text;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();

                Dictionary<string, object> root = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (root == null) { error = "搜索返回不是 JSON"; return null; }
                object[] results = root["results"] as object[];
                if (results == null || results.Length == 0) { error = "没有搜索结果"; return null; }

                StringBuilder sb = new StringBuilder();
                int i = 0;
                foreach (object o in results)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    i++;
                    string title = d.ContainsKey("title") ? (d["title"] as string) : "";
                    string summary = d.ContainsKey("summary") ? (d["summary"] as string) : "";
                    sb.Append(i).Append(". ").Append(title).Append('\n').Append(summary).Append("\n\n");
                    if (i >= count) break;
                }
                Log.Write("search q=[" + query + "] hits=" + i);
                return sb.ToString().Trim();
            }
            catch (WebException wex)
            {
                error = wex.Message;
                Log.Write("search FAIL: " + wex.Message);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static string Ask(AppConfig cfg, List<object> messages, out string error, out UsageInfo usage)
        {
            return Ask(cfg, messages, out error, out usage, false);
        }

        // noThink=true 时不走思考模式：自动作答循环里用，省时省钱
        public static string Ask(AppConfig cfg, List<object> messages, out string error, out UsageInfo usage, bool noThink)
        {
            error = null;
            usage = null;
            Stopwatch sw = Stopwatch.StartNew();
            long tSer = 0, tSent = 0, tTtfb = 0;
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["model"] = cfg.ApiModel;
                payload["messages"] = messages;
                payload["temperature"] = 0.2;
                payload["stream"] = false;
                if (noThink)
                {
                    Dictionary<string, object> th = new Dictionary<string, object>();
                    th["type"] = "disabled";
                    payload["thinking"] = th;
                }

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(payload);
                tSer = sw.ElapsedMilliseconds;

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BuildEndpoint(cfg.ApiBase));
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.Headers["Authorization"] = "Bearer " + cfg.ApiKey;
                req.Timeout = 180000;
                req.ReadWriteTimeout = 180000;
                req.Proxy = null;

                byte[] body = Encoding.UTF8.GetBytes(json);
                req.ContentLength = body.Length;
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(body, 0, body.Length);
                }
                tSent = sw.ElapsedMilliseconds;

                string text;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    tTtfb = sw.ElapsedMilliseconds;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        text = sr.ReadToEnd();
                    }
                }

                long tRead = sw.ElapsedMilliseconds;
                string result = Extract(text, out error, out usage);
                Log.Write(string.Format(
                    "api model={0} body={1}KB msgs={2} ser={3}ms send={4}ms ttfb={5}ms gen={6}ms parse={7}ms total={8}ms chars={9} err={10}",
                    cfg.ApiModel, (body.Length + 1023) / 1024, messages.Count, tSer, tSent - tSer,
                    tTtfb - tSent, tRead - tTtfb, sw.ElapsedMilliseconds - tRead,
                    sw.ElapsedMilliseconds, text.Length, error == null ? "-" : "yes"));
                return result;
            }
            catch (WebException wex)
            {
                Log.Write("api FAIL " + sw.ElapsedMilliseconds + "ms: " + wex.Message);
                string detail = wex.Message;
                try
                {
                    HttpWebResponse r = wex.Response as HttpWebResponse;
                    if (r != null)
                    {
                        using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        {
                            string b = sr.ReadToEnd();
                            if (b.Length > 600) b = b.Substring(0, 600);
                            detail = "HTTP " + (int)r.StatusCode + " " + b;
                        }
                    }
                }
                catch { }
                error = detail;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static int ReadInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string Extract(string json, out string error, out UsageInfo usage)
        {
            error = null;
            usage = new UsageInfo();
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) { error = "返回内容不是 JSON 对象"; return null; }

                if (root.ContainsKey("error"))
                {
                    error = json.Length > 500 ? json.Substring(0, 500) : json;
                    return null;
                }

                object usageObj;
                if (root.TryGetValue("usage", out usageObj))
                {
                    Dictionary<string, object> u = usageObj as Dictionary<string, object>;
                    if (u != null)
                    {
                        usage.Prompt = ReadInt(u, "prompt_tokens");
                        usage.Completion = ReadInt(u, "completion_tokens");
                        usage.CacheHit = ReadInt(u, "prompt_cache_hit_tokens");
                        usage.CacheMiss = ReadInt(u, "prompt_cache_miss_tokens");
                        object detObj;
                        if (u.TryGetValue("completion_tokens_details", out detObj))
                        {
                            Dictionary<string, object> det = detObj as Dictionary<string, object>;
                            if (det != null) usage.Reasoning = ReadInt(det, "reasoning_tokens");
                        }
                        Log.Write(string.Format("usage prompt={0} (hit={1} miss={2}) completion={3} (reason={4})",
                            usage.Prompt, usage.CacheHit, usage.CacheMiss, usage.Completion, usage.Reasoning));
                    }
                }

                object[] choices = root["choices"] as object[];
                if (choices == null || choices.Length == 0) { error = "返回里没有 choices"; return null; }

                Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
                Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
                object content = msg["content"];

                string s = content as string;
                if (s != null) return s.Trim();

                object[] parts = content as object[];
                if (parts != null)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (object p in parts)
                    {
                        Dictionary<string, object> d = p as Dictionary<string, object>;
                        if (d != null && d.ContainsKey("text")) sb.AppendLine(d["text"] as string);
                    }
                    return sb.ToString().Trim();
                }

                error = "看不懂返回的 content 结构";
                return null;
            }
            catch (Exception ex)
            {
                error = "解析返回失败：" + ex.Message;
                return null;
            }
        }

        public static string Test(AppConfig cfg, out string error)
        {
            List<object> messages = new List<object>();
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["role"] = "user";
            m["content"] = "只回复两个字：可以";
            messages.Add(m);
            UsageInfo usage;
            return Ask(cfg, messages, out error, out usage);
        }
    }

    // ======================= 自动作答 =======================

    internal sealed class AutoPick
    {
        public string Label = "";
        public string Text = "";
        public double X, Y, W, H;   // 归一化 0..1（相对送进模型的那张图）

        public double CX { get { return X + W / 2.0; } }
        public double CY { get { return Y + H / 2.0; } }
    }

    internal sealed class AutoQuestion
    {
        public string No = "";
        public string Kind = "single";   // single / multi / judge / blank
        public string Blank = "";
        public string Note = "";
        public List<AutoPick> Picks = new List<AutoPick>();

        public string Key()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(No).Append('|').Append(Kind).Append('|').Append(Blank);
            foreach (AutoPick p in Picks) sb.Append('|').Append(p.Label).Append(':').Append(p.Text);
            return sb.ToString();
        }
    }

    internal static class AutoPrompt
    {
        public const string Ask =
            "你是答题自动化助手。看图，找出图中所有还没作答的题目，给出正确答案，并标出「要点击的选项」在图里的位置。\n" +
            "\n# 坐标规则（很重要）\n" +
            "- 坐标是相对整张图片的比例：左上角 (0,0)，右下角 (1,1)，保留两位小数。\n" +
            "- box = [x, y, w, h]，是「这个选项」的矩形，要刚好包住选项文字和它前面的圆圈/字母，不要盖住相邻选项。\n" +
            "- 宁可框得紧一点，也不要框到别的选项。\n" +
            "\n# 输出格式\n" +
            "只输出一个 JSON 对象，不要 markdown 代码块，不要任何解释文字：\n" +
            "{\"questions\":[{\"no\":\"1\",\"kind\":\"single\",\"picks\":[{\"label\":\"B\",\"text\":\"北京\",\"box\":[0.31,0.40,0.42,0.05]}],\"note\":\"\"}]}\n" +
            "\n# 规则\n" +
            "1. no 沿用题目原有编号；没有编号就按出现顺序从 \"1\" 开始。\n" +
            "2. kind：单选题 single，多选题 multi，判断题 judge，填空题 blank。\n" +
            "3. single / judge 只有一个 pick；multi 按字母顺序给出全部正确项，每项一个 pick。\n" +
            "4. text 照抄该选项去掉字母后的原文；实在看不清就写 \"\"，不要编造。\n" +
            "5. 填空题 kind 填 blank，blank 里写要填的内容（多个空用「；」隔开），picks 给出输入框的位置。\n" +
            "6. 已经选中 / 已勾选 / 已填好的题不要再输出。\n" +
            "7. 整张图没有可作答的题，就只输出 {\"questions\":[]}。\n" +
            "8. 不确定时给出你认为最可能的答案，并把原因写在 note 里；不要为了凑数瞎猜。\n" +
            "9. 图里题目多的时候要全部列出来，不要只给前几道；只露出一半、题干不完整的不要输出。\n" +
            "10. box 必须压在这一行选项的文字上（含字母），不要框到选项之间的空白里。";

        public const string Ocr =
            "只输出图片里出现的文字，逐行照抄。不要解释，不要加引号，不要输出任何多余内容。";

        public const string Query =
            "为下面这道题生成一个用于网络搜索的查询词。只输出查询词本身，不超过 30 个字，不要解释、不要加引号。";

        public const string WithWeb =
            "下面给你题目截图和若干条检索资料。结合资料与题目给出答案，只输出答案（格式同前：选择题「字母 选项原文」，判断「对/错」，填空只给内容）。\n资料可能有噪音或过时，以题目原文为准；资料与题目无关时忽略资料。";

        // 单题重识别：只给它一道题的截图，只问这一题的答案
        public const string One =
            "这是一道题的截图。只回答这道题的答案，不要解释，不要复述题干。\n" +
            "规则：\n" +
            "- 单选题：输出「字母 选项原文」，例如：B 党的十一届三中全会\n" +
            "- 多选题：每个选项一行，格式同上，按字母顺序\n" +
            "- 判断题：只输出「对」或「错」\n" +
            "- 填空题：只输出要填的内容，多个空用「；」隔开\n" +
            "选项原文必须照抄图片里的文字，看不清就只输出「看不清」。";
    }

    internal static class AutoJson
    {
        public static string Clean(string raw)
        {
            if (raw == null) return "";
            string s = raw.Trim();
            int fence = s.IndexOf("```");
            if (fence >= 0)
            {
                int nl = s.IndexOf('\n', fence);
                if (nl > 0) s = s.Substring(nl + 1);
                int end = s.LastIndexOf("```");
                if (end > 0) s = s.Substring(0, end);
            }
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a >= 0 && b > a) s = s.Substring(a, b - a + 1);
            return s.Trim();
        }

        public static List<AutoQuestion> Parse(string raw, out string error)
        {
            error = null;
            List<AutoQuestion> list = new List<AutoQuestion>();
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.DeserializeObject(Clean(raw)) as Dictionary<string, object>;
                if (root == null) { error = "返回不是 JSON 对象"; return list; }

                object arrObj;
                if (!root.TryGetValue("questions", out arrObj)) { error = "缺少 questions 字段"; return list; }
                object[] arr = arrObj as object[];
                if (arr == null) return list;

                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;

                    AutoQuestion q = new AutoQuestion();
                    q.No = Str(d, "no");
                    if (q.No.Length == 0) q.No = (list.Count + 1).ToString();
                    q.Kind = Str(d, "kind");
                    if (q.Kind.Length == 0) q.Kind = "single";
                    q.Blank = Str(d, "blank");
                    q.Note = Str(d, "note");

                    object picksObj;
                    if (d.TryGetValue("picks", out picksObj))
                    {
                        object[] pa = picksObj as object[];
                        if (pa != null)
                        {
                            foreach (object po in pa)
                            {
                                Dictionary<string, object> pd = po as Dictionary<string, object>;
                                if (pd == null) continue;
                                AutoPick p = new AutoPick();
                                p.Label = Str(pd, "label");
                                p.Text = Str(pd, "text");
                                object boxObj;
                                if (pd.TryGetValue("box", out boxObj)) ReadBox(boxObj, p);
                                if (p.W <= 0.001 || p.H <= 0.001) continue;
                                if (p.X < -0.02 || p.Y < -0.02 || p.X > 1.02 || p.Y > 1.02) continue;
                                q.Picks.Add(p);
                            }
                        }
                    }
                    list.Add(q);
                }
            }
            catch (Exception ex)
            {
                error = "解析 JSON 失败：" + ex.Message;
            }
            return list;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            string s = v as string;
            if (s != null) return s.Trim();
            return v.ToString().Trim();
        }

        private static void ReadBox(object boxObj, AutoPick p)
        {
            object[] a = boxObj as object[];
            if (a != null && a.Length >= 4)
            {
                p.X = Num(a[0]);
                p.Y = Num(a[1]);
                p.W = Num(a[2]);
                p.H = Num(a[3]);
                return;
            }
            Dictionary<string, object> d = boxObj as Dictionary<string, object>;
            if (d != null)
            {
                object v;
                if (d.TryGetValue("x", out v)) p.X = Num(v);
                if (d.TryGetValue("y", out v)) p.Y = Num(v);
                if (d.TryGetValue("w", out v)) p.W = Num(v);
                if (d.TryGetValue("h", out v)) p.H = Num(v);
            }
        }

        private static double Num(object v)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // 去掉空白和标点，只保留文字与数字，用于比对
        public static string Norm(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        public static bool TextMatches(string got, string expect)
        {
            string a = Norm(got);
            string b = Norm(expect);
            if (b.Length == 0) return true;      // 期望文字为空时不作判断
            if (a.Length == 0) return false;
            if (a.Contains(b) || b.Contains(a)) return true;
            if (b.Length < 3) return false;
            int hit = 0;
            for (int i = 0; i + 1 < b.Length; i++)
            {
                if (a.Contains(b.Substring(i, 2))) hit++;
            }
            return hit >= (b.Length - 1) * 3 / 4;
        }
    }

    internal static class Clicker
    {
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
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

        // 前台点击：真的移动光标
        public static void LeftClick(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
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

    // 只画框、不接收点击的覆盖层
    internal sealed class OverlayForm : Form
    {
        private readonly List<Rectangle> _rects;
        private readonly List<string> _labels;
        private readonly System.Windows.Forms.Timer _timer;

        public OverlayForm(List<Rectangle> rects, List<string> labels)
        {
            _rects = rects;
            _labels = labels;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Bounds = SystemInformation.VirtualScreen;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1600;
            _timer.Tick += delegate { try { Close(); } catch { } };
            _timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80000 | 0x8;   // TRANSPARENT | LAYERED | TOPMOST
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (Pen pen = new Pen(Color.FromArgb(255, 186, 0), 2f))
            using (Brush text = new SolidBrush(Color.White))
            using (Brush labelBg = new SolidBrush(Color.FromArgb(235, 22, 22, 26)))
            {
                for (int i = 0; i < _rects.Count; i++)
                {
                    Rectangle r = _rects[i];
                    Rectangle local = new Rectangle(r.X - Bounds.X, r.Y - Bounds.Y, r.Width, r.Height);
                    g.DrawRectangle(pen, local);
                    if (i < _labels.Count && _labels[i].Length > 0)
                    {
                        SizeF sz = g.MeasureString(_labels[i], f);
                        Rectangle lb = new Rectangle(local.X, local.Y - (int)sz.Height - 4, (int)sz.Width + 10, (int)sz.Height + 4);
                        if (lb.Y < 0) lb.Y = local.Bottom + 2;
                        g.FillRectangle(labelBg, lb);
                        g.DrawString(_labels[i], f, text, lb.X + 5, lb.Y + 1);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // 自动作答的观察窗：当前阶段、本轮情况、最近一帧（带框）
    internal sealed class AutoStatusForm : Form
    {
        private readonly Label _stage;
        private readonly Label _info;
        private readonly PictureBox _thumb;
        private readonly TextBox _log;
        private readonly List<string> _lines = new List<string>();
        private Bitmap _frame;
        private int _questions;
        private int _clicks;
        private int _calls;
        private long _tokens;
        private double _cost;

        public Action StopRequested;
        public Rectangle ScreenBounds = Rectangle.Empty;

        public AutoStatusForm()
        {
            Text = "自动作答";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(320, 232);
            BackColor = Color.FromArgb(18, 18, 20);
            ForeColor = Color.FromArgb(235, 235, 235);
            Padding = new Padding(1);

            Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(52, 52, 58)))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
            MouseDown += DragAnywhere;
            LocationChanged += delegate { ScreenBounds = Bounds; };
            Resize += delegate { ScreenBounds = Bounds; };

            Label title = new Label();
            title.Text = "自动作答 · 状态";
            title.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(245, 245, 245);
            title.Bounds = new Rectangle(12, 9, 200, 20);
            title.MouseDown += DragAnywhere;
            Controls.Add(title);

            Label stop = new Label();
            stop.Text = "✕";
            stop.TextAlign = ContentAlignment.MiddleCenter;
            stop.Font = new Font("Microsoft YaHei UI", 9f);
            stop.ForeColor = Color.FromArgb(160, 160, 166);
            stop.Bounds = new Rectangle(288, 8, 22, 20);
            stop.Cursor = Cursors.Hand;
            stop.Click += delegate { if (StopRequested != null) StopRequested(); };
            Controls.Add(stop);

            Panel rule = new Panel();
            rule.Bounds = new Rectangle(12, 34, 296, 1);
            rule.BackColor = Color.FromArgb(40, 40, 44);
            Controls.Add(rule);

            _stage = new Label();
            _stage.Text = "待机";
            _stage.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _stage.ForeColor = Color.FromArgb(255, 196, 72);
            _stage.Bounds = new Rectangle(12, 42, 296, 24);
            Controls.Add(_stage);

            _info = new Label();
            _info.Text = "";
            _info.Font = new Font("Microsoft YaHei UI", 8f);
            _info.ForeColor = Color.FromArgb(170, 170, 178);
            _info.Bounds = new Rectangle(12, 68, 296, 34);
            Controls.Add(_info);

            _thumb = new PictureBox();
            _thumb.Bounds = new Rectangle(12, 106, 152, 86);
            _thumb.BackColor = Color.FromArgb(10, 10, 12);
            _thumb.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(_thumb);

            _log = new TextBox();
            _log.Bounds = new Rectangle(172, 106, 136, 86);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Color.FromArgb(14, 14, 16);
            _log.ForeColor = Color.FromArgb(150, 150, 158);
            _log.Font = new Font("Consolas", 7.5f);
            Controls.Add(_log);

            Label hint = new Label();
            hint.Text = "拖动可移动 · ✕ 停止";
            hint.Font = new Font("Microsoft YaHei UI", 7f);
            hint.ForeColor = Color.FromArgb(110, 110, 118);
            hint.Bounds = new Rectangle(12, 198, 296, 16);
            Controls.Add(hint);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        private void DragAnywhere(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        public void PlaceAt(Screen scr)
        {
            float scale = Ui.DpiScale(scr);
            if (scale > 1.001f) Ui.ScaleForm(this, scale);
            int x = scr.WorkingArea.Right - Width - 12;
            int y = scr.WorkingArea.Top + 72;
            Location = new Point(x, y);
            ScreenBounds = Bounds;
        }

        public void SetStage(string text)
        {
            _stage.Text = text;
        }

        public void SetTarget(string title, string mode)
        {
            _info.Text = "目标：" + (title.Length > 0 ? title : "（未命名窗口）") + "\r\n模式：" + mode;
        }

        public void SetRound(int questions)
        {
            _questions = questions;
            RefreshInfo();
        }

        public void NoteClick()
        {
            _clicks++;
            RefreshInfo();
        }

        public void NoteUsage(long tokens, double cost)
        {
            _calls++;
            _tokens += tokens;
            _cost += cost;
            RefreshInfo();
        }

        public void ResetRound()
        {
            _clicks = 0;
            RefreshInfo();
        }

        private void RefreshInfo()
        {
            _info.Text = _info.Text.Split('\n')[0].TrimEnd('\r') + "\r\n"
                + string.Format("本轮 {0} 题 · 已点 {1} 次 · 调用 {2} 次 · {3} tok · {4}",
                    _questions, _clicks, _calls, Pricing.Tok(_tokens), Pricing.Money(_cost));
        }

        public void AddLog(string line)
        {
            _lines.Add(DateTime.Now.ToString("HH:mm:ss") + " " + line);
            while (_lines.Count > 60) _lines.RemoveAt(0);
            int show = Math.Max(0, _lines.Count - 7);
            _log.Text = string.Join("\r\n", _lines.GetRange(show, _lines.Count - show).ToArray());
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        // 传入已经合成好的缩略图（调用方负责不再使用它）
        public void SetFrame(Bitmap composed)
        {
            try
            {
                Image old = _thumb.Image;
                _thumb.Image = composed;
                if (old != null && !ReferenceEquals(old, composed)) old.Dispose();
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_thumb.Image != null) { _thumb.Image.Dispose(); _thumb.Image = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class AutoPilot : IDisposable
    {
        public const int Off = 0;
        public const int Preview = 1;
        public const int Click = 2;

        private readonly AppConfig _cfg;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _watch;
        private DateTime _lastMouseAt = DateTime.Now;
        private Point _lastMousePos;
        private readonly SynchronizationContext _ctx;
        private readonly Dictionary<string, DateTime> _done = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _warned = new HashSet<string>();

        private IntPtr _hwnd;
        private DateTime _nextScanAfter = DateTime.MinValue;
        private bool _busy;
        private int _clicksThisRound;
        private DateTime _roundStart;
        private OverlayForm _overlay;
        private int _tickCount;
        private int _failTicks;
        private double _dyCorrection;   // 本轮学到的纵向修正（归一化）

        public Control UiHost;
        public AutoStatusForm Status;
        public int Mode;

        public AutoPilot(AppConfig cfg)
        {
            _cfg = cfg;
            _ctx = SynchronizationContext.Current;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(800, cfg.AutoIntervalMs);
            _timer.Tick += delegate { Tick(); };

            // 100ms 看一眼鼠标，记录最后一次活动时间
            _watch = new System.Windows.Forms.Timer();
            _watch.Interval = 100;
            _watch.Tick += delegate
            {
                try
                {
                    Point p = Cursor.Position;
                    if (p != _lastMousePos || Clicker.AnyMouseButtonDown())
                    {
                        _lastMousePos = p;
                        _lastMouseAt = DateTime.Now;
                    }
                    // 有输入就把「下一轮」的冷却清掉，用户一动就立刻重新识别
                    if (Clicker.SystemIdleMs() < 400) _nextScanAfter = DateTime.MinValue;
                }
                catch { }
            };
        }

        public void Start(int mode, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                Notify("自动作答：没有可用的目标窗口。先切到题目窗口，再按 Ctrl+Alt+Q。");
                return;
            }
            _hwnd = hwnd;
            Mode = mode;
            _done.Clear();
            _lastMouseAt = DateTime.Now;
            try { _lastMousePos = Cursor.Position; } catch { }
            _watch.Start();
            _timer.Interval = Math.Max(800, _cfg.AutoIntervalMs);
            _timer.Start();
            Log.Write("auto start mode=" + mode + " hwnd=" + hwnd + " interval=" + _timer.Interval + " idle=" + _cfg.AutoIdleMs + "ms");
            UiStatus(delegate(AutoStatusForm f)
            {
                f.SetStage(mode == Preview ? "预览中（只画框）" : "运行中（会自动点击）");
                f.SetTarget(WindowText.Get(hwnd), mode == Preview ? "预览" : "自动点击");
                f.AddLog("启动，间隔 " + _timer.Interval + " ms");
            });
            Notify(mode == Preview ? "自动作答：预览模式（只画框，不点击）" : "自动作答：已开启，识别到答案后自动点击");
        }

        public void Stop()
        {
            _timer.Stop();
            try { _watch.Stop(); } catch { }
            Mode = Off;
            _hwnd = IntPtr.Zero;
            UiStatus(delegate(AutoStatusForm f) { f.SetStage("已停止"); f.AddLog("已停止"); });
            Ui(delegate
            {
                if (_overlay != null) { try { _overlay.Close(); } catch { } _overlay = null; }
            });
            Log.Write("auto stop");
        }

        public void Dispose()
        {
            Stop();
            try { _timer.Dispose(); } catch { }
            try { _watch.Dispose(); } catch { }
        }

        public void Toggle(IntPtr hwnd)
        {
            if (Mode == Off) Start(Click, hwnd);
            else Stop();
        }

        private void Notify(string text)
        {
            Log.Write("auto: " + text);
            Ui(delegate
            {
                Form f = null;
                try
                {
                    // 复用托盘的气泡提示
                    foreach (Form form in Application.OpenForms)
                    {
                        f = form;
                        break;
                    }
                }
                catch { }
            });
            if (Notice != null) Notice(text);
        }

        public event Action<string> Notice;

        private void Ui(Action action)
        {
            if (UiHost != null && UiHost.IsHandleCreated)
            {
                try { UiHost.BeginInvoke(action); }
                catch (Exception ex) { Log.Write("auto ui post: " + ex.Message); }
                return;
            }
            Log.Write("auto ui: 没有 UI 宿主，跳过界面更新");
        }

        private void UiStatus(Action<AutoStatusForm> action)
        {
            AutoStatusForm form = Status;
            if (form == null || UiHost == null || !UiHost.IsHandleCreated) return;
            try
            {
                UiHost.BeginInvoke((Action)delegate
                {
                    try { if (!form.IsDisposed) action(form); }
                    catch (Exception ex) { Log.Write("auto status: " + ex.Message); }
                });
            }
            catch { }
        }

        // ---------------- 主循环 ----------------

        private void Tick()
        {
            _tickCount++;
            if (Mode == Off || _busy)
            {
                if (_tickCount <= 3) Log.Write("auto tick #" + _tickCount + " skipped mode=" + Mode + " busy=" + _busy);
                return;
            }
            if (_hwnd == IntPtr.Zero) { Stop(); return; }

            if (!WindowText.IsAlive(_hwnd))
            {
                Log.Write("auto tick: 目标窗口已失效 hwnd=" + _hwnd);
                Notify("目标窗口已关闭，自动作答已停止。");
                Stop();
                return;
            }

            // 人还在操作（键盘、鼠标移动、点击、滚轮任意一种）就先不抓
            int idleMs = Math.Max(300, _cfg.AutoIdleMs);
            double idleFor = (DateTime.Now - _lastMouseAt).TotalMilliseconds;
            long sysIdle = Clicker.SystemIdleMs();
            if (sysIdle >= 0 && sysIdle < idleFor) idleFor = sysIdle;
            if (idleFor < idleMs)
            {
                double left = Math.Round((idleMs - idleFor) / 1000.0, 1);
                string t = "等输入停下 " + left + "s";
                UiStatus(delegate(AutoStatusForm f) { f.SetStage(t); });
                return;
            }

            // 上一轮什么都没做（没题/都答过）时，隔 10 秒再试，避免空转烧 token；一有输入就立刻恢复
            if (DateTime.Now < _nextScanAfter)
            {
                double wait = Math.Ceiling((_nextScanAfter - DateTime.Now).TotalSeconds);
                string t2 = "下一轮 " + wait + "s 后（或你一操作就立刻开始）";
                UiStatus(delegate(AutoStatusForm f) { f.SetStage(t2); });
                return;
            }

            Bitmap frame = null;
            Rectangle win;
            try
            {
                frame = Capture.Window(_hwnd);
                win = Capture.WindowBounds(_hwnd);
            }
            catch (Exception ex)
            {
                Log.Write("auto capture failed: " + ex.Message);
                return;
            }
            if (frame == null || win.Width < 64 || win.Height < 64)
            {
                if (frame != null) frame.Dispose();
                _failTicks++;
                if (_tickCount <= 5) Log.Write("auto tick #" + _tickCount + " 抓屏失败 hwnd=" + _hwnd + " win=" + win.Width + "x" + win.Height);
                UiStatus(delegate(AutoStatusForm f) { f.SetStage("抓不到目标窗口"); });
                if (_failTicks >= 5)
                {
                    Notify("抓不到目标窗口（可能最小化了或被关掉），自动作答已停止。");
                    Stop();
                }
                return;
            }
            _failTicks = 0;
            MaskStatus(frame, win);

            byte[] sig = Signature(frame);
            if (_tickCount <= 5)
            {
                Screen scr = Screen.FromHandle(_hwnd);
                float dpiScale = scr != null ? DshShot.Ui.DpiScale(scr) : 1f;
                Rectangle vs = SystemInformation.VirtualScreen;
                Log.Write(string.Format(
                    "auto tick #{0} 开始识别 bmp={1}x{2} win={3}x{4}@{5},{6} dpi={7:0.##} virtual={8}x{9}",
                    _tickCount, frame.Width, frame.Height, win.Width, win.Height, win.Left, win.Top,
                    dpiScale, vs.Width, vs.Height));
            }

            _busy = true;
            int mode = Mode;
            Bitmap snap = frame;
            Task.Factory.StartNew(delegate
            {
                try { Run(snap, win, mode, sig); }
                catch (Exception ex) { Log.Write("auto run failed: " + ex); Notify("自动作答出错：" + ex.Message); }
                finally { snap.Dispose(); _busy = false; }
            });
        }

        private void Run(Bitmap frame, Rectangle win, int mode, byte[] sigAtAnalysis)
        {
            _roundStart = DateTime.Now;
            _clicksThisRound = 0;

            Rectangle crop = new Rectangle(0, 0, frame.Width, frame.Height);
            Bitmap send = frame;
            bool owned = false;
            try
            {
                if (_cfg.AutoCrop)
                {
                    string note;
                    Rectangle box = AutoCrop.Detect(frame, out note);
                    if (box.Width < frame.Width || box.Height < frame.Height)
                    {
                        send = frame.Clone(box, PixelFormat.Format32bppArgb);
                        owned = true;
                        crop = box;
                    }
                }

                int edge = _cfg.AutoMaxEdge > 0 ? _cfg.AutoMaxEdge : _cfg.MaxEdge;
                int quality = _cfg.AutoQuality > 0 ? _cfg.AutoQuality : _cfg.JpegQuality;
                string stat;
                string dataUrl = AiClient.ToDataUrl(send, edge, quality, out stat);
                Log.Write("auto " + stat);

                UiStatus(delegate(AutoStatusForm f) { f.SetStage("识别中…"); });

                string err;
                UsageInfo usage;
                List<AutoQuestion> qs = Scan(send, dataUrl, edge, quality, out err, out usage);
                if (err != null)
                {
                    UiStatus(delegate(AutoStatusForm f) { f.SetStage("接口失败"); f.AddLog("接口失败：" + err); });
                    Notify("自动作答：接口失败 " + err);
                    return;
                }
                ReportUsage(usage);

                // 快扫时缺答案（填空题没给内容 / 没给出可点的框），再用思考模式补一次
                if (Incomplete(qs) && _cfg.AutoRetryThink)
                {
                    Log.Write("auto incomplete -> retry with thinking");
                    UiStatus(delegate(AutoStatusForm f) { f.SetStage("答案不全，思考模式重试…"); });
                    string err2;
                    UsageInfo usage2;
                    List<AutoQuestion> qs2 = Analyze(dataUrl, false, out err2, out usage2);
                    ReportUsage(usage2);
                    if (err2 == null && qs2.Count > 0 && !Incomplete(qs2)) qs = qs2;
                }

                Log.Write("auto questions=" + qs.Count);
                if (qs.Count == 0)
                {
                    Bitmap empty = ComposeFrame(send, new List<Rectangle>());
                    UiStatus(delegate(AutoStatusForm f)
                    {
                        f.SetStage("这一屏没有可作答的题");
                        f.SetRound(0);
                        f.SetFrame(empty);
                    });
                    return;
                }

                UiStatus(delegate(AutoStatusForm f)
                {
                    f.SetStage("识别到 " + qs.Count + " 题");
                    f.SetRound(qs.Count);
                    f.ResetRound();
                });

                List<Rectangle> boxes = new List<Rectangle>();
                List<Rectangle> frameBoxes = new List<Rectangle>();
                List<string> labels = new List<string>();

                foreach (AutoQuestion q in qs)
                {
                    if (_clicksThisRound >= _cfg.AutoMaxClicks) break;

                    DateTime seen;
                    if (_done.TryGetValue(q.Key(), out seen) && (DateTime.Now - seen).TotalSeconds < 90)
                    {
                        Log.Write("auto skip done q=" + q.No);
                        continue;
                    }

                    // 第二遍：把这题单独裁出来放大再问一次，和整页给的答案比对；不一致就不点
                    if (_cfg.AutoDoubleCheck && q.Picks.Count > 0)
                    {
                        string stage = "逐题复核 " + q.No + "…";
                        UiStatus(delegate(AutoStatusForm f) { f.SetStage(stage); });
                        if (!DoubleCheck(send, q)) continue;
                    }

                    foreach (AutoPick p in q.Picks)
                    {
                        string why;
                        string stageText = "复检 " + q.No + " " + p.Label + "…";
                        UiStatus(delegate(AutoStatusForm f) { f.SetStage(stageText); });

                        // 先在模型给的框上 OCR 校对；对不上就在上下附近找文字真正的位置，并把 p.Y 修正过来
                        bool verified = Verify(send, p, out why);

                        int sx = win.Left + crop.Left + (int)Math.Round(p.CX * crop.Width);
                        int sy = win.Top + crop.Top + (int)Math.Round(p.CY * crop.Height);
                        Rectangle box = new Rectangle(
                            win.Left + crop.Left + (int)Math.Round(p.X * crop.Width),
                            win.Top + crop.Top + (int)Math.Round(p.Y * crop.Height),
                            Math.Max(4, (int)Math.Round(p.W * crop.Width)),
                            Math.Max(4, (int)Math.Round(p.H * crop.Height)));

                        string label = q.No + " " + p.Label;
                        if (q.Kind == "blank") label = q.No + " 填空";
                        boxes.Add(box);
                        labels.Add(label);
                        Log.Write(string.Format(
                            "auto pick q={0} {1} ok={2} box=({3:0.000},{4:0.000},{5:0.000},{6:0.000}) px={7},{8} win={9},{10}",
                            q.No, p.Label, verified, p.X, p.Y, p.W, p.H, sx, sy, win.Left, win.Top));
                        frameBoxes.Add(new Rectangle(
                            (int)Math.Round(p.X * crop.Width),
                            (int)Math.Round(p.Y * crop.Height),
                            Math.Max(4, (int)Math.Round(p.W * crop.Width)),
                            Math.Max(4, (int)Math.Round(p.H * crop.Height))));

                        if (mode == Preview) continue;
                        if (_clicksThisRound >= _cfg.AutoMaxClicks) break;

                        if (!verified)
                        {
                            Log.Write("auto skip q=" + q.No + " " + why);
                            string skipText = "跳过 " + q.No + "：" + why;
                            UiStatus(delegate(AutoStatusForm f) { f.AddLog(skipText); });
                            Notify("第 " + q.No + " 题没点：" + why);
                            continue;
                        }
                        if (!StillSame(win, sigAtAnalysis))
                        {
                            Log.Write("auto abort: 画面已变化");
                            UiStatus(delegate(AutoStatusForm f) { f.SetStage("画面变了，本轮放弃"); });
                            Notify("画面在分析期间变了，本轮不点击。");
                            return;
                        }
                        if (!ClickAt(q, p, sx, sy)) return;
                    }
                }

                if (mode == Preview && boxes.Count > 0) ShowOverlay(boxes, labels);

                Bitmap composed = ComposeFrame(send, frameBoxes);
                try
                {
                    if (composed != null)
                    {
                        string dump = Path.Combine(Path.GetTempPath(), "dsh-auto-last.png");
                        composed.Save(dump, ImageFormat.Png);
                    }
                }
                catch { }
                UiStatus(delegate(AutoStatusForm f)
                {
                    f.SetFrame(composed);
                    if (mode == Preview) f.SetStage("预览：已标出 " + boxes.Count + " 处");
                    else f.SetStage("本轮完成");
                });
            }
            finally
            {
                // 点了东西就 1 秒后再看；什么都没做（没题/都答过）就歇 10 秒，别空转烧 token
                _nextScanAfter = DateTime.Now.AddSeconds(_clicksThisRound > 0 ? 1 : 10);
                if (owned) send.Dispose();
            }
        }

        // 观察窗自己也是置顶的，会出现在截图里；把它那块涂白，免得被当成题目
        private void MaskStatus(Bitmap frame, Rectangle win)
        {
            AutoStatusForm form = Status;
            if (form == null) return;
            Rectangle r = form.ScreenBounds;
            if (r.Width <= 0 || r.Height <= 0) return;
            r.Offset(-win.Left, -win.Top);
            r.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
            if (r.Width <= 2 || r.Height <= 2) return;
            try
            {
                using (Graphics g = Graphics.FromImage(frame))
                using (SolidBrush b = new SolidBrush(Color.White))
                {
                    g.FillRectangle(b, r);
                }
            }
            catch { }
        }

        // 把这一帧和框合成一张小图给观察窗（必须在后台线程里先画好，不然位图被释放就画不出来了）
        private static Bitmap ComposeFrame(Bitmap src, List<Rectangle> boxes)
        {
            try
            {
                Bitmap composed = new Bitmap(src.Width, src.Height);
                using (Graphics g = Graphics.FromImage(composed))
                {
                    g.DrawImage(src, 0, 0);
                    if (boxes != null && boxes.Count > 0)
                    {
                        using (Pen pen = new Pen(Color.FromArgb(255, 186, 0), Math.Max(2f, src.Width / 320f)))
                        {
                            foreach (Rectangle r in boxes) g.DrawRectangle(pen, r);
                        }
                    }
                }
                return composed;
            }
            catch { return null; }
        }

        private void ReportUsage(UsageInfo u)
        {
            if (u == null || u.Total <= 0) return;
            double cost = Pricing.Cost(_cfg.ApiModel, u);
            _cfg.StatPrompt += u.Prompt;
            _cfg.StatCompletion += u.Completion;
            _cfg.StatCacheHit += u.CacheHit;
            _cfg.StatCacheMiss += u.CacheMiss;
            _cfg.StatCost += cost;
            _cfg.Save();

            string text = "识别 " + u.Total + " tok · " + Pricing.Money(cost);
            UiStatus(delegate(AutoStatusForm f) { f.NoteUsage(u.Total, cost); f.AddLog(text); });
        }

        // 整屏扫：太高的图分上下两半各扫一次再合并，底部那半能拿到更多像素，减少漏最后一题
        private List<AutoQuestion> Scan(Bitmap send, string fullUrl, int edge, int quality, out string error, out UsageInfo usage)
        {
            error = null;
            usage = null;

            if (!_cfg.AutoSplitScan || send.Height < 700)
                return Analyze(fullUrl, true, out error, out usage);

            int h = send.Height;
            int halfH = (int)(h * 0.58);
            if (halfH >= h || halfH < 200)
                return Analyze(fullUrl, true, out error, out usage);

            Rectangle top = new Rectangle(0, 0, send.Width, halfH);
            Rectangle bottom = new Rectangle(0, h - halfH, send.Width, halfH);

            string e1, e2;
            UsageInfo u1, u2;
            List<AutoQuestion> a = AnalyzeRegion(send, top, edge, quality, out e1, out u1);
            List<AutoQuestion> b = AnalyzeRegion(send, bottom, edge, quality, out e2, out u2);

            usage = SumUsage(u1, u2);
            if (e1 != null && e2 != null) error = e1;

            List<AutoQuestion> merged = Merge(a, b);
            Log.Write("auto split: top=" + a.Count + " bottom=" + b.Count + " merged=" + merged.Count);
            return merged;
        }

        private List<AutoQuestion> AnalyzeRegion(Bitmap img, Rectangle region, int edge, int quality,
            out string error, out UsageInfo usage)
        {
            error = null;
            usage = null;
            Bitmap sub = null;
            try
            {
                sub = img.Clone(region, PixelFormat.Format32bppArgb);
                string stat;
                string url = AiClient.ToDataUrl(sub, edge, quality, out stat);
                List<AutoQuestion> list = Analyze(url, true, out error, out usage);

                // 半区坐标 → 整图归一化坐标
                double x0 = (double)region.Left / img.Width;
                double y0 = (double)region.Top / img.Height;
                double w = (double)region.Width / img.Width;
                double hh = (double)region.Height / img.Height;
                foreach (AutoQuestion q in list)
                {
                    foreach (AutoPick p in q.Picks)
                    {
                        p.X = x0 + p.X * w;
                        p.Y = y0 + p.Y * hh;
                        p.W = p.W * w;
                        p.H = p.H * hh;
                    }
                }
                return list;
            }
            finally
            {
                if (sub != null) sub.Dispose();
            }
        }

        private static UsageInfo SumUsage(UsageInfo a, UsageInfo b)
        {
            UsageInfo u = new UsageInfo();
            if (a != null)
            {
                u.Prompt += a.Prompt; u.Completion += a.Completion;
                u.CacheHit += a.CacheHit; u.CacheMiss += a.CacheMiss; u.Reasoning += a.Reasoning;
            }
            if (b != null)
            {
                u.Prompt += b.Prompt; u.Completion += b.Completion;
                u.CacheHit += b.CacheHit; u.CacheMiss += b.CacheMiss; u.Reasoning += b.Reasoning;
            }
            return u;
        }

        // 两半在中间有重叠，按题号去重；同一题保留信息更全的那份
        private static List<AutoQuestion> Merge(List<AutoQuestion> a, List<AutoQuestion> b)
        {
            List<AutoQuestion> result = new List<AutoQuestion>();
            Dictionary<string, AutoQuestion> byNo = new Dictionary<string, AutoQuestion>();
            foreach (AutoQuestion q in a)
            {
                byNo[q.No] = q;
                result.Add(q);
            }
            foreach (AutoQuestion q in b)
            {
                AutoQuestion exist;
                if (!byNo.TryGetValue(q.No, out exist))
                {
                    byNo[q.No] = q;
                    result.Add(q);
                    continue;
                }
                if (exist.Picks.Count < q.Picks.Count)
                {
                    int idx = result.IndexOf(exist);
                    if (idx >= 0) result[idx] = q;
                    byNo[q.No] = q;
                }
                else if (exist.Blank.Length == 0 && q.Blank.Length > 0)
                {
                    exist.Blank = q.Blank;
                }
            }
            return result;
        }

        private List<AutoQuestion> Analyze(string dataUrl, bool noThink, out string error, out UsageInfo usage)        {
            error = null;
            usage = null;
            List<object> msgs = new List<object>();
            Dictionary<string, object> sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = AutoPrompt.Ask;
            msgs.Add(sys);
            msgs.Add(AiClient.UserMessage("按上面的 JSON 格式输出这张图里的可作答题目。", dataUrl));

            string text = AiClient.Ask(_cfg, msgs, out error, out usage, noThink);
            if (text == null) return new List<AutoQuestion>();

            string perr;
            List<AutoQuestion> qs = AutoJson.Parse(text, out perr);
            if (perr != null) Log.Write("auto parse: " + perr);
            return qs;
        }

        private static bool Incomplete(List<AutoQuestion> qs)
        {
            foreach (AutoQuestion q in qs)
            {
                if (q.Picks.Count == 0) return true;
                if (q.Kind == "blank" && q.Blank.Length == 0) return true;
            }
            return false;
        }

        private bool ClickAt(AutoQuestion q, AutoPick p, int sx, int sy)
        {
            if (string.Equals(_cfg.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase))
            {
                Clicker.PostLeftClick(_hwnd, sx, sy);
            }
            else
            {
                if (Clicker.Foreground() != _hwnd)
                {
                    Notify("目标窗口不在前台，已暂停自动点击。");
                    Stop();
                    return false;
                }
                Clicker.LeftClick(sx, sy);
            }

            _clicksThisRound++;
            _done[q.Key()] = DateTime.Now;
            Log.Write("auto click q=" + q.No + " label=" + p.Label + " text=" + p.Text + " at " + sx + "," + sy);
            string clickText = "点击 " + q.No + " " + p.Label + (p.Text.Length > 0 ? " " + p.Text : "");
            UiStatus(delegate(AutoStatusForm f) { f.NoteClick(); f.AddLog(clickText); });
            Thread.Sleep(240);

            if (q.Kind == "blank" && _cfg.AutoType && q.Blank.Length > 0)
            {
                Clicker.PasteText(q.Blank);
                _done[q.Key()] = DateTime.Now;
                Log.Write("auto typed q=" + q.No + " -> " + q.Blank);
            }
            return true;
        }

        // 第二遍：把这题单独裁出来放大，只问这一题；和整页答案不一致就不点
        private bool DoubleCheck(Bitmap send, AutoQuestion q)
        {
            string pageAnswer = q.Kind == "blank" ? q.Blank : JoinLabels(q);
            if (pageAnswer.Length == 0)
            {
                Log.Write("auto dc q=" + q.No + " 整页没给答案，跳过");
                return false;
            }

            Rectangle r = QuestionRegion(q, send.Width, send.Height);
            if (r.Width < 40 || r.Height < 20) return false;

            string err;
            string one = AskOne(send, r, false, out err);
            if (one == null)
            {
                Log.Write("auto dc q=" + q.No + " 失败：" + err);
                return false;
            }

            if (Agree(q, pageAnswer, one) && TextConsistent(q, one))
            {
                Log.Write("auto dc q=" + q.No + " 一致 " + pageAnswer);
                return true;
            }

            Log.Write("auto dc q=" + q.No + " 不一致 page=" + pageAnswer + " one=" + Short(one));

            // 冲突时用思考模式裁决第三次：跟整页一致就照原计划点，跟单题一致就改用单题的答案
            string think = null;
            if (_cfg.AutoThinkTie)
            {
                string stage = "第 " + q.No + " 题冲突，思考模式裁决…";
                UiStatus(delegate(AutoStatusForm f) { f.SetStage(stage); });

                string err2;
                think = AskOne(send, r, true, out err2);
                if (think != null)
                {
                    if (Agree(q, pageAnswer, think))
                    {
                        Log.Write("auto tie q=" + q.No + " 裁决=整页 " + pageAnswer);
                        return true;
                    }
                    if (Agree(q, one, think) && ApplyAnswer(q, think))
                    {
                        Log.Write("auto tie q=" + q.No + " 裁决=单题 " + Short(think) + "，已改用");
                        return true;
                    }
                    Log.Write("auto tie q=" + q.No + " 三方不一致 think=" + Short(think));
                }
                else
                {
                    Log.Write("auto tie q=" + q.No + " 裁决失败：" + err2);
                }
            }

            // 还定不下来就联网查一次：先让模型出搜索词，搜回来的资料再让它答一遍
            if (_cfg.AutoWebSearch)
            {
                UiStatus(delegate(AutoStatusForm f) { f.SetStage("第 " + q.No + " 题联网核对…"); });

                string qerr;
                string qtext = AskCrop(send, r, AutoPrompt.Query, "输出这道题的搜索词。", false, out qerr);
                string query = (qtext ?? "").Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '“', '”');
                if (query.Length > 60) query = query.Substring(0, 60);

                if (query.Length > 0)
                {
                    string serr;
                    string web = AiClient.Search(_cfg, query, 4, out serr);
                    if (web != null)
                    {
                        string werr;
                        string webAns = AskCrop(send, r, AutoPrompt.WithWeb,
                            "检索资料：\n" + web + "\n\n只输出答案。", false, out werr);
                        if (webAns != null)
                        {
                            if (Agree(q, pageAnswer, webAns))
                            {
                                Log.Write("auto web q=" + q.No + " 采信整页 " + pageAnswer + " (q=" + query + ")");
                                return true;
                            }
                            if (Agree(q, one, webAns) && ApplyAnswer(q, webAns))
                            {
                                Log.Write("auto web q=" + q.No + " 采信单题 " + Short(webAns) + " (q=" + query + ")");
                                return true;
                            }
                            if (think != null && Agree(q, think, webAns) && ApplyAnswer(q, webAns))
                            {
                                Log.Write("auto web q=" + q.No + " 采信裁决 " + Short(webAns) + " (q=" + query + ")");
                                return true;
                            }
                            Log.Write("auto web q=" + q.No + " 仍不一致 web=" + Short(webAns));
                        }
                        else
                        {
                            Log.Write("auto web q=" + q.No + " 回答失败：" + werr);
                        }
                    }
                    else
                    {
                        Log.Write("auto web q=" + q.No + " 搜索失败：" + serr);
                    }
                }
            }

            string msg = "第 " + q.No + " 题两次答案不一致，已跳过";
            UiStatus(delegate(AutoStatusForm f) { f.AddLog(msg); });
            Notify(msg);
            return false;
        }

        // 用裁决出来的答案替换这题的选项，坐标还是用整页给的那份
        private static bool ApplyAnswer(AutoQuestion q, string answer)
        {
            if (q.Kind == "blank")
            {
                string t = (answer ?? "").Trim();
                if (t.Length == 0) return false;
                q.Blank = t;
                return q.Picks.Count > 0;
            }
            if (q.Kind == "judge")
            {
                string j = Judge(answer);
                if (j.Length == 0) return false;
                List<AutoPick> keepJ = new List<AutoPick>();
                foreach (AutoPick p in q.Picks)
                {
                    string pj = Judge(p.Text);
                    if (pj.Length == 0) pj = Judge(p.Label);
                    if (pj == j) keepJ.Add(p);
                }
                if (keepJ.Count == 0) return false;
                q.Picks = keepJ;
                return true;
            }
            string want = Letters(answer);
            if (want.Length == 0) return false;
            List<AutoPick> keep = new List<AutoPick>();
            foreach (AutoPick p in q.Picks)
            {
                string l = (p.Label ?? "").ToUpperInvariant();
                if (l.Length > 0 && want.IndexOf(l[0]) >= 0) keep.Add(p);
            }
            if (keep.Count == 0) return false;
            q.Picks = keep;
            return true;
        }

        private static string JoinLabels(AutoQuestion q)
        {
            StringBuilder sb = new StringBuilder();
            foreach (AutoPick p in q.Picks) sb.Append(p.Label);
            return sb.ToString();
        }

        // 题目区域：选项块的上下各留一点，左右取满宽（保证题干不被切）
        private static Rectangle QuestionRegion(AutoQuestion q, int W, int H)
        {
            double top = 1, bottom = 0;
            foreach (AutoPick p in q.Picks)
            {
                if (p.Y < top) top = p.Y;
                if (p.Y + p.H > bottom) bottom = p.Y + p.H;
            }
            if (bottom <= top) return Rectangle.Empty;

            double y0 = Math.Max(0.0, top - 0.10);
            double y1 = Math.Min(1.0, bottom + 0.04);
            int iy = (int)Math.Round(y0 * H);
            int ih = (int)Math.Round((y1 - y0) * H);
            return Rectangle.Intersect(new Rectangle(0, iy, W, ih), new Rectangle(0, 0, W, H));
        }

        private string AskOne(Bitmap send, Rectangle region, bool think, out string error)
        {
            return AskCrop(send, region, AutoPrompt.One, "只输出这道题的答案。", think, out error);
        }

        // 单题裁剪 + 放大 + 只问这一题；sys/user 可换（回答、生成搜索词、带资料回答）
        private string AskCrop(Bitmap send, Rectangle region, string sysPrompt, string userText, bool think, out string error)
        {
            error = null;
            Bitmap crop = null;
            Bitmap big = null;
            try
            {
                crop = send.Clone(region, PixelFormat.Format32bppArgb);
                big = Upscale2x(crop);

                string stat;
                int edge = Math.Max(_cfg.AutoMaxEdge, 2560);   // 单题裁剪不做二次压缩，保住笔画
                int quality = Math.Max(_cfg.AutoQuality, 94);
                string url = AiClient.ToDataUrl(big, edge, quality, out stat);

                List<object> msgs = new List<object>();
                Dictionary<string, object> sys = new Dictionary<string, object>();
                sys["role"] = "system";
                sys["content"] = sysPrompt;
                msgs.Add(sys);
                msgs.Add(AiClient.UserMessage(userText, url));

                UsageInfo usage;
                string text = AiClient.Ask(_cfg, msgs, out error, out usage, !think);
                ReportUsage(usage);
                return text;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                if (big != null) big.Dispose();
                if (crop != null) crop.Dispose();
            }
        }

        // 2 倍双三次放大 + 轻提对比度，细笔画更容易分开
        private static Bitmap Upscale2x(Bitmap src)
        {
            Bitmap big = new Bitmap(src.Width * 2, src.Height * 2);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float c = 1.12f;
                float t = (1f - c) / 2f;
                ColorMatrix cm = new ColorMatrix(new float[][]
                {
                    new float[] { c, 0, 0, 0, 0 },
                    new float[] { 0, c, 0, 0, 0 },
                    new float[] { 0, 0, c, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { t, t, t, 0, 1 }
                });
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src, new Rectangle(0, 0, big.Width, big.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return big;
        }

        // 两次答案是否一致：选择题比字母集合，判断题比语义，填空题比文字包含
        private static bool Agree(AutoQuestion q, string a, string b)
        {
            if (q.Kind == "blank")
            {
                string x = AutoJson.Norm(a);
                string y = AutoJson.Norm(b);
                if (x.Length == 0 || y.Length == 0) return false;
                return x.Contains(y) || y.Contains(x);
            }
            if (q.Kind == "judge")
            {
                string x = Judge(a);
                string y = Judge(b);
                if (x.Length == 0 || y.Length == 0) return false;
                return x == y;
            }
            string la = Letters(a);
            string lb = Letters(b);
            return la.Length > 0 && la == lb;
        }

        private static string Letters(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (string raw in s.Replace("\r", "\n").Split('\n'))
            {
                string t = raw.TrimStart(' ', '　', '-', '*', '·', '　');
                int i = 0;
                while (i < t.Length && i < 4 && t[i] >= 'A' && t[i] <= 'Z') i++;
                if (i == 0)
                {
                    while (i < t.Length && i < 4 && t[i] >= 'a' && t[i] <= 'z') i++;
                    if (i > 0) sb.Append(t.Substring(0, i).ToUpperInvariant());
                    continue;
                }
                sb.Append(t.Substring(0, i));
            }
            if (sb.Length == 0 || sb.Length > 8) return "";
            char[] arr = sb.ToString().ToCharArray();
            Array.Sort(arr);
            return new string(arr);
        }

        // "B 党的十一届三中全会" / "A 甲\nB 乙" → {B: 党的十一届三中全会}
        private static Dictionary<string, string> ParseLoose(string s)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            if (s == null) return map;
            foreach (string raw in s.Replace("\r", "\n").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                int i = 0;
                while (i < t.Length && i < 3 && char.IsLetter(t[i]) && t[i] < 128) i++;
                if (i == 0) continue;
                string key = t.Substring(0, i).ToUpperInvariant();
                string val = t.Substring(i).Trim(' ', '　', '：', ':', '.', '、', '．', '-', '—');
                if (!map.ContainsKey(key)) map[key] = val;
            }
            return map;
        }

        // 字母一致还不够：它引用的选项原文也必须对得上
        private static bool TextConsistent(AutoQuestion q, string single)
        {
            if (q.Kind == "blank" || q.Kind == "judge") return true;
            Dictionary<string, string> map = ParseLoose(single);
            foreach (AutoPick p in q.Picks)
            {
                string label = (p.Label ?? "").ToUpperInvariant();
                if (label.Length == 0) continue;
                string quoted;
                if (!map.TryGetValue(label, out quoted)) return false;
                string a = AutoJson.Norm(quoted);
                string b = AutoJson.Norm(p.Text);
                if (a.Length < 2 || b.Length < 2) continue;
                if (a.IndexOf(b) < 0 && b.IndexOf(a) < 0) return false;
            }
            return true;
        }

        private static string Judge(string s)
        {
            if (s == null) return "";
            if (s.IndexOf('对') >= 0 || s.IndexOf('√') >= 0 || s.IndexOf("正确") >= 0) return "对";
            if (s.IndexOf('错') >= 0 || s.IndexOf('×') >= 0 || s.IndexOf("错误") >= 0) return "错";
            return "";
        }

        // 复检：把框里的内容 OCR 一遍，和模型说的选项文字比对，防止点歪
        // 模型给的框可能整体偏上/偏下（实测偏下约 1 行）。这里在框的上下试探，
        // 用 OCR 找到真正包含该选项文字的位置，并把 p.Y 就地修正，后续点击用它。
        private bool Verify(Bitmap img, AutoPick p, out string why)
        {
            why = "";
            if (!_cfg.AutoVerify) return true;
            if (AutoJson.Norm(p.Text).Length < 2)
            {
                // 没有文字可比对，就用本轮已经学到的纵向修正
                if (Math.Abs(_dyCorrection) > 0.0001) p.Y = p.Y + _dyCorrection;
                return true;
            }

            double[] offsets;
            if (Math.Abs(_dyCorrection) > 0.0001)
                offsets = new double[] { _dyCorrection, 0, -0.03, 0.03, -0.06, 0.06, -0.09, 0.09, -0.12, 0.12, -0.15, 0.15 };
            else
                offsets = new double[] { 0, -0.03, 0.03, -0.06, 0.06, -0.09, 0.09, -0.12, 0.12, -0.15, 0.15, -0.18, 0.18 };

            string firstWhy = null;
            foreach (double dy in offsets)
            {
                double ny = p.Y + dy;
                if (ny < -0.02 || ny > 1.0) continue;

                string got;
                if (!TryOcr(img, p, ny, out got))
                {
                    if (firstWhy == null) firstWhy = "复检接口失败";
                    continue;
                }
                if (StrictMatch(got, p.Text))
                {
                    if (Math.Abs(dy) > 0.0001)
                        Log.Write(string.Format("auto dy={0:0.000} label={1} text={2}", dy, p.Label, p.Text));
                    _dyCorrection = dy;
                    p.Y = ny;
                    return true;
                }
                if (firstWhy == null)
                    firstWhy = "复检不符（框里是「" + Short(got) + "」，期望「" + Short(p.Text) + "」）";
            }

            why = firstWhy ?? "复检未命中";
            return false;
        }

        // 严格匹配：只认「完全包含」，不做模糊。
        // 选项经常只差一个字（十一届三中全会 / 十一届六中全会），模糊匹配会把错误的那行放行。
        private static bool StrictMatch(string got, string expect)
        {
            string a = AutoJson.Norm(got);
            string b = AutoJson.Norm(expect);
            if (b.Length == 0) return true;
            if (a.Length == 0) return false;
            return a.Contains(b) || b.Contains(a);
        }

        private bool TryOcr(Bitmap img, AutoPick p, double y, out string text)
        {
            text = null;
            try
            {
                int pad = 2;
                int x = (int)Math.Round(p.X * img.Width) - pad;
                int yy = (int)Math.Round(y * img.Height) - pad;
                // 横向至少取 40% 图宽：模型给的框常常只套住开头几个字，长选项会被切掉导致复检失败
                double wide = Math.Max(p.W, 0.40);
                if (p.X + wide > 1.0) wide = 1.0 - p.X;
                int w = (int)Math.Round(wide * img.Width) + pad * 2;
                int h = (int)Math.Round(p.H * img.Height) + pad * 2;
                Rectangle r = Rectangle.Intersect(new Rectangle(x, yy, w, h), new Rectangle(0, 0, img.Width, img.Height));
                if (r.Width < 16 || r.Height < 8) return false;

                using (Bitmap sub = img.Clone(r, PixelFormat.Format32bppArgb))
                {
                    string stat;
                    string url = AiClient.ToDataUrl(sub, 640, 92, out stat);
                    List<object> msgs = new List<object>();
                    Dictionary<string, object> sys = new Dictionary<string, object>();
                    sys["role"] = "system";
                    sys["content"] = AutoPrompt.Ocr;
                    msgs.Add(sys);
                    msgs.Add(AiClient.UserMessage("这张小图里是什么字？", url));

                    string err;
                    UsageInfo usage;
                    text = AiClient.Ask(_cfg, msgs, out err, out usage, true);
                    ReportUsage(usage);
                    return text != null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("auto ocr: " + ex.Message);
                return false;
            }
        }

        private static string Short(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 24 ? t.Substring(0, 24) + "…" : t;
        }

        private bool StillSame(Rectangle win, byte[] sigAtAnalysis)
        {
            Bitmap now = null;
            try
            {
                now = Capture.Window(_hwnd);
                if (now == null) return false;
                if (now.Width != win.Width || now.Height != win.Height) return false;
                MaskStatus(now, win);   // 分析时观察窗被涂白，这里也要涂，否则永远判「画面变了」
                double diff = Similar(Signature(now), sigAtAnalysis);
                if (diff >= 2.5) Log.Write("auto stillsame diff=" + diff.ToString("0.00"));
                return diff < 2.5;
            }
            catch { return false; }
            finally { if (now != null) now.Dispose(); }
        }

        private void ShowOverlay(List<Rectangle> boxes, List<string> labels)
        {
            Ui(delegate
            {
                if (_overlay != null) { try { _overlay.Close(); } catch { } }
                _overlay = new OverlayForm(boxes, labels);
                _overlay.Show();
            });
        }

        // 24x14 灰度指纹，用来判断画面有没有变
        private const int SigW = 24;
        private const int SigH = 14;

        private static byte[] Signature(Bitmap bmp)
        {
            byte[] data = new byte[SigW * SigH];
            using (Bitmap small = new Bitmap(SigW, SigH))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, 0, 0, SigW, SigH);
                }
                for (int y = 0; y < SigH; y++)
                {
                    for (int x = 0; x < SigW; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        data[y * SigW + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                    }
                }
            }
            return data;
        }

        private static double Similar(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 999;
            long sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return (double)sum / a.Length;
        }
    }

    internal static class WindowText
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        public static bool IsAlive(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try { return IsWindow(h); }
            catch { return false; }
        }

        // 是不是「别的程序」的窗口（排除本程序的菜单、答案窗）
        public static bool IsForeign(IntPtr h)
        {
            if (!IsAlive(h)) return false;
            try
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                return pid != 0 && pid != (uint)Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        public static string Get(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowTextW(h, sb, sb.Capacity);
                string s = sb.ToString().Trim();
                if (s.Length > 26) s = s.Substring(0, 26) + "…";
                return s;
            }
            catch { return ""; }
        }
    }

    internal sealed class AutoForm : Form
    {
        private readonly NumericUpDown _interval;
        private readonly NumericUpDown _max;
        private readonly ComboBox _backend;
        private readonly CheckBox _verify;
        private readonly CheckBox _type;

        public AutoForm(AppConfig cfg)
        {
            Text = "自动作答设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(340, 246);
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.FromArgb(235, 235, 235);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label l1 = new Label();
            l1.Text = "抓屏间隔（毫秒）";
            l1.Bounds = new Rectangle(18, 18, 150, 22);
            Controls.Add(l1);

            _interval = new NumericUpDown();
            _interval.Minimum = 800;
            _interval.Maximum = 60000;
            _interval.Increment = 500;
            _interval.Value = Math.Max(800, Math.Min(60000, cfg.AutoIntervalMs));
            _interval.Bounds = new Rectangle(180, 16, 140, 24);
            Controls.Add(_interval);

            Label l2 = new Label();
            l2.Text = "每轮最多点击";
            l2.Bounds = new Rectangle(18, 54, 150, 22);
            Controls.Add(l2);

            _max = new NumericUpDown();
            _max.Minimum = 1;
            _max.Maximum = 40;
            _max.Value = Math.Max(1, Math.Min(40, cfg.AutoMaxClicks));
            _max.Bounds = new Rectangle(180, 52, 140, 24);
            Controls.Add(_max);

            Label l3 = new Label();
            l3.Text = "点击方式";
            l3.Bounds = new Rectangle(18, 90, 150, 22);
            Controls.Add(l3);

            _backend = new ComboBox();
            _backend.DropDownStyle = ComboBoxStyle.DropDownList;
            _backend.Items.Add("发送鼠标事件（前台）");
            _backend.Items.Add("投递窗口消息（后台）");
            _backend.SelectedIndex = string.Equals(cfg.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _backend.Bounds = new Rectangle(180, 88, 140, 24);
            Controls.Add(_backend);

            _verify = new CheckBox();
            _verify.Text = "点击前复检选项文字（更准，多一次调用）";
            _verify.Checked = cfg.AutoVerify;
            _verify.Bounds = new Rectangle(18, 126, 300, 24);
            Controls.Add(_verify);

            _type = new CheckBox();
            _type.Text = "填空题自动粘贴答案";
            _type.Checked = cfg.AutoType;
            _type.Bounds = new Rectangle(18, 154, 300, 24);
            Controls.Add(_type);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Bounds = new Rectangle(150, 198, 84, 28);
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = Color.FromArgb(240, 240, 240);
            ok.ForeColor = Color.FromArgb(12, 12, 13);
            ok.Click += delegate
            {
                cfg.AutoIntervalMs = (int)_interval.Value;
                cfg.AutoMaxClicks = (int)_max.Value;
                cfg.AutoBackend = _backend.SelectedIndex == 1 ? "postmessage" : "sendinput";
                cfg.AutoVerify = _verify.Checked;
                cfg.AutoType = _type.Checked;
                cfg.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Bounds = new Rectangle(240, 198, 84, 28);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.BackColor = Color.FromArgb(48, 48, 52);
            cancel.ForeColor = Color.FromArgb(235, 235, 235);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
        }
    }

    internal static class HotkeyText
    {
        public static string Describe(uint mods, uint vk)
        {
            if (vk == 0) return "（未设置）";
            StringBuilder sb = new StringBuilder();
            if ((mods & 2) != 0) sb.Append("Ctrl+");
            if ((mods & 1) != 0) sb.Append("Alt+");
            if ((mods & 4) != 0) sb.Append("Shift+");
            if ((mods & 8) != 0) sb.Append("Win+");
            sb.Append(KeyName(vk));
            return sb.ToString();
        }

        public static string KeyName(uint vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            switch ((Keys)vk)
            {
                case Keys.Space: return "Space";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PgUp";
                case Keys.PageDown: return "PgDn";
                case Keys.Insert: return "Ins";
                case Keys.Delete: return "Del";
                case Keys.Tab: return "Tab";
                case Keys.Enter: return "Enter";
                case Keys.Escape: return "Esc";
            }
            return "Key" + vk;
        }

        public static void Parse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split(',');
            if (parts.Length != 2) return;
            uint.TryParse(parts[0].Trim(), out mods);
            uint.TryParse(parts[1].Trim(), out vk);
        }

        public static string Format(uint mods, uint vk)
        {
            return mods + "," + vk;
        }
    }

    // 点一下、按下组合键就记下来的输入框
    internal sealed class HotkeyBox : TextBox
    {
        public uint Mods;
        public uint Vk;

        public HotkeyBox(uint mods, uint vk)
        {
            Mods = mods;
            Vk = vk;
            ReadOnly = true;
            TextAlign = HorizontalAlignment.Center;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Color.FromArgb(32, 32, 36);
            ForeColor = Color.FromArgb(240, 240, 240);
            Font = new Font("Microsoft YaHei UI", 9f);
            Text = HotkeyText.Describe(mods, vk);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.Menu || k == Keys.ShiftKey ||
                k == Keys.LWin || k == Keys.RWin || k == Keys.None)
                return;

            if (k == Keys.Back || k == Keys.Delete)
            {
                Mods = 0;
                Vk = 0;
                Text = HotkeyText.Describe(0, 0);
                return;
            }

            uint mods = 0;
            if (e.Control) mods |= 2;
            if (e.Alt) mods |= 1;
            if (e.Shift) mods |= 4;

            Mods = mods;
            Vk = (uint)k;
            Text = HotkeyText.Describe(mods, (uint)k);
        }
    }

    internal sealed class HotkeyForm : Form
    {
        private readonly HotkeyBox _preview;
        private readonly HotkeyBox _click;

        public HotkeyForm(AppConfig cfg)
        {
            Text = "热键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(360, 208);
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.FromArgb(235, 235, 235);
            Font = new Font("Microsoft YaHei UI", 9f);

            uint pm, pvk, cm, cvk;
            HotkeyText.Parse(cfg.HotkeyPreview, out pm, out pvk);
            HotkeyText.Parse(cfg.HotkeyClick, out cm, out cvk);

            Label l1 = new Label();
            l1.Text = "预览（只画框）";
            l1.Bounds = new Rectangle(18, 20, 120, 22);
            Controls.Add(l1);

            _preview = new HotkeyBox(pm, pvk);
            _preview.Bounds = new Rectangle(150, 18, 190, 26);
            Controls.Add(_preview);

            Label l2 = new Label();
            l2.Text = "自动点击";
            l2.Bounds = new Rectangle(18, 60, 120, 22);
            Controls.Add(l2);

            _click = new HotkeyBox(cm, cvk);
            _click.Bounds = new Rectangle(150, 58, 190, 26);
            Controls.Add(_click);

            Label hint = new Label();
            hint.Text = "点进输入框，直接按下想要的组合键。\n支持两键（Ctrl+X）、三键、或单个 F 键；Del 清除。";
            hint.ForeColor = Color.FromArgb(150, 150, 158);
            hint.Bounds = new Rectangle(18, 94, 322, 40);
            Controls.Add(hint);

            Button reset = new Button();
            reset.Text = "恢复默认";
            reset.Bounds = new Rectangle(18, 156, 92, 30);
            reset.FlatStyle = FlatStyle.Flat;
            reset.BackColor = Color.FromArgb(48, 48, 52);
            reset.ForeColor = Color.FromArgb(235, 235, 235);
            reset.Click += delegate
            {
                _preview.Mods = 3; _preview.Vk = 81; _preview.Text = "Ctrl+Alt+Q";
                _click.Mods = 3; _click.Vk = 88; _click.Text = "Ctrl+Alt+X";
            };
            Controls.Add(reset);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Bounds = new Rectangle(172, 156, 82, 30);
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = Color.FromArgb(240, 240, 240);
            ok.ForeColor = Color.FromArgb(12, 12, 13);
            ok.Click += delegate
            {
                cfg.HotkeyPreview = HotkeyText.Format(_preview.Mods, _preview.Vk);
                cfg.HotkeyClick = HotkeyText.Format(_click.Mods, _click.Vk);
                cfg.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Bounds = new Rectangle(262, 156, 82, 30);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.BackColor = Color.FromArgb(48, 48, 52);
            cancel.ForeColor = Color.FromArgb(235, 235, 235);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _keys;
        private string _lastPath;
        private AnswerForm _answer;
        private AutoPilot _auto;
        private IntPtr _autoTarget;
        private IntPtr _lastForeign;
        private Control _uiHost;
        private System.Windows.Forms.Timer _fgWatch;
        private AutoStatusForm _status;
        private ToolStripMenuItem _miAutoOff, _miAutoPreview, _miAutoClick;
        private ToolStripMenuItem _miAutoVerify, _miAutoType, _miTarget;
        private ToolStripMenuItem _miInt2, _miInt3, _miInt5, _miInt8;
        private ToolStripMenuItem _miBackInput, _miBackPost;
        public static AppConfig Config;

        public TrayContext()
        {
            Config = AppConfig.Load();
            AiClient.Lossless = Config.NoCompress;
            try { Directory.CreateDirectory(Config.ShotsDir); } catch { }
            PruneShots(Config.ShotsDir, Config.MaxShots);

            _tray = new NotifyIcon();
            _tray.Icon = LoadAppIcon();
            _tray.Text = Program.AppName;
            _tray.Visible = true;
            _tray.DoubleClick += OnTrayDoubleClick;
            _tray.ContextMenuStrip = BuildMenu();

            Log.Write("tray ctor: tray ok, api=" + Config.HasApi);
            _auto = new AutoPilot(Config);
            _auto.Notice += delegate(string s) { Toast(s, Program.AppName); };

            // 隐藏控件只为拿到 UI 线程句柄：覆盖层和气泡都要回到 UI 线程
            _uiHost = new Control();
            IntPtr forced = _uiHost.Handle;
            _auto.UiHost = _uiHost;

            // 记住「最近一个别的程序的窗口」，避免从托盘菜单开启时把菜单窗口当目标
            _fgWatch = new System.Windows.Forms.Timer();
            _fgWatch.Interval = 1000;
            _fgWatch.Tick += delegate
            {
                IntPtr h = Capture.ForegroundWindow();
                if (WindowText.IsForeign(h)) _lastForeign = h;
            };
            _fgWatch.Start();

            _keys = new HotkeyWindow();
            Log.Write("hotkey window handle=" + _keys.Handle);
            _keys.Pressed += OnHotkey;
            bool ok = RegisterAllHotkeys();
            if (!ok)
            {
                Toast("有热键注册失败（可能被别的程序占用）。\n右键托盘 → 热键设置… 换一个组合。", Program.AppName);
            }
            else if (!Config.HasApi)
            {
                Toast("Ctrl+Alt+S 截窗口 / Ctrl+Alt+A 截区域\n\n还没填 API，现在只会存图并复制路径。\n右键托盘图标 → 设置，填入接口后即可自动出答案。", Program.AppName);
            }
            else
            {
                Toast("已就绪，截完自动出答案。\nCtrl+Alt+S 窗口 / Ctrl+Alt+A 区域 / Ctrl+Alt+D 全屏\n"
                    + HotkeyText.Describe(ParseMods(Config.HotkeyPreview), ParseVk(Config.HotkeyPreview)) + " 预览 / "
                    + HotkeyText.Describe(ParseMods(Config.HotkeyClick), ParseVk(Config.HotkeyClick)) + " 自动点击", Program.AppName);
            }

            // 手动双击启动时直接把设置窗弹出来；开机自启（-Silent）只留在托盘
            if (!Program.Silent) ShowMainWindowSoon();
        }

        private void ShowMainWindowSoon()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 350;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                ShowMainWindow();
            };
            t.Start();
        }

        private bool _settingsOpen;

        // 把设置窗弹出来；已经开着就别再开第二个
        internal void ShowMainWindow()
        {
            if (_settingsOpen) return;
            _settingsOpen = true;
            try { OpenSettings(); }
            catch (Exception ex) { Log.Write("open settings: " + ex.Message); }
            finally { _settingsOpen = false; }
        }

        // 监听「第二个实例启动」的信号，收到就把窗口弹出来
        internal void ListenShowSignal(EventWaitHandle ev)
        {
            Thread th = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        ev.WaitOne();
                        if (_uiHost != null && _uiHost.IsHandleCreated)
                            _uiHost.BeginInvoke((Action)delegate { ShowMainWindow(); });
                    }
                    catch (Exception ex)
                    {
                        Log.Write("show signal: " + ex.Message);
                        return;
                    }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private static uint ParseMods(string s)
        {
            uint m, v;
            HotkeyText.Parse(s, out m, out v);
            return m;
        }

        private static uint ParseVk(string s)
        {
            uint m, v;
            HotkeyText.Parse(s, out m, out v);
            return v;
        }

        // 注册全部热键；返回是否全部成功
        private bool RegisterAllHotkeys()
        {
            _keys.UnregisterAll();

            bool a = _keys.Register(1, Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.S);
            bool b = _keys.Register(2, Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.A);
            bool c = _keys.Register(3, Native.MOD_CONTROL | Native.MOD_ALT, (uint)Keys.D);

            uint pm = ParseMods(Config.HotkeyPreview);
            uint pv = ParseVk(Config.HotkeyPreview);
            uint cm = ParseMods(Config.HotkeyClick);
            uint cv = ParseVk(Config.HotkeyClick);

            bool d = pv != 0 && _keys.Register(4, pm, pv);
            bool e = cv != 0 && _keys.Register(5, cm, cv);

            Log.Write("registered: " + a + "/" + b + "/" + c + "/" + d + "/" + e
                + " preview=" + Config.HotkeyPreview + " click=" + Config.HotkeyClick);
            return a && b && c && d && e;
        }

        private void OpenHotkeys()
        {
            using (SettingsWindow form = new SettingsWindow(Config, "other"))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                bool ok = RegisterAllHotkeys();
                Toast(ok ? "热键已更新。"
                         : "热键保存了，但有组合被占用，注册失败；换一个再试。", Program.AppName);
            }
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                string dir = Program.BaseDir.Length > 0
                    ? Program.BaseDir
                    : Path.GetDirectoryName(Application.ExecutablePath);
                string ico = Path.Combine(dir, "app.ico");
                if (File.Exists(ico)) return new Icon(ico);
            }
            catch { }
            try
            {
                Icon ico2 = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico2 != null) return ico2;
            }
            catch { }
            return SystemIcons.Application;
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(Item("截图并解析：当前窗口      Ctrl+Alt+S", delegate { DoWindow(); }));
            menu.Items.Add(Item("截图并解析：拖选区域      Ctrl+Alt+A", delegate { DoRegion(); }));
            menu.Items.Add(Item("截图并解析：光标所在的屏幕   Ctrl+Alt+D", delegate { DoFull(); }));
            menu.Items.Add(Item("截图并解析：所有屏幕（拼接）", delegate { DoAll(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("打开截图文件夹", delegate { OpenShotsFolder(); }));
            menu.Items.Add(Item("清理旧截图（按保留上限）", delegate { CleanShots(); }));
            menu.Items.Add(Item("复制最后一张的路径", delegate { CopyLastPath(); }));
            menu.Items.Add(Item("复制最后一张的图片", delegate { CopyLastImage(); }));
            menu.Items.Add(Item("用量统计（累计 tokens / 费用）", delegate { ShowUsage(); }));
            menu.Items.Add(Item("重置用量统计", delegate { ResetUsage(); }));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem("截完自动解析");
            auto.CheckOnClick = true;
            auto.Checked = Config.AutoAnalyze;
            auto.Click += delegate(object s, EventArgs e)
            {
                Config.AutoAnalyze = ((ToolStripMenuItem)s).Checked;
                Config.Save();
            };
            menu.Items.Add(auto);

            ToolStripMenuItem cropItem = new ToolStripMenuItem("自动裁掉留白（省 token）");
            cropItem.CheckOnClick = true;
            cropItem.Checked = Config.AutoCrop;
            cropItem.Click += delegate(object s, EventArgs e)
            {
                Config.AutoCrop = ((ToolStripMenuItem)s).Checked;
                Config.Save();
            };
            menu.Items.Add(cropItem);

            ToolStripMenuItem boot = new ToolStripMenuItem("开机自动启动");
            boot.CheckOnClick = true;
            boot.Checked = IsAutoStart();
            boot.Click += OnToggleAutoStart;
            menu.Items.Add(boot);

            ToolStripMenuItem autoMenu = new ToolStripMenuItem("自动作答（待优化）");
            _miAutoOff = new ToolStripMenuItem("关闭");
            _miAutoOff.Click += delegate { StopAuto("已关闭自动作答。"); };
            _miAutoPreview = new ToolStripMenuItem("预览（只画框，不点击）    Ctrl+Alt+Q");
            _miAutoPreview.Click += delegate { StartAuto(AutoPilot.Preview); };
            _miAutoClick = new ToolStripMenuItem("自动点击（识别到答案就直接点）    Ctrl+Alt+X");
            _miAutoClick.Click += delegate { StartAuto(AutoPilot.Click); };
            autoMenu.DropDownItems.Add(_miAutoOff);
            autoMenu.DropDownItems.Add(_miAutoPreview);
            autoMenu.DropDownItems.Add(_miAutoClick);
            autoMenu.DropDownItems.Add(new ToolStripSeparator());

            _miTarget = new ToolStripMenuItem("目标窗口：未选择");
            _miTarget.Click += delegate { SetAutoTarget(); };
            autoMenu.DropDownItems.Add(_miTarget);

            ToolStripMenuItem intervalMenu = new ToolStripMenuItem("抓取间隔");
            int[] ms = new int[] { 2000, 3000, 5000, 8000 };
            string[] names = new string[] { "2 秒（快）", "3 秒（默认）", "5 秒（省 token）", "8 秒（最省）" };
            for (int i = 0; i < ms.Length; i++)
            {
                int value = ms[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(names[i]);
                mi.Click += delegate
                {
                    Config.AutoIntervalMs = value;
                    Config.Save();
                    Toast("抓取间隔：" + (value / 1000) + " 秒", Program.AppName);
                };
                intervalMenu.DropDownItems.Add(mi);
                if (i == 0) _miInt2 = mi;
                else if (i == 1) _miInt3 = mi;
                else if (i == 2) _miInt5 = mi;
                else _miInt8 = mi;
            }
            autoMenu.DropDownItems.Add(intervalMenu);

            ToolStripMenuItem backMenu = new ToolStripMenuItem("点击方式");
            _miBackInput = new ToolStripMenuItem("发送鼠标事件（前台，会移动光标）");
            _miBackInput.Click += delegate { Config.AutoBackend = "sendinput"; Config.Save(); };
            _miBackPost = new ToolStripMenuItem("投递窗口消息（后台，不动光标）");
            _miBackPost.Click += delegate { Config.AutoBackend = "postmessage"; Config.Save(); };
            backMenu.DropDownItems.Add(_miBackInput);
            backMenu.DropDownItems.Add(_miBackPost);
            autoMenu.DropDownItems.Add(backMenu);

            _miAutoVerify = new ToolStripMenuItem("点击前复检选项文字");
            _miAutoVerify.Click += delegate
            {
                Config.AutoVerify = !Config.AutoVerify;
                Config.Save();
                Toast(Config.AutoVerify ? "复检已开：点之前先 OCR 比对选项文字" : "复检已关：点得更快，但可能点歪", Program.AppName);
            };
            autoMenu.DropDownItems.Add(_miAutoVerify);

            _miAutoType = new ToolStripMenuItem("填空题自动粘贴答案");
            _miAutoType.Click += delegate
            {
                Config.AutoType = !Config.AutoType;
                Config.Save();
            };
            autoMenu.DropDownItems.Add(_miAutoType);

            autoMenu.DropDownItems.Add(new ToolStripSeparator());
            autoMenu.DropDownItems.Add(Item("自动作答设置…", delegate { OpenAutoSettings(); }));
            menu.Items.Add(autoMenu);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item("设置…（填 API）", delegate { OpenSettings(); }));
            menu.Items.Add(Item("热键设置…", delegate { OpenHotkeys(); }));
            menu.Items.Add(Item("帮助 / 用法", delegate { ShowHelp(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("退出", delegate { Quit(); }));
            menu.Opening += delegate { RefreshAutoMenu(); };
            return menu;
        }

        private void RefreshAutoMenu()
        {
            int mode = _auto == null ? AutoPilot.Off : _auto.Mode;
            _miAutoOff.Checked = mode == AutoPilot.Off;
            _miAutoPreview.Checked = mode == AutoPilot.Preview;
            _miAutoClick.Checked = mode == AutoPilot.Click;
            _miAutoVerify.Checked = Config.AutoVerify;
            _miAutoType.Checked = Config.AutoType;
            _miBackInput.Checked = !string.Equals(Config.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase);
            _miBackPost.Checked = !_miBackInput.Checked;
            _miInt2.Checked = Config.AutoIntervalMs <= 2400;
            _miInt3.Checked = Config.AutoIntervalMs > 2400 && Config.AutoIntervalMs <= 4000;
            _miInt5.Checked = Config.AutoIntervalMs > 4000 && Config.AutoIntervalMs <= 6500;
            _miInt8.Checked = Config.AutoIntervalMs > 6500;

            string title = WindowText.Get(_autoTarget);
            _miTarget.Text = title.Length > 0 ? ("目标窗口：" + title) : "目标窗口：未选择（点我选当前窗口）";

            _miAutoPreview.Text = "预览（只画框，不点击）    "
                + HotkeyText.Describe(ParseMods(Config.HotkeyPreview), ParseVk(Config.HotkeyPreview));
            _miAutoClick.Text = "自动点击（识别到答案就直接点）    "
                + HotkeyText.Describe(ParseMods(Config.HotkeyClick), ParseVk(Config.HotkeyClick));
        }

        private IntPtr TargetOrFallback()
        {
            if (WindowText.IsAlive(_lastForeign)) return _lastForeign;
            if (WindowText.IsAlive(_autoTarget)) return _autoTarget;
            return IntPtr.Zero;
        }

        private void StartAuto(int mode)
        {
            if (!Config.HasApi)
            {
                Toast("先填 API 才能自动作答。", Program.AppName);
                return;
            }
            IntPtr h = TargetOrFallback();
            if (h == IntPtr.Zero)
            {
                Toast("没找到目标窗口：先切到题目窗口，再按 Ctrl+Alt+Q。", Program.AppName);
                return;
            }
            _autoTarget = h;
            Config.AutoMode = mode;
            Config.Save();

            if (_status != null) { try { _status.Close(); } catch { } _status = null; }
            _status = new AutoStatusForm();
            _status.StopRequested = delegate { StopAuto("已停止自动作答。"); };
            Screen scr = Screen.FromHandle(h);
            _status.PlaceAt(scr != null ? scr : Screen.PrimaryScreen);
            _status.Show();
            _auto.Status = _status;

            _auto.Start(mode, h);
        }

        private void StopAuto(string msg)
        {
            if (_auto != null)
            {
                _auto.Stop();
                _auto.Status = null;
            }
            if (_status != null)
            {
                try { _status.Close(); } catch { }
                _status = null;
            }
            if (msg != null) Toast(msg, Program.AppName);
        }

        // Ctrl+Alt+Q：预览（只画框）
        private void ToggleAuto()
        {
            if (_auto != null && _auto.Mode != AutoPilot.Off)
            {
                StopAuto("已停止自动作答。");
                return;
            }
            StartAuto(AutoPilot.Preview);
        }

        // Ctrl+Alt+X：直接自动点击
        private void ToggleAutoClick()
        {
            if (_auto != null && _auto.Mode != AutoPilot.Off)
            {
                StopAuto("已停止自动作答。");
                return;
            }
            StartAuto(AutoPilot.Click);
        }

        private void SetAutoTarget()
        {
            IntPtr h = Capture.ForegroundWindow();
            if (!WindowText.IsForeign(h)) h = _lastForeign;
            if (!WindowText.IsAlive(h))
            {
                Toast("没拿到目标窗口：先点一下题目的窗口，再回来点这一项。", Program.AppName);
                return;
            }
            _autoTarget = h;
            Log.Write("auto target=" + h + " " + WindowText.Get(h));
            Toast("目标窗口：" + WindowText.Get(h), Program.AppName);
        }

        private void OpenAutoSettings()
        {
            using (SettingsWindow form = new SettingsWindow(Config, "auto"))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                RegisterAllHotkeys();
                Toast("设置已保存（自动作答正在跑的话，下一轮生效）。", Program.AppName);
            }
        }

        private static ToolStripMenuItem Item(string text, EventHandler handler)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Click += handler;
            return mi;
        }

        private void OnHotkey(int id)
        {
            Log.Write("hotkey fired " + id);
            if (id == 1) DoWindow();
            else if (id == 2) DoRegion();
            else if (id == 3) DoFull();
            else if (id == 4) ToggleAuto();
            else if (id == 5) ToggleAutoClick();
        }

        private void OnTrayDoubleClick(object sender, EventArgs e)
        {
            DoRegion();
        }

        private void DoWindow()
        {
            IntPtr h = Capture.ForegroundWindow();
            if (h == _keys.Handle) return;
            Bitmap bmp = Capture.Window(h);
            if (bmp == null)
            {
                Toast("当前窗口无法截图（可能已最小化，或没有焦点窗口）。", Program.AppName);
                return;
            }
            HandleShot(bmp);
        }

        private void DoFull()
        {
            // 只截光标所在的那块屏幕，双屏时不会把另一块也拼进来
            Screen scr = Screen.FromPoint(Cursor.Position);
            Log.Write("DoFull " + scr.DeviceName);
            Bitmap bmp = Capture.ScreenRegion(scr.Bounds);
            if (bmp == null)
            {
                Toast("截图失败。", Program.AppName);
                return;
            }
            HandleShot(bmp);
        }

        private void DoAll()
        {
            Log.Write("DoAll");
            Bitmap bmp = Capture.FullScreen();
            if (bmp == null)
            {
                Toast("截图失败。", Program.AppName);
                return;
            }
            HandleShot(bmp);
        }

        private void DoRegion()
        {
            Rectangle vb = SystemInformation.VirtualScreen;
            Bitmap full = Capture.ScreenRegion(vb);
            if (full == null)
            {
                Toast("截图失败。", Program.AppName);
                return;
            }
            try
            {
                using (RegionForm form = new RegionForm(full, vb))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    Rectangle sel = form.SelectedRect;
                    if (sel.Width < 4 || sel.Height < 4) return;
                    HandleShot(full.Clone(sel, PixelFormat.Format32bppArgb));
                }
            }
            finally
            {
                full.Dispose();
            }
        }

        private void HandleShot(Bitmap bmp)
        {
            Log.Write("HandleShot " + bmp.Width + "x" + bmp.Height);
            string path;
            try
            {
                path = SaveShot(bmp);
            }
            catch (Exception ex)
            {
                Toast("保存失败：" + ex.Message, Program.AppName);
                bmp.Dispose();
                return;
            }

            Log.Write("saved " + path);
            _lastPath = path;
            bool copied = CopyText(path);

            bool analyze = Config.HasApi && Config.AutoAnalyze;
            if (analyze)
            {
                Analyze(bmp, path);
                bmp.Dispose();
                if (!copied) Toast("已保存（复制路径失败）\n" + Path.GetFileName(path), Program.AppName);
            }
            else
            {
                bmp.Dispose();
                if (Config.HasApi)
                    Toast((copied ? "已保存，路径已复制" : "已保存") + "\n" + Path.GetFileName(path), "截图完成");
                else
                    Toast("已保存，路径已复制到剪贴板\n（填了 API 就能自动出答案）\n" + Path.GetFileName(path), "截图完成");
            }
        }

        private static string SaveShot(Bitmap bmp)
        {
            Directory.CreateDirectory(Config.ShotsDir);
            string name = "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            string path = Path.Combine(Config.ShotsDir, name);
            bmp.Save(path, ImageFormat.Png);
            PruneShots(Config.ShotsDir, Config.MaxShots);
            return path;
        }

        // 只保留最近 max 张 shot-*.png；max<=0 表示不限制
        internal static int PruneShots(string dir, int max)
        {
            if (max <= 0) return 0;
            try
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                if (!di.Exists) return 0;

                FileInfo[] files = di.GetFiles("shot-*.png");
                if (files.Length <= max) return 0;

                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return b.LastWriteTime.CompareTo(a.LastWriteTime);
                });

                int removed = 0;
                for (int i = max; i < files.Length; i++)
                {
                    try { files[i].Delete(); removed++; }
                    catch { }
                }
                if (removed > 0) Log.Write("prune shots: keep=" + max + " removed=" + removed);
                return removed;
            }
            catch (Exception ex)
            {
                Log.Write("prune shots failed: " + ex.Message);
                return 0;
            }
        }

        private void Analyze(Bitmap bmp, string path)
        {
            if (_answer != null)
            {
                try { _answer.Close(); } catch { }
                _answer = null;
            }
            Bitmap use = bmp;
            if (Config.AutoCrop)
            {
                string note;
                Stopwatch cropWatch = Stopwatch.StartNew();
                Rectangle box = AutoCrop.Detect(bmp, out note);
                if (box.Width < bmp.Width || box.Height < bmp.Height)
                {
                    use = bmp.Clone(box, PixelFormat.Format32bppArgb);
                    Log.Write("autocrop " + note + " crop=" + cropWatch.ElapsedMilliseconds + "ms");
                }
            }
            _answer = new AnswerForm(Config, use, path);
            _answer.Show();
            if (!ReferenceEquals(use, bmp)) use.Dispose();
            Log.Write("shown bounds=" + _answer.Bounds + " visible=" + _answer.Visible);
            _answer.BeginAnalyze();
        }

        private void Reanalyze()
        {
            if (_lastPath == null || !File.Exists(_lastPath))
            {
                Toast("这个会话里还没有截图。", Program.AppName);
                return;
            }
            using (Bitmap bmp = new Bitmap(_lastPath))
            {
                Analyze(new Bitmap(bmp), _lastPath);
            }
        }

        private static bool CopyText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch { Thread.Sleep(60); }
            }
            return false;
        }

        private void CopyLastPath()
        {
            if (_lastPath == null)
            {
                Toast("这个会话里还没有截图。", Program.AppName);
                return;
            }
            Toast(CopyText(_lastPath) ? "已复制：" + _lastPath : "复制失败，剪贴板被占用。", Program.AppName);
        }

        private void CopyLastImage()
        {
            if (_lastPath == null || !File.Exists(_lastPath))
            {
                Toast("这个会话里还没有截图。", Program.AppName);
                return;
            }
            try
            {
                using (Bitmap bmp = new Bitmap(_lastPath))
                {
                    Clipboard.SetImage(bmp);
                }
                Toast("图片已复制，可直接粘贴到聊天窗口。", Program.AppName);
            }
            catch (Exception ex)
            {
                Toast("复制图片失败：" + ex.Message, Program.AppName);
            }
        }

        private void OpenShotsFolder()
        {
            try
            {
                Directory.CreateDirectory(Config.ShotsDir);
                Process.Start("explorer.exe", "\"" + Config.ShotsDir + "\"");
            }
            catch { }
        }

        private void ShowUsage()
        {
            long total = Config.StatPrompt + Config.StatCompletion;
            bool peak = Pricing.IsPeak(DateTime.Now);
            string text = string.Format(
                "模型：{0}\n" +
                "累计 {1} tokens（输入 {2}，其中缓存命中 {3}；输出 {4}）\n" +
                "估算费用：{5}\n\n" +
                "计费规则：{6}（输入未命中 ¥{7}/M，输出 ¥{8}/M，缓存命中 ¥{9}/M）",
                Config.ApiModel,
                total.ToString("N0"), Config.StatPrompt.ToString("N0"), Config.StatCacheHit.ToString("N0"),
                Config.StatCompletion.ToString("N0"), Pricing.Money(Config.StatCost),
                peak ? "当前为高峰时段" : "当前为空闲时段（半价）",
                "2.00", "8.00", "0.04");
            MessageBox.Show(text, "用量统计", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ResetUsage()
        {
            if (MessageBox.Show("把累计的 token 和费用清零？（不影响账单）", "重置用量统计",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Config.StatPrompt = 0;
            Config.StatCompletion = 0;
            Config.StatCacheHit = 0;
            Config.StatCacheMiss = 0;
            Config.StatCost = 0;
            Config.Save();
            Toast("用量统计已清零。", Program.AppName);
        }

        private void CleanShots()
        {
            int n = PruneShots(Config.ShotsDir, Config.MaxShots);
            string msg;
            if (Config.MaxShots <= 0) msg = "当前设为不限制（0 张），没有清理。";
            else if (n > 0) msg = "已清理 " + n + " 张旧截图，保留最近 " + Config.MaxShots + " 张。";
            else msg = "没有超出上限的截图（上限 " + Config.MaxShots + " 张）。";
            Toast(msg, Program.AppName);
        }

        private void OpenSettings()
        {
            using (SettingsWindow form = new SettingsWindow(Config, "api"))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                try { Directory.CreateDirectory(Config.ShotsDir); } catch { }
                bool ok = RegisterAllHotkeys();
                Toast(Config.HasApi
                    ? "设置已保存。" + (ok ? "" : "（热键有冲突，换一个组合）")
                    : "已保存，但 API 没填全，暂时只存图。", Program.AppName);
            }
        }

        private void ShowHelp()
        {
            string text =
                "三种截法（任何程序里都能按）：\n" +
                "  Ctrl+Alt+S   截当前窗口\n" +
                "  Ctrl+Alt+A   拖选区域（Esc 取消）\n" +
                "  Ctrl+Alt+D   截整个屏幕\n\n" +
                "截完会自动弹出一个解析窗：答案 + 依据，可以继续追问、复制。\n" +
                "截图同时存到：" + Config.ShotsDir + "\n\n" +
                "接口在“设置”里填（OpenAI 兼容格式）：\n" +
                "  Base URL   如 https://api.deepseek.com/v1\n" +
                "  模型名     如 deepseek-chat\n" +
                "  API Key    只存在本机，DPAPI 加密\n\n" +
                "想让我（DSH）深度讲题时，同时按 Ctrl+C 复制答案，\n" +
                "或把截图路径粘到对话里。";
            MessageBox.Show(text, Program.AppName + " - 用法", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnToggleAutoStart(object sender, EventArgs e)
        {
            ToolStripMenuItem mi = (ToolStripMenuItem)sender;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (mi.Checked) key.SetValue(Program.AppId, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(Program.AppId, false);
                }
                Toast(mi.Checked ? "已加入开机启动" : "已取消开机启动", Program.AppName);
            }
            catch (Exception ex)
            {
                mi.Checked = !mi.Checked;
                Toast("设置开机启动失败：" + ex.Message, Program.AppName);
            }
        }

        internal static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(Program.AppId) != null;
                }
            }
            catch { return false; }
        }

        internal static bool SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on)
                    {
                        // 优先用 wscript + vbs：不会闪 PowerShell 黑框；没有 vbs 时退回直接起 powershell
                        string dir = Program.BaseDir.Length > 0
                            ? Program.BaseDir
                            : Path.GetDirectoryName(Application.ExecutablePath);
                        string vbs = Path.Combine(dir, "DshShot.vbs");
                        string script = Path.Combine(dir, "launch.ps1");
                        string cmd = File.Exists(vbs)
                            ? ("wscript.exe //nologo \"" + vbs + "\" -Silent")
                            : ("powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File \""
                                + script + "\" -Silent");
                        key.SetValue(Program.AppId, cmd);
                    }
                    else
                    {
                        key.DeleteValue(Program.AppId, false);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private void Quit()
        {
            try { if (_auto != null) _auto.Dispose(); } catch { }
            try { if (_fgWatch != null) _fgWatch.Dispose(); } catch { }
            _keys.Dispose();
            if (_answer != null)
            {
                try { _answer.Close(); } catch { }
            }
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }

        private void Toast(string text, string title)
        {
            try { _tray.ShowBalloonTip(2500, title, text, ToolTipIcon.Info); }
            catch { }
        }
    }

    internal sealed class AnswerForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly string _dataUrl;
        private readonly PictureBox _thumb;
        private readonly RichTextBox _answer;
        private readonly TextBox _ask;
        private readonly Button _send;
        private readonly Label _status;
        private readonly Label _title;
        private readonly Label _close;
        private readonly Label _copy;
        private readonly System.Windows.Forms.Timer _tick;
        private readonly List<object> _history = new List<object>();
        private DateTime _started;
        private bool _busy;
        private UsageInfo _lastUsage;

        public AnswerForm(AppConfig cfg, Bitmap shot, string path)
        {
            _cfg = cfg;
            _dataUrl = AiClient.ToDataUrl(shot, cfg.MaxEdge, cfg.JpegQuality);
            _history.Add(new Dictionary<string, object> { { "role", "system" }, { "content", cfg.Prompt } });

            Text = "题目解析";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(10, 10, 11);
            ForeColor = Color.FromArgb(242, 242, 242);
            ClientSize = new Size(330, 378);
            KeyPreview = true;
            Padding = new Padding(1);
            Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(42, 42, 45)))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            _title = new Label();
            _title.Text = "题目解析";
            _title.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _title.ForeColor = Color.FromArgb(245, 245, 245);
            _title.BackColor = Color.Transparent;
            _title.Bounds = new Rectangle(14, 10, 180, 22);
            _title.MouseDown += DragWindow;
            Controls.Add(_title);

            _copy = new Label();
            _copy.Text = "复制";
            _copy.TextAlign = ContentAlignment.MiddleCenter;
            _copy.Font = new Font("Microsoft YaHei UI", 8f);
            _copy.ForeColor = Color.FromArgb(150, 150, 156);
            _copy.BackColor = Color.Transparent;
            _copy.Bounds = new Rectangle(230, 8, 56, 22);
            _copy.Cursor = Cursors.Hand;
            _copy.Click += delegate { CopyAnswer(); };
            Controls.Add(_copy);

            _close = new Label();
            _close.Text = "✕";
            _close.TextAlign = ContentAlignment.MiddleCenter;
            _close.Font = new Font("Microsoft YaHei UI", 9f);
            _close.ForeColor = Color.FromArgb(150, 150, 156);
            _close.BackColor = Color.Transparent;
            _close.Bounds = new Rectangle(292, 8, 24, 22);
            _close.Cursor = Cursors.Hand;
            _close.Click += delegate { Close(); };
            Controls.Add(_close);

            Panel rule1 = new Panel();
            rule1.Bounds = new Rectangle(14, 38, 302, 1);
            rule1.BackColor = Color.FromArgb(38, 38, 41);
            Controls.Add(rule1);

            _thumb = new PictureBox();
            _thumb.Bounds = new Rectangle(14, 46, 60, 38);
            _thumb.SizeMode = PictureBoxSizeMode.Zoom;
            _thumb.BackColor = Color.FromArgb(17, 17, 19);
            using (Bitmap t = new Bitmap(shot))
            {
                _thumb.Image = thumbImage(t, 60, 38);
            }
            Controls.Add(_thumb);

            _status = new Label();
            _status.Bounds = new Rectangle(84, 46, 232, 38);
            _status.Font = new Font("Microsoft YaHei UI", 8f);
            _status.ForeColor = Color.FromArgb(140, 140, 146);
            _status.Text = "准备中…";
            Controls.Add(_status);

            Panel rule2 = new Panel();
            rule2.Bounds = new Rectangle(14, 92, 302, 1);
            rule2.BackColor = Color.FromArgb(38, 38, 41);
            Controls.Add(rule2);

            Panel card = new Panel();
            card.Bounds = new Rectangle(14, 100, 302, 210);
            card.BackColor = Color.FromArgb(17, 17, 19);
            card.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(38, 38, 41)))
                    e.Graphics.DrawRectangle(pen, 0, 0, ((Panel)s).Width - 1, ((Panel)s).Height - 1);
            };
            Controls.Add(card);

            _answer = new RichTextBox();
            _answer.Bounds = new Rectangle(9, 9, 284, 192);
            _answer.BackColor = Color.FromArgb(17, 17, 19);
            _answer.ForeColor = Color.FromArgb(238, 238, 238);
            _answer.BorderStyle = BorderStyle.None;
            _answer.Font = new Font("Microsoft YaHei UI", 9f);
            _answer.ReadOnly = true;
            _answer.DetectUrls = true;
            _answer.ScrollBars = RichTextBoxScrollBars.Vertical;
            _answer.Text = "";
            _answer.TabStop = false;
            card.Controls.Add(_answer);

            _ask = new TextBox();
            _ask.Bounds = new Rectangle(14, 320, 222, 26);
            _ask.BorderStyle = BorderStyle.FixedSingle;
            _ask.BackColor = Color.FromArgb(17, 17, 19);
            _ask.ForeColor = Color.FromArgb(238, 238, 238);
            _ask.Font = new Font("Microsoft YaHei UI", 8.5f);
            _ask.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendFollowUp(); }
            };
            Controls.Add(_ask);

            _send = new Button();
            _send.Text = "追问";
            _send.Bounds = new Rectangle(242, 320, 74, 26);
            _send.FlatStyle = FlatStyle.Flat;
            _send.FlatAppearance.BorderSize = 0;
            _send.BackColor = Color.FromArgb(240, 240, 240);
            _send.ForeColor = Color.FromArgb(12, 12, 13);
            _send.Font = new Font("Microsoft YaHei UI", 8f);
            _send.Click += delegate { SendFollowUp(); };
            Controls.Add(_send);

            Label hint = new Label();
            hint.Text = "Esc 关闭 · 拖动标题移动 · 追问沿用这张图";
            hint.Bounds = new Rectangle(14, 352, 302, 16);
            hint.Font = new Font("Microsoft YaHei UI", 7f);
            hint.ForeColor = Color.FromArgb(110, 110, 116);
            Controls.Add(hint);

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 1000;
            _tick.Tick += delegate
            {
                if (!_busy) return;
                _status.Text = "识别中… " + (int)(DateTime.Now - _started).TotalSeconds + "s";
            };

            Screen scr = Ui.ScreenUnderCursor();
            Ui.ScaleForm(this, Ui.DpiScale(scr));
            PositionWindow(scr);
        }

        private static Image thumbImage(Bitmap src, int w, int h)
        {
            Bitmap b = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double k = Math.Max((double)w / src.Width, (double)h / src.Height);
                int dw = (int)(src.Width * k), dh = (int)(src.Height * k);
                g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            return b;
        }

        private void PositionWindow(Screen scr)
        {
            Rectangle wa = scr.WorkingArea;
            int x = wa.Right - Width - 24;
            int y = wa.Top + 72;
            if (x < wa.Left) x = wa.Left + 8;
            if (y + Height > wa.Bottom) y = Math.Max(wa.Top + 8, wa.Bottom - Height - 8);
            Location = new Point(x, y);
            Log.Write("position x=" + x + " y=" + y + " form=" + Width + "x" + Height);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _ask.Focus();
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        public void BeginAnalyze()
        {
            _history.Add(AiClient.UserMessage("请解析这张截图。", _dataUrl));
            Send();
        }

        private void SendFollowUp()
        {
            if (_busy) return;
            string q = _ask.Text.Trim();
            if (q.Length == 0) return;
            _ask.Text = "";
            _history.Add(AiClient.UserMessage(q, null));
            _answer.AppendText("\n\n— 你：" + q + "\n");
            Send();
        }

        private void Send()
        {
            _busy = true;
            _started = DateTime.Now;
            _status.Text = "识别中… 0s";
            _tick.Start();
            _send.Enabled = false;
            _ask.Enabled = false;

            List<object> snapshot = new List<object>(_history);
            AppConfig cfg = _cfg;
            Log.Write("ask start msgs=" + snapshot.Count + " maxEdge=" + cfg.MaxEdge + " q=" + cfg.JpegQuality);

            Task<string[]> task = Task.Factory.StartNew<string[]>(delegate
            {
                string err;
                UsageInfo usage;
                string text = AiClient.Ask(cfg, snapshot, out err, out usage);
                _lastUsage = usage;
                return new string[] { text, err };
            });

            task.ContinueWith(delegate(Task<string[]> t)
            {
                string text = null, err = null;
                try
                {
                    text = t.Result[0];
                    err = t.Result[1];
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnResult(text, err); });
                }
                catch { }
            });
        }

        private void OnResult(string text, string error)
        {
            Log.Write("ask end " + (int)(DateTime.Now - _started).TotalMilliseconds + "ms err=" + (error == null ? "-" : "yes"));
            _tick.Stop();
            _busy = false;
            _send.Enabled = true;
            _ask.Enabled = true;

            if (error != null)
            {
                _status.Text = "× 请求失败";
                _status.ForeColor = Color.FromArgb(158, 158, 164);
                _answer.SelectionColor = Color.FromArgb(222, 222, 226);
                string detail = error.Length > 300 ? error.Substring(0, 300) + "…" : error;
                _answer.AppendText(detail + "\n\n检查 Base URL（要带 /v1）、模型是否支持图片、Key 是否有效。");
                _answer.SelectionColor = Color.FromArgb(232, 232, 232);
                return;
            }

            int secs = (int)(DateTime.Now - _started).TotalSeconds;
            if (_lastUsage != null && _lastUsage.Total > 0)
            {
                double cost = Pricing.Cost(_cfg.ApiModel, _lastUsage);
                _cfg.StatPrompt += _lastUsage.Prompt;
                _cfg.StatCompletion += _lastUsage.Completion;
                _cfg.StatCacheHit += _lastUsage.CacheHit;
                _cfg.StatCacheMiss += _lastUsage.CacheMiss;
                _cfg.StatCost += cost;
                _cfg.Save();
                long total = _cfg.StatPrompt + _cfg.StatCompletion;
                Log.Write(string.Format("cost tok={0} (in={1} hit={2} out={3}) money={4} totalTok={5} totalCost={6}",
                    _lastUsage.Total, _lastUsage.Prompt, _lastUsage.CacheHit, _lastUsage.Completion,
                    cost.ToString("0.000000", CultureInfo.InvariantCulture), total,
                    _cfg.StatCost.ToString("0.000000", CultureInfo.InvariantCulture)));
                _status.Text = string.Format("完成 · {0}s · 本次 {1} tok · {2}\n累计 {3} tok · {4}",
                    secs, Pricing.Tok(_lastUsage.Total), Pricing.Money(cost),
                    Pricing.Tok(total), Pricing.Money(_cfg.StatCost));
            }
            else
            {
                _status.Text = "完成 · " + secs + "s";
            }
            _status.ForeColor = Color.FromArgb(158, 158, 164);
            _answer.AppendText(text + "\n");
            _answer.SelectionStart = _answer.TextLength;
            _answer.ScrollToCaret();
            _history.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", text } });
        }

        private void CopyAnswer()
        {
            try
            {
                if (_answer.TextLength == 0)
                {
                    Clipboard.SetText(_status.Text);
                }
                else
                {
                    Clipboard.SetText(_answer.Text);
                }
                _copy.Text = "已复制";
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1200;
                t.Tick += delegate { _copy.Text = "复制"; t.Stop(); t.Dispose(); };
                t.Start();
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tick != null) _tick.Dispose();
                if (_thumb != null && _thumb.Image != null) _thumb.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class RegionForm : Form
    {
        private readonly Bitmap _shot;
        private readonly Font _labelFont;
        private readonly SolidBrush _dim;
        private Point _start;
        private Point _cur;
        private bool _dragging;

        public Rectangle SelectedRect;

        public RegionForm(Bitmap shot, Rectangle virtualBounds)
        {
            _shot = shot;
            _labelFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            _dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0));

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = virtualBounds;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Focus();
        }

        private Rectangle CurrentRect()
        {
            if (!_dragging && _cur == Point.Empty) return Rectangle.Empty;
            int x = Math.Min(_start.X, _cur.X);
            int y = Math.Min(_start.Y, _cur.Y);
            int w = Math.Abs(_cur.X - _start.X);
            int h = Math.Abs(_cur.Y - _start.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(_shot, 0, 0);
            g.FillRectangle(_dim, ClientRectangle);

            Rectangle sel = CurrentRect();
            if (sel.Width > 0 && sel.Height > 0)
            {
                g.SetClip(sel);
                g.DrawImageUnscaled(_shot, 0, 0);
                g.ResetClip();

                using (Pen p = new Pen(Color.FromArgb(255, 245, 245, 245), 2))
                    g.DrawRectangle(p, sel);

                string label = sel.Width + " × " + sel.Height;
                SizeF size = g.MeasureString(label, _labelFont);
                float tx = sel.Left;
                float ty = sel.Top - size.Height - 8;
                if (ty < 0) ty = sel.Bottom + 8;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20)))
                    g.FillRectangle(bg, tx, ty, size.Width + 12, size.Height + 6);
                g.DrawString(label, _labelFont, Brushes.White, tx + 6, ty + 3);
            }
            else
            {
                string hint = "拖动鼠标框选，Esc 取消";
                SizeF size = g.MeasureString(hint, _labelFont);
                float x = (ClientSize.Width - size.Width) / 2f;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                    g.FillRectangle(bg, x - 16, 28, size.Width + 32, size.Height + 16);
                g.DrawString(hint, _labelFont, Brushes.White, x, 36);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _start = e.Location;
                _cur = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _cur = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _cur = e.Location;
            Rectangle sel = CurrentRect();
            if (sel.Width >= 4 && sel.Height >= 4)
            {
                SelectedRect = Rectangle.Intersect(sel, new Rectangle(0, 0, _shot.Width, _shot.Height));
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                _start = Point.Empty;
                _cur = Point.Empty;
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_labelFont != null) _labelFont.Dispose();
                if (_dim != null) _dim.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // 统一的设置窗：黑白简约，三个分页，整体按系统 DPI 缩放
    internal sealed class SettingsWindow : Form
    {
        private static readonly Color Bg = Color.FromArgb(16, 16, 16);
        private static readonly Color Field = Color.FromArgb(28, 28, 28);
        private static readonly Color Ink = Color.FromArgb(236, 236, 236);
        private static readonly Color Muted = Color.FromArgb(146, 146, 152);
        private static readonly Color Line = Color.FromArgb(48, 48, 52);

        private readonly AppConfig _cfg;

        private Panel _paneApi, _paneAuto, _paneOther;
        private Label _tabApi, _tabAuto, _tabOther;

        private TextBox _baseUrl, _model, _key, _prompt, _shotsDir;
        private Label _testState;
        private NumericUpDown _interval, _idle, _maxClicks, _autoEdge, _autoQuality;
        private NumericUpDown _maxEdge, _quality, _maxShots;
        private ComboBox _backend;
        private CheckBox _verify, _autoType, _split, _doubleCheck, _thinkTie, _webSearch, _noCompress;
        private CheckBox _autoAnalyze, _autoCrop, _autoStart;
        private HotkeyBox _hkPreview, _hkClick;

        public SettingsWindow(AppConfig cfg, string startTab)
        {
            _cfg = cfg;

            Text = "设置";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(560, 640);
            MinimumSize = new Size(576, 679);
            DoubleBuffered = true;

            Label title = new Label();
            title.Text = "设置";
            title.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            title.ForeColor = Ink;
            title.Bounds = new Rectangle(24, 18, 300, 32);
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "接口、自动作答、截图与热键";
            sub.Font = new Font("Microsoft YaHei UI", 8.5f);
            sub.ForeColor = Muted;
            sub.Bounds = new Rectangle(24, 50, 400, 20);
            Controls.Add(sub);

            _tabApi = MakeTab("接口", 24, 84);
            _tabAuto = MakeTab("自动作答", 128, 84);
            _tabOther = MakeTab("其他", 232, 84);

            Panel host = new Panel();
            host.Bounds = new Rectangle(24, 128, 512, 452);
            host.BackColor = Bg;
            Controls.Add(host);

            BuildApi(host);
            BuildAuto(host);
            BuildOther(host);

            _tabApi.Click += delegate { ShowPane(0); };
            _tabAuto.Click += delegate { ShowPane(1); };
            _tabOther.Click += delegate { ShowPane(2); };

            Button defaults = MakeButton("恢复默认", 24, 596, 96, false);
            defaults.Click += delegate { ApplyDefaults(); };
            Controls.Add(defaults);

            Button cancel = MakeButton("取消", 352, 596, 88, false);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            Button save = MakeButton("保存", 448, 596, 88, true);
            save.Click += delegate { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            Controls.Add(save);

            ShowPane(startTab == "auto" ? 1 : (startTab == "other" ? 2 : 0));

            // 先按 DPI 缩放，再设锚点：顺序反了的话缩放时锚点会把控件拉出边界
            float scale = DshShot.Ui.DpiScale(Screen.PrimaryScreen);
            if (scale > 1.001f) DshShot.Ui.ScaleForm(this, scale);

            host.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            defaults.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            AnchorPane(_paneApi);
            AnchorPane(_paneAuto);
            AnchorPane(_paneOther);
        }

        // 面板铺满容器；贴着右边/占大半宽度的控件跟随右边缘，其余保持左边距
        private static void AnchorPane(Panel pane)
        {
            pane.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            int w = pane.Width;
            int h = pane.Height;
            foreach (Control c in pane.Controls)
            {
                AnchorStyles a = AnchorStyles.Top | AnchorStyles.Left;
                if (c.Right >= w - 12 || c.Width >= 250 || c.Left > w / 2) a |= AnchorStyles.Right;
                if (c.Bottom >= h - 12) a |= AnchorStyles.Bottom;
                c.Anchor = a;
            }
        }

        // ---------------- 分页 ----------------

        private void ShowPane(int index)
        {
            _paneApi.Visible = index == 0;
            _paneAuto.Visible = index == 1;
            _paneOther.Visible = index == 2;

            StyleTab(_tabApi, index == 0);
            StyleTab(_tabAuto, index == 1);
            StyleTab(_tabOther, index == 2);
        }

        private void StyleTab(Label tab, bool active)
        {
            tab.BackColor = active ? Ink : Bg;
            tab.ForeColor = active ? Bg : Muted;
        }

        private Label MakeTab(string text, int x, int y)
        {
            Label tab = new Label();
            tab.Text = text;
            tab.TextAlign = ContentAlignment.MiddleCenter;
            tab.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            tab.Bounds = new Rectangle(x, y, 96, 32);
            tab.Cursor = Cursors.Hand;
            Controls.Add(tab);
            return tab;
        }

        private void BuildApi(Control host)
        {
            _paneApi = MakePane(host);

            AddLabel(_paneApi, "Base URL", 0, 10, 110);
            _baseUrl = AddText(_paneApi, _cfg.ApiBase, 118, 8, 394, false);
            AddLabel(_paneApi, "模型名", 0, 46, 110);
            _model = AddText(_paneApi, _cfg.ApiModel, 118, 44, 394, false);
            AddLabel(_paneApi, "API Key", 0, 82, 110);
            _key = AddText(_paneApi, _cfg.ApiKey, 118, 80, 394, true);

            AddLabel(_paneApi, "回答风格（系统提示词）", 0, 118, 240);
            _prompt = AddText(_paneApi, _cfg.Prompt, 0, 142, 512, false);
            _prompt.Multiline = true;
            _prompt.ScrollBars = ScrollBars.Vertical;
            _prompt.Height = 186;
            _prompt.AcceptsReturn = true;

            Button test = MakeButton("测试连接", 0, 348, 108, false);
            test.Parent = _paneApi;
            test.Click += delegate { TestConnection(); };

            _testState = new Label();
            _testState.Text = "";
            _testState.ForeColor = Muted;
            _testState.Bounds = new Rectangle(120, 352, 392, 22);
            _paneApi.Controls.Add(_testState);

            Label note = new Label();
            note.Text = "Key 只存本机（DPAPI 加密）。模型必须支持图片输入。";
            note.ForeColor = Muted;
            note.Bounds = new Rectangle(0, 386, 512, 20);
            _paneApi.Controls.Add(note);
        }

        private void BuildAuto(Control host)
        {
            _paneAuto = MakePane(host);

            AddLabel(_paneAuto, "抓屏间隔（毫秒）", 0, 12, 180);
            _interval = AddNum(_paneAuto, _cfg.AutoIntervalMs, 800, 60000, 500, 200, 10, 120);

            AddLabel(_paneAuto, "停手等待（毫秒）", 0, 48, 180);
            _idle = AddNum(_paneAuto, _cfg.AutoIdleMs, 300, 10000, 100, 200, 46, 120);

            AddLabel(_paneAuto, "每轮最多点击", 0, 84, 180);
            _maxClicks = AddNum(_paneAuto, _cfg.AutoMaxClicks, 1, 40, 1, 200, 82, 120);

            AddLabel(_paneAuto, "点击方式", 0, 120, 180);
            _backend = new ComboBox();
            _backend.DropDownStyle = ComboBoxStyle.DropDownList;
            _backend.Items.Add("前台鼠标（会移动光标）");
            _backend.Items.Add("后台消息（不动光标）");
            _backend.SelectedIndex = string.Equals(_cfg.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _backend.Bounds = new Rectangle(200, 118, 240, 26);
            _backend.FlatStyle = FlatStyle.Flat;
            _backend.BackColor = Field;
            _backend.ForeColor = Ink;
            _paneAuto.Controls.Add(_backend);

            AddLabel(_paneAuto, "送图最长边", 0, 156, 180);
            _autoEdge = AddNum(_paneAuto, _cfg.AutoMaxEdge, 512, 4096, 64, 200, 154, 120);

            AddLabel(_paneAuto, "JPEG 质量", 0, 192, 180);
            _autoQuality = AddNum(_paneAuto, _cfg.AutoQuality, 50, 98, 1, 200, 190, 120);

            _verify = AddCheck(_paneAuto, "点击前复检选项文字", _cfg.AutoVerify, 0, 232, 250);
            _autoType = AddCheck(_paneAuto, "填空题自动粘贴", _cfg.AutoType, 260, 232, 200);
            _split = AddCheck(_paneAuto, "上下分半各扫一次", _cfg.AutoSplitScan, 0, 262, 250);
            _doubleCheck = AddCheck(_paneAuto, "逐题复核（两次答案比对）", _cfg.AutoDoubleCheck, 260, 262, 250);
            _thinkTie = AddCheck(_paneAuto, "冲突时用思考模式裁决", _cfg.AutoThinkTie, 0, 292, 300);
            _webSearch = AddCheck(_paneAuto, "还定不下来就联网搜索核对", _cfg.AutoWebSearch, 0, 322, 320);
            _noCompress = AddCheck(_paneAuto, "送原图（不缩放、不转 JPEG）", _cfg.NoCompress, 0, 352, 320);

            Label note = new Label();
            note.Text = "复检/复核越开越准，但每轮调用次数和耗时也越多。";
            note.ForeColor = Muted;
            note.Bounds = new Rectangle(0, 392, 512, 20);
            _paneAuto.Controls.Add(note);
        }

        private void BuildOther(Control host)
        {
            _paneOther = MakePane(host);

            AddLabel(_paneOther, "截图目录", 0, 12, 110);
            _shotsDir = AddText(_paneOther, _cfg.ShotsDir, 118, 10, 268, false);
            Button browse = MakeButton("浏览…", 400, 8, 112, false);
            browse.Parent = _paneOther;
            browse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    if (Directory.Exists(_shotsDir.Text)) dlg.SelectedPath = _shotsDir.Text;
                    if (dlg.ShowDialog() == DialogResult.OK) _shotsDir.Text = dlg.SelectedPath;
                }
            };

            AddLabel(_paneOther, "手动截图最长边", 0, 48, 180);
            _maxEdge = AddNum(_paneOther, _cfg.MaxEdge, 400, 4096, 64, 200, 46, 120);

            AddLabel(_paneOther, "手动 JPEG 质量", 0, 84, 180);
            _quality = AddNum(_paneOther, _cfg.JpegQuality, 40, 95, 1, 200, 82, 120);

            _autoAnalyze = AddCheck(_paneOther, "截完自动解析", _cfg.AutoAnalyze, 0, 124, 220);
            _autoCrop = AddCheck(_paneOther, "自动裁掉留白（省 token）", _cfg.AutoCrop, 240, 124, 260);
            _autoStart = AddCheck(_paneOther, "开机自动启动", TrayContext.IsAutoStart(), 0, 154, 220);

            AddLabel(_paneOther, "最多保留截图（张）", 0, 190, 180);
            _maxShots = AddNum(_paneOther, _cfg.MaxShots, 0, 100000, 50, 200, 188, 120);
            Label shotsNote = new Label();
            shotsNote.Text = "0 = 不限制；超出上限时自动删最旧的";
            shotsNote.ForeColor = Muted;
            shotsNote.Bounds = new Rectangle(330, 192, 182, 20);
            _paneOther.Controls.Add(shotsNote);

            Label hk = new Label();
            hk.Text = "热键";
            hk.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            hk.ForeColor = Ink;
            hk.Bounds = new Rectangle(0, 232, 100, 22);
            _paneOther.Controls.Add(hk);

            AddLabel(_paneOther, "预览（只画框）", 0, 264, 130);
            uint pm, pv, cm, cv;
            HotkeyText.Parse(_cfg.HotkeyPreview, out pm, out pv);
            HotkeyText.Parse(_cfg.HotkeyClick, out cm, out cv);
            _hkPreview = new HotkeyBox(pm, pv);
            _hkPreview.Bounds = new Rectangle(136, 262, 180, 26);
            _paneOther.Controls.Add(_hkPreview);

            AddLabel(_paneOther, "自动点击", 0, 298, 130);
            _hkClick = new HotkeyBox(cm, cv);
            _hkClick.Bounds = new Rectangle(136, 296, 180, 26);
            _paneOther.Controls.Add(_hkClick);

            Label note = new Label();
            note.Text = "点进输入框直接按组合键；支持 Ctrl+X 这种两键、单个 F 键；Del 清除。";
            note.ForeColor = Muted;
            note.Bounds = new Rectangle(0, 336, 512, 20);
            _paneOther.Controls.Add(note);
        }

        // ---------------- 小工具 ----------------

        private Panel MakePane(Control host)
        {
            Panel p = new Panel();
            p.Bounds = new Rectangle(0, 0, 512, 452);
            p.BackColor = Bg;
            p.Visible = false;
            host.Controls.Add(p);
            return p;
        }

        private static void AddLabel(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Muted;
            l.Bounds = new Rectangle(x, y, w, 22);
            parent.Controls.Add(l);
        }

        private static TextBox AddText(Control parent, string value, int x, int y, int w, bool password)
        {
            TextBox t = new TextBox();
            t.Text = value;
            if (password) t.PasswordChar = '●';
            t.Bounds = new Rectangle(x, y, w, 26);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = Field;
            t.ForeColor = Ink;
            t.Font = new Font("Microsoft YaHei UI", 9f);
            parent.Controls.Add(t);
            return t;
        }

        private static NumericUpDown AddNum(Control parent, int value, int min, int max, int step, int x, int y, int w)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Bounds = new Rectangle(x, y, w, 26);
            n.BorderStyle = BorderStyle.FixedSingle;
            n.BackColor = Field;
            n.ForeColor = Ink;
            parent.Controls.Add(n);
            return n;
        }

        private static CheckBox AddCheck(Control parent, string text, bool value, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = value;
            c.ForeColor = Ink;
            c.Bounds = new Rectangle(x, y, w, 24);
            parent.Controls.Add(c);
            return c;
        }

        private static Button MakeButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, 32);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? Ink : Color.FromArgb(38, 38, 42);
            b.ForeColor = primary ? Bg : Ink;
            b.Font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            return b;
        }

        // ---------------- 行为 ----------------

        private void ApplyDefaults()
        {
            AppConfig d = new AppConfig();
            _interval.Value = d.AutoIntervalMs;
            _idle.Value = d.AutoIdleMs;
            _maxClicks.Value = d.AutoMaxClicks;
            _autoEdge.Value = d.AutoMaxEdge;
            _autoQuality.Value = d.AutoQuality;
            _backend.SelectedIndex = 0;
            _verify.Checked = d.AutoVerify;
            _autoType.Checked = d.AutoType;
            _split.Checked = d.AutoSplitScan;
            _doubleCheck.Checked = d.AutoDoubleCheck;
            _thinkTie.Checked = d.AutoThinkTie;
            _maxEdge.Value = d.MaxEdge;
            _quality.Value = d.JpegQuality;
            _autoAnalyze.Checked = d.AutoAnalyze;
            _autoCrop.Checked = d.AutoCrop;
            _hkPreview.Text = "Ctrl+Alt+Q"; _hkPreview.Mods = 3; _hkPreview.Vk = 81;
            _hkClick.Text = "Ctrl+Alt+X"; _hkClick.Mods = 3; _hkClick.Vk = 88;
        }

        private void TestConnection()
        {
            AppConfig probe = new AppConfig();
            probe.ApiBase = _baseUrl.Text.Trim();
            probe.ApiModel = _model.Text.Trim();
            probe.ApiKey = _key.Text;
            probe.Prompt = _prompt.Text;

            _testState.Text = "测试中…";
            _testState.ForeColor = Muted;

            Task.Factory.StartNew(delegate
            {
                string err;
                string reply = AiClient.Test(probe, out err);
                string msg;
                Color color;
                if (reply == null)
                {
                    msg = "失败：" + (err ?? "无响应");
                    color = Color.FromArgb(235, 120, 120);
                }
                else
                {
                    msg = "连接正常，返回：" + (reply.Length > 24 ? reply.Substring(0, 24) + "…" : reply);
                    color = Color.FromArgb(140, 210, 150);
                }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _testState.Text = msg;
                        _testState.ForeColor = color;
                    });
                }
                catch { }
            });
        }

        private bool Commit()
        {
            _cfg.ApiBase = _baseUrl.Text.Trim();
            _cfg.ApiModel = _model.Text.Trim();
            _cfg.ApiKey = _key.Text;
            if (_prompt.Text.Trim().Length > 0) _cfg.Prompt = _prompt.Text;

            _cfg.AutoIntervalMs = (int)_interval.Value;
            _cfg.AutoIdleMs = (int)_idle.Value;
            _cfg.AutoMaxClicks = (int)_maxClicks.Value;
            _cfg.AutoMaxEdge = (int)_autoEdge.Value;
            _cfg.AutoQuality = (int)_autoQuality.Value;
            _cfg.AutoBackend = _backend.SelectedIndex == 1 ? "postmessage" : "sendinput";
            _cfg.AutoVerify = _verify.Checked;
            _cfg.AutoType = _autoType.Checked;
            _cfg.AutoSplitScan = _split.Checked;
            _cfg.AutoDoubleCheck = _doubleCheck.Checked;
            _cfg.AutoThinkTie = _thinkTie.Checked;
            _cfg.AutoWebSearch = _webSearch.Checked;
            _cfg.NoCompress = _noCompress.Checked;
            AiClient.Lossless = _cfg.NoCompress;

            if (_shotsDir.Text.Trim().Length > 0) _cfg.ShotsDir = _shotsDir.Text.Trim();
            _cfg.MaxEdge = (int)_maxEdge.Value;
            _cfg.JpegQuality = (int)_quality.Value;
            _cfg.MaxShots = (int)_maxShots.Value;
            _cfg.AutoAnalyze = _autoAnalyze.Checked;
            _cfg.AutoCrop = _autoCrop.Checked;
            _cfg.HotkeyPreview = HotkeyText.Format(_hkPreview.Mods, _hkPreview.Vk);
            _cfg.HotkeyClick = HotkeyText.Format(_hkClick.Mods, _hkClick.Vk);

            TrayContext.SetAutoStart(_autoStart.Checked);
            _cfg.Save();
            return true;
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox _dir;
        private readonly TextBox _base;
        private readonly TextBox _model;
        private readonly TextBox _key;
        private readonly CheckBox _auto;
        private readonly TextBox _prompt;
        private readonly Label _test;

        public string ShotsDir { get { return _dir.Text.Trim(); } }
        public string ApiBase { get { return _base.Text.Trim(); } }
        public string ApiModel { get { return _model.Text.Trim(); } }
        public string ApiKey { get { return _key.Text.Trim(); } }
        public string Prompt { get { return _prompt.Text.Trim(); } }

        public SettingsForm(AppConfig cfg)
        {
            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(620, 612);

            AddLabel("截图保存目录", 16, 14);
            _dir = AddBox(cfg.ShotsDir, 16, 38, 470);
            Button browse = new Button();
            browse.Text = "浏览…";
            browse.Bounds = new Rectangle(496, 37, 88, 27);
            browse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择截图保存目录";
                    dlg.SelectedPath = _dir.Text;
                    if (dlg.ShowDialog() == DialogResult.OK) _dir.Text = dlg.SelectedPath;
                }
            };
            Controls.Add(browse);

            AddLabel("接口地址 Base URL（OpenAI 兼容，一般以 /v1 结尾）", 16, 78);
            _base = AddBox(cfg.ApiBase, 16, 102, 568);

            AddLabel("模型名（必须支持图片输入）", 16, 140);
            _model = AddBox(cfg.ApiModel, 16, 164, 280);

            AddLabel("API Key（只存本机，DPAPI 加密）", 316, 140);
            _key = AddBox(cfg.ApiKey, 316, 164, 268);
            _key.UseSystemPasswordChar = true;

            _auto = new CheckBox();
            _auto.Text = "截完自动解析（否则只存图并复制路径）";
            _auto.Bounds = new Rectangle(16, 208, 420, 24);
            _auto.Checked = cfg.AutoAnalyze;
            Controls.Add(_auto);

            Button test = new Button();
            test.Text = "测试连接";
            test.Bounds = new Rectangle(16, 246, 100, 30);
            test.Click += delegate { RunTest(); };
            Controls.Add(test);

            _test = new Label();
            _test.Bounds = new Rectangle(128, 250, 476, 44);
            _test.Font = new Font("Microsoft YaHei UI", 9f);
            Controls.Add(_test);

            Label tip = new Label();
            tip.Bounds = new Rectangle(16, 300, 588, 40);
            tip.Font = new Font("Microsoft YaHei UI", 8.5f);
            tip.ForeColor = Color.FromArgb(120, 120, 120);
            tip.Text = "DeepSeek 官方地址：https://api.deepseek.com/v1\n" +
                       "通义 / 智谱 / Kimi / OpenAI 等均可用，填对应的 /v1 地址与视觉模型名。";
            Controls.Add(tip);

            AddLabel("回答风格（提示词，可自己改）", 16, 348);
            _prompt = new TextBox();
            _prompt.Bounds = new Rectangle(16, 372, 588, 130);
            _prompt.Multiline = true;
            _prompt.ScrollBars = ScrollBars.Vertical;
            _prompt.AcceptsReturn = true;
            _prompt.Font = new Font("Microsoft YaHei UI", 9f);
            _prompt.Text = cfg.Prompt;
            Controls.Add(_prompt);

            Button restore = new Button();
            restore.Text = "恢复默认";
            restore.Bounds = new Rectangle(16, 510, 110, 28);
            restore.Click += delegate { _prompt.Text = Program.DefaultPrompt; };
            Controls.Add(restore);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Bounds = new Rectangle(418, 566, 84, 30);
            ok.Click += delegate { Apply(cfg); };

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Bounds = new Rectangle(512, 566, 84, 30);

            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);

            BackColor = Color.FromArgb(10, 10, 11);
            ForeColor = Color.FromArgb(238, 238, 238);
            foreach (Control c in Controls)
            {
                Button b = c as Button;
                if (b != null)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = Color.FromArgb(58, 58, 62);
                    b.BackColor = Color.FromArgb(17, 17, 19);
                    b.ForeColor = Color.FromArgb(238, 238, 238);
                    continue;
                }
                TextBox t = c as TextBox;
                if (t != null)
                {
                    t.BackColor = Color.FromArgb(17, 17, 19);
                    t.ForeColor = Color.FromArgb(238, 238, 238);
                    t.BorderStyle = BorderStyle.FixedSingle;
                    continue;
                }
                Label l = c as Label;
                if (l != null)
                {
                    l.ForeColor = Color.FromArgb(168, 168, 174);
                    continue;
                }
                CheckBox cb = c as CheckBox;
                if (cb != null) cb.ForeColor = Color.FromArgb(210, 210, 214);
            }

            Ui.ScaleForm(this, Ui.DpiScale(Screen.PrimaryScreen));
        }

        private void Apply(AppConfig cfg)
        {
            if (ShotsDir.Length > 0) cfg.ShotsDir = ShotsDir;
            cfg.ApiBase = ApiBase;
            cfg.ApiModel = ApiModel;
            cfg.ApiKey = ApiKey;
            cfg.AutoAnalyze = _auto.Checked;
            if (Prompt.Length > 0) cfg.Prompt = Prompt;
        }

        private void RunTest()
        {
            AppConfig probe = new AppConfig();
            probe.ApiBase = ApiBase;
            probe.ApiModel = ApiModel;
            probe.ApiKey = ApiKey;

            if (!probe.HasApi)
            {
                _test.ForeColor = Color.FromArgb(214, 214, 220);
                _test.Text = "先把地址、模型名、Key 三项都填上。";
                return;
            }

            _test.ForeColor = Color.FromArgb(140, 140, 146);
            _test.Text = "测试中…";
            Cursor = Cursors.WaitCursor;
            try
            {
                string err;
                string answer = AiClient.Test(probe, out err);
                if (err != null)
                {
                    _test.ForeColor = Color.FromArgb(214, 214, 220);
                    string e = err.Length > 180 ? err.Substring(0, 180) + "…" : err;
                    _test.Text = "失败：" + e;
                }
                else
                {
                    _test.ForeColor = Color.FromArgb(190, 190, 196);
                    _test.Text = "连接正常，返回：" + (answer != null ? answer : "(空)");
                }
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Bounds = new Rectangle(x, y, 560, 20);
            l.Font = new Font("Microsoft YaHei UI", 9f);
            Controls.Add(l);
        }

        private TextBox AddBox(string text, int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.Bounds = new Rectangle(x, y, w, 26);
            Controls.Add(t);
            return t;
        }
    }
}
