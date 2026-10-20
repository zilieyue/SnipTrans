using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnipTrans.Services;
// WPF 与 WinForms 混用时的同名类型消歧
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;

namespace SnipTrans.Views;

public partial class ResultWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly DispatcherTimer _toastTimer;

    public ResultWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _autoCloseTimer = new DispatcherTimer();
        _autoCloseTimer.Tick += (_, _) => { _autoCloseTimer.Stop(); Close(); };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText.Opacity = 0; };

        CloseButton.Click += (_, _) => Close();
        OriginalText.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) CopyOriginal(); };
        TranslatedText.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) CopyTranslation(); };

        // 顶部区域可拖动窗口
        MetaText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
        };

        KeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            if (_settings.CloseOnDeactivate) Close();
        };
        Closed += (_, _) =>
        {
            _autoCloseTimer.Stop();
            _toastTimer.Stop();
        };
    }

    /// <summary>先弹出一个「处理中」骨架，让用户知道截图被接收了。</summary>
    public void SetLoading()
    {
        MetaText.Text = "正在识别文字…";
        OriginalText.Text = "";
        OriginalScroll.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;
        TranslatedText.Text = "…";
        TranslatedText.Foreground = System.Windows.Media.Brushes.Gray;
        HintText.Text = "Esc 关闭";
    }

    /// <summary>OCR 完成、翻译进行中的中间态：先把原文亮出来，用户不用干等。</summary>
    public void SetTranslating(OcrOutcome ocr)
    {
        MetaText.Text = "正在翻译…";

        if (_settings.ShowOriginal && ocr is not null && !string.IsNullOrWhiteSpace(ocr.Text))
        {
            OriginalText.Text = ocr.Text;
            OriginalScroll.Visibility = Visibility.Visible;
            Divider.Visibility = Visibility.Visible;
        }
        else
        {
            OriginalScroll.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
        }

        TranslatedText.Foreground = System.Windows.Media.Brushes.Gray;
        TranslatedText.Text = "…";
        ClampToScreen();
    }

    public void SetResult(OcrOutcome ocr, TranslateResult tr)
    {
        MetaText.Text = $"{tr.Direction} · {tr.EngineTag} · {ocr.ElapsedMs + tr.ElapsedMs}ms";

        if (_settings.ShowOriginal && !string.IsNullOrWhiteSpace(ocr.Text))
        {
            OriginalText.Text = ocr.Text;
            OriginalScroll.Visibility = Visibility.Visible;
            Divider.Visibility = Visibility.Visible;
        }
        else
        {
            OriginalScroll.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
        }

        TranslatedText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF3, 0xF5, 0xF9));
        TranslatedText.Text = tr.Text;
        HintText.Text = "Esc 关闭 · 双击译文复制";

        ClampToScreen();
        StartAutoCloseIfNeeded();
    }

    /// <summary>翻译失败时仍然把识别到的原文显示出来，用户可以直接复制走。</summary>
    public void SetError(string message, OcrOutcome ocr)
    {
        MetaText.Text = "翻译失败";
        TranslatedText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF0, 0x9B, 0x9B));
        TranslatedText.Text = message;

        if (ocr is not null && !string.IsNullOrWhiteSpace(ocr.Text))
        {
            OriginalText.Text = ocr.Text;
            OriginalScroll.Visibility = Visibility.Visible;
            Divider.Visibility = Visibility.Visible;
            HintText.Text = "已识别到原文，双击原文可复制";
        }
        else
        {
            OriginalScroll.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
            HintText.Text = "Esc 关闭";
        }

        ClampToScreen();
        StartAutoCloseIfNeeded();
    }

    private void StartAutoCloseIfNeeded()
    {
        if (_settings.AutoCloseSeconds > 0)
        {
            _autoCloseTimer.Interval = TimeSpan.FromSeconds(_settings.AutoCloseSeconds);
            _autoCloseTimer.Start();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CopyTranslation()
    {
        if (string.IsNullOrWhiteSpace(TranslatedText.Text)) return;
        TrySetClipboard(TranslatedText.Text, "已复制译文");
    }

    private void CopyOriginal()
    {
        if (string.IsNullOrWhiteSpace(OriginalText.Text)) return;
        TrySetClipboard(OriginalText.Text, "已复制原文");
    }

    private void TrySetClipboard(string text, string toast)
    {
        try
        {
            Clipboard.SetText(text);
            ShowToast(toast);
        }
        catch
        {
            ShowToast("复制失败");
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastText.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>出现在鼠标附近，并保证整个窗口落在所在屏幕的工作区内。</summary>
    public void PositionNearCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        double scale = DpiHelper.GetScaleForPoint(cursor);

        double mx = cursor.X / scale;
        double my = cursor.Y / scale;

        UpdateLayout();

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 120;

        double left = mx + 20;
        double top = my + 20;

        if (left + w > screen.WorkingArea.Right / scale) left = mx - w - 14;
        if (top + h > screen.WorkingArea.Bottom / scale) top = my - h - 14;

        Left = Clamp(left, screen.WorkingArea.Left / scale, screen.WorkingArea.Right / scale - w);
        Top = Clamp(top, screen.WorkingArea.Top / scale, screen.WorkingArea.Bottom / scale - h);

        Log.Write($"[ui] 定位 left={Left:F0} top={Top:F0} size={w:F0}x{h:F0} " +
                  $"cursor={cursor.X},{cursor.Y} scale={scale:F2} " +
                  $"work={screen.WorkingArea}");
    }

    /// <summary>内容高度变化后重新约束位置，避免溢出屏幕。</summary>
    private void ClampToScreen()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                UpdateLayout();
                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + 20)));
                double scale = DpiHelper.GetScaleForPoint(
                    new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + 20)));

                double maxTop = screen.WorkingArea.Bottom / scale - ActualHeight;
                double maxLeft = screen.WorkingArea.Right / scale - ActualWidth;

                if (Top > maxTop) Top = Math.Max(screen.WorkingArea.Top / scale, maxTop);
                if (Left > maxLeft) Left = Math.Max(screen.WorkingArea.Left / scale, maxLeft);
            }
            catch { }
        }), DispatcherPriority.Loaded);
    }

    public void FadeIn()
    {
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private static double Clamp(double v, double min, double max) =>
        max < min ? min : Math.Max(min, Math.Min(v, max));
}

internal static class DpiHelper
{
    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public static double GetScaleForPoint(System.Drawing.Point p)
    {
        try
        {
            var hmon = MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2 /* NEAREST */);
            if (GetDpiForMonitor(hmon, 0 /* EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { }
        return 1.0;
    }
}
