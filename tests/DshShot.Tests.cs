using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using DshShot;

// 轻量测试宿主：不依赖测试框架/NuGet，直接用系统 csc 编译运行。
// 覆盖纯逻辑：价目表、配置读写、JSON 解析、热键、留白裁剪、空图判定。
internal static class TestRunner
{
    private static int _passed;
    private static int _failed;

    private static void Check(bool ok, string name)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL " + name);
        }
    }

    private static void CheckEqual(string expect, string actual, string name)
    {
        Check(expect == actual, name + " (expect=" + expect + ", actual=" + actual + ")");
    }

    private static void CheckNear(double expect, double actual, double eps, string name)
    {
        Check(Math.Abs(expect - actual) <= eps,
            name + " (expect=" + expect.ToString("0.########") + ", actual=" + actual.ToString("0.########") + ")");
    }

    private static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "DshShot.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            TestPricing(tmp);
            TestConfig(tmp);
            TestAutoJson();
            TestHotkeys();
            TestCrop();
            TestBlankDetect();
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("  FAIL exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void TestPricing(string tmp)
    {
        Console.WriteLine("Pricing");
        Pricing.Load(tmp); // no pricing.ini -> built-in defaults
        Check(Pricing.HasRate("deepseek-flash"), "default rate present");
        Check(!Pricing.HasRate("no-such-model"), "unknown model");

        UsageInfo u = new UsageInfo();
        u.Prompt = 100000;
        u.Completion = 50000;
        u.CacheHit = 20000;
        u.CacheMiss = 80000;

        // (20k*0.04 + 80k*2.00 + 50k*8.00) / 1e6 = 0.5608
        double peak = Pricing.Cost("deepseek-flash", u, new DateTime(2026, 10, 26, 10, 0, 0)); // Monday
        double off = Pricing.Cost("deepseek-flash", u, new DateTime(2026, 10, 24, 10, 0, 0));  // Saturday
        CheckNear(0.5608, peak, 1e-9, "peak price");
        CheckNear(0.2804, off, 1e-9, "off-peak half price");

        File.WriteAllText(Path.Combine(tmp, "pricing.ini"),
            "# model = cache-hit, cache-miss, output (CNY / 1M tokens)\r\ndeepseek-flash = 1, 2, 3\r\ninvalid line\r\n");
        Pricing.Load(tmp);
        // (20k*1 + 80k*2 + 50k*3) / 1e6 = 0.33
        CheckNear(0.33, Pricing.Cost("deepseek-flash", u, new DateTime(2026, 10, 26, 10, 0, 0)), 1e-9,
            "pricing.ini override");

        CheckEqual("¥0.0050", Pricing.Money(0.005), "money formatting");
        CheckEqual("12.3k", Pricing.Tok(12345), "token formatting");
    }

    private static void TestConfig(string tmp)
    {
        Console.WriteLine("AppConfig");
        AppConfig d = new AppConfig();
        CheckEqual("postmessage", d.AutoBackend, "default backend is background");
        CheckEqual("auto", d.CaptureBackend, "default capture backend");
        Check(d.AutoRestoreCursor, "default restore cursor");
        Check(d.NoCompress && d.AutoCrop, "defaults for image pipeline");

        AppConfig c = new AppConfig();
        c.ApiBase = "https://api.example.com/v1";
        c.ApiModel = "deepseek-flash";
        c.ApiKey = "sk-test-123456";
        c.Prompt = "line1\nline2";
        c.CaptureBackend = "printwindow";
        c.AutoBackend = "sendinput";
        c.AutoRestoreCursor = false;
        c.MaxShots = 42;
        c.AutoIntervalMs = 4500;

        string path = Path.Combine(tmp, "config.ini");
        c.SaveTo(path);
        AppConfig back = AppConfig.LoadFrom(path);
        CheckEqual(c.ApiKey, back.ApiKey, "api key round-trip (DPAPI)");
        CheckEqual(c.Prompt, back.Prompt, "multiline prompt round-trip");
        CheckEqual("printwindow", back.CaptureBackend, "capture backend round-trip");
        CheckEqual("sendinput", back.AutoBackend, "click backend round-trip");
        Check(!back.AutoRestoreCursor, "restore cursor round-trip");
        CheckEqual("42", back.MaxShots.ToString(), "max shots round-trip");
        CheckEqual("4500", back.AutoIntervalMs.ToString(), "interval round-trip");

        AppConfig missing = AppConfig.LoadFrom(Path.Combine(tmp, "does-not-exist.ini"));
        CheckEqual("auto", missing.CaptureBackend, "missing file -> defaults");
    }

    private static void TestAutoJson()
    {
        Console.WriteLine("AutoJson");
        CheckEqual("{\"a\":1}", AutoJson.Clean("```json\n{\"a\":1}\n```"), "strip markdown fence");
        CheckEqual("a北京", AutoJson.Norm("  A 北京，！"), "normalize text");

        string raw = "```json\r\n{\"questions\":[" +
            "{\"no\":\"1\",\"kind\":\"single\",\"picks\":[{\"label\":\"B\",\"text\":\"北京\",\"box\":[0.30,0.40,0.40,0.05]}],\"note\":\"\"}," +
            "{\"no\":\"2\",\"kind\":\"blank\",\"blank\":\"42\",\"picks\":[{\"label\":\"\",\"text\":\"\",\"box\":[0.1,0.2,0.3,0.04]}]}" +
            "]}\r\n```";
        string err;
        var qs = AutoJson.Parse(raw, out err);
        Check(err == null, "parse fenced json");
        CheckEqual("2", qs.Count.ToString(), "question count");
        if (qs.Count == 2)
        {
            CheckEqual("1", qs[0].No, "question no");
            CheckEqual("single", qs[0].Kind, "question kind");
            CheckEqual("B", qs[0].Picks[0].Label, "pick label");
            CheckNear(0.30, qs[0].Picks[0].X, 1e-9, "pick x");
            CheckNear(0.425, qs[0].Picks[0].CY, 1e-9, "pick center y");
            CheckEqual("blank", qs[1].Kind, "blank kind");
            CheckEqual("42", qs[1].Blank, "blank answer");
        }

        var empty = AutoJson.Parse("{\"questions\":[]}", out err);
        Check(err == null && empty.Count == 0, "empty questions");

        var bad = AutoJson.Parse("not json", out err);
        Check(err != null && bad.Count == 0, "invalid json reports error");

        var outOfRange = AutoJson.Parse(
            "{\"questions\":[{\"no\":\"9\",\"picks\":[{\"label\":\"A\",\"text\":\"x\",\"box\":[1.4,0.2,0.2,0.05]}]}]}", out err);
        Check(outOfRange.Count == 1 && outOfRange[0].Picks.Count == 0, "out-of-range box dropped");

        AutoQuestion q1 = new AutoQuestion();
        q1.No = "1"; q1.Kind = "single";
        q1.Picks.Add(new AutoPick { Label = "A", Text = "x" });
        AutoQuestion q2 = new AutoQuestion();
        q2.No = "1"; q2.Kind = "single";
        q2.Picks.Add(new AutoPick { Label = "B", Text = "y" });
        Check(q1.Key() != q2.Key(), "question key differs by answer");
    }

    private static void TestHotkeys()
    {
        Console.WriteLine("HotkeyText");
        uint mods, vk;
        HotkeyText.Parse("3,81", out mods, out vk);
        Check(mods == 3 && vk == 81, "parse ctrl+alt+q");
        CheckEqual("Ctrl+Alt+Q", HotkeyText.Describe(mods, vk), "describe ctrl+alt+q");
        CheckEqual("3,81", HotkeyText.Format(mods, vk), "format round-trip");
        HotkeyText.Parse("bad", out mods, out vk);
        Check(mods == 0 && vk == 0, "invalid hotkey -> empty");
    }

    private static void TestCrop()
    {
        Console.WriteLine("AutoCrop");
        using (Bitmap bmp = new Bitmap(400, 300))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using (Pen p = new Pen(Color.FromArgb(40, 40, 40), 2f))
                {
                    for (int y = 60; y <= 240; y += 12)
                        g.DrawLine(p, 80, y, 320, y);
                }
            }
            string note;
            Rectangle r = AutoCrop.Detect(bmp, out note);
            Check(r.Width < 400 && r.Height < 300, "trims blank margins (" + note + ")");
            Check(r.Contains(200, 150), "keeps center content");
            Check(r.Width > 120 && r.Height > 100, "does not over-crop");
        }
    }

    private static void TestBlankDetect()
    {
        Console.WriteLine("Capture.LooksBlank");
        using (Bitmap white = new Bitmap(64, 64))
        using (Graphics g = Graphics.FromImage(white))
        {
            g.Clear(Color.White);
            Check(Capture.LooksBlank(white), "solid white is blank");

            g.DrawLine(Pens.Black, 0, 32, 63, 32);
            Check(!Capture.LooksBlank(white), "black line is not blank");
        }
    }
}
