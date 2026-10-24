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
    internal static class Program
    {
        public const string AppName = "DSH 取景器";
        public const string AppId = "DshShot";

        // 由启动器设置：程序所在目录（DLL 被 PowerShell 宿主加载时用）
        public static string BaseDir = "";

        // 第二个实例用它叫醒第一个实例弹窗
        public const string ShowEventName = "Global\\DshShot.ShowWindow";

        // 开机自启时由 launch.ps1 置为 true，不弹窗口，只留在托盘
        public static bool Silent = false;

        // 程序所在目录：DLL 由 PowerShell 宿主加载时 BaseDir 已被赋值
        public static string AppDir
        {
            get
            {
                try
                {
                    if (!string.IsNullOrEmpty(BaseDir)) return BaseDir;
                    return Path.GetDirectoryName(Application.ExecutablePath);
                }
                catch { return ""; }
            }
        }

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
            if (mode == "window") bmp = Capture.Window(Capture.ForegroundWindow(), "auto");
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
}
