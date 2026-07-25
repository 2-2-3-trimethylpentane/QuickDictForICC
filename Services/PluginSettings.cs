using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// QuickDict 插件持久化设置数据模型。
    /// </summary>
    public class PluginSettings
    {
        /// <summary>
        /// ECDICT CSV 文件路径。
        /// </summary>
        public string EcDictPath { get; set; }

        /// <summary>
        /// MDict 词典文件路径（.mdx）。
        /// </summary>
        public string MDictPath { get; set; }

        /// <summary>
        /// MDict 资源包路径（.mdd，可选）。
        /// </summary>
        public string MDictResourcePath { get; set; }

        /// <summary>
        /// 当前使用的 TTS 引擎。
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TtsEngineType TtsEngine { get; set; } = TtsEngineType.Edge;

        /// <summary>
        /// Edge TTS 音色名称。
        /// </summary>
        public string EdgeVoice { get; set; } = "en-US-AriaNeural";

        /// <summary>
        /// Edge TTS 语速百分比（-50 到 +50）。
        /// </summary>
        public int EdgeRatePercent { get; set; } = 0;

        /// <summary>
        /// Piper 可执行文件路径（piper.exe）。
        /// </summary>
        public string PiperExecutablePath { get; set; }

        /// <summary>
        /// Piper 语音模型路径（.onnx）。
        /// </summary>
        public string PiperModelPath { get; set; }

        /// <summary>
        /// 将当前设置转换为 TTS 选项。
        /// </summary>
        public TtsOptions ToTtsOptions()
        {
            return new TtsOptions
            {
                Engine = TtsEngine,
                Voice = TtsEngine == TtsEngineType.Piper
                    ? Path.GetFileNameWithoutExtension(PiperModelPath ?? string.Empty)
                    : (EdgeVoice ?? "en-US-AriaNeural"),
                Rate = $"{EdgeRatePercent:+#;-#;+0}%",
                PiperExecutablePath = PiperExecutablePath,
                PiperModelPath = PiperModelPath
            };
        }
    }

    /// <summary>
    /// 插件设置读写管理器。
    /// </summary>
    public static class SettingsManager
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuickDictForICC");

        private static readonly string SettingsFilePath = Path.Combine(
            SettingsDirectory,
            "settings.json");

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// 设置文件完整路径。
        /// </summary>
        public static string FilePath => SettingsFilePath;

        /// <summary>
        /// 从本地文件加载设置；文件不存在或损坏时返回默认实例。
        /// </summary>
        public static PluginSettings Load()
        {
            if (!File.Exists(SettingsFilePath))
                return new PluginSettings();

            try
            {
                string json = File.ReadAllText(SettingsFilePath);
                if (string.IsNullOrWhiteSpace(json))
                    return new PluginSettings();

                return JsonSerializer.Deserialize<PluginSettings>(json, SerializerOptions) ?? new PluginSettings();
            }
            catch
            {
                return new PluginSettings();
            }
        }

        /// <summary>
        /// 保存设置到本地文件。
        /// </summary>
        public static void Save(PluginSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
    }
}
