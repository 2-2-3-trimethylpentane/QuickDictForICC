using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// ECDICT 数据文件读取服务。
    /// 支持常见 CSV 格式（含 word、phonetic、definition、translation、pos、exchange 等列）。
    /// </summary>
    public class EcDictService : IDictionaryService
    {
        private readonly string _csvPath;
        private readonly Dictionary<string, WordEntry> _entries;

        /// <inheritdoc />
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// 初始化 <see cref="EcDictService"/>。
        /// </summary>
        /// <param name="csvPath">ECDICT CSV 文件路径；可为空，表示不加载。</param>
        public EcDictService(string csvPath)
        {
            _csvPath = csvPath;
            _entries = new Dictionary<string, WordEntry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public void Load()
        {
            Load(CancellationToken.None);
        }

        private void Load(CancellationToken cancellationToken)
        {
            IsLoaded = false;
            _entries.Clear();

            if (string.IsNullOrWhiteSpace(_csvPath))
                return;

            if (!File.Exists(_csvPath))
                return;

            try
            {
                using var stream = new FileStream(_csvPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                    return;

                var headers = ParseCsvLine(headerLine);
                int wordIndex = headers.IndexOf("word");
                int phoneticIndex = headers.IndexOf("phonetic");
                int definitionIndex = headers.IndexOf("definition");
                int translationIndex = headers.IndexOf("translation");
                int posIndex = headers.IndexOf("pos");
                int exchangeIndex = headers.IndexOf("exchange");
                int phraseIndex = headers.FindIndex(h => string.Equals(h, "phrase", StringComparison.OrdinalIgnoreCase));
                int sentenceIndex = headers.FindIndex(h => string.Equals(h, "sentence", StringComparison.OrdinalIgnoreCase));
                int synonymIndex = headers.FindIndex(h => string.Equals(h, "synonym", StringComparison.OrdinalIgnoreCase));

                // ECDICT 必须有 word 列
                if (wordIndex < 0)
                    return;

                int lineCount = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var fields = ParseCsvLine(line);
                    if (fields.Count <= wordIndex)
                        continue;

                    string word = fields[wordIndex];
                    if (string.IsNullOrWhiteSpace(word))
                        continue;

                    _entries[word] = new WordEntry
                    {
                        Word = word,
                        Phonetic = GetField(fields, phoneticIndex),
                        Definition = GetField(fields, definitionIndex),
                        Translation = GetField(fields, translationIndex),
                        Pos = GetField(fields, posIndex),
                        Exchange = GetField(fields, exchangeIndex),
                        Source = "ECDICT",
                        Phrases = SplitListField(GetField(fields, phraseIndex)),
                        Sentences = SplitListField(GetField(fields, sentenceIndex)),
                        Synonyms = SplitListField(GetField(fields, synonymIndex))
                    };

                    // 每处理 1000 行再检查一次取消，平衡响应与性能。
                    if (++lineCount % 1000 == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                }

                IsLoaded = _entries.Count > 0;
            }
            catch (OperationCanceledException)
            {
                IsLoaded = false;
                throw;
            }
            catch
            {
                IsLoaded = false;
            }
        }

        /// <inheritdoc />
        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Load(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);
        }

        /// <inheritdoc />
        public IWordEntry Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word) || !IsLoaded)
                return null;

            if (_entries.TryGetValue(word, out var entry))
                return entry;

            return null;
        }

        /// <summary>
        /// 根据前缀获取候选单词列表（大小写不敏感）。
        /// </summary>
        /// <param name="prefix">前缀。</param>
        /// <param name="maxCount">最大返回数量。</param>
        /// <returns>候选单词集合。</returns>
        public IEnumerable<string> GetSuggestions(string prefix, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(prefix) || maxCount <= 0 || !IsLoaded || _entries == null || _entries.Count == 0)
                return Enumerable.Empty<string>();

            return _entries.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(maxCount);
        }

        private static string GetField(IList<string> fields, int index)
        {
            if (index < 0 || index >= fields.Count)
                return null;

            string value = fields[index];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// 将 CSV 列表字段按常见分隔符拆分，过滤空条目。
        /// 支持换行符、分号、管道符以及字面量 "\n"。
        /// </summary>
        private static List<string> SplitListField(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return list;

            // 先处理字面量 "\n"，再按实际分隔符拆分。
            string normalized = value.Replace("\\n", "\n");
            char[] separators = new[] { '\n', ';', '|' };
            foreach (string part in normalized.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    list.Add(trimmed);
            }

            return list;
        }

        /// <summary>
        /// 简易 CSV 行解析，支持双引号包裹字段及转义引号。
        /// </summary>
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            fields.Add(sb.ToString());
            return fields;
        }
    }
}
