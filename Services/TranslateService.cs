using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SnipTrans.Services;

public sealed record TranslateResult(
    string Text,
    string SourceLang,
    string TargetLang,
    string EngineTag,
    long ElapsedMs)
{
    public string Direction => $"{LangName(SourceLang)} → {LangName(TargetLang)}";

    public static string LangName(string tag) => tag switch
    {
        "zh-CN" or "zh-Hans" => "中文",
        "en" => "英文",
        "ja" => "日文",
        "ko" => "韩文",
        "auto" => "自动",
        _ => tag
    };
}

/// <summary>
/// 多引擎翻译。MyMemory 为默认免费引擎：无需 Key，但单次查询有字节上限，
/// 且超限时会「静默返回原文而不报错」，因此必须严格分段并做结果校验。
/// </summary>
public sealed class TranslateService
{
    private static readonly HttpClient Http = CreateClient();

    // MyMemory 单段安全上限（UTF-8 字节）。官方约 500，这里留足余量避免静默失败。
    private const int MyMemoryMaxBytes = 380;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        return c;
    }

    public async Task<TranslateResult> TranslateAsync(string text, AppSettings settings)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (src, tgt) = ResolveDirection(text, settings);

        string output = settings.Engine switch
        {
            TranslateEngine.OpenAi => await TranslateOpenAiAsync(text, src, tgt, settings),
            _ => await TranslateMyMemoryAsync(text, src, tgt)
        };

        sw.Stop();
        return new TranslateResult(output, src, tgt, EngineName(settings.Engine), sw.ElapsedMilliseconds);
    }

    public static string EngineName(TranslateEngine e) => e switch
    {
        TranslateEngine.OpenAi => "AI",
        _ => "MyMemory"
    };

    /// <summary>根据内容判断语种与方向。默认译成中文，原文是中文时若开启自动反向则译成英文。</summary>
    private static (string src, string tgt) ResolveDirection(string text, AppSettings s)
    {
        int cjk = 0, letters = 0;
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff') cjk++;
            else if (char.IsLetter(ch) && ch < 128) letters++;
        }

        bool isChinese = cjk >= 2 && cjk * 3 >= letters;

        if (isChinese && s.AutoSwitchDirection)
            return ("zh-CN", "en");

        if (isChinese)
            return ("zh-CN", s.TargetLanguage.StartsWith("zh") ? "en" : s.TargetLanguage);

        return ("en", s.TargetLanguage.StartsWith("zh") ? "zh-CN" : s.TargetLanguage);
    }

    // ---------------- MyMemory ----------------

    private static async Task<string> TranslateMyMemoryAsync(string text, string src, string tgt)
    {
        var chunks = SplitByBytes(text, MyMemoryMaxBytes);
        if (chunks.Count == 0) return "";

        var results = new string[chunks.Count];
        using var gate = new SemaphoreSlim(3);

        var tasks = chunks.Select(async (chunk, index) =>
        {
            await gate.WaitAsync();
            try
            {
                results[index] = await MyMemoryOneAsync(chunk, src, tgt);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return string.Join("\n", results.Where(r => !string.IsNullOrWhiteSpace(r))).Trim();
    }

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static async Task<string> MyMemoryOneAsync(string chunk, string src, string tgt)
    {
        var url = "https://api.mymemory.translated.net/get" +
                  $"?q={Uri.EscapeDataString(chunk)}" +
                  $"&langpair={Uri.EscapeDataString(src)}%7C{Uri.EscapeDataString(tgt)}" +
                  "&de=sniptrans@outlook.com";

        using var resp = await Http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new TranslateException($"MyMemory 返回 HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(body, LenientJson);
        var root = doc.RootElement;

        int status = root.TryGetProperty("responseStatus", out var st) && st.TryGetInt32(out var sv) ? sv : 200;
        string translated = root.TryGetProperty("responseData", out var rd)
                            && rd.TryGetProperty("translatedText", out var tt)
            ? tt.GetString() ?? ""
            : "";

        if (status != 200)
            throw new TranslateException($"MyMemory 错误 {status}: {Shorten(translated, 80)}");

        if (translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            throw new TranslateException("MyMemory 今日免费额度已用完，请在设置里换用 AI 引擎。");

        // 超限时 MyMemory 会原样返回输入，这里做兜底提示
        if (string.IsNullOrWhiteSpace(translated) ||
            (translated.Trim() == chunk.Trim() && src != tgt && chunk.Length > 3))
        {
            throw new TranslateException("MyMemory 未能翻译（可能超出单次长度或额度限制）。");
        }

        return translated.Trim();
    }

    // ---------------- OpenAI 兼容（DeepSeek / 通义 / GPT 等）----------------

    private const string SystemPrompt =
        "你是专业的截图文字翻译引擎。规则：\n" +
        "1. 只输出译文本身，不要解释、不要加引号、不要复述原文；\n" +
        "2. 保留原文的换行与段落结构；\n" +
        "3. 代码、变量名、URL、专有名词保持原样不翻译；\n" +
        "4. 如果原文已经是目标语言，原样返回。";

    private static async Task<string> TranslateOpenAiAsync(string text, string src, string tgt, AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.OpenAiApiKey))
            throw new TranslateException("尚未填写 AI 引擎的 API Key，请到设置里配置，或切回 MyMemory。");

        var baseUrl = s.OpenAiBaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions") ? baseUrl : baseUrl + "/chat/completions";

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(s.OpenAiModel) ? "deepseek-chat" : s.OpenAiModel,
            temperature = 0.2,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = $"把下面的文本从 {TranslateResult.LangName(src)} 翻译成 {TranslateResult.LangName(tgt)}：\n\n{text}"
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.OpenAiApiKey.Trim());
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new TranslateException($"AI 引擎 HTTP {(int)resp.StatusCode}: {Shorten(body, 120)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new TranslateException("AI 引擎返回空内容。");

        return content.Trim();
    }

    // ---------------- 工具 ----------------

    /// <summary>按 UTF-8 字节上限分段，优先在换行处切，保留段落结构。</summary>
    internal static List<string> SplitByBytes(string text, int maxBytes)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var sb = new StringBuilder();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // 单行本身就超限 → 硬切
            if (Encoding.UTF8.GetByteCount(line) > maxBytes)
            {
                if (sb.Length > 0) { chunks.Add(sb.ToString().Trim()); sb.Clear(); }
                foreach (var piece in HardSplit(line, maxBytes))
                    chunks.Add(piece);
                continue;
            }

            // 加上这行会超限 → 先把当前块收走
            if (sb.Length > 0 && Encoding.UTF8.GetByteCount(sb + "\n" + line) > maxBytes)
            {
                chunks.Add(sb.ToString().Trim());
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        if (sb.Length > 0) chunks.Add(sb.ToString().Trim());
        return chunks.Where(c => c.Length > 0).ToList();
    }

    private static IEnumerable<string> HardSplit(string line, int maxBytes)
    {
        var sb = new StringBuilder();
        int bytes = 0;

        foreach (var ch in line)
        {
            int cb = Utf8Len(ch);
            if (bytes + cb > maxBytes && sb.Length > 0)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
                bytes = 0;
            }
            sb.Append(ch);
            bytes += cb;
        }

        if (sb.Length > 0) yield return sb.ToString().Trim();
    }

    /// <summary>单个 UTF-16 char 的 UTF-8 字节数（代理对偏保守地按 3+3 计）。</summary>
    private static int Utf8Len(char c) => c < 0x80 ? 1 : c < 0x800 ? 2 : 3;

    private static string Shorten(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}

public sealed class TranslateException : Exception
{
    public TranslateException(string message) : base(message) { }
}
