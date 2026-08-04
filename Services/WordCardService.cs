using QuickDictForICC.Views;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 单词卡位图渲染服务：将 <see cref="WordCardView"/> 渲染为 <see cref="BitmapSource"/>。
    /// </summary>
    public class WordCardService
    {
        private const int CardWidth = 400;
        private const int CardHeight = 600;
        private const double Dpi = 96.0;

        /// <summary>
        /// 生成指定单词条目的位图。
        /// </summary>
        public async Task<BitmapSource> GenerateBitmapAsync(IWordEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            try
            {
                return await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
                {
                    var view = new WordCardView();
                    view.ShowEntry(entry);

                    view.Measure(new Size(CardWidth, CardHeight));
                    view.Arrange(new Rect(0, 0, CardWidth, CardHeight));
                    view.UpdateLayout();

                    var bitmap = new RenderTargetBitmap(
                        CardWidth,
                        CardHeight,
                        Dpi,
                        Dpi,
                        PixelFormats.Pbgra32);

                    bitmap.Render(view);
                    bitmap.Freeze();

                    return bitmap;
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to generate word card bitmap for '{entry.Word}'.",
                    ex);
            }
        }
    }
}
