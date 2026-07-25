namespace QuickDictForICC.Services
{
    /// <summary>
    /// 单词查询结果条目实现。
    /// </summary>
    public class WordEntry : IWordEntry
    {
        /// <summary>查询的单词。</summary>
        public string Word { get; set; }

        /// <summary>音标。</summary>
        public string Phonetic { get; set; }

        /// <summary>英文释义（ECDICT 原始定义）。</summary>
        public string Definition { get; set; }

        /// <summary>中文翻译。</summary>
        public string Translation { get; set; }

        /// <summary>词性，多个词性以分隔符连接。</summary>
        public string Pos { get; set; }

        /// <summary>时态、派生、变形等交换信息。</summary>
        public string Exchange { get; set; }

        /// <summary>MDict 返回的 HTML 释义（如有）。</summary>
        public string HtmlDefinition { get; set; }

        /// <summary>结果来源标识，如 "MDict"、"ECDICT"。</summary>
        public string Source { get; set; }
    }
}
