using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// TTS 引擎接口。
    /// </summary>
    public interface ITtsEngine
    {
        /// <summary>
        /// 将文本合成为音频字节。
        /// </summary>
        /// <param name="text">要朗读的文本。</param>
        /// <param name="options">TTS 选项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>音频文件字节（Edge 为 MP3，Piper 为 WAV）。</returns>
        Task<byte[]> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default);
    }
}
