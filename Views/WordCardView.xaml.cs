using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 单词卡视图。
    /// </summary>
    public partial class WordCardView : UserControl
    {
        private IWordEntry _currentEntry;
        private static readonly Regex PosPrefixRegex = new Regex(
            @"^([a-zA-Z]+\.\s*)(.*)$",
            RegexOptions.Compiled);

        public WordCardView()
        {
            InitializeComponent();
        }

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
            RefreshDefinitionTab(entry);
            RefreshListTab(PhrasesPanel, entry.Phrases, Properties.Resources.WordCard_NoPhrases);
            RefreshListTab(SentencesPanel, entry.Sentences, Properties.Resources.WordCard_NoSentences);
            RefreshListTab(SynonymsPanel, entry.Synonyms, Properties.Resources.WordCard_NoSynonyms);
        }

        private void ClearDisplay()
        {
            WordText.Text = string.Empty;
            PhoneticText.Text = string.Empty;
            DefinitionPanel.Children.Clear();
            PhrasesPanel.Children.Clear();
            SentencesPanel.Children.Clear();
            SynonymsPanel.Children.Clear();
        }

        private void RefreshHeader(IWordEntry entry)
        {
            WordText.Text = entry.Word ?? string.Empty;
            PhoneticText.Text = string.IsNullOrWhiteSpace(entry.Phonetic)
                ? string.Empty
                : string.Format("[{0}]", entry.Phonetic);
        }

        private void RefreshDefinitionTab(IWordEntry entry)
        {
            DefinitionPanel.Children.Clear();

            var lines = ParseDefinitionLines(entry);
            if (lines.Count == 0)
            {
                DefinitionPanel.Children.Add(CreateEmptyMessage(Properties.Resources.WordCard_NoDefinitions));
                return;
            }

            foreach (var line in lines)
            {
                DefinitionPanel.Children.Add(CreateDefinitionLineBlock(line));
            }
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
                Foreground = new SolidColorBrush(Colors.Black),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var match = PosPrefixRegex.Match(line);
            if (match.Success)
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = match.Groups[1].Value,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00A86B"))
                });
                textBlock.Inlines.Add(new Run
                {
                    Text = match.Groups[2].Value,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"))
                });
            }
            else
            {
                textBlock.Text = line;
                textBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }

            return textBlock;
        }

        private void RefreshListTab(StackPanel panel, IReadOnlyList<string> items, string emptyMessage)
        {
            panel.Children.Clear();

            var safeItems = items ?? new List<string>();
            if (safeItems.Count == 0)
            {
                panel.Children.Add(CreateEmptyMessage(emptyMessage));
                return;
            }

            foreach (var item in safeItems)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = item,
                    FontSize = 18,
                    LineHeight = 28,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333")),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }

        private TextBlock CreateEmptyMessage(string message)
        {
            return new TextBlock
            {
                Text = message,
                FontSize = 16,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#999999")),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            };
        }
    }
}
