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
            return Window(hwnd, "auto");
        }

        // backend: auto / screen / printwindow
        // auto：前台窗口用屏幕复制；被别的窗口盖住时改用 PrintWindow（不需要 WGC）。
        // PrintWindow 对 GDI/DWM 窗口有效；个别 GPU 合成/DRM 窗口会返回黑图，此时自动退回屏幕复制。
        public static Bitmap Window(IntPtr hwnd, string backend)
        {
            if (hwnd == IntPtr.Zero) return null;
            if (Native.IsIconic(hwnd)) return null;
            Rectangle r = WindowBounds(hwnd);
            if (r.Width < 8 || r.Height < 8) return null;

            string mode = (backend ?? "auto").Trim().ToLowerInvariant();
            bool foreground = Native.GetForegroundWindow() == hwnd;
            bool tryPrint = mode == "printwindow" || (mode != "screen" && !foreground);

            if (tryPrint)
            {
                Bitmap shot = PrintWindowBitmap(hwnd, r);
                if (shot != null)
                {
                    Log.Write("capture printwindow " + r.Width + "x" + r.Height + " fg=" + foreground);
                    return shot;
                }
                Log.Write("capture printwindow failed, fallback screen " + r.Width + "x" + r.Height);
            }
            return ScreenRegion(r);
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

        private const uint PW_RENDERFULLCONTENT = 2;

        private static Bitmap PrintWindowBitmap(IntPtr hwnd, Rectangle r)
        {
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                    if (!ok) { bmp.Dispose(); return null; }
                }
                if (LooksBlank(bmp)) { bmp.Dispose(); return null; }
                return bmp;
            }
            catch
            {
                if (bmp != null) bmp.Dispose();
                return null;
            }
        }

        // 判断一张图是不是「整块同色」——PrintWindow 在不支持的窗口上通常返回全黑或全白。
        internal static bool LooksBlank(Bitmap bmp)
        {
            if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return true;
            int stepX = Math.Max(1, bmp.Width / 24);
            int stepY = Math.Max(1, bmp.Height / 24);
            int min = 765, max = 0;
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    Color c = bmp.GetPixel(x, y);
                    int v = c.R + c.G + c.B;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            return max - min < 12;
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
}
