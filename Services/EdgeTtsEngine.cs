using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// 基于微软 Edge 在线 TTS 服务的引擎。
    /// 实现参考了 edge-tts 的 WebSocket 协议与 Sec-MS-GEC 签名逻辑。
    /// </summary>
    public class EdgeTtsEngine : ITtsEngine
    {
        private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
        private const string ChromiumFullVersion = "143.0.3650.75";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
        private static readonly long WinEpochSeconds = 11644473600L;
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public async Task<byte[]> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("朗读文本不能为空。", nameof(text));

            string voice = string.IsNullOrWhiteSpace(options.Voice)
                ? "en-US-AriaNeural"
                : options.Voice;
            string rate = NormalizeRate(options.Rate);

            using var webSocket = new ClientWebSocket();
            ConfigureHeaders(webSocket);

            string connectionId = Guid.NewGuid().ToString("N");
            string secMsGec = GenerateSecMsGec();
            string url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={connectionId}&Sec-MS-GEC={secMsGec}&Sec-MS-GEC-Version=1-{ChromiumFullVersion}";

            await webSocket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            await SendTextAsync(webSocket, BuildConfigMessage(), cancellationToken).ConfigureAwait(false);
            await SendTextAsync(webSocket, BuildSsmlMessage(text, voice, rate, connectionId), cancellationToken).ConfigureAwait(false);

            using var audioBuffer = new MemoryStream();
            bool turnEndReceived = false;

            while (webSocket.State == WebSocketState.Open && !turnEndReceived)
            {
                var (messageType, messageBytes) = await ReceiveMessageAsync(webSocket, cancellationToken).ConfigureAwait(false);

                if (messageType == WebSocketMessageType.Text)
                {
                    Dictionary<string, string> headers = ParseTextHeaders(messageBytes);
                    if (headers.TryGetValue("Path", out string path) && path == "turn.end")
                    {
                        turnEndReceived = true;
                        break;
                    }
                }
                else if (messageType == WebSocketMessageType.Binary)
                {
                    byte[] audioChunk = ExtractAudioFromBinaryMessage(messageBytes);
                    if (audioChunk != null && audioChunk.Length > 0)
                    {
                        await audioBuffer.WriteAsync(audioChunk, 0, audioChunk.Length, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (messageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }

            if (audioBuffer.Length == 0)
                throw new InvalidOperationException("未从 Edge TTS 服务收到音频数据，请检查网络连接、音色名称或服务可用性。");

            return audioBuffer.ToArray();
        }

        private static void ConfigureHeaders(ClientWebSocket webSocket)
        {
            var headers = new Dictionary<string, string>
            {
                { "Pragma", "no-cache" },
                { "Cache-Control", "no-cache" },
                { "Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold" },
                { "User-Agent", UserAgent },
                { "Accept-Encoding", "gzip, deflate, br, zstd" },
                { "Accept-Language", "en-US,en;q=0.9" },
                { "Cookie", $"muid={GenerateMuid()}" }
            };

            foreach (var header in headers)
            {
                try
                {
                    webSocket.Options.SetRequestHeader(header.Key, header.Value);
                }
                catch (ArgumentException)
                {
                    // 某些 .NET 版本会禁止设置特定请求头，跳过即可。
                }
            }
        }

        private static Task SendTextAsync(ClientWebSocket webSocket, string message, CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            return webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        private static string BuildConfigMessage()
        {
            return $"X-Timestamp:{GetEdgeTimestamp()}\r\n" +
                   "Content-Type:application/json; charset=utf-8\r\n" +
                   "Path:speech.config\r\n\r\n" +
                   "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        }

        private static string BuildSsmlMessage(string text, string voice, string rate, string requestId)
        {
            string escapedText = EscapeXml(text);
            string language = InferLanguage(voice);
            string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{language}'>" +
                          $"<voice name='{voice}'><prosody pitch='+0Hz' rate='{rate}' volume='+0%'>{escapedText}</prosody></voice></speak>";

            return $"X-RequestId:{requestId}\r\n" +
                   "Content-Type:application/ssml+xml\r\n" +
                   $"X-Timestamp:{GetEdgeTimestamp()}\r\n" +
                   "Path:ssml\r\n\r\n" +
                   ssml;
        }

        private static string InferLanguage(string voice)
        {
            if (string.IsNullOrWhiteSpace(voice))
                return "en-US";

            int firstDash = voice.IndexOf('-');
            if (firstDash < 0)
                return "en-US";

            int secondDash = voice.IndexOf('-', firstDash + 1);
            if (secondDash < 0)
                return voice;

            return voice.Substring(0, secondDash);
        }

        private static string NormalizeRate(string rate)
        {
            if (string.IsNullOrWhiteSpace(rate))
                return "+0%";

            rate = rate.Trim();
            if (!rate.EndsWith("%"))
                rate += "%";

            return rate;
        }

        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return System.Security.SecurityElement.Escape(text);
        }

        private static string GetEdgeTimestamp()
        {
            // Edge 服务期望 JavaScript 风格的 GMT 时间戳。
            return DateTime.UtcNow.ToString(
                "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string GenerateSecMsGec()
        {
            long ticks = (long)DateTime.UtcNow.Subtract(UnixEpoch).TotalSeconds + WinEpochSeconds;
            ticks -= ticks % 300;
            ticks *= 10_000_000L;

            string input = $"{ticks}{TrustedClientToken}";
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(input));
                return BytesToUpperHex(hash);
            }
        }

        private static string GenerateMuid()
        {
            byte[] bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BytesToUpperHex(bytes);
        }

        private static string BytesToUpperHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("X2"));
            return builder.ToString();
        }

        private static async Task<(WebSocketMessageType MessageType, byte[] Data)> ReceiveMessageAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            WebSocketMessageType messageType = WebSocketMessageType.Binary;
            byte[] buffer = new byte[8192];

            while (true)
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                messageType = result.MessageType;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, null);
                }

                await stream.WriteAsync(buffer, 0, result.Count, cancellationToken).ConfigureAwait(false);

                if (result.EndOfMessage)
                    break;
            }

            return (messageType, stream.ToArray());
        }

        private static Dictionary<string, string> ParseTextHeaders(byte[] data)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (data == null || data.Length == 0)
                return headers;

            string text = Encoding.UTF8.GetString(data);
            int separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headerText = separator >= 0 ? text.Substring(0, separator) : text;

            foreach (string line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
            }

            return headers;
        }

        private static byte[] ExtractAudioFromBinaryMessage(byte[] data)
        {
            if (data == null || data.Length < 2)
                return null;

            int headerLength = (data[0] << 8) | data[1];
            if (headerLength > data.Length || headerLength < 2)
                return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string headerText = Encoding.UTF8.GetString(data, 0, headerLength);
            foreach (string line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }
            }

            if (!headers.TryGetValue("Path", out string path) || path != "audio")
                return null;

            if (headers.TryGetValue("Content-Type", out string contentType) && contentType != "audio/mpeg")
                return null;

            int audioStart = headerLength + 2;
            if (audioStart >= data.Length)
                return null;

            byte[] audio = new byte[data.Length - audioStart];
            Buffer.BlockCopy(data, audioStart, audio, 0, audio.Length);
            return audio;
        }
    }
}
