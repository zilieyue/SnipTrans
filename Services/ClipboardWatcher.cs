using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// WPF 与 WinForms 混用时的同名类型消歧
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using PixelFormat = System.Windows.Media.PixelFormat;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace SnipTrans.Services;

/// <summary>
/// 监听剪贴板中的新图像。Win+Shift+S 是系统级固定功能，无法被第三方替换，
/// 但截图工具会把框选结果写入剪贴板 —— 监听剪贴板即可实现「零侵入」接入。
/// 用 GetClipboardSequenceNumber 轮询而非剪贴板链，避免影响其他程序。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private readonly DispatcherTimer _timer;
    private readonly Func<BitmapSource, Task> _onImage;

    private uint _lastSeq;
    private bool _busy;
    private int _ticks;

    /// <summary>冷却截止时间：截图工具可能连续写入两次（DIB + PNG），用冷却窗去抖。</summary>
    private DateTime _cooldownUntil = DateTime.MinValue;

    /// <summary>上一张已处理图像的指纹，用于跳过重复内容。</summary>
    private string _lastFingerprint;

    public bool Enabled { get; set; } = true;

    public bool SkipDuplicate { get; set; } = true;

    public int MaxImageSide { get; set; } = 4000;

    public ClipboardWatcher(Func<BitmapSource, Task> onImage)
    {
        _onImage = onImage;
        _lastSeq = GetClipboardSequenceNumber();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(280)
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    /// <summary>主动丢弃当前剪贴板序号，避免把用户上一次的旧内容当成新截图。</summary>
    public void SyncBaseline() => _lastSeq = GetClipboardSequenceNumber();

    private async void OnTick(object sender, EventArgs e)
    {
        _ticks++;
        if (_ticks % 30 == 0)
            Log.Write($"[watch] 心跳 tick={_ticks} busy={_busy} enabled={Enabled}");

        if (!Enabled || _busy) return;

        uint seq;
        try { seq = GetClipboardSequenceNumber(); }
        catch { return; }

        if (seq == _lastSeq) return;
        _lastSeq = seq;
        Log.Write($"[watch] 剪贴板序号变化 -> {seq}");

        // 冷却窗内只更新序号、不处理，避开截图工具的二次写入
        if (DateTime.UtcNow < _cooldownUntil)
        {
            Log.Write("[watch] 冷却中，跳过");
            return;
        }

        // 读取放到独立 STA 线程并加超时：若占用剪贴板的程序无响应，
        // 在 UI 线程上直接读会连带冻结整个托盘程序。
        BitmapSource image = await ReadImageWithTimeoutAsync(2000);
        if (image is null)
        {
            Log.Write("[watch] 未取到图像（超时或无图像内容）");
            return;
        }
        Log.Write($"[watch] 读到图像 {image.PixelWidth}x{image.PixelHeight} {image.Format}");

        // 尺寸过滤：壁纸、大图之类误触发的场景
        if (image.PixelWidth > MaxImageSide || image.PixelHeight > MaxImageSide)
        {
            Log.Write($"[watch] 图像过大（上限 {MaxImageSide}），忽略");
            return;
        }
        if (image.PixelWidth < 8 || image.PixelHeight < 8) return;

        var fingerprint = Fingerprint(image);
        if (SkipDuplicate && fingerprint == _lastFingerprint) return;
        _lastFingerprint = fingerprint;

        _busy = true;
        _cooldownUntil = DateTime.UtcNow.AddMilliseconds(1200);
        try
        {
            await _onImage(image);
        }
        catch
        {
            // 单次处理异常不应终止监听
        }
        finally
        {
            _busy = false;
            _lastSeq = GetClipboardSequenceNumber();
        }
    }

    /// <summary>
    /// 在独立 STA 线程读取剪贴板，并设超时。剪贴板采用 OLE 延迟渲染，
    /// 若数据所有者进程无响应，读取调用会长时间挂起 —— 不能让它拖住 UI 线程。
    /// </summary>
    private static async Task<BitmapSource> ReadImageWithTimeoutAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(ReadImageCore()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "SnipTrans.ClipboardReader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task)
        {
            Log.Write($"[watch] 读取剪贴板超时（{timeoutMs}ms），占用方可能无响应");
            return null;
        }

        try { return await tcs.Task; }
        catch (Exception ex)
        {
            Log.Error("ReadImageWithTimeout", ex);
            return null;
        }
    }

    /// <summary>
    /// 按优先级从剪贴板取图：标准位图 → PNG 流 → CF_DIB → 图片文件。
    /// 不同截图工具写入的格式不同，只判断 ContainsImage() 会漏掉部分来源。
    /// </summary>
    private static BitmapSource ReadImageCore()
    {
        // 1) 标准位图（绝大多数截图工具、浏览器复制图片）
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img is not null)
                {
                    img.Freeze();
                    return img;
                }
            }
        }
        catch { }

        // 2) PNG 流（Chromium 系应用优先写入该格式）
        try
        {
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is Stream png)
            {
                using var ms = new MemoryStream();
                png.CopyTo(ms);
                ms.Position = 0;
                var dec = new PngBitmapDecoder(ms,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames[0];
                frame.Freeze();
                Log.Write("[watch] 来源格式: PNG");
                return frame;
            }
        }
        catch { }

        // 3) CF_DIB（部分工具只放设备无关位图，需补 BMP 文件头再解码）
        try
        {
            if (Clipboard.ContainsData(DataFormats.Dib) && Clipboard.GetData(DataFormats.Dib) is Stream dib)
            {
                var frame = DecodeDib(dib);
                if (frame is not null)
                {
                    Log.Write("[watch] 来源格式: DIB");
                    return frame;
                }
            }
        }
        catch { }

        // 4) 从资源管理器复制的图片文件
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var file in Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrEmpty(file)) continue;
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) continue;

                    using var fs = File.OpenRead(file);
                    var dec = BitmapDecoder.Create(fs,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var frame = dec.Frames[0];
                    frame.Freeze();
                    Log.Write($"[watch] 来源格式: 文件 {ext}");
                    return frame;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>把裸 DIB 数据加上 14 字节 BITMAPFILEHEADER 后交给 BMP 解码器。</summary>
    private static BitmapSource DecodeDib(Stream dibStream)
    {
        using var ms = new MemoryStream();
        dibStream.CopyTo(ms);
        var data = ms.ToArray();
        if (data.Length < 40) return null;

        int headerSize = BitConverter.ToInt32(data, 0);
        int bitCount = BitConverter.ToInt16(data, 14);
        int clrUsed = BitConverter.ToInt32(data, 32);

        int offset = 14 + headerSize;
        if (bitCount <= 8)
            offset += (clrUsed == 0 ? 1 << bitCount : clrUsed) * 4;

        using var outMs = new MemoryStream();
        using (var bw = new BinaryWriter(outMs, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((ushort)0x4D42);          // 'BM'
            bw.Write(14 + data.Length);        // 文件总大小
            bw.Write((ushort)0);               // 保留
            bw.Write((ushort)0);
            bw.Write(offset);                  // 像素数据偏移
            bw.Write(data);
        }

        outMs.Position = 0;
        var decoder = new BmpBitmapDecoder(outMs,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>用尺寸 + 稀疏采样像素生成轻量指纹，避免同一张图被反复翻译。</summary>
    private static string Fingerprint(BitmapSource img)
    {
        try
        {
            int w = img.PixelWidth, h = img.PixelHeight;

            BitmapSource src = img;
            if (src.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var conv = new FormatConvertedBitmap();
                conv.BeginInit();
                conv.Source = src;
                conv.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                conv.EndInit();
                conv.Freeze();
                src = conv;
            }

            int stride = w * 4;
            var pixels = new byte[stride * h];
            src.CopyPixels(pixels, stride, 0);

            unchecked
            {
                uint hash = 2166136261;
                int stepY = Math.Max(1, h / 24);
                int stepX = Math.Max(1, w / 24);
                for (int y = 0; y < h; y += stepY)
                {
                    for (int x = 0; x < w; x += stepX)
                    {
                        int i = y * stride + x * 4;
                        hash = (hash ^ pixels[i]) * 16777619;
                        hash = (hash ^ pixels[i + 1]) * 16777619;
                        hash = (hash ^ pixels[i + 2]) * 16777619;
                    }
                }
                return $"{w}x{h}:{hash:x8}";
            }
        }
        catch
        {
            return $"{img.PixelWidth}x{img.PixelHeight}";
        }
    }
}
