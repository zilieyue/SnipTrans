using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipTrans.Services;

/// <summary>翻译引擎类型。</summary>
public enum TranslateEngine
{
    /// <summary>免费免配置，国内可直连。</summary>
    MyMemory = 0,

    /// <summary>OpenAI 兼容接口（DeepSeek / 通义 / GLM / GPT 等），需自备 Key。</summary>
    OpenAi = 1
}

/// <summary>持久化到 %APPDATA%\SnipTrans\settings.json 的用户设置。</summary>
public sealed class AppSettings
{
    /// <summary>是否监听剪贴板中的新截图。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>目标语言（BCP-47 风格标签）。</summary>
    public string TargetLanguage { get; set; } = "zh-CN";

    /// <summary>原文已是中文时自动反向翻译成英文。</summary>
    public bool AutoSwitchDirection { get; set; } = true;

    public TranslateEngine Engine { get; set; } = TranslateEngine.MyMemory;

    // ---- OpenAI 兼容接口（可选，填了就用）----
    public string OpenAiBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "deepseek-chat";

    /// <summary>浮窗中同时显示识别到的原文。</summary>
    public bool ShowOriginal { get; set; } = true;

    /// <summary>浮窗自动关闭秒数，0 表示不自动关闭。</summary>
    public int AutoCloseSeconds { get; set; } = 0;

    /// <summary>浮窗失去焦点时自动关闭。</summary>
    public bool CloseOnDeactivate { get; set; } = false;

    /// <summary>图像任一边超过该像素值时忽略（避免误处理大图/壁纸）。</summary>
    public int MaxImageSide { get; set; } = 4000;

    /// <summary>OCR 出的文字少于此长度则视为无文字，不弹窗。</summary>
    public int MinTextLength { get; set; } = 2;

    /// <summary>同一张图（尺寸+少量采样）重复出现时是否跳过。</summary>
    public bool SkipDuplicateImage { get; set; } = true;

    // ---------- 持久化 ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipTrans");

    public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            // 配置损坏或含已移除的引擎枚举值时，备份并回到默认值，不阻塞启动
            Log.Write($"[settings] 读取失败，改用默认设置: {ex.Message}");
            try
            {
                var backup = ConfigPath + ".bak";
                if (File.Exists(ConfigPath)) File.Copy(ConfigPath, backup, overwrite: true);
            }
            catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 写入失败不影响本次运行
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts);
}
