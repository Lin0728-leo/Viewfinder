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
            Pricing.Load(Program.AppDir);
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
            Bitmap bmp = Capture.Window(h, Config.CaptureBackend);
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
}
