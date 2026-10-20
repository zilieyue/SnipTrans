using System.IO;

namespace SnipTrans.Services;

/// <summary>轻量文件日志，便于用户反馈问题时定位。写入 %APPDATA%\SnipTrans\debug.log。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static bool Enabled { get; set; } = true;

    private static string LogPath => Path.Combine(AppSettings.ConfigDir, "debug.log");

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.ConfigDir);

                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(LogPath, "");

                File.AppendAllText(LogPath,
                    $"{DateTime.Now:MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }

    public static void Error(string where, Exception ex) =>
        Write($"[ERROR] {where}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
