using iNKORE.UI.WPF.Modern;
using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 单词卡视图。无 Tab，垂直滚动，支持点击发音。
    /// </summary>
    public partial class WordCardView : UserControl
    {
        private IWordEntry _currentEntry;
        private TtsService _ttsService;
        private TtsOptions _ttsOptions;

        private MouseButtonEventHandler _selectHandler;
        private EventHandler<TouchEventArgs> _touchSelectHandler;

        /// <summary>
        /// 白板选择处理器执行完毕后触发，用于补救 ICC 对非图片元素的工具栏隐藏行为。
        /// </summary>
        public event EventHandler SelectionRequested;

        private static readonly Regex PosPrefixRegex = new Regex(
            @"^([a-zA-Z]+\.\s*)(.*)$",
            RegexOptions.Compiled);

        public WordCardView()
        {
            InitializeComponent();
            SpeakerButton.Click += OnSpeakerButtonClick;
        }

        /// <summary>
        /// 注入 TTS 服务。
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

        /// <summary>
        /// 注册白板选择处理器。由外部白板手势系统调用。
        /// </summary>
        public void RegisterSelectHandler(MouseButtonEventHandler handler)
        {
            _selectHandler = handler;
        }

        /// <summary>
        /// 注册白板触摸选择处理器。由外部白板手势系统调用。
        /// </summary>
        public void RegisterTouchSelectHandler(EventHandler<TouchEventArgs> handler)
        {
            _touchSelectHandler = handler;
        }

        /// <summary>
        /// 判断命中目标是否为内部交互子控件，避免点击按钮、滚动条等时触发外层选中/拖动。
        /// </summary>
        public static bool IsInteractiveChildTarget(DependencyObject current)
        {
            while (current != null)
            {
                if (current is ButtonBase ||
                    current is Slider ||
                    current is ScrollBar ||
                    current is ScrollViewer ||
                    current is ScrollContentPresenter ||
                    current is ComboBox ||
                    current is ComboBoxItem ||
                    current is Thumb)
                {
                    return true;
                }

                if (current is WordCardView)
                {
                    return false;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (IsInteractiveChildTarget(e.OriginalSource as DependencyObject))
            {
                e.Handled = false;
                base.OnPreviewMouseLeftButtonDown(e);
                return;
            }

            if (_selectHandler != null)
            {
                _selectHandler.Invoke(this, e);
                e.Handled = true;
                SelectionRequested?.Invoke(this, EventArgs.Empty);
            }

            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnPreviewTouchDown(TouchEventArgs e)
        {
            if (IsInteractiveChildTarget(e.OriginalSource as DependencyObject))
            {
                e.Handled = false;
                base.OnPreviewTouchDown(e);
                return;
            }

            if (_touchSelectHandler != null)
            {
                _touchSelectHandler.Invoke(this, e);
                e.Handled = true;
                SelectionRequested?.Invoke(this, EventArgs.Empty);
            }

            base.OnPreviewTouchDown(e);
        }

        /// <summary>
        /// 当前显示的单词条目。
        /// </summary>
        public IWordEntry CurrentEntry => _currentEntry;

        /// <summary>
        /// 显示指定的单词条目。
        /// </summary>
        public void ShowEntry(IWordEntry entry)
        {
            _currentEntry = entry;

            if (entry == null)
            {
                ClearDisplay();
                return;
            }

            RefreshHeader(entry);
            RefreshDefinitionSection(entry);
            // 单词卡中不显示词组、例句、近义词
            // RefreshListSection(PhrasesSection, PhrasesPanel, entry.Phrases, Properties.Resources.WordCard_NoPhrases);
            // RefreshListSection(SentencesSection, SentencesPanel, entry.Sentences, Properties.Resources.WordCard_NoSentences);
            // RefreshListSection(SynonymsSection, SynonymsPanel, entry.Synonyms, Properties.Resources.WordCard_NoSynonyms);
        }

        private void ClearDisplay()
        {
            WordText.Text = string.Empty;
            PhoneticText.Text = string.Empty;
            DefinitionPanel.Children.Clear();
            PhrasesPanel.Children.Clear();
            SentencesPanel.Children.Clear();
            SynonymsPanel.Children.Clear();
            SetSectionVisibility(DefinitionSection, false);
            SetSectionVisibility(PhrasesSection, false);
            SetSectionVisibility(SentencesSection, false);
            SetSectionVisibility(SynonymsSection, false);
        }

        private void RefreshHeader(IWordEntry entry)
        {
            WordText.Text = entry.Word ?? string.Empty;
            PhoneticText.Text = string.IsNullOrWhiteSpace(entry.Phonetic)
                ? string.Empty
                : string.Format("[{0}]", entry.Phonetic);
        }

        private async void OnSpeakerButtonClick(object sender, RoutedEventArgs e)
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

        private void RefreshDefinitionSection(IWordEntry entry)
        {
            DefinitionPanel.Children.Clear();

            var lines = ParseDefinitionLines(entry);
            if (lines.Count == 0)
            {
                DefinitionPanel.Children.Add(CreateEmptyMessage(Properties.Resources.WordCard_NoDefinitions));
                SetSectionVisibility(DefinitionSection, true);
                return;
            }

            foreach (var line in lines)
            {
                DefinitionPanel.Children.Add(CreateDefinitionLineBlock(line));
            }

            SetSectionVisibility(DefinitionSection, true);
        }

        private IReadOnlyList<string> ParseDefinitionLines(IWordEntry entry)
        {
            var source = entry.Definition;
            if (string.IsNullOrWhiteSpace(source))
            {
                source = entry.Translation;
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                return new List<string>();
            }

            source = source.Replace("\\n", "\n");
            return source
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private TextBlock CreateDefinitionLineBlock(string line)
        {
            var textBlock = new TextBlock
            {
                FontSize = 18,
                LineHeight = 28,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                IsHitTestVisible = false
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.SystemControlForegroundBaseHighBrushKey);

            var match = PosPrefixRegex.Match(line);
            if (match.Success)
            {
                var run1 = new Run
                {
                    Text = match.Groups[1].Value,
                    FontWeight = FontWeights.Bold
                };
                run1.SetResourceReference(Run.ForegroundProperty, ThemeKeys.SystemControlForegroundAccentBrushKey);

                var run2 = new Run
                {
                    Text = match.Groups[2].Value
                };
                run2.SetResourceReference(Run.ForegroundProperty, ThemeKeys.SystemControlForegroundBaseHighBrushKey);

                textBlock.Inlines.Add(run1);
                textBlock.Inlines.Add(run2);
            }
            else
            {
                textBlock.Text = line;
            }

            return textBlock;
        }

        private void RefreshListSection(StackPanel section, StackPanel panel, IReadOnlyList<string> items, string emptyMessage)
        {
            panel.Children.Clear();

            var safeItems = items ?? new List<string>();
            if (safeItems.Count == 0)
            {
                panel.Children.Add(CreateEmptyMessage(emptyMessage));
                SetSectionVisibility(section, true);
                return;
            }

            foreach (var item in safeItems)
            {
                var tb = new TextBlock
                {
                    Text = item,
                    FontSize = 18,
                    LineHeight = 28,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                    IsHitTestVisible = false
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.SystemControlForegroundBaseHighBrushKey);
                panel.Children.Add(tb);
            }

            SetSectionVisibility(section, true);
        }

        private TextBlock CreateEmptyMessage(string message)
        {
            var tb = new TextBlock
            {
                Text = message,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
                IsHitTestVisible = false
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.SystemControlForegroundBaseMediumBrushKey);
            return tb;
        }

        private static void SetSectionVisibility(StackPanel section, bool visible)
        {
            section.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
