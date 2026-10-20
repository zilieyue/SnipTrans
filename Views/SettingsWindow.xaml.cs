using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SnipTrans.Services;

namespace SnipTrans.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly TranslateService _translator = new();

    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        _original = current.Clone();
        Result = current;
        InitializeComponent();
        LoadFrom(_original);
    }

    private void LoadFrom(AppSettings s)
    {
        EngineCombo.SelectedIndex = (int)s.Engine;
        BaseUrlBox.Text = s.OpenAiBaseUrl;
        ApiKeyBox.Text = s.OpenAiApiKey;
        ModelBox.Text = s.OpenAiModel;

        TargetLangCombo.SelectedIndex = s.TargetLanguage.StartsWith("en") ? 1 : 0;

        AutoSwitchCheck.IsChecked = s.AutoSwitchDirection;
        ShowOriginalCheck.IsChecked = s.ShowOriginal;
        CloseOnDeactivateCheck.IsChecked = s.CloseOnDeactivate;
        SkipDuplicateCheck.IsChecked = s.SkipDuplicateImage;

        AutoCloseBox.Text = s.AutoCloseSeconds.ToString();
        MaxSideBox.Text = s.MaxImageSide.ToString();

        UpdateOpenAiPanelVisibility();
        StatusText.Text = $"配置文件：{AppSettings.ConfigPath}";
    }

    private AppSettings ReadForm()
    {
        var s = _original.Clone();

        s.Engine = (TranslateEngine)Math.Max(0, EngineCombo.SelectedIndex);
        s.OpenAiBaseUrl = BaseUrlBox.Text.Trim();
        s.OpenAiApiKey = ApiKeyBox.Text.Trim();
        s.OpenAiModel = ModelBox.Text.Trim();

        s.TargetLanguage = TargetLangCombo.SelectedIndex == 1 ? "en" : "zh-CN";

        s.AutoSwitchDirection = AutoSwitchCheck.IsChecked == true;
        s.ShowOriginal = ShowOriginalCheck.IsChecked == true;
        s.CloseOnDeactivate = CloseOnDeactivateCheck.IsChecked == true;
        s.SkipDuplicateImage = SkipDuplicateCheck.IsChecked == true;

        s.AutoCloseSeconds = ParseInt(AutoCloseBox.Text, 0, 0, 600);
        s.MaxImageSide = ParseInt(MaxSideBox.Text, 4000, 200, 20000);

        return s;
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text?.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void UpdateOpenAiPanelVisibility()
    {
        OpenAiPanel.Visibility = EngineCombo.SelectedIndex == (int)TranslateEngine.OpenAi
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateOpenAiPanelVisibility();

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResult.Foreground = System.Windows.Media.Brushes.Gray;
        TestResult.Text = "正在测试…";

        try
        {
            var settings = ReadForm();
            var r = await _translator.TranslateAsync(
                "Hello world. This is a screenshot translation test.", settings);

            TestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x8F, 0x4B));
            TestResult.Text = $"✓ 成功（{r.EngineTag}，{r.ElapsedMs}ms）：{r.Text}";
        }
        catch (Exception ex)
        {
            TestResult.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
            TestResult.Text = $"✗ 失败：{ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.ConfigDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppSettings.ConfigDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ReadForm();
        Result.Save();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
