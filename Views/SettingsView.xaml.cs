using Ink_Canvas.Plugins;
using Microsoft.Win32;
using QuickDictForICC.Services;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// QuickDict 插件设置面板。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private readonly IPluginHost _host;
        private readonly PluginSettings _settings;
        private readonly Action _onSettingsSaved;

        /// <summary>
        /// 初始化设置视图。
        /// </summary>
        /// <param name="settings">当前设置实例；视图会修改该实例并保存到磁盘。</param>
        /// <param name="host">插件主机，用于记录日志；可为空。</param>
        /// <param name="onSettingsSaved">保存成功后的回调；可为空。</param>
        public SettingsView(PluginSettings settings, IPluginHost host = null, Action onSettingsSaved = null)
        {
            InitializeComponent();

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _host = host;
            _onSettingsSaved = onSettingsSaved;

            // 代码后置挂接事件，避免 XAML 初始化阶段触发事件时控件尚未构造。
            TtsEngineComboBox.SelectionChanged += TtsEngineComboBox_SelectionChanged;
            EdgeRateSlider.ValueChanged += EdgeRateSlider_ValueChanged;
            BrowseEcDictButton.Click += BrowseEcDictButton_Click;
            BrowseMDictButton.Click += BrowseMDictButton_Click;
            BrowseMDictResourceButton.Click += BrowseMDictResourceButton_Click;
            BrowsePiperExecutableButton.Click += BrowsePiperExecutableButton_Click;
            BrowsePiperModelButton.Click += BrowsePiperModelButton_Click;
            SaveButton.Click += SaveButton_Click;
            ClearCacheButton.Click += ClearCacheButton_Click;

            LoadSettingsIntoUi();
        }

        private void LoadSettingsIntoUi()
        {
            EcDictPathTextBox.Text = _settings.EcDictPath ?? string.Empty;
            MDictPathTextBox.Text = _settings.MDictPath ?? string.Empty;
            MDictResourcePathTextBox.Text = _settings.MDictResourcePath ?? string.Empty;

            TtsEngineComboBox.SelectedIndex = _settings.TtsEngine == TtsEngineType.Piper ? 1 : 0;
            UpdateEnginePanels();

            EdgeVoiceComboBox.Text = _settings.EdgeVoice ?? "en-US-AriaNeural";
            EdgeRateSlider.Value = Clamp(_settings.EdgeRatePercent, -50, 50);
            UpdateRateDisplay();

            PiperExecutablePathTextBox.Text = _settings.PiperExecutablePath ?? string.Empty;
            PiperModelPathTextBox.Text = _settings.PiperModelPath ?? string.Empty;
        }

        private void TtsEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateEnginePanels();
        }

        private void UpdateEnginePanels()
        {
            // XAML 初始化阶段可能先触发 SelectionChanged，此时面板控件尚未构造完成。
            if (TtsEngineComboBox == null || EdgeSettingsPanel == null || PiperSettingsPanel == null)
                return;

            bool isPiper = TtsEngineComboBox.SelectedIndex == 1;
            EdgeSettingsPanel.Visibility = isPiper ? Visibility.Collapsed : Visibility.Visible;
            PiperSettingsPanel.Visibility = isPiper ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EdgeRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateRateDisplay();
        }

        private void UpdateRateDisplay()
        {
            if (EdgeRateSlider == null || EdgeRateValueText == null)
                return;

            int value = (int)EdgeRateSlider.Value;
            EdgeRateValueText.Text = $"{value:+#;-#;+0}%";
        }

        private void BrowseEcDictButton_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFile("选择 ECDICT 数据文件", "CSV 文件|*.csv|所有文件|*.*");
            if (!string.IsNullOrEmpty(path))
            {
                EcDictPathTextBox.Text = path;
            }
        }

        private void BrowseMDictButton_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFile("选择 MDict 词典文件", "MDict 词典|*.mdx|所有文件|*.*");
            if (!string.IsNullOrEmpty(path))
            {
                MDictPathTextBox.Text = path;
            }
        }

        private void BrowseMDictResourceButton_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFile("选择 MDict 资源包（可选）", "MDict 资源包|*.mdd|所有文件|*.*");
            if (!string.IsNullOrEmpty(path))
            {
                MDictResourcePathTextBox.Text = path;
            }
        }

        private void BrowsePiperExecutableButton_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFile("选择 Piper 可执行文件", "可执行文件|*.exe|所有文件|*.*");
            if (!string.IsNullOrEmpty(path))
            {
                PiperExecutablePathTextBox.Text = path;
            }
        }

        private void BrowsePiperModelButton_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFile("选择 Piper 语音模型", "ONNX 模型|*.onnx|所有文件|*.*");
            if (!string.IsNullOrEmpty(path))
            {
                PiperModelPathTextBox.Text = path;
            }
        }

        private static string PickFile(string title, string filter)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyUiToSettings();
                ValidateSettings(out string warning);
                SettingsManager.Save(_settings);

                if (string.IsNullOrEmpty(warning))
                {
                    UpdateStatus("设置已保存。", Colors.Green);
                }
                else
                {
                    UpdateStatus($"设置已保存，但检测到以下问题：{warning}", Colors.Orange);
                }

                _host?.Log("QuickDict 设置已保存到 " + SettingsManager.FilePath);
                _onSettingsSaved?.Invoke();
            }
            catch (Exception ex)
            {
                UpdateStatus($"保存失败：{ex.Message}", Colors.Red);
                _host?.LogError("保存 QuickDict 设置失败", ex);
            }
        }

        private void ApplyUiToSettings()
        {
            _settings.EcDictPath = NormalizePath(EcDictPathTextBox.Text);
            _settings.MDictPath = NormalizePath(MDictPathTextBox.Text);
            _settings.MDictResourcePath = NormalizePath(MDictResourcePathTextBox.Text);
            _settings.TtsEngine = TtsEngineComboBox.SelectedIndex == 1 ? TtsEngineType.Piper : TtsEngineType.Edge;
            _settings.EdgeVoice = string.IsNullOrWhiteSpace(EdgeVoiceComboBox.Text)
                ? "en-US-AriaNeural"
                : EdgeVoiceComboBox.Text.Trim();
            _settings.EdgeRatePercent = (int)EdgeRateSlider.Value;
            _settings.PiperExecutablePath = NormalizePath(PiperExecutablePathTextBox.Text);
            _settings.PiperModelPath = NormalizePath(PiperModelPathTextBox.Text);
        }

        private static string NormalizePath(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private void ValidateSettings(out string warning)
        {
            var builder = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(_settings.EcDictPath) && !File.Exists(_settings.EcDictPath))
                builder.Append("ECDICT 文件不存在。");

            if (!string.IsNullOrWhiteSpace(_settings.MDictPath) && !File.Exists(_settings.MDictPath))
                builder.Append("MDict 词典文件不存在。");

            if (!string.IsNullOrWhiteSpace(_settings.MDictResourcePath) && !File.Exists(_settings.MDictResourcePath))
                builder.Append("MDict 资源包不存在。");

            if (_settings.TtsEngine == TtsEngineType.Piper)
            {
                if (string.IsNullOrWhiteSpace(_settings.PiperExecutablePath) || !File.Exists(_settings.PiperExecutablePath))
                    builder.Append("Piper 可执行文件路径无效。");
                if (string.IsNullOrWhiteSpace(_settings.PiperModelPath) || !File.Exists(_settings.PiperModelPath))
                    builder.Append("Piper 模型路径无效。");
            }

            warning = builder.ToString();
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "QuickDictForICC", "TtsCache");
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    UpdateStatus("TTS 缓存已清除。", Colors.Green);
                }
                else
                {
                    UpdateStatus("暂无 TTS 缓存。", Colors.Gray);
                }

                _host?.Log("QuickDict TTS 缓存已清除");
            }
            catch (Exception ex)
            {
                UpdateStatus($"清除缓存失败：{ex.Message}", Colors.Red);
                _host?.LogError("清除 QuickDict TTS 缓存失败", ex);
            }
        }

        private void UpdateStatus(string message, Color color)
        {
            StatusText.Text = message;
            StatusText.Foreground = new SolidColorBrush(color);
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
