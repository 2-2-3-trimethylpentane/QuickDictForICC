using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 基于本地 Piper 可执行文件的 TTS 引擎。
    /// 需要用户自行下载 piper.exe 与对应的 .onnx 语音模型。
    /// </summary>
    public class PiperTtsEngine : ITtsEngine
    {
        private readonly Action<string> _log;

        public PiperTtsEngine(Action<string> log = null)
        {
            _log = log;
        }

        public async Task<byte[]> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("朗读文本不能为空。", nameof(text));

            ValidateConfiguration(options);

            string outputPath = Path.Combine(
                Path.GetTempPath(),
                "QuickDictForICC",
                "Piper",
                Guid.NewGuid().ToString("N") + ".wav");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            string arguments = BuildArguments(options, outputPath);

            string singleLineText = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

            Log($"[PiperTtsEngine] 准备启动: exe=\"{options.PiperExecutablePath}\", model=\"{options.PiperModelPath}\", output=\"{outputPath}\", args={arguments}, 文本长度={singleLineText.Length}, 文本预览=\"{Truncate(singleLineText, 80)}\"");

            var startInfo = new ProcessStartInfo
            {
                FileName = options.PiperExecutablePath,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            var stderrBuffer = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                try
                {
                    process.Start();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
                {
                    Log($"[PiperTtsEngine] 进程启动失败: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                Log($"[PiperTtsEngine] 进程已启动 (PID={process.Id})");

                Task stderrTask = Task.Run(async () =>
                {
                    string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    stderrBuffer.Append(stderr);
                }, cancellationToken);

                await process.StandardInput.WriteLineAsync(singleLineText).ConfigureAwait(false);
                process.StandardInput.Close();

                await Task.WhenAll(
                    process.WaitForExitAsync(cancellationToken),
                    stderrTask).ConfigureAwait(false);

                stopwatch.Stop();

                string stderrText = stderrBuffer.ToString();
                Log($"[PiperTtsEngine] 进程结束: ExitCode={process.ExitCode}, 耗时={stopwatch.ElapsedMilliseconds}ms, outputExists={File.Exists(outputPath)}, outputBytes={(File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0)}, stderr长度={stderrText.Length}");

                if (process.ExitCode != 0)
                {
                    string error = stderrText.Length > 0
                        ? stderrText
                        : "（无错误输出）";
                    string fullError = $"Piper 进程退出码 {process.ExitCode}：{error}";
                    Log($"[PiperTtsEngine] 合成失败: {fullError}");
                    throw new InvalidOperationException(fullError);
                }

                if (!File.Exists(outputPath))
                {
                    string detail = $"Piper 未生成 WAV 文件。stderr: {(stderrText.Length > 0 ? stderrText : "（无输出）")}";
                    Log($"[PiperTtsEngine] 合成失败: {detail}");
                    throw new InvalidOperationException(detail);
                }

                byte[] wavBytes = ReadDeclaredWavBytes(outputPath);
                Log($"[PiperTtsEngine] 合成成功: {wavBytes.Length} 字节有效 WAV 音频");
                return wavBytes;
            }
            finally
            {
                TryDelete(outputPath);
            }
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        private void Log(string message)
        {
            _log?.Invoke(message);
        }

        private void ValidateConfiguration(TtsOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.PiperExecutablePath))
            {
                Log("[PiperTtsEngine] 配置校验失败: PiperExecutablePath 为空");
                throw new InvalidOperationException("未配置 Piper 可执行文件路径（PiperExecutablePath）。请先下载 Piper 并填写路径。");
            }

            if (string.IsNullOrWhiteSpace(options.PiperModelPath))
            {
                Log("[PiperTtsEngine] 配置校验失败: PiperModelPath 为空");
                throw new InvalidOperationException("未配置 Piper 语音模型路径（PiperModelPath）。请下载 .onnx 模型并填写路径。");
            }

            if (!File.Exists(options.PiperExecutablePath))
            {
                Log($"[PiperTtsEngine] 配置校验失败: 找不到可执行文件 \"{options.PiperExecutablePath}\"");
                throw new FileNotFoundException(
                    $"找不到 Piper 可执行文件：{options.PiperExecutablePath}。请从 https://github.com/rhasspy/piper/releases 下载 Windows 版 piper 并解压。",
                    options.PiperExecutablePath);
            }

            if (!File.Exists(options.PiperModelPath))
            {
                Log($"[PiperTtsEngine] 配置校验失败: 找不到模型文件 \"{options.PiperModelPath}\"");
                throw new FileNotFoundException(
                    $"找不到 Piper 语音模型：{options.PiperModelPath}。请从 Piper 模型库下载对应的 .onnx 文件（通常还需同名的 .onnx.json 配置文件）。",
                    options.PiperModelPath);
            }

            // 检查模型配置文件（.onnx.json）是否存在，缺失时 Piper 可能报错。
            string modelConfigPath = options.PiperModelPath + ".json";
            if (!File.Exists(modelConfigPath))
            {
                Log($"[PiperTtsEngine] 警告: 模型配置文件缺失 \"{modelConfigPath}\"，Piper 可能无法正常加载模型");
            }
        }

        private static byte[] ReadDeclaredWavBytes(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length < 44 ||
                Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            {
                throw new InvalidOperationException("Piper 生成的文件不是有效的 RIFF/WAV 音频。");
            }

            int declaredLength = checked((int)ReadUInt32LittleEndian(bytes, 4) + 8);
            if (declaredLength < 44 || declaredLength > bytes.Length)
            {
                throw new InvalidOperationException(
                    $"Piper WAV 文件长度无效：声明 {declaredLength} 字节，实际 {bytes.Length} 字节。");
            }

            if (declaredLength == bytes.Length)
                return bytes;

            var declaredBytes = new byte[declaredLength];
            Buffer.BlockCopy(bytes, 0, declaredBytes, 0, declaredLength);
            return declaredBytes;
        }

        private static uint ReadUInt32LittleEndian(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                bytes[offset + 1] << 8 |
                bytes[offset + 2] << 16 |
                bytes[offset + 3] << 24);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static string BuildArguments(TtsOptions options, string outputPath)
        {
            var builder = new StringBuilder();
            builder.Append($"--model \"{options.PiperModelPath}\" ");
            builder.Append($"--output_file \"{outputPath}\"");

            double? lengthScale = ParseRateToLengthScale(options.Rate);
            if (lengthScale.HasValue)
            {
                builder.Append($" --length_scale {lengthScale.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            return builder.ToString();
        }

        private static double? ParseRateToLengthScale(string rate)
        {
            if (string.IsNullOrWhiteSpace(rate))
                return null;

            string trimmed = rate.Trim().TrimEnd('%', ' ');
            if (!double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out double percent))
                return null;

            if (Math.Abs(percent) < 0.001)
                return null;

            // Edge 语速百分比：+50% 表示 1.5 倍速 -> length_scale = 1/1.5；
            // -20% 表示 0.8 倍速 -> length_scale = 1/0.8。
            return 1.0 / (1.0 + percent / 100.0);
        }
    }
}
