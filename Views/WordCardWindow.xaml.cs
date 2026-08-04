using QuickDictForICC.Services;
using System;
using System.Windows;

namespace QuickDictForICC.Views
{
    /// <summary>
    /// 单词卡独立弹窗。承载 <see cref="WordCardView"/>，在选择模式下展示查询结果。
    /// </summary>
    public partial class WordCardWindow : Window
    {
        public WordCardWindow(
            IWordEntry entry,
            TtsService ttsService,
            TtsOptions ttsOptions)
        {
            InitializeComponent();

            if (entry != null)
            {
                Title = string.Format("{0} - {1}", entry.Word, Properties.Resources.WordCard_Title);
            }

            CardView.SetTtsService(ttsService);
            CardView.SetTtsOptions(ttsOptions);
            CardView.ShowEntry(entry);

            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                CardView.Focus();
            }
            catch (Exception ex)
            {
                // 焦点设置失败不影响展示。
                System.Diagnostics.Debug.WriteLine($"WordCardWindow focus failed: {ex.Message}");
            }
        }
    }
}
