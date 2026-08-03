using QuickDictForICC.Properties;
using QuickDictForICC.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// QuickDict 英语单词查询弹窗。
    /// 包含搜索、候选词条、结果占位以及虚拟键盘。
    /// </summary>
    public partial class DictionaryPopup : UserControl
    {
        private IDictionaryService _dictionaryService;
        private TtsService _ttsService;
        private ResultView _resultView;
        private Task _loadingTask;
        private DispatcherTimer _loadingTimer;

        /// <summary>
        /// 最近一次查询结果。未命中时为 <c>null</c>。
        /// </summary>
        public IWordEntry LastResult { get; private set; }

        /// <summary>
        /// 查询结果就绪时触发的事件。
        /// </summary>
        public event EventHandler<IWordEntry> ResultReady;

        /// <summary>
        /// 弹窗关闭请求事件。
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// 初始化 <see cref="DictionaryPopup"/>。
        /// </summary>
        public DictionaryPopup()
        {
            InitializeComponent();

            ClearButton.Click += OnClearButtonClick;
            CloseButton.Click += OnCloseButtonClick;
            GenerateCardButton.Click += (s, e) => _resultView?.GenerateWordCard();

            SearchTextBox.KeyDown += OnSearchTextBoxKeyDown;
            SearchTextBox.TextChanged += OnSearchTextBoxTextChanged;
        }

        /// <summary>
        /// 注入词典服务。
        /// </summary>
        /// <param name="service">词典服务实例。</param>
        public void SetDictionaryService(IDictionaryService service)
        {
            _dictionaryService = service;
        }

        /// <summary>
        /// 设置词典后台加载任务，加载完成前显示覆盖层并禁用搜索。
        /// </summary>
        /// <param name="loadingTask">后台加载任务；可为 <c>null</c>。</param>
        public void SetLoadingTask(Task loadingTask)
        {
            _loadingTask = loadingTask;
            StartLoadingMonitor();
        }

        private void StartLoadingMonitor()
        {
            StopLoadingMonitor();

            if (_loadingTask == null || _loadingTask.IsCompleted)
            {
                UpdateLoadingState(false);
                return;
            }

            UpdateLoadingState(true);

            _loadingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };

            _loadingTimer.Tick += (s, e) =>
            {
                if (_loadingTask == null || _loadingTask.IsCompleted)
                {
                    StopLoadingMonitor();
                    UpdateLoadingState(false);
                }
            };

            _loadingTimer.Start();
        }

        private void StopLoadingMonitor()
        {
            _loadingTimer?.Stop();
            _loadingTimer = null;
        }

        private void UpdateLoadingState(bool isLoading)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            KeyboardSearchButton.IsEnabled = !isLoading;
            SearchTextBox.IsEnabled = !isLoading;

            if (!isLoading)
            {
                LoadingDetailText.Text = Properties.Resources.DictionaryPopup_LoadingDetail;
            }
        }

        /// <summary>
        /// 注入 TTS 服务。
        /// </summary>
        /// <param name="service">TTS 服务实例。</param>
        public void SetTtsService(TtsService service)
        {
            _ttsService = service;
            _resultView?.SetTtsService(service);
        }

        /// <summary>
        /// 将结果视图嵌入到结果占位区。
        /// </summary>
        /// <param name="resultView">结果视图实例。</param>
        public void SetResultView(ResultView resultView)
        {
            _resultView = resultView;
            if (_resultView != null)
            {
                ResultContentHost.Content = _resultView;
                if (_ttsService != null)
                    _resultView.SetTtsService(_ttsService);
            }
            else
            {
                ShowEmptyResult();
            }
        }

        /// <summary>
        /// 设置内置标题栏是否可见。
        /// </summary>
        /// <param name="visible">是否显示标题栏。</param>
        public void SetTitleBarVisible(bool visible)
        {
            TitleBarGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 执行查询。
        /// </summary>
        public void Search()
        {
            string input = SearchTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                LastResult = null;
                ShowEmptyResult();
                ResultReady?.Invoke(this, null);
                return;
            }

            if (_dictionaryService == null)
            {
                ShowMessage(Properties.Resources.Message_DictionaryServiceNotReady);
                LastResult = null;
                ResultReady?.Invoke(this, null);
                return;
            }

            if (_loadingTask != null && !_loadingTask.IsCompleted)
            {
                ShowMessage(Properties.Resources.Message_DictionaryLoading);
                LastResult = null;
                ResultReady?.Invoke(this, null);
                return;
            }

            if (!_dictionaryService.IsLoaded)
            {
                ShowMessage(Properties.Resources.Message_DictionaryNotConfigured);
                LastResult = null;
                ResultReady?.Invoke(this, null);
                return;
            }

            IWordEntry result = _dictionaryService.Lookup(input);
            LastResult = result;

            if (result != null)
            {
                ShowResult(result);
            }
            else
            {
                ShowMessage(string.Format(Properties.Resources.Message_WordNotFound_Format, input));
            }

            GenerateCardButton.IsEnabled = LastResult != null;
            ResultReady?.Invoke(this, result);
            RefreshSuggestions(input);
        }

        private void ShowResult(IWordEntry entry)
        {
            if (_resultView != null)
            {
                ResultContentHost.Content = _resultView;
                _resultView.ShowResult(entry);
            }
            else
            {
                ShowFallbackResult(entry);
            }
        }

        private void ShowFallbackResult(IWordEntry entry)
        {
            if (entry == null)
            {
                ShowEmptyResult();
                return;
            }

            var panel = new StackPanel();

            if (!string.IsNullOrWhiteSpace(entry.Word))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.Word,
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.Black,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            if (!string.IsNullOrWhiteSpace(entry.Phonetic))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"/ {entry.Phonetic} /",
                    FontSize = 14,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            if (!string.IsNullOrWhiteSpace(entry.Translation))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.Translation,
                    FontSize = 15,
                    Foreground = System.Windows.Media.Brushes.DarkSlateGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            if (!string.IsNullOrWhiteSpace(entry.Definition))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.Definition,
                    FontSize = 13,
                    Foreground = System.Windows.Media.Brushes.DimGray,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            ResultContentHost.Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        /// <summary>
        /// 设置候选词列表。
        /// </summary>
        /// <param name="suggestions">候选词集合。</param>
        public void SetSuggestions(IEnumerable<string> suggestions)
        {
            SuggestionsPanel.Children.Clear();
            if (suggestions == null)
                return;

            foreach (string word in suggestions)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                string suggestion = word.Trim();
                var button = new Button
                {
                    Content = suggestion,
                    Style = (Style)FindResource("SuggestionButton"),
                    Tag = suggestion
                };
                button.Click += OnSuggestionButtonClick;
                SuggestionsPanel.Children.Add(button);
            }
        }

        /// <summary>
        /// 清空候选词。
        /// </summary>
        public void ClearSuggestions()
        {
            SuggestionsPanel.Children.Clear();
        }

        private void OnSearchTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Search();
                e.Handled = true;
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            Search();
        }

        private void OnClearButtonClick(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Clear();
            SearchTextBox.Focus();
            ClearSuggestions();
            LastResult = null;
            ShowEmptyResult();
            ResultReady?.Invoke(this, null);
        }

        private void OnCloseButtonClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void KeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string character)
            {
                AppendText(character);
            }
        }

        private void BackspaceButton_Click(object sender, RoutedEventArgs e)
        {
            Backspace();
        }

        private void SpaceButton_Click(object sender, RoutedEventArgs e)
        {
            AppendText(" ");
        }

        private void OnSuggestionButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string word)
            {
                SearchTextBox.Text = word;
                SearchTextBox.CaretIndex = word.Length;
                SearchTextBox.Focus();
                Search();
            }
        }

        private void SuggestionsScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            SuggestionsScrollViewer?.LineLeft();
        }

        private void SuggestionsScrollRight_Click(object sender, RoutedEventArgs e)
        {
            SuggestionsScrollViewer?.LineRight();
        }

        private void OnSearchTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            string text = SearchTextBox.Text?.Trim() ?? string.Empty;

            if (text.Length < 2 || _dictionaryService == null || !_dictionaryService.IsLoaded)
            {
                ClearSuggestions();
                return;
            }

            var suggestions = _dictionaryService.GetSuggestions(text, 10);
            SetSuggestions(suggestions);
        }

        private void AppendText(string text)
        {
            int caretIndex = SearchTextBox.CaretIndex;
            string current = SearchTextBox.Text ?? string.Empty;

            if (caretIndex < 0 || caretIndex > current.Length)
                caretIndex = current.Length;

            SearchTextBox.Text = current.Insert(caretIndex, text);
            SearchTextBox.CaretIndex = caretIndex + text.Length;
            SearchTextBox.Focus();
        }

        private void Backspace()
        {
            string current = SearchTextBox.Text;
            if (string.IsNullOrEmpty(current))
                return;

            int caretIndex = SearchTextBox.CaretIndex;
            if (caretIndex <= 0)
                caretIndex = current.Length;

            if (caretIndex > current.Length)
                caretIndex = current.Length;

            SearchTextBox.Text = current.Remove(caretIndex - 1, 1);
            SearchTextBox.CaretIndex = caretIndex - 1;
            SearchTextBox.Focus();
        }

        private void ShowEmptyResult()
        {
            GenerateCardButton.IsEnabled = false;

            if (_resultView != null)
            {
                ResultContentHost.Content = _resultView;
                _resultView.ShowResult(null);
                return;
            }

            ResultContentHost.Content = new TextBlock
            {
                Text = Properties.Resources.DictionaryPopup_EmptyHint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 14
            };
        }

        private void ShowMessage(string message)
        {
            GenerateCardButton.IsEnabled = false;

            ResultContentHost.Content = new TextBlock
            {
                Text = message,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private void RefreshSuggestions(string input)
        {
            // 当前仅展示查询词本身作为占位，后续可接入词典的联想接口。
            ClearSuggestions();

            if (string.IsNullOrWhiteSpace(input))
                return;

            // 示例：将当前查询词作为唯一候选按钮展示。
            var button = new Button
            {
                Content = input,
                Style = (Style)FindResource("SuggestionButton"),
                Tag = input
            };
            button.Click += OnSuggestionButtonClick;
            SuggestionsPanel.Children.Add(button);
        }
    }
}
