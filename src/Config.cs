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
    // 默认值内置，可用程序目录下的 pricing.ini 覆盖（换代理/换价目表时不用重新编译）。
    internal static class Pricing
    {
        private sealed class Rate
        {
            public readonly double Hit;
            public readonly double Miss;
            public readonly double Out;
            public Rate(double hit, double miss, double output) { Hit = hit; Miss = miss; Out = output; }
        }

        private static readonly Dictionary<string, Rate> Defaults = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            { "deepseek-flash", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-flash", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-flash-vision-exp", new Rate(0.04, 2.00, 8.00) },
            { "deepseek-v4-pro", new Rate(0.30, 9.00, 27.00) }
        };

        private static Dictionary<string, Rate> _table = new Dictionary<string, Rate>(Defaults, StringComparer.OrdinalIgnoreCase);

        public static int Count { get { return _table.Count; } }

        public static bool HasRate(string model)
        {
            return model != null && _table.ContainsKey(model.Trim());
        }

        // 读程序目录下的 pricing.ini；格式：模型名 = 缓存命中价, 未命中价, 输出价（元/百万 tokens）
        // 读不到或某行不合法时保留默认值，绝不因为价目表损坏而让程序起不来。
        public static void Load(string dir)
        {
            _table = new Dictionary<string, Rate>(Defaults, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(dir)) return;
                string path = Path.Combine(dir, "pricing.ini");
                if (!File.Exists(path)) return;

                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string model = line.Substring(0, eq).Trim();
                    string[] parts = line.Substring(eq + 1).Split(',');
                    if (model.Length == 0 || parts.Length < 3) continue;

                    double hit, miss, output;
                    if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hit)) continue;
                    if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out miss)) continue;
                    if (!double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out output)) continue;
                    if (hit < 0 || miss < 0 || output < 0) continue;

                    _table[model] = new Rate(hit, miss, output);
                    Log.Write("pricing: " + model + " = " + hit + "," + miss + "," + output);
                }
            }
            catch (Exception ex)
            {
                Log.Write("pricing load failed: " + ex.Message);
            }
        }

        // 北京时间周一至周五 9:00-12:00、14:00-18:00 为高峰，其余时段减半（法定节假日未单独处理）
        public static bool IsPeak(DateTime t)
        {
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday) return false;
            double h = t.Hour + t.Minute / 60.0;
            return (h >= 9.0 && h < 12.0) || (h >= 14.0 && h < 18.0);
        }

        public static double Cost(string model, UsageInfo u)
        {
            return Cost(model, u, DateTime.Now);
        }

        public static double Cost(string model, UsageInfo u, DateTime at)
        {
            Rate r;
            if (model == null || !_table.TryGetValue(model.Trim(), out r)) r = _table["deepseek-flash"];
            long hit = u.CacheHit;
            long miss = u.CacheMiss > 0 ? u.CacheMiss : Math.Max(0, u.Prompt - u.CacheHit);
            double peak = IsPeak(at) ? 1.0 : 0.5;
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

        // 抓屏：auto = 前台用屏幕复制、被遮挡时用 PrintWindow；screen / printwindow 为强制模式
        public string CaptureBackend;

        // 自动作答
        public int AutoIntervalMs;    // 抓屏间隔
        public string AutoBackend;    // sendinput / postmessage
        public bool AutoRestoreCursor; // 前台点击后把光标放回原处
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
            CaptureBackend = "auto";
            AutoIntervalMs = 3000;
            AutoBackend = "postmessage";   // 默认不移动真实光标
            AutoRestoreCursor = true;
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
            return LoadFrom(ConfigPath());
        }

        internal static AppConfig LoadFrom(string path)
        {
            AppConfig c = new AppConfig();
            try
            {
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
                    else if (key == "CaptureBackend" && (val == "auto" || val == "screen" || val == "printwindow")) c.CaptureBackend = val;
                    else if (key == "AutoIntervalMs") { int n; if (int.TryParse(val, out n) && n >= 800 && n <= 60000) c.AutoIntervalMs = n; }
                    else if (key == "AutoBackend") c.AutoBackend = val;
                    else if (key == "AutoRestoreCursor") c.AutoRestoreCursor = (val == "1" || val.ToLowerInvariant() == "true");
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
            SaveTo(ConfigPath());
        }

        internal void SaveTo(string path)
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
                sb.AppendLine("CaptureBackend=" + CaptureBackend);
                sb.AppendLine("AutoIntervalMs=" + AutoIntervalMs);
                sb.AppendLine("AutoBackend=" + AutoBackend);
                sb.AppendLine("AutoRestoreCursor=" + (AutoRestoreCursor ? "1" : "0"));
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
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
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
}
