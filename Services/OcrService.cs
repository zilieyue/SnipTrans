using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace SnipTrans.Services;

public sealed record OcrOutcome(string Text, string EngineTag, int CjkCount, long ElapsedMs)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// 基于 Windows 系统自带 OCR（Windows.Media.Ocr）的识别服务。
/// 英文引擎与简体中文引擎各跑一遍再择优：中文引擎会逐字插空格且英文易错，
/// 英文引擎则完全识别不了汉字，所以必须按内容类型挑选。
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine _enEngine;
    private readonly OcrEngine _zhEngine;

    // 中日韩字符 + 全角标点
    private static readonly Regex CjkGap = new(
        @"(?<=[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uFF00-\uFFEF])[ \t]+(?=[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uFF00-\uFFEF])",
        RegexOptions.Compiled);

    private static readonly Regex CjkChar = new(
        @"[\u3400-\u4DBF\u4E00-\u9FFF]", RegexOptions.Compiled);

    public OcrService()
    {
        _enEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        _zhEngine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"));
    }

    /// <summary>当前机器可用的 OCR 引擎说明，用于启动自检。</summary>
    public string Availability =>
        $"英文={( _enEngine is null ? "不可用" : "可用")}, 简体中文={(_zhEngine is null ? "不可用" : "可用")}";

    public bool IsAvailable => _enEngine is not null || _zhEngine is not null;

    public async Task<OcrOutcome> RecognizeAsync(BitmapSource image)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 超大图先缩到 2600px 内，兼顾速度与识别率
        var prepared = DownscaleIfHuge(image, 2600);
        var software = ToSoftwareBitmap(prepared);

        OcrResult enResult = null;
        OcrResult zhResult = null;

        if (_enEngine is not null)
        {
            try { enResult = await _enEngine.RecognizeAsync(software); }
            catch { /* 单引擎失败不影响整体 */ }
        }

        if (_zhEngine is not null)
        {
            try { zhResult = await _zhEngine.RecognizeAsync(software); }
            catch { /* ignore */ }
        }

        software.Dispose();
        sw.Stop();

        var enText = enResult is null ? "" : JoinLines(enResult);
        var zhText = zhResult is null ? "" : JoinLines(zhResult);

        var zhCjk = CountCjk(zhText);
        var enCjk = CountCjk(enText);

        // 择优：中文引擎能识出成规模汉字时用它（并修复字间空格）；
        // 否则用英文引擎，避开中文引擎对英文的大小写／断词退化。
        bool preferZh = _zhEngine is not null
                        && zhCjk >= 2
                        && (zhText.Length == 0 || zhCjk >= zhText.Length * 0.12);

        if (preferZh)
        {
            var text = (zhResult is not null && enResult is not null)
                ? FuseLines(zhResult, enResult)
                : CleanChinese(zhText);
            return new OcrOutcome(text, "zh-Hans", zhCjk, sw.ElapsedMilliseconds);
        }

        if (!string.IsNullOrWhiteSpace(enText))
        {
            return new OcrOutcome(enText, "en", enCjk, sw.ElapsedMilliseconds);
        }

        // 英文引擎没结果时兜底用中文引擎
        if (!string.IsNullOrWhiteSpace(zhText))
        {
            return new OcrOutcome(CleanChinese(zhText), "zh-Hans", zhCjk, sw.ElapsedMilliseconds);
        }

        return new OcrOutcome("", "-", 0, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// 双引擎融合：
    /// 1) 整行都是英文 → 直接采用英文引擎的对应行（连词最准）；
    /// 2) 中英混排行 → 只把「纯 ASCII 词」按位置替换成英文引擎的结果，
    ///    汉字部分保持中文引擎不动（中文引擎认英文易错，英文引擎认不出汉字）。
    /// </summary>
    private static string FuseLines(OcrResult zh, OcrResult en)
    {
        var enLines = en.Lines
            .Select(l => (Y: LineCenterY(l), Text: l.Text.Trim()))
            .Where(x => x.Text.Length > 0)
            .ToList();

        // 英文引擎的所有词，用于混合行的词级替换
        var enWords = en.Lines
            .SelectMany(l => l.Words)
            .Select(w => (Rect: w.BoundingRect, Text: w.Text.Trim()))
            .Where(x => x.Text.Length > 0)
            .ToList();

        var usedEnWords = new HashSet<int>();
        var sb = new StringBuilder();
        int lineReplaced = 0, wordReplaced = 0;

        foreach (var zl in zh.Lines)
        {
            var lineText = zl.Text.Trim();
            if (lineText.Length == 0) continue;
            sb.AppendLine(FuseOneLine(zl, lineText, enLines, enWords, usedEnWords,
                ref lineReplaced, ref wordReplaced));
        }

        if (lineReplaced > 0 || wordReplaced > 0)
            Log.Write($"[ocr] 融合：整行替换 {lineReplaced} 行，词级替换 {wordReplaced} 处");

        return sb.ToString().TrimEnd();
    }

    private static string FuseOneLine(
        OcrLine zhLine,
        string lineText,
        List<(double Y, string Text)> enLines,
        List<(Windows.Foundation.Rect Rect, string Text)> enWords,
        HashSet<int> usedEnWords,
        ref int lineReplaced,
        ref int wordReplaced)
    {
        int cjk = CountCjk(lineText);

        // --- 1) 纯英文行：整行采用英文引擎 ---
        if (cjk == 0 && enLines.Count > 0)
        {
            double zy = LineCenterY(zhLine);
            double tolerance = Math.Max(10, LineHeight(zhLine) * 0.75);
            var best = enLines.OrderBy(e => Math.Abs(e.Y - zy)).First();

            if (Math.Abs(best.Y - zy) <= tolerance && CountCjk(best.Text) == 0)
            {
                lineReplaced++;
                return best.Text;
            }
        }

        // --- 2) 混合行：只替换纯 ASCII 词 ---
        if (cjk > 0 && enWords.Count > 0)
        {
            var replacements = new List<(int Start, int Length, string Text)>();
            int from = 0;

            foreach (var zw in zhLine.Words)
            {
                var wt = zw.Text.Trim();
                if (wt.Length == 0) continue;
                if (CountCjk(wt) > 0) continue;           // 含汉字，保留
                if (!wt.Any(char.IsLetter)) continue;      // 纯符号/数字，保留

                int best = FindBestEnWord(zw.BoundingRect, enWords, usedEnWords, out var enText);
                if (best < 0 || string.Equals(enText, wt, StringComparison.Ordinal)) continue;

                int idx = lineText.IndexOf(wt, from, StringComparison.Ordinal);
                if (idx < 0) continue;

                replacements.Add((idx, wt.Length, enText));
                usedEnWords.Add(best);
                from = idx + wt.Length;
            }

            // 从后往前替换，避免位移影响前面的下标
            foreach (var r in replacements.OrderByDescending(x => x.Start))
            {
                lineText = lineText.Remove(r.Start, r.Length).Insert(r.Start, r.Text);
                wordReplaced++;
            }
        }

        return CleanChinese(lineText);
    }

    /// <summary>在英文引擎的词里找与指定区域重叠度最高的那个（每个词最多用一次）。</summary>
    private static int FindBestEnWord(
        Windows.Foundation.Rect target,
        List<(Windows.Foundation.Rect Rect, string Text)> enWords,
        HashSet<int> used,
        out string text)
    {
        text = null;
        double targetArea = Math.Max(1, target.Width * target.Height);

        int bestIndex = -1;
        double bestRatio = 0;

        for (int i = 0; i < enWords.Count; i++)
        {
            if (used.Contains(i)) continue;

            double ratio = OverlapArea(target, enWords[i].Rect) / targetArea;
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0 && bestRatio >= 0.4)
        {
            text = enWords[bestIndex].Text;
            return bestIndex;
        }
        return -1;
    }

    private static double OverlapArea(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
    {
        double x1 = Math.Max(a.X, b.X);
        double y1 = Math.Max(a.Y, b.Y);
        double x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        double y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x2 <= x1 || y2 <= y1) return 0;
        return (x2 - x1) * (y2 - y1);
    }

    private static double LineCenterY(OcrLine line)
    {
        if (line.Words.Count == 0) return 0;
        var r = line.Words[0].BoundingRect;
        return r.Y + r.Height / 2;
    }

    private static double LineHeight(OcrLine line) =>
        line.Words.Count == 0 ? 20 : line.Words[0].BoundingRect.Height;

    private static string JoinLines(OcrResult r) =>
        string.Join("\n", r.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));

    private static int CountCjk(string s) => string.IsNullOrEmpty(s) ? 0 : CjkChar.Matches(s).Count;

    /// <summary>去掉中文引擎在汉字之间插入的空格，保留中英之间的正常间隔。</summary>
    private static string CleanChinese(string s) =>
        string.IsNullOrEmpty(s) ? s : CjkGap.Replace(s, "");

    private static BitmapSource DownscaleIfHuge(BitmapSource src, int maxSide)
    {
        if (src.PixelWidth <= maxSide && src.PixelHeight <= maxSide) return src;

        double scale = Math.Min((double)maxSide / src.PixelWidth, (double)maxSide / src.PixelHeight);
        var t = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    /// <summary>BitmapSource 统一转 BGRA8 → IBuffer → SoftwareBitmap（系统 OCR 的输入类型）。</summary>
    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgra32)
        {
            var conv = new FormatConvertedBitmap();
            conv.BeginInit();
            conv.Source = src;
            conv.DestinationFormat = PixelFormats.Bgra32;
            conv.EndInit();
            conv.Freeze();
            src = conv;
        }

        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[(long)stride * h];
        src.CopyPixels(pixels, stride, 0);

        var buffer = CryptographicBuffer.CreateFromByteArray(pixels);
        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }
}
