using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnipTrans.Services;
using SnipTrans.Views;
using WinForms = System.Windows.Forms;
// WPF 与 WinForms 混用时的同名类型消歧
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace SnipTrans;

public partial class App : Application
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SnipTrans";

    private Mutex _instanceMutex;
    private AppSettings _settings;
    private ClipboardWatcher _watcher;
    private OcrService _ocr;
    private TranslateService _translator;

    private WinForms.NotifyIcon _tray;
    private WinForms.ToolStripMenuItem _enabledItem;
    private WinForms.ToolStripMenuItem _startupItem;

    private ResultWindow _currentWindow;
    private bool _processing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, @"Local\SnipTrans.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _ocr = new OcrService();
        _translator = new TranslateService();
        Log.Write($"===== 启动 v1.0.0 | OCR: {_ocr.Availability} | 引擎: {_settings.Engine} | 启用: {_settings.Enabled}");

        if (!_ocr.IsAvailable)
        {
            MessageBox.Show(
                "当前系统未安装可用的 OCR 语言组件，无法识别文字。\n\n" +
                "请在「设置 → 时间和语言 → 语言」中安装简体中文或英文的语言包后重试。",
                "SnipTrans",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        BuildTray();
        Log.Write("[init] 托盘已创建");

        _watcher = new ClipboardWatcher(HandleClipboardImageAsync)
        {
            Enabled = _settings.Enabled,
            SkipDuplicate = _settings.SkipDuplicateImage,
            MaxImageSide = _settings.MaxImageSide
        };
        _watcher.SyncBaseline();
        _watcher.Start();
        Log.Write("[init] 剪贴板监听已启动");

        // 启动后延迟提示一次，避免抢焦点
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowBalloon("SnipTrans 已启动",
                "按 Win + Shift + S 框选任意区域，译文会自动弹出。");
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ---------------- 托盘 ----------------

    private void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

        var title = new WinForms.ToolStripMenuItem("SnipTrans 截图翻译") { Enabled = false };
        menu.Items.Add(title);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _enabledItem = new WinForms.ToolStripMenuItem("启用截图翻译")
        {
            CheckOnClick = true,
            Checked = _settings.Enabled
        };
        _enabledItem.CheckedChanged += (_, _) =>
        {
            _settings.Enabled = _enabledItem.Checked;
            _settings.Save();
            if (_watcher is not null) _watcher.Enabled = _settings.Enabled;
            UpdateTrayText();
        };
        menu.Items.Add(_enabledItem);

        var translateNow = new WinForms.ToolStripMenuItem("立即翻译剪贴板图片");
        translateNow.Click += async (_, _) => await TranslateClipboardNowAsync();
        menu.Items.Add(translateNow);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        _startupItem = new WinForms.ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);
        menu.Items.Add(_startupItem);

        var aboutItem = new WinForms.ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "SnipTrans 截图翻译",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        UpdateTrayText();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        var state = _settings.Enabled ? "监听中" : "已暂停";
        _tray.Text = $"SnipTrans · {state} · {TranslateResult.LangName(
            _settings.TargetLanguage.StartsWith("zh") ? "zh-CN" : _settings.TargetLanguage)}";
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            if (_tray is null || !_tray.Visible) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(3000);
        }
        catch { }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "SnipTrans 截图翻译\n\n" +
            "用 Win + Shift + S 框选屏幕区域，程序监听剪贴板后自动识别文字并翻译。\n\n" +
            $"OCR 引擎：Windows 系统自带（{_ocr.Availability}）\n" +
            $"翻译引擎：{TranslateService.EngineName(_settings.Engine)}\n" +
            $"配置文件：{AppSettings.ConfigPath}",
            "关于 SnipTrans",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------- 核心流程 ----------------

    private async Task HandleClipboardImageAsync(BitmapSource image)
    {
        if (_processing) return;
        _processing = true;

        try
        {
            var window = EnsureWindow();
            window.SetLoading();
            window.PositionNearCursor();
            Log.Write("[flow] 浮窗已显示，开始 OCR");

            var ocr = await _ocr.RecognizeAsync(image);
            Log.Write($"[flow] OCR 完成 引擎={ocr.EngineTag} 字数={ocr.Text.Length} 耗时={ocr.ElapsedMs}ms");

            if (ocr.IsEmpty || ocr.Text.Trim().Length < _settings.MinTextLength)
            {
                Log.Write("[flow] 未识别到有效文字，静默收起浮窗");
                CloseCurrentWindow();
                return;
            }

            // OCR 已经出结果，先展示原文，翻译期间不给用户看空窗
            window.SetTranslating(ocr);

            try
            {
                var result = await _translator.TranslateAsync(ocr.Text, _settings);
                Log.Write($"[flow] 翻译完成 {result.EngineTag} {result.ElapsedMs}ms: {result.Text}");
                window.SetResult(ocr, result);
            }
            catch (Exception ex)
            {
                Log.Error("translate", ex);
                window.SetError(ex.Message, ocr);
            }
        }
        catch (Exception ex)
        {
            Log.Error("HandleClipboardImageAsync", ex);
            CloseCurrentWindow();
            ShowBalloon("处理失败", ex.Message);
        }
        finally
        {
            _processing = false;
        }
    }

    private ResultWindow EnsureWindow()
    {
        if (_currentWindow is { IsLoaded: true })
            return _currentWindow;

        var w = new ResultWindow(_settings);
        w.Show();
        w.PositionNearCursor();
        w.FadeIn();
        _currentWindow = w;
        return w;
    }

    private void CloseCurrentWindow()
    {
        try
        {
            if (_currentWindow is { IsLoaded: true }) _currentWindow.Close();
        }
        catch { }
        finally
        {
            _currentWindow = null;
        }
    }

    private async Task TranslateClipboardNowAsync()
    {
        BitmapSource image = null;
        try
        {
            if (Clipboard.ContainsImage())
            {
                image = Clipboard.GetImage();
                image?.Freeze();
            }
        }
        catch { }

        if (image is null)
        {
            ShowBalloon("剪贴板里没有图片", "先用 Win + Shift + S 截个图，再点这里。");
            return;
        }

        _watcher.SyncBaseline();
        await HandleClipboardImageAsync(image);
    }

    // ---------------- 设置 / 开机自启 ----------------

    private void OpenSettings()
    {
        CloseCurrentWindow();
        var dialog = new SettingsWindow(_settings) { Owner = null };
        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Result;
            _settings.Save();
            ApplySettings();
        }
    }

    private void ApplySettings()
    {
        if (_watcher is not null)
        {
            _watcher.Enabled = _settings.Enabled;
            _watcher.SkipDuplicate = _settings.SkipDuplicateImage;
            _watcher.MaxImageSide = _settings.MaxImageSide;
        }
        if (_enabledItem is not null) _enabledItem.Checked = _settings.Enabled;
        UpdateTrayText();
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    // ---------------- 生命周期 ----------------

    private void App_DispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 后台处理异常不应让托盘程序直接退出
        e.Handled = true;
        Log.Error("DispatcherUnhandled", e.Exception);
        try { ShowBalloon("发生错误", e.Exception.Message); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _watcher?.Dispose();
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _instanceMutex?.Dispose();
        }
        catch { }

        base.OnExit(e);
    }
}
