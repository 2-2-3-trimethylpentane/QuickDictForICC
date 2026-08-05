using Ink_Canvas.Plugins;
using QuickDictForICC.Services;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 英汉字典独立窗口。
    /// 内部承载 <see cref="DictionaryPopup"/> 作为实际 UI。
    /// </summary>
    public partial class DictionaryWindow : Window
    {
        private DictionaryPopup _popup;
        private ResultView _resultView;
        private WordCardService _wordCardService;
        private IPluginHost _host;
        private TtsService _ttsService;
        private TtsOptions _ttsOptions;
        private CanvasReflectionAdapter _canvasAdapter;
        private EventHandler _generateWordCardRequestedHandler;

        private const double DefaultCardWidth = 400;
        private const double DefaultCardHeight = 600;

        /// <summary>
        /// 初始化 <see cref="DictionaryWindow"/>。
        /// </summary>
        public DictionaryWindow(
            IDictionaryService dictionaryService,
            TtsService ttsService,
            Task loadingTask,
            PluginSettings settings,
            WordCardService wordCardService,
            IPluginHost host)
        {
            InitializeComponent();

            _wordCardService = wordCardService;
            _host = host;
            _ttsService = ttsService;
            _ttsOptions = settings?.ToTtsOptions();
            _canvasAdapter = new CanvasReflectionAdapter(host, Dispatcher);

            _resultView = new ResultView();
            _resultView.SetTtsService(ttsService);
            if (settings != null)
            {
                _resultView.SetTtsOptions(settings.ToTtsOptions());
            }

            _popup = new DictionaryPopup();
            _popup.SetDictionaryService(dictionaryService);
            _popup.SetTtsService(ttsService);
            _popup.SetResultView(_resultView);
            _popup.SetLoadingTask(loadingTask);
            _popup.SetTitleBarVisible(true);

            _generateWordCardRequestedHandler = async (s, e) => await OnGenerateWordCardRequestedAsync();
            _resultView.GenerateWordCardRequested += _generateWordCardRequestedHandler;

            MainContentHost.Content = _popup;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            Cleanup();
            base.OnClosing(e);
        }

        private async Task OnGenerateWordCardRequestedAsync()
        {
            var entry = _popup?.LastResult;
            if (entry == null)
            {
                MessageBox.Show(
                    Properties.Resources.Message_NoWordToGenerateCard,
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            object canvasInkService = _canvasAdapter.GetCanvasInkService();
            if (canvasInkService == null)
            {
                // 画布服务不可用时，直接以弹窗形式展示，保证功能可用。
                ShowWordCardPopup(entry);
                return;
            }

            object mainWindow = _canvasAdapter.GetMainWindow(canvasInkService);

            if (_canvasAdapter.IsSelectionMode(mainWindow))
            {
                // 选择模式下不占用画布，使用独立弹窗。
                ShowWordCardPopup(entry);
                return;
            }

            // 非选择模式：优先尝试插入可交互控件。
            if (TryInsertWordCardControl(mainWindow, entry))
                return;

            // 控件插入失败时回退到位图插入。
            try
            {
                BitmapSource bitmap = await _wordCardService.GenerateBitmapAsync(entry);
                if (bitmap == null)
                {
                    MessageBox.Show(
                        string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, "位图为空"),
                        Properties.Resources.MessageBox_Title_Notice,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!_canvasAdapter.TryInsertBitmap(canvasInkService, bitmap))
                {
                    MessageBox.Show(
                        string.Format(Properties.Resources.Message_WordCardInsertFailed_Format, string.Empty),
                        Properties.Resources.MessageBox_Title_Notice,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, ex.Message),
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 构造单词卡控件并尝试插入 ICC 画布。
        /// </summary>
        private bool TryInsertWordCardControl(object mainWindow, IWordEntry entry)
        {
            if (mainWindow == null)
                return false;

            var cardView = new WordCardView();
            cardView.SetTtsService(_ttsService);
            cardView.SetTtsOptions(_ttsOptions);
            cardView.ShowEntry(entry);

            return _canvasAdapter.TryInsertInteractiveControl(
                mainWindow,
                cardView,
                new Size(DefaultCardWidth, DefaultCardHeight));
        }

        /// <summary>
        /// 显示单词卡独立弹窗。
        /// </summary>
        private void ShowWordCardPopup(IWordEntry entry)
        {
            try
            {
                var window = new WordCardWindow(entry, _ttsService, _ttsOptions)
                {
                    Owner = this
                };
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format(Properties.Resources.Message_WordCardRenderFailed_Format, ex.Message),
                    Properties.Resources.MessageBox_Title_Notice,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void Cleanup()
        {
            if (_resultView != null && _generateWordCardRequestedHandler != null)
            {
                _resultView.GenerateWordCardRequested -= _generateWordCardRequestedHandler;
            }

            if (_popup != null)
            {
                _popup.SetResultView(null);
                _popup.SetDictionaryService(null);
                _popup.SetTtsService(null);
            }

            MainContentHost.Content = null;
            _popup = null;
            _resultView = null;
            _generateWordCardRequestedHandler = null;
        }
    }
}
