using NAudio.Wave;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// TTS 服务：根据选项选择 Edge 或 Piper 引擎合成音频，
    /// 缓存结果到本地临时目录，并使用 NAudio 播放。
    /// </summary>
    public class TtsService : IDisposable
    {
        private readonly EdgeTtsEngine _edgeEngine;
        private readonly PiperTtsEngine _piperEngine;
        private readonly string _cacheDirectory;
        private readonly SemaphoreSlim _playbackSemaphore;
        private readonly Action<string> _log;

        private WaveOutEvent _currentWaveOut;
        private CancellationTokenSource _currentPlaybackCts;

        public TtsService(Action<string> log = null)
        {
            _log = log;
            _edgeEngine = new EdgeTtsEngine();
            _piperEngine = new PiperTtsEngine(log);
            _playbackSemaphore = new SemaphoreSlim(1, 1);

            try
            {
                _cacheDirectory = Path.Combine(Path.GetTempPath(), "QuickDictForICC", "TtsCache");
                Directory.CreateDirectory(_cacheDirectory);
            }
            catch
            {
                // 缓存目录创建失败不是致命错误；后续缓存写入会跳过。
                _cacheDirectory = null;
            }
        }

        /// <summary>
        /// 合成音频并返回字节（带缓存）。
        /// </summary>
        public async Task<byte[]> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            string cacheKey = ComputeCacheKey(text, options);
            string extension = options.Engine == TtsEngineType.Piper ? ".wav" : ".mp3";
            string cachePath = _cacheDirectory != null
                ? Path.Combine(_cacheDirectory, cacheKey + extension)
                : null;

            if (cachePath != null && File.Exists(cachePath))
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                if (cached.Length > 0)
                {
                    _log?.Invoke($"[TtsService] 使用缓存音频: engine={options.Engine}, bytes={cached.Length}");
                    return cached;
                }
            }

            _log?.Invoke($"[TtsService] 缓存未命中，开始合成: engine={options.Engine}");
            ITtsEngine engine = GetEngine(options.Engine);
            byte[] audio;
            try
            {
                audio = await engine.SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (options.Engine == TtsEngineType.Piper)
            {
                _log?.Invoke($"[TtsService] Piper 合成异常: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[TtsService] {options.Engine} 合成异常: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            if (cachePath != null)
            {
                try
                {
                    await File.WriteAllBytesAsync(cachePath, audio, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 缓存写入失败不影响主流程。
                }
            }

            return audio;
        }

        /// <summary>
        /// 合成音频并使用 NAudio 播放。
        /// </summary>
        public async Task SpeakAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            byte[] audio = await SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false);
            await PlayAsync(audio, options.Engine, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 停止当前播放。
        /// </summary>
        public void Stop()
        {
            _currentPlaybackCts?.Cancel();
            try
            {
                _currentWaveOut?.Stop();
            }
            catch
            {
                // 停止操作尽力而为。
            }
        }

        public void Dispose()
        {
            try
            {
                Stop();
                _currentWaveOut?.Dispose();
                _currentWaveOut = null;
                _currentPlaybackCts?.Dispose();
                _currentPlaybackCts = null;
                _playbackSemaphore?.Dispose();
            }
            catch
            {
                // 尽力清理；NAudio 等依赖程序集可能在卸载后不可用。
            }
        }

        private ITtsEngine GetEngine(TtsEngineType engineType)
        {
            return engineType == TtsEngineType.Piper ? (ITtsEngine)_piperEngine : _edgeEngine;
        }

        private async Task PlayAsync(byte[] audio, TtsEngineType engineType, CancellationToken cancellationToken)
        {
            await _playbackSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StopCurrentPlayback();

                _currentPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationTokenSource cts = _currentPlaybackCts;

                using var stream = new MemoryStream(audio, writable: false);
                using WaveStream reader = engineType == TtsEngineType.Piper
                    ? (WaveStream)new WaveFileReader(stream)
                    : new Mp3FileReader(stream);

                using var waveOut = new WaveOutEvent();
                _currentWaveOut = waveOut;
                waveOut.Init(reader);
                waveOut.Play();

                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    if (cts.IsCancellationRequested)
                    {
                        waveOut.Stop();
                        break;
                    }

                    try
                    {
                        await Task.Delay(50, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        waveOut.Stop();
                        break;
                    }
                }
            }
            finally
            {
                _currentWaveOut = null;
                _currentPlaybackCts?.Dispose();
                _currentPlaybackCts = null;
                _playbackSemaphore.Release();
            }
        }

        private void StopCurrentPlayback()
        {
            _currentPlaybackCts?.Cancel();
            try
            {
                _currentWaveOut?.Stop();
            }
            catch
            {
                // ignored
            }

            _currentWaveOut?.Dispose();
            _currentWaveOut = null;
            _currentPlaybackCts?.Dispose();
            _currentPlaybackCts = null;
        }

        private static string ComputeCacheKey(string text, TtsOptions options)
        {
            const string cacheFormatVersion = "piper-file-output-v2";
            string input = options.Engine == TtsEngineType.Piper
                ? $"{cacheFormatVersion}|{options.Engine}|{options.Voice}|{options.Rate}|{options.PiperExecutablePath}|{options.PiperModelPath}|{text}"
                : $"edge-v1|{options.Engine}|{options.Voice}|{options.Rate}|{text}";
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BytesToHex(hash);
            }
        }

        private static string BytesToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
