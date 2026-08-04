using System.Windows.Media.Imaging;

namespace Ink_Canvas.Plugins
{
    /// <summary>
    /// 画布墨迹服务接口，由 ICC 宿主提供，用于向白板插入位图等内容。
    /// </summary>
    public interface ICanvasInkService
    {
        /// <summary>
        /// 将位图插入到当前画布中。
        /// </summary>
        /// <param name="bitmap">要插入的位图。</param>
        /// <returns>是否插入成功。</returns>
        bool InsertBitmap(BitmapSource bitmap);
    }
}
