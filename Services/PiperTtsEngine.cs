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
        public async Task<byte[]> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("朗读文本不能为空。", nameof(text));

            ValidateConfiguration(options);

            string arguments = BuildArguments(options);

            var startInfo = new ProcessStartInfo
            {
                FileName = options.PiperExecutablePath,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            var stdoutBuffer = new MemoryStream();
            var stderrBuffer = new StringBuilder();

            process.Start();

            Task stdoutTask = Task.Run(async () =>
            {
                await process.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, 81920, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            Task stderrTask = Task.Run(async () =>
            {
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                stderrBuffer.Append(stderr);
            }, cancellationToken);

            // Piper 按行读取输入，多行会被合成为多段音频；这里将文本整理为一行。
            string singleLineText = text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            await process.StandardInput.WriteLineAsync(singleLineText).ConfigureAwait(false);
            process.StandardInput.Close();

            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                stdoutTask,
                stderrTask).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string error = stderrBuffer.Length > 0
                    ? stderrBuffer.ToString()
                    : "（无错误输出）";
                throw new InvalidOperationException($"Piper 进程退出码 {process.ExitCode}：{error}");
            }

            byte[] wavBytes = stdoutBuffer.ToArray();
            if (wavBytes.Length == 0)
                throw new InvalidOperationException("Piper 未输出任何 WAV 音频数据。");

            return wavBytes;
        }

        private static void ValidateConfiguration(TtsOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.PiperExecutablePath))
                throw new InvalidOperationException("未配置 Piper 可执行文件路径（PiperExecutablePath）。请先下载 Piper 并填写路径。");

            if (string.IsNullOrWhiteSpace(options.PiperModelPath))
                throw new InvalidOperationException("未配置 Piper 语音模型路径（PiperModelPath）。请下载 .onnx 模型并填写路径。");

            if (!File.Exists(options.PiperExecutablePath))
                throw new FileNotFoundException(
                    $"找不到 Piper 可执行文件：{options.PiperExecutablePath}。请从 https://github.com/rhasspy/piper/releases 下载 Windows 版 piper 并解压。",
                    options.PiperExecutablePath);

            if (!File.Exists(options.PiperModelPath))
                throw new FileNotFoundException(
                    $"找不到 Piper 语音模型：{options.PiperModelPath}。请从 Piper 模型库下载对应的 .onnx 文件（通常还需同名的 .onnx.json 配置文件）。",
                    options.PiperModelPath);
        }

        private static string BuildArguments(TtsOptions options)
        {
            var builder = new StringBuilder();
            builder.Append($"--model \"{options.PiperModelPath}\" ");
            builder.Append("--output_file -");

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
