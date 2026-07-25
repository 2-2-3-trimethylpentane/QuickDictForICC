namespace QuickDictForICC.Services
{
    /// <summary>
    /// TTS 合成选项。
    /// </summary>
    public class TtsOptions
    {
        /// <summary>
        /// 使用的 TTS 引擎。
        /// </summary>
        public TtsEngineType Engine { get; set; } = TtsEngineType.Edge;

        /// <summary>
        /// 音色名称。Edge 使用完整 Voice Name（如 en-US-AriaNeural），Piper 使用模型文件名（不含扩展名）。
        /// </summary>
        public string Voice { get; set; }

        /// <summary>
        /// 语速，Edge 格式如 "+0%"、"-20%"、"+50%"；Piper 会映射为 length_scale。
        /// </summary>
        public string Rate { get; set; } = "+0%";

        /// <summary>
        /// Piper 可执行文件路径（piper.exe）。
        /// </summary>
        public string PiperExecutablePath { get; set; }

        /// <summary>
        /// Piper 语音模型文件路径（.onnx）。
        /// </summary>
        public string PiperModelPath { get; set; }
    }
}
