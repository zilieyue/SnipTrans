namespace TestHarness;

internal static class Program
{
    /// <summary>
    /// 用法：
    ///   TestHarness clipboard [等待秒数] [截图输出路径]  —— 把一张英文测试图放进剪贴板，等待后截屏
    ///   TestHarness shot [输出路径]                      —— 只截屏
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "shot";

        // set 模式：只把测试图放进剪贴板就退出，由外部脚本控制后续时机
        if (mode == "set")
        {
            var kind = args.Length > 1 ? args[1] : "en";
            PutTestImageOnClipboard(kind);
            Console.WriteLine($"clipboard: {kind} image set");
            return;
        }

        if (mode is "clipboard" or "clipboard-zh" or "clipboard-mix")
        {
            int waitSeconds = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 7;
            var shotPath = args.Length > 2 ? args[2] : "test-shot.png";
            var kind = mode switch
            {
                "clipboard-zh" => "zh",
                "clipboard-mix" => "mix",
                _ => "en"
            };

            PutTestImageOnClipboard(kind);
            Console.WriteLine($"clipboard: {kind} image set, waiting {waitSeconds}s ...");

            // 必须跑消息循环：Thread.Sleep 会让本进程无法响应剪贴板的
            // OLE 延迟渲染请求，导致读取方卡住（这正是真实场景要避免的）
            var until = DateTime.UtcNow.AddSeconds(waitSeconds);
            while (DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }

            SaveScreen(shotPath);
            Console.WriteLine($"saved: {Path.GetFullPath(shotPath)}");
            return;
        }

        var path = args.Length > 1 ? args[1] : "shot.png";
        SaveScreen(path);
        Console.WriteLine($"saved: {Path.GetFullPath(path)}");
    }

    private static void PutTestImageOnClipboard(string mode)
    {
        bool zh = mode == "zh";
        bool mix = mode == "mix";

        using var bmp = new Bitmap(mix ? 820 : 780, mix ? 200 : 190);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            if (mix)
            {
                using var f1 = new Font("Microsoft YaHei", 17);
                using var f2 = new Font("Segoe UI", 17);
                g.DrawString("这个功能的价值在于：省掉复制粘贴。", f1, Brushes.Black, 14, 18);
                g.DrawString("Win + Shift + S 之后译文立刻出现。", f1, Brushes.Black, 14, 58);
                g.DrawString("PaddleOCR / RapidOCR 都可以替换系统 OCR。", f2, Brushes.Black, 14, 100);
                g.DrawString("延迟控制在 1.5 秒以内比较好。", f1, Brushes.Black, 14, 142);
            }
            else if (zh)
            {
                using var f = new Font("Microsoft YaHei", 19);
                g.DrawString("截图之后自动识别文字并翻译。", f, Brushes.Black, 14, 20);
                g.DrawString("中文断句要保留，不能逐字断开。", f, Brushes.Black, 14, 66);
                g.DrawString("标点符号：逗号、句号、括号都要正确。", f, Brushes.Black, 14, 112);
            }
            else
            {
                using var f = new Font("Segoe UI", 19);
                g.DrawString("The quick brown fox jumps over the lazy dog.", f, Brushes.Black, 14, 20);
                g.DrawString("Screenshot translation should appear below.", f, Brushes.Black, 14, 66);
                // 带上时间戳，保证每次内容不同，不被去重逻辑跳过
                g.DrawString($"Please check whether OCR works correctly. [{DateTime.Now:HH:mm:ss.fff}]",
                    f, Brushes.Black, 14, 112);
            }
        }

        // copy:true 让数据在进程退出后仍留在剪贴板
        Clipboard.SetDataObject(bmp, true);
    }

    private static void SaveScreen(string path)
    {
        var screen = SystemInformation.VirtualScreen;
        using var shot = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(shot))
        {
            g.CopyFromScreen(screen.Left, screen.Top, 0, 0, shot.Size);
        }
        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"screen: {screen.Width}x{screen.Height}");
    }
}
