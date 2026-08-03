using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using System;
using System.Windows;
using System.Windows.Controls;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 单词查询结果展示界面。
    /// </summary>
    public partial class ResultView : UserControl
    {
        private TtsService _ttsService;
        private TtsOptions _ttsOptions;
        private IWordEntry _currentEntry;

        /// <summary>
        /// 当用户点击"生成单词卡"且外部有订阅者时触发。
        /// </summary>
        public event EventHandler GenerateWordCardRequested;

        public ResultView()
        {
            InitializeComponent();
            SpeakButton.Click += OnSpeakButtonClick;
            GenerateCardButton.Click += OnGenerateCardButtonClick;
        }

        /// <summary>
        /// 注入 TTS 服务实例。
        /// </summary>
        public void SetTtsService(TtsService service)
        {
            _ttsService = service;
        }

        /// <summary>
        /// 注入 TTS 选项。
        /// </summary>
        public void SetTtsOptions(TtsOptions options)
        {
            _ttsOptions = options;
        }

        private static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // First replace literal "\\n" (two characters), then actual newline characters.
            return text.Replace("\\n", Environment.NewLine).Replace("\n", Environment.NewLine).Replace("\r", string.Empty);
        }

        /// <summary>
        /// 更新界面显示指定的单词查询结果。
        /// </summary>
        public void ShowResult(IWordEntry entry)
        {
            _currentEntry = entry;
            if (entry == null)
            {
                ClearDisplay();
                return;
            }

            WordText.Text = entry.Word ?? string.Empty;
            PhoneticText.Text = string.IsNullOrWhiteSpace(entry.Phonetic)
                ? string.Empty
                : string.Format("[{0}]", entry.Phonetic);

            if (!string.IsNullOrWhiteSpace(entry.HtmlDefinition))
            {
                ShowHtmlDefinition(entry.HtmlDefinition);
            }
            else
            {
                ShowTextDefinition(entry);
            }

            PhrasesText.Text = string.IsNullOrWhiteSpace(entry.Exchange)
                ? Properties.Resources.ResultView_NoPhrases
                : entry.Exchange;

            ResultTabs.SelectedIndex = 0;
        }

        private void ClearDisplay()
        {
            WordText.Text = string.Empty;
            PhoneticText.Text = string.Empty;
            DefinitionWebView.Visibility = Visibility.Collapsed;
            TextDefinitionScroll.Visibility = Visibility.Collapsed;
            PosText.Text = string.Empty;
            DefinitionText.Text = string.Empty;
            TranslationText.Text = string.Empty;
            ExchangeText.Text = string.Empty;
            PhrasesText.Text = Properties.Resources.ResultView_NoPhrases;
        }

        private async void ShowHtmlDefinition(string html)
        {
            try
            {
                TextDefinitionScroll.Visibility = Visibility.Collapsed;
                DefinitionWebView.Visibility = Visibility.Visible;

                if (DefinitionWebView.CoreWebView2 == null)
                {
                    await DefinitionWebView.EnsureCoreWebView2Async();
                }

                DefinitionWebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                // WebView2 初始化失败时，降级为文本模式，避免弹窗崩溃。
                DefinitionWebView.Visibility = Visibility.Collapsed;
                TextDefinitionScroll.Visibility = Visibility.Visible;

                PosText.Text = string.Empty;
                PosText.Visibility = Visibility.Collapsed;

                DefinitionText.Text = string.Format(Properties.Resources.ResultView_HtmlRenderError_Format, ex.Message);
                DefinitionText.Visibility = Visibility.Visible;

                TranslationText.Text = NormalizeNewlines(_currentEntry?.Translation ?? string.Empty);
                TranslationText.Visibility = string.IsNullOrWhiteSpace(TranslationText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                ExchangeText.Text = NormalizeNewlines(_currentEntry?.Exchange ?? string.Empty);
                ExchangeText.Visibility = string.IsNullOrWhiteSpace(ExchangeText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        private void ShowTextDefinition(IWordEntry entry)
        {
            DefinitionWebView.Visibility = Visibility.Collapsed;
            TextDefinitionScroll.Visibility = Visibility.Visible;

            PosText.Text = entry.Pos ?? string.Empty;
            PosText.Visibility = string.IsNullOrWhiteSpace(PosText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            DefinitionText.Text = NormalizeNewlines(entry.Definition ?? string.Empty);
            DefinitionText.Visibility = string.IsNullOrWhiteSpace(DefinitionText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            TranslationText.Text = NormalizeNewlines(entry.Translation ?? string.Empty);
            TranslationText.Visibility = string.IsNullOrWhiteSpace(TranslationText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            ExchangeText.Text = NormalizeNewlines(entry.Exchange ?? string.Empty);
            ExchangeText.Visibility = string.IsNullOrWhiteSpace(ExchangeText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void OnSpeakButtonClick(object sender, RoutedEventArgs e)
        {
            if (_ttsService == null || _currentEntry == null)
                return;

            TtsOptions options = _ttsOptions ?? new TtsOptions
            {
                Engine = TtsEngineType.Edge,
                Voice = "en-US-AriaNeural",
                Rate = "+0%"
            };

            try
            {
                await _ttsService.SpeakAsync(_currentEntry.Word, options);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(Properties.Resources.Message_SpeakFailed_Format, ex.Message),
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnGenerateCardButtonClick(object sender, RoutedEventArgs e)
        {
            if (GenerateWordCardRequested != null)
            {
                GenerateWordCardRequested.Invoke(this, EventArgs.Empty);
            }
            else
            {
                MessageBox.Show(
                    Properties.Resources.Message_WordCardComingSoon,
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }
}
