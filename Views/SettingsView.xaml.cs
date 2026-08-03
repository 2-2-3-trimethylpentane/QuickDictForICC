using Ink_Canvas.Plugins;
using Microsoft.Win32;
using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly TtsService _ttsService;

        /// <summary>
        /// 初始化设置视图。
        /// </summary>
        /// <param name="settings">当前设置实例；视图会修改该实例并保存到磁盘。</param>
        /// <param name="host">插件主机，用于记录日志；可为空。</param>
        /// <param name="onSettingsSaved">保存成功后的回调；可为空。</param>
        public SettingsView(PluginSettings settings, IPluginHost host = null, Action onSettingsSaved = null, TtsService ttsService = null)
        {
            InitializeComponent();

            RootScrollViewer.PreviewMouseWheel += OnRootScrollViewerPreviewMouseWheel;

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _host = host;
            _onSettingsSaved = onSettingsSaved;
            _ttsService = ttsService;

            // 代码后置挂接事件，避免 XAML 初始化阶段触发事件时控件尚未构造。
            TtsEngineComboBox.SelectionChanged += OnTtsEngineComboBoxSelectionChanged;
            EdgeRateSlider.ValueChanged += OnEdgeRateSliderValueChanged;
            BrowseEcDictButton.Click += OnBrowseEcDictButtonClick;
            BrowseMDictButton.Click += OnBrowseMDictButtonClick;
            BrowseMDictResourceButton.Click += OnBrowseMDictResourceButtonClick;
            BrowsePiperExecutableButton.Click += OnBrowsePiperExecutableButtonClick;
            BrowsePiperModelButton.Click += OnBrowsePiperModelButtonClick;
            TestTtsButton.Click += OnTestTtsButtonClick;
            SaveButton.Click += OnSaveButtonClick;
            ClearCacheButton.Click += OnClearCacheButtonClick;

            LoadSettingsIntoUi();
        }

        private void LoadSettingsIntoUi()
        {
            EcDictPathTextBox.Text = _settings.EcDictPath ?? string.Empty;
            MDictPathTextBox.Text = _settings.MDictPath ?? string.Empty;
            MDictResourcePathTextBox.Text = _settings.MDictResourcePath ?? string.Empty;

            TtsEngineComboBox.SelectedIndex = _settings.TtsEngine == TtsEngineType.Piper ? 1 : 0;
            UpdateEnginePanels();

            EdgeVoiceComboBox.Text = _settings.EdgeVoice ?? Properties.Resources.Settings_EdgeVoice_AriaNeural;
            EdgeRateSlider.Value = Clamp(_settings.EdgeRatePercent, -50, 50);
            UpdateRateDisplay();

            PiperExecutablePathTextBox.Text = _settings.PiperExecutablePath ?? string.Empty;
            PiperModelPathTextBox.Text = _settings.PiperModelPath ?? string.Empty;
        }

        private void OnTtsEngineComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateEnginePanels();
        }

        private void UpdateEnginePanels()
        {
            // XAML 初始化阶段可能先触发 SelectionChanged，此时卡片控件尚未构造完成。
            if (TtsEngineComboBox == null || EdgeVoiceCard == null || EdgeRateCard == null || PiperExecutableCard == null || PiperModelCard == null)
                return;

            bool isPiper = TtsEngineComboBox.SelectedIndex == 1;
            EdgeVoiceCard.Visibility = isPiper ? Visibility.Collapsed : Visibility.Visible;
            EdgeRateCard.Visibility = isPiper ? Visibility.Collapsed : Visibility.Visible;
            PiperExecutableCard.Visibility = isPiper ? Visibility.Visible : Visibility.Collapsed;
            PiperModelCard.Visibility = isPiper ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnEdgeRateSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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

        private void OnBrowseEcDictButtonClick(object sender, RoutedEventArgs e)
        {
            string path = PickFile(Properties.Resources.Settings_Dialog_EcDictTitle, Properties.Resources.Settings_Dialog_CsvFilter);
            if (!string.IsNullOrEmpty(path))
            {
                EcDictPathTextBox.Text = path;
            }
        }

        private void OnBrowseMDictButtonClick(object sender, RoutedEventArgs e)
        {
            string path = PickFile(Properties.Resources.Settings_Dialog_MDictTitle, Properties.Resources.Settings_Dialog_MDictFilter);
            if (!string.IsNullOrEmpty(path))
            {
                MDictPathTextBox.Text = path;
            }
        }

        private void OnBrowseMDictResourceButtonClick(object sender, RoutedEventArgs e)
        {
            string path = PickFile(Properties.Resources.Settings_Dialog_MDictResourceTitle, Properties.Resources.Settings_Dialog_MDictResourceFilter);
            if (!string.IsNullOrEmpty(path))
            {
                MDictResourcePathTextBox.Text = path;
            }
        }

        private void OnBrowsePiperExecutableButtonClick(object sender, RoutedEventArgs e)
        {
            string path = PickFile(Properties.Resources.Settings_Dialog_PiperExecutableTitle, Properties.Resources.Settings_Dialog_ExeFilter);
            if (!string.IsNullOrEmpty(path))
            {
                PiperExecutablePathTextBox.Text = path;
            }
        }

        private void OnBrowsePiperModelButtonClick(object sender, RoutedEventArgs e)
        {
            string path = PickFile(Properties.Resources.Settings_Dialog_PiperModelTitle, Properties.Resources.Settings_Dialog_OnnxFilter);
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

        private void OnSaveButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyUiToSettings();
                ValidateSettings(out string warning);
                SettingsManager.Save(_settings);

                if (string.IsNullOrEmpty(warning))
                {
                    UpdateStatus(Properties.Resources.Message_SaveSuccess, Colors.Green);
                }
                else
                {
                    UpdateStatus(string.Format(Properties.Resources.Message_SaveSuccessWithWarning_Format, warning), Colors.Orange);
                }

                _host?.Log("QuickDict 设置已保存到 " + SettingsManager.FilePath);
                _onSettingsSaved?.Invoke();
            }
            catch (Exception ex)
            {
                UpdateStatus(string.Format(Properties.Resources.Message_SaveFailed_Format, ex.Message), Colors.Red);
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
                ? Properties.Resources.Settings_EdgeVoice_AriaNeural
                : EdgeVoiceComboBox.Text.Trim();
            _settings.EdgeRatePercent = (int)EdgeRateSlider.Value;
            _settings.PiperExecutablePath = NormalizePath(PiperExecutablePathTextBox.Text);
            _settings.PiperModelPath = NormalizePath(PiperModelPathTextBox.Text);
        }

        private static string NormalizePath(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private TtsOptions BuildTtsOptionsFromUi()
        {
            TtsEngineType engine = TtsEngineComboBox.SelectedIndex == 1 ? TtsEngineType.Piper : TtsEngineType.Edge;
            string voice = engine == TtsEngineType.Piper
                ? Path.GetFileNameWithoutExtension(PiperModelPathTextBox.Text ?? string.Empty)
                : (string.IsNullOrWhiteSpace(EdgeVoiceComboBox.Text) ? Properties.Resources.Settings_EdgeVoice_AriaNeural : EdgeVoiceComboBox.Text.Trim());

            return new TtsOptions
            {
                Engine = engine,
                Voice = voice,
                Rate = $"{(int)EdgeRateSlider.Value:+#;-#;+0}%",
                PiperExecutablePath = NormalizePath(PiperExecutablePathTextBox.Text),
                PiperModelPath = NormalizePath(PiperModelPathTextBox.Text)
            };
        }

        private void ValidateSettings(out string warning)
        {
            var builder = new System.Text.StringBuilder();

            if (!string.IsNullOrWhiteSpace(_settings.EcDictPath) && !File.Exists(_settings.EcDictPath))
                builder.Append(Properties.Resources.Message_ValidateEcDictNotFound);

            if (!string.IsNullOrWhiteSpace(_settings.MDictPath) && !File.Exists(_settings.MDictPath))
                builder.Append(Properties.Resources.Message_ValidateMDictNotFound);

            if (!string.IsNullOrWhiteSpace(_settings.MDictResourcePath) && !File.Exists(_settings.MDictResourcePath))
                builder.Append(Properties.Resources.Message_ValidateMDictResourceNotFound);

            if (_settings.TtsEngine == TtsEngineType.Piper)
            {
                if (string.IsNullOrWhiteSpace(_settings.PiperExecutablePath) || !File.Exists(_settings.PiperExecutablePath))
                    builder.Append(Properties.Resources.Message_ValidatePiperExecutableInvalid);
                if (string.IsNullOrWhiteSpace(_settings.PiperModelPath) || !File.Exists(_settings.PiperModelPath))
                    builder.Append(Properties.Resources.Message_ValidatePiperModelInvalid);
            }

            warning = builder.ToString();
        }

        private async void OnTestTtsButtonClick(object sender, RoutedEventArgs e)
        {
            if (_ttsService == null)
            {
                MessageBox.Show(Properties.Resources.Message_TtsServiceNotReady, Properties.Resources.MessageBox_Title_Notice, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string text = TtsTestTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(Properties.Resources.Message_TtsTestTextEmpty, Properties.Resources.MessageBox_Title_Notice, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TtsOptions options = BuildTtsOptionsFromUi();
            try
            {
                await _ttsService.SpeakAsync(text, options);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Resources.Message_TtsTestFailed_Format, ex.Message), Properties.Resources.MessageBox_Title_Notice, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnClearCacheButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string cacheDir = Path.Combine(Path.GetTempPath(), "QuickDictForICC", "TtsCache");
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    UpdateStatus(Properties.Resources.Message_CacheCleared, Colors.Green);
                }
                else
                {
                    UpdateStatus(Properties.Resources.Message_NoCache, Colors.Gray);
                }

                _host?.Log("QuickDict TTS 缓存已清除");
            }
            catch (Exception ex)
            {
                UpdateStatus(string.Format(Properties.Resources.Message_ClearCacheFailed_Format, ex.Message), Colors.Red);
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

        private void OnRootScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }
}
