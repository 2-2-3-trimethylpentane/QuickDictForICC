using QuickDictForICC.Services;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 英汉字典独立窗口。
    /// 内部承载 <see cref="DictionaryPopup"/> 作为实际 UI。
    /// </summary>
    public partial class DictionaryWindow : Window
    {
        private DictionaryPopup _popup;

        /// <summary>
        /// 初始化 <see cref="DictionaryWindow"/>。
        /// </summary>
        public DictionaryWindow(
            IDictionaryService dictionaryService,
            TtsService ttsService,
            Task loadingTask,
            PluginSettings settings)
        {
            InitializeComponent();

            var resultView = new ResultView();
            resultView.SetTtsService(ttsService);
            if (settings != null)
            {
                resultView.SetTtsOptions(settings.ToTtsOptions());
            }

            _popup = new DictionaryPopup();
            _popup.SetDictionaryService(dictionaryService);
            _popup.SetTtsService(ttsService);
            _popup.SetResultView(resultView);
            _popup.SetLoadingTask(loadingTask);
            _popup.SetTitleBarVisible(true);

            MainContentHost.Content = _popup;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            Cleanup();
            base.OnClosing(e);
        }

        private void Cleanup()
        {
            if (_popup != null)
            {
                _popup.SetResultView(null);
                _popup.SetDictionaryService(null);
                _popup.SetTtsService(null);
            }

            MainContentHost.Content = null;
            _popup = null;
        }
    }
}
