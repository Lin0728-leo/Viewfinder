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
        private CancellationTokenSource _cts;

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
            if (_cts != null) { try { _cts.Cancel(); } catch { } _cts.Dispose(); }
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            Log.Write("ask start msgs=" + snapshot.Count + " maxEdge=" + cfg.MaxEdge + " q=" + cfg.JpegQuality);

            Task<object[]> task = Task.Factory.StartNew<object[]>(delegate
            {
                string err;
                UsageInfo usage;
                string text = AiClient.Ask(cfg, snapshot, out err, out usage, false, token);
                return new object[] { text, err, usage };
            });

            task.ContinueWith(delegate(Task<object[]> t)
            {
                string text = null, err = null;
                UsageInfo usage = null;
                try
                {
                    text = t.Result[0] as string;
                    err = t.Result[1] as string;
                    usage = t.Result[2] as UsageInfo;
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnResult(text, err, usage); });
                }
                catch { }
            });
        }

        private void OnResult(string text, string error, UsageInfo usage)
        {
            _lastUsage = usage;
            Log.Write("ask end " + (int)(DateTime.Now - _started).TotalMilliseconds + "ms err=" + (error == null ? "-" : "yes"));
            _tick.Stop();
            _busy = false;
            _send.Enabled = true;
            _ask.Enabled = true;

            if (error == "已取消")
            {
                _status.Text = "已取消";
                return;
            }

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
                if (_cts != null) { try { _cts.Cancel(); } catch { } _cts.Dispose(); _cts = null; }
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
        private string _keyMasked = "";
        private Label _testState;
        private NumericUpDown _interval, _idle, _maxClicks, _autoEdge, _autoQuality;
        private NumericUpDown _maxEdge, _quality, _maxShots;
        private ComboBox _backend;
        private ComboBox _capture;
        private CheckBox _restoreCursor;
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
            // 已有 Key 时只显示掩码：避免在截图、录屏、远程协助里被看到明文
            _keyMasked = _cfg.ApiKey.Length > 0 ? ("已保存 " + MaskKey(_cfg.ApiKey) + "（点这里可换新的）") : "";
            _key = AddText(_paneApi, _keyMasked, 118, 80, 330, false);
            if (_keyMasked.Length > 0) _key.ForeColor = Muted;
            _key.Enter += delegate
            {
                if (_key.Text == _keyMasked)
                {
                    _key.Text = "";
                    _key.ForeColor = Ink;
                    _key.PasswordChar = '●';
                }
            };

            Button clearKey = MakeButton("清除", 456, 78, 56, false);
            clearKey.Parent = _paneApi;
            clearKey.Click += delegate
            {
                _key.Text = "";
                _key.ForeColor = Ink;
                _key.PasswordChar = '●';
            };

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

            AddLabel(_paneAuto, "抓屏间隔（毫秒）", 0, 8, 180);
            _interval = AddNum(_paneAuto, _cfg.AutoIntervalMs, 800, 60000, 500, 200, 6, 120);

            AddLabel(_paneAuto, "停手等待（毫秒）", 0, 38, 180);
            _idle = AddNum(_paneAuto, _cfg.AutoIdleMs, 300, 10000, 100, 200, 36, 120);

            AddLabel(_paneAuto, "每轮最多点击", 0, 68, 180);
            _maxClicks = AddNum(_paneAuto, _cfg.AutoMaxClicks, 1, 40, 1, 200, 66, 120);

            AddLabel(_paneAuto, "点击方式", 0, 98, 180);
            _backend = new ComboBox();
            _backend.DropDownStyle = ComboBoxStyle.DropDownList;
            _backend.Items.Add("前台鼠标（会移动光标）");
            _backend.Items.Add("后台消息（不动光标）");
            _backend.SelectedIndex = string.Equals(_cfg.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _backend.Bounds = new Rectangle(200, 96, 240, 26);
            _backend.FlatStyle = FlatStyle.Flat;
            _backend.BackColor = Field;
            _backend.ForeColor = Ink;
            _paneAuto.Controls.Add(_backend);

            AddLabel(_paneAuto, "抓屏方式", 0, 128, 180);
            _capture = new ComboBox();
            _capture.DropDownStyle = ComboBoxStyle.DropDownList;
            _capture.Items.Add("自动（前台用屏幕，遮挡用 PrintWindow）");
            _capture.Items.Add("屏幕复制（要求窗口在最上面）");
            _capture.Items.Add("PrintWindow（可截被遮挡的窗口）");
            _capture.SelectedIndex = _cfg.CaptureBackend == "screen" ? 1 : (_cfg.CaptureBackend == "printwindow" ? 2 : 0);
            _capture.Bounds = new Rectangle(200, 126, 300, 26);
            _capture.FlatStyle = FlatStyle.Flat;
            _capture.BackColor = Field;
            _capture.ForeColor = Ink;
            _paneAuto.Controls.Add(_capture);

            AddLabel(_paneAuto, "送图最长边", 0, 158, 180);
            _autoEdge = AddNum(_paneAuto, _cfg.AutoMaxEdge, 512, 4096, 64, 200, 156, 120);

            AddLabel(_paneAuto, "JPEG 质量", 0, 188, 180);
            _autoQuality = AddNum(_paneAuto, _cfg.AutoQuality, 50, 98, 1, 200, 186, 120);

            _verify = AddCheck(_paneAuto, "点击前复检选项文字", _cfg.AutoVerify, 0, 218, 250);
            _autoType = AddCheck(_paneAuto, "填空题自动粘贴", _cfg.AutoType, 260, 218, 200);
            _restoreCursor = AddCheck(_paneAuto, "前台点击后把光标放回原处", _cfg.AutoRestoreCursor, 0, 248, 320);
            _split = AddCheck(_paneAuto, "上下分半各扫一次", _cfg.AutoSplitScan, 0, 278, 250);
            _doubleCheck = AddCheck(_paneAuto, "逐题复核（两次答案比对）", _cfg.AutoDoubleCheck, 260, 278, 250);
            _thinkTie = AddCheck(_paneAuto, "冲突时用思考模式裁决", _cfg.AutoThinkTie, 0, 308, 300);
            _webSearch = AddCheck(_paneAuto, "还定不下来就联网搜索核对", _cfg.AutoWebSearch, 0, 338, 320);
            _noCompress = AddCheck(_paneAuto, "送原图（不缩放、不转 JPEG）", _cfg.NoCompress, 0, 368, 320);

            Label note = new Label();
            note.Text = "后台点击不动光标，但少数程序会忽略；前台点击兼容性好，点完自动还原光标。";
            note.ForeColor = Muted;
            note.Bounds = new Rectangle(0, 398, 512, 20);
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
            _backend.SelectedIndex = string.Equals(d.AutoBackend, "postmessage", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _capture.SelectedIndex = d.CaptureBackend == "screen" ? 1 : (d.CaptureBackend == "printwindow" ? 2 : 0);
            _restoreCursor.Checked = d.AutoRestoreCursor;
            _verify.Checked = d.AutoVerify;
            _autoType.Checked = d.AutoType;
            _split.Checked = d.AutoSplitScan;
            _doubleCheck.Checked = d.AutoDoubleCheck;
            _thinkTie.Checked = d.AutoThinkTie;
            _webSearch.Checked = d.AutoWebSearch;
            _noCompress.Checked = d.NoCompress;
            _maxEdge.Value = d.MaxEdge;
            _quality.Value = d.JpegQuality;
            _maxShots.Value = Math.Max(_maxShots.Minimum, Math.Min(_maxShots.Maximum, d.MaxShots));
            _autoAnalyze.Checked = d.AutoAnalyze;
            _autoCrop.Checked = d.AutoCrop;
            _hkPreview.Text = "Ctrl+Alt+Q"; _hkPreview.Mods = 3; _hkPreview.Vk = 81;
            _hkClick.Text = "Ctrl+Alt+X"; _hkClick.Mods = 3; _hkClick.Vk = 88;
        }

        private static string MaskKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return "";
            if (k.Length <= 8) return "****";
            return k.Substring(0, 3) + "****" + k.Substring(k.Length - 4);
        }

        private void TestConnection()
        {
            AppConfig probe = new AppConfig();
            probe.ApiBase = _baseUrl.Text.Trim();
            probe.ApiModel = _model.Text.Trim();
            probe.ApiKey = (_key.Text == _keyMasked) ? _cfg.ApiKey : _key.Text.Trim();
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
            // 显示掩码时表示没改过；真清空（点了「清除」）就存空
            if (_key.Text != _keyMasked) _cfg.ApiKey = _key.Text.Trim();
            if (_prompt.Text.Trim().Length > 0) _cfg.Prompt = _prompt.Text;

            _cfg.AutoIntervalMs = (int)_interval.Value;
            _cfg.AutoIdleMs = (int)_idle.Value;
            _cfg.AutoMaxClicks = (int)_maxClicks.Value;
            _cfg.AutoMaxEdge = (int)_autoEdge.Value;
            _cfg.AutoQuality = (int)_autoQuality.Value;
            _cfg.AutoBackend = _backend.SelectedIndex == 1 ? "postmessage" : "sendinput";
            _cfg.CaptureBackend = _capture.SelectedIndex == 1 ? "screen" : (_capture.SelectedIndex == 2 ? "printwindow" : "auto");
            _cfg.AutoRestoreCursor = _restoreCursor.Checked;
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
}
