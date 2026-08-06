using System.Windows.Media.Imaging;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 画布墨迹服务接口，由 ICC 宿主提供，用于向白板插入位图等内容。
    /// </summary>
    public interface ICanvasInkService
    {
        bool InsertBitmap(BitmapSource bitmap);
    }
}
