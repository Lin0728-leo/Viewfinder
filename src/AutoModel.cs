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

    }
}
