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
        private CancellationTokenSource _cts;
        private CancellationToken _token = CancellationToken.None;

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
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            _cts = new CancellationTokenSource();
            _token = _cts.Token;
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
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
            _token = CancellationToken.None;
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
            bool fromScreen = true;
            try
            {
                frame = Capture.Window(_hwnd, _cfg.CaptureBackend, out fromScreen);
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
            // PrintWindow 拿到的是目标窗口自己的画面，不含本程序状态窗；只有屏幕复制才需要涂白
            if (fromScreen) MaskStatus(frame, win);

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
            CancellationToken token = _token;
            Task.Factory.StartNew(delegate
            {
                try { Run(snap, win, mode, sig, token); }
                catch (Exception ex) { Log.Write("auto run failed: " + ex); Notify("自动作答出错：" + ex.Message); }
                finally { snap.Dispose(); _busy = false; }
            });
        }

        private void Run(Bitmap frame, Rectangle win, int mode, byte[] sigAtAnalysis, CancellationToken token)
        {
            _roundStart = DateTime.Now;
            _clicksThisRound = 0;
            if (token.IsCancellationRequested) return;

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
                if (token.IsCancellationRequested) return;

                UiStatus(delegate(AutoStatusForm f) { f.SetStage("识别中…"); });

                string err;
                UsageInfo usage;
                List<AutoQuestion> qs = Scan(send, dataUrl, edge, quality, out err, out usage, token);
                if (token.IsCancellationRequested) return;
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
                    List<AutoQuestion> qs2 = Analyze(dataUrl, false, out err2, out usage2, token);
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
                    if (token.IsCancellationRequested) return;
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
                        if (!DoubleCheck(send, q, token)) continue;
                    }

                    foreach (AutoPick p in q.Picks)
                    {
                        string why;
                        string stageText = "复检 " + q.No + " " + p.Label + "…";
                        UiStatus(delegate(AutoStatusForm f) { f.SetStage(stageText); });

                        // 先在模型给的框上 OCR 校对；对不上就在上下附近找文字真正的位置，并把 p.Y 修正过来
                        bool verified = Verify(send, p, out why, token);

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
                        if (!ClickAt(q, p, sx, sy, win, sigAtAnalysis, token)) return;
                    }
                }

                if (token.IsCancellationRequested) return;
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
        private List<AutoQuestion> Scan(Bitmap send, string fullUrl, int edge, int quality, out string error, out UsageInfo usage, CancellationToken token)
        {
            error = null;
            usage = null;

            if (!_cfg.AutoSplitScan || send.Height < 700)
                return Analyze(fullUrl, true, out error, out usage, token);

            int h = send.Height;
            int halfH = (int)(h * 0.58);
            if (halfH >= h || halfH < 200)
                return Analyze(fullUrl, true, out error, out usage, token);

            Rectangle top = new Rectangle(0, 0, send.Width, halfH);
            Rectangle bottom = new Rectangle(0, h - halfH, send.Width, halfH);

            string e1, e2;
            UsageInfo u1, u2;
            List<AutoQuestion> a = AnalyzeRegion(send, top, edge, quality, out e1, out u1, token);
            List<AutoQuestion> b = AnalyzeRegion(send, bottom, edge, quality, out e2, out u2, token);

            usage = SumUsage(u1, u2);
            if (e1 != null && e2 != null) error = e1;

            List<AutoQuestion> merged = Merge(a, b);
            Log.Write("auto split: top=" + a.Count + " bottom=" + b.Count + " merged=" + merged.Count);
            return merged;
        }

        private List<AutoQuestion> AnalyzeRegion(Bitmap img, Rectangle region, int edge, int quality,
            out string error, out UsageInfo usage, CancellationToken token)
        {
            error = null;
            usage = null;
            Bitmap sub = null;
            try
            {
                sub = img.Clone(region, PixelFormat.Format32bppArgb);
                string stat;
                string url = AiClient.ToDataUrl(sub, edge, quality, out stat);
                List<AutoQuestion> list = Analyze(url, true, out error, out usage, token);

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

        private List<AutoQuestion> Analyze(string dataUrl, bool noThink, out string error, out UsageInfo usage, CancellationToken token)
        {
            error = null;
            usage = null;
            List<object> msgs = new List<object>();
            Dictionary<string, object> sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = AutoPrompt.Ask;
            msgs.Add(sys);
            msgs.Add(AiClient.UserMessage("按上面的 JSON 格式输出这张图里的可作答题目。", dataUrl));

            string text = AiClient.Ask(_cfg, msgs, out error, out usage, noThink, token);
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

        private bool ClickAt(AutoQuestion q, AutoPick p, int sx, int sy, Rectangle win, byte[] sigAtAnalysis, CancellationToken token)
        {
            if (token.IsCancellationRequested) return false;
            bool post = string.Equals(_cfg.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase);
            if (post)
            {
                Clicker.PostLeftClick(_hwnd, sx, sy);
                Thread.Sleep(220);
                // 后台消息不是所有程序都认；画面没动静时提示用户换前台模式，但不自动补点（可能造成双选）
                if (StillSame(win, sigAtAnalysis))
                {
                    Log.Write("auto post no-change q=" + q.No);
                    UiStatus(delegate(AutoStatusForm f)
                    {
                        f.AddLog("后台点击后画面未变化（该程序可能忽略后台消息）");
                    });
                }
            }
            else
            {
                if (Clicker.Foreground() != _hwnd)
                {
                    Notify("目标窗口不在前台，已暂停自动点击。");
                    Stop();
                    return false;
                }
                Clicker.LeftClick(sx, sy, _cfg.AutoRestoreCursor);
            }

            _clicksThisRound++;
            _done[q.Key()] = DateTime.Now;
            Log.Write("auto click q=" + q.No + " label=" + p.Label + " text=" + p.Text + " at " + sx + "," + sy + " backend=" + (post ? "post" : "input"));
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
        private bool DoubleCheck(Bitmap send, AutoQuestion q, CancellationToken token)
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
            string one = AskOne(send, r, false, out err, token);
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
                think = AskOne(send, r, true, out err2, token);
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
                string qtext = AskCrop(send, r, AutoPrompt.Query, "输出这道题的搜索词。", false, out qerr, token);
                string query = (qtext ?? "").Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '“', '”');
                if (query.Length > 60) query = query.Substring(0, 60);

                if (query.Length > 0)
                {
                    string serr;
                    string web = AiClient.Search(_cfg, query, 4, out serr, token);
                    if (web != null)
                    {
                        string werr;
                        string webAns = AskCrop(send, r, AutoPrompt.WithWeb,
                            "检索资料：\n" + web + "\n\n只输出答案。", false, out werr, token);
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

        private string AskOne(Bitmap send, Rectangle region, bool think, out string error, CancellationToken token)
        {
            return AskCrop(send, region, AutoPrompt.One, "只输出这道题的答案。", think, out error, token);
        }

        // 单题裁剪 + 放大 + 只问这一题；sys/user 可换（回答、生成搜索词、带资料回答）
        private string AskCrop(Bitmap send, Rectangle region, string sysPrompt, string userText, bool think, out string error, CancellationToken token)
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
                string text = AiClient.Ask(_cfg, msgs, out error, out usage, !think, token);
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
        private bool Verify(Bitmap img, AutoPick p, out string why, CancellationToken token)
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
                if (!TryOcr(img, p, ny, out got, token))
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

        private bool TryOcr(Bitmap img, AutoPick p, double y, out string text, CancellationToken token)
        {
            text = null;
            if (token.IsCancellationRequested) return false;
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
                    text = AiClient.Ask(_cfg, msgs, out err, out usage, true, token);
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
                bool fromScreen;
                now = Capture.Window(_hwnd, _cfg.CaptureBackend, out fromScreen);
                if (now == null) return false;
                if (now.Width != win.Width || now.Height != win.Height) return false;
                if (fromScreen) MaskStatus(now, win);   // 与分析阶段保持一致，否则会误判「画面变了」
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
}
