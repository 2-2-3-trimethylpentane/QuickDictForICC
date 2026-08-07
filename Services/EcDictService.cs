using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickDictForICC.Services
{
    public class EcDictService : IDictionaryService
    {
        private readonly string _sourcePath;
        private readonly Dictionary<string, WordEntry> _fallbackEntries;
        private string _databasePath;
        private string _entrySelectSql;
        private bool _useDatabase;

        public bool IsLoaded { get; private set; }

        public EcDictService(string path)
        {
            _sourcePath = path;
            _fallbackEntries = new Dictionary<string, WordEntry>(StringComparer.OrdinalIgnoreCase);
        }

        public void Load()
        {
            Load(CancellationToken.None);
        }

        private void Load(CancellationToken cancellationToken)
        {
            IsLoaded = false;
            _useDatabase = false;
            _databasePath = null;
            _entrySelectSql = null;
            _fallbackEntries.Clear();

            if (string.IsNullOrWhiteSpace(_sourcePath) || !File.Exists(_sourcePath))
                return;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(_sourcePath);
                if (string.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase))
                {
                    if (OpenDatabase(_sourcePath, out string entrySelectSql))
                    {
                        _databasePath = _sourcePath;
                        _entrySelectSql = entrySelectSql;
                        _useDatabase = true;
                        IsLoaded = true;
                    }
                    return;
                }

                if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
                    return;

                string cachedDatabasePath = Path.ChangeExtension(_sourcePath, ".db");
                if (!File.Exists(cachedDatabasePath) || File.GetLastWriteTimeUtc(cachedDatabasePath) < File.GetLastWriteTimeUtc(_sourcePath))
                    ConvertCsvToDatabase(_sourcePath, cachedDatabasePath, cancellationToken);

                if (OpenDatabase(cachedDatabasePath, out string cachedEntrySelectSql))
                {
                    _databasePath = cachedDatabasePath;
                    _entrySelectSql = cachedEntrySelectSql;
                    _useDatabase = true;
                    IsLoaded = true;
                    return;
                }

                LoadCsvFallback(_sourcePath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                IsLoaded = false;
                throw;
            }
            catch
            {
                try
                {
                    if (string.Equals(Path.GetExtension(_sourcePath), ".csv", StringComparison.OrdinalIgnoreCase))
                        LoadCsvFallback(_sourcePath, cancellationToken);
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
        }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Load(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);
        }

        public IWordEntry Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word) || !IsLoaded)
                return null;

            if (!_useDatabase)
                return _fallbackEntries.TryGetValue(word, out WordEntry fallbackEntry) ? fallbackEntry : null;

            using var connection = OpenConnection(_databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = _entrySelectSql + " WHERE word = $word COLLATE NOCASE LIMIT 1";
            command.Parameters.AddWithValue("$word", word);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }

        public IEnumerable<string> GetSuggestions(string prefix, int maxCount)
        {
            if (string.IsNullOrWhiteSpace(prefix) || maxCount <= 0 || !IsLoaded)
                return Enumerable.Empty<string>();

            if (!_useDatabase)
            {
                return _fallbackEntries.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Take(maxCount)
                    .ToList();
            }

            var suggestions = new List<string>(maxCount);
            using var connection = OpenConnection(_databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT word FROM entries WHERE word LIKE $prefix || '%' COLLATE NOCASE ORDER BY word COLLATE NOCASE LIMIT $maxCount";
            command.Parameters.AddWithValue("$prefix", prefix);
            command.Parameters.AddWithValue("$maxCount", maxCount);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                suggestions.Add(reader.GetString(0));
            return suggestions;
        }

        private static bool OpenDatabase(string path, out string entrySelectSql)
        {
            entrySelectSql = null;
            if (!File.Exists(path))
                return false;

            using var connection = OpenConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'entries'";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                return false;

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            command.CommandText = "PRAGMA table_info(entries)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));

            string[] requiredColumns = { "word", "phonetic", "definition", "translation", "pos", "exchange" };
            if (requiredColumns.Any(column => !columns.Contains(column)))
                return false;

            entrySelectSql = "SELECT word, phonetic, definition, translation, pos, exchange, "
                + SelectColumnOrNull(columns, "phrase") + ", "
                + SelectColumnOrNull(columns, "sentence") + ", "
                + SelectColumnOrNull(columns, "synonym")
                + " FROM entries";
            return true;
        }

        private static string SelectColumnOrNull(ISet<string> columns, string column)
        {
            return columns.Contains(column) ? column : "NULL";
        }

        private static SqliteConnection OpenConnection(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        private static WordEntry ReadEntry(IDataRecord record)
        {
            return new WordEntry
            {
                Word = GetRecordValue(record, 0),
                Phonetic = GetRecordValue(record, 1),
                Definition = GetRecordValue(record, 2),
                Translation = GetRecordValue(record, 3),
                Pos = GetRecordValue(record, 4),
                Exchange = GetRecordValue(record, 5),
                Source = "ECDICT",
                Phrases = SplitListField(GetRecordValue(record, 6)),
                Sentences = SplitListField(GetRecordValue(record, 7)),
                Synonyms = SplitListField(GetRecordValue(record, 8))
            };
        }

        private static string GetRecordValue(IDataRecord record, int index)
        {
            return record.IsDBNull(index) ? null : record.GetString(index);
        }

        private static void ConvertCsvToDatabase(string csvPath, string databasePath, CancellationToken cancellationToken)
        {
            string temporaryPath = databasePath + ".tmp";
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            try
            {
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    ExecuteNonQuery(connection, transaction, "CREATE TABLE entries (word TEXT NOT NULL, phonetic TEXT, definition TEXT, translation TEXT, pos TEXT, exchange TEXT, phrase TEXT, sentence TEXT, synonym TEXT)");
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO entries (word, phonetic, definition, translation, pos, exchange, phrase, sentence, synonym) VALUES ($word, $phonetic, $definition, $translation, $pos, $exchange, $phrase, $sentence, $synonym)";
                    foreach (string parameter in new[] { "$word", "$phonetic", "$definition", "$translation", "$pos", "$exchange", "$phrase", "$sentence", "$synonym" })
                        insert.Parameters.Add(parameter, SqliteType.Text);

                    using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string headerLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(headerLine))
                        throw new InvalidDataException("ECDICT CSV header is empty.");

                    List<string> headers = ParseCsvLine(headerLine);
                    int wordIndex = FindHeader(headers, "word");
                    if (wordIndex < 0)
                        throw new InvalidDataException("ECDICT CSV does not contain a word column.");

                    int phoneticIndex = FindHeader(headers, "phonetic");
                    int definitionIndex = FindHeader(headers, "definition");
                    int translationIndex = FindHeader(headers, "translation");
                    int posIndex = FindHeader(headers, "pos");
                    int exchangeIndex = FindHeader(headers, "exchange");
                    int phraseIndex = FindHeader(headers, "phrase");
                    int sentenceIndex = FindHeader(headers, "sentence");
                    int synonymIndex = FindHeader(headers, "synonym");
                    int lineCount = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        List<string> fields = ParseCsvLine(line);
                        string word = GetField(fields, wordIndex);
                        if (string.IsNullOrWhiteSpace(word))
                            continue;

                        insert.Parameters["$word"].Value = word;
                        insert.Parameters["$phonetic"].Value = GetDbValue(fields, phoneticIndex);
                        insert.Parameters["$definition"].Value = GetDbValue(fields, definitionIndex);
                        insert.Parameters["$translation"].Value = GetDbValue(fields, translationIndex);
                        insert.Parameters["$pos"].Value = GetDbValue(fields, posIndex);
                        insert.Parameters["$exchange"].Value = GetDbValue(fields, exchangeIndex);
                        insert.Parameters["$phrase"].Value = GetDbValue(fields, phraseIndex);
                        insert.Parameters["$sentence"].Value = GetDbValue(fields, sentenceIndex);
                        insert.Parameters["$synonym"].Value = GetDbValue(fields, synonymIndex);
                        insert.ExecuteNonQuery();

                        if (++lineCount % 1000 == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                    }

                    ExecuteNonQuery(connection, transaction, "CREATE INDEX idx_entries_word_nocase ON entries(word COLLATE NOCASE)");
                    transaction.Commit();
                }

                File.Move(temporaryPath, databasePath, true);
            }
            catch
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                throw;
            }
        }

        private void LoadCsvFallback(string csvPath, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                return;

            List<string> headers = ParseCsvLine(headerLine);
            int wordIndex = FindHeader(headers, "word");
            if (wordIndex < 0)
                return;

            int phoneticIndex = FindHeader(headers, "phonetic");
            int definitionIndex = FindHeader(headers, "definition");
            int translationIndex = FindHeader(headers, "translation");
            int posIndex = FindHeader(headers, "pos");
            int exchangeIndex = FindHeader(headers, "exchange");
            int phraseIndex = FindHeader(headers, "phrase");
            int sentenceIndex = FindHeader(headers, "sentence");
            int synonymIndex = FindHeader(headers, "synonym");
            string line;
            int lineCount = 0;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                List<string> fields = ParseCsvLine(line);
                string word = GetField(fields, wordIndex);
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                _fallbackEntries[word] = new WordEntry
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

                if (++lineCount % 1000 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
            }

            IsLoaded = _fallbackEntries.Count > 0;
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static object GetDbValue(IList<string> fields, int index)
        {
            return string.IsNullOrEmpty(GetField(fields, index)) ? DBNull.Value : GetField(fields, index);
        }

        private static int FindHeader(IList<string> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string GetField(IList<string> fields, int index)
        {
            if (index < 0 || index >= fields.Count)
                return null;
            string value = fields[index];
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static List<string> SplitListField(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
                return list;

            string normalized = value.Replace("\\n", "\n");
            foreach (string part in normalized.Split(new[] { '\n', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    list.Add(trimmed);
            }
            return list;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var value = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char current = line[i];
                if (current == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (current == ',' && !inQuotes)
                {
                    fields.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(current);
                }
            }
            fields.Add(value.ToString());
            return fields;
        }
    }
}
