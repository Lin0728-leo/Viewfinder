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


    internal sealed class AutoStatusForm : Form
    {
        private readonly Label _stage;
        private readonly Label _info;
        private readonly PictureBox _thumb;
        private readonly TextBox _log;
        private readonly List<string> _lines = new List<string>();
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
}
