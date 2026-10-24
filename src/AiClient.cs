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
            return Search(cfg, query, count, out error, CancellationToken.None);
        }

        public static string Search(AppConfig cfg, string query, int count, out string error, CancellationToken token)
        {
            error = null;
            if (token.IsCancellationRequested) { error = "已取消"; return null; }
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

                string text;
                using (token.Register(req.Abort))
                {
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = sr.ReadToEnd();
                }

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
                error = token.IsCancellationRequested ? "已取消" : wex.Message;
                Log.Write("search FAIL: " + error);
                return null;
            }
            catch (Exception ex)
            {
                error = token.IsCancellationRequested ? "已取消" : ex.Message;
                return null;
            }
        }

        public static string Ask(AppConfig cfg, List<object> messages, out string error, out UsageInfo usage)
        {
            return Ask(cfg, messages, out error, out usage, false, CancellationToken.None);
        }

        public static string Ask(AppConfig cfg, List<object> messages, out string error, out UsageInfo usage, bool noThink)
        {
            return Ask(cfg, messages, out error, out usage, noThink, CancellationToken.None);
        }

        // noThink=true 时不走思考模式：自动作答循环里用，省时省钱
        public static string Ask(AppConfig cfg, List<object> messages, out string error, out UsageInfo usage, bool noThink, CancellationToken token)
        {
            error = null;
            usage = null;
            if (token.IsCancellationRequested) { error = "已取消"; return null; }
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

                string text;
                using (token.Register(req.Abort))
                {
                    using (Stream s = req.GetRequestStream())
                    {
                        s.Write(body, 0, body.Length);
                    }
                    tSent = sw.ElapsedMilliseconds;

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        tTtfb = sw.ElapsedMilliseconds;
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            text = sr.ReadToEnd();
                        }
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
                if (token.IsCancellationRequested)
                {
                    error = "已取消";
                    return null;
                }
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
                error = token.IsCancellationRequested ? "已取消" : ex.Message;
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
}
