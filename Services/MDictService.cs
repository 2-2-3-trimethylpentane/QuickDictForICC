using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace QuickDictForICC.Services
{
    /// <summary>
    /// MDict 词典读取服务（.mdx + 可选 .mdd）。
    /// 当前实现为最小化读取器：加载 mdx 关键词索引并返回 HTML 释义字符串。
    /// </summary>
    public class MDictService : IDictionaryService
    {
        private readonly string _mdxPath;
        private readonly string _mddPath;
        private MinimalMdxReader _reader;

        /// <inheritdoc />
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// 初始化 <see cref="MDictService"/>。
        /// </summary>
        /// <param name="mdxPath">.mdx 文件路径；可为空，表示不加载。</param>
        /// <param name="mddPath">可选的 .mdd 资源文件路径。</param>
        public MDictService(string mdxPath, string mddPath = null)
        {
            _mdxPath = mdxPath;
            _mddPath = mddPath;
        }

        /// <inheritdoc />
        public void Load()
        {
            Load(CancellationToken.None);
        }

        private void Load(CancellationToken cancellationToken)
        {
            IsLoaded = false;
            _reader?.Dispose();
            _reader = null;

            if (string.IsNullOrWhiteSpace(_mdxPath) || !File.Exists(_mdxPath))
                return;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _reader = new MinimalMdxReader(_mdxPath, _mddPath, cancellationToken);
                IsLoaded = _reader.KeyCount > 0;
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
            if (!IsLoaded || _reader == null || string.IsNullOrWhiteSpace(word))
                return null;

            string html = _reader.Lookup(word);
            if (string.IsNullOrWhiteSpace(html))
                return null;

            return new WordEntry
            {
                Word = word,
                HtmlDefinition = html,
                Source = "MDict"
            };
        }
    }

    /// <summary>
    /// MDict .mdx 文件的最小化读取器。
    /// 支持 EngineVersion 1.x / 2.x、UTF-8/UTF-16 头部、zlib 压缩。
    /// </summary>
    internal class MinimalMdxReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _encodingName;
        private readonly Encoding _encoding;
        private readonly int _engineVersion;
        private readonly List<KeyEntry> _entries;
        private readonly Dictionary<string, int> _keyIndex;
        private readonly List<RecordBlockInfo> _recordBlocks;
        private readonly CancellationToken _cancellationToken;
        private long _totalRecordSize;

        private struct KeyEntry
        {
            public string Key;
            public long Offset;
        }

        private struct RecordBlockInfo
        {
            public long CompressedSize;
            public long DecompressedSize;
            public long StreamOffset;
        }

        public int KeyCount => _entries.Count;

        public MinimalMdxReader(string mdxPath, string mddPath, CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            _cancellationToken.ThrowIfCancellationRequested();

            _stream = new FileStream(mdxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _entries = new List<KeyEntry>();
            _keyIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _recordBlocks = new List<RecordBlockInfo>();

            var header = ReadHeader();
            _engineVersion = header.EngineVersion;
            _encodingName = header.Encoding;
            _encoding = ResolveEncoding(_encodingName);

            _cancellationToken.ThrowIfCancellationRequested();
            ReadKeyBlocks();

            _cancellationToken.ThrowIfCancellationRequested();
            ReadRecordBlockInfo();
        }

        public string Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word) || !_keyIndex.TryGetValue(word, out int index))
                return null;

            var entry = _entries[index];
            long endOffset = (index + 1 < _entries.Count)
                ? _entries[index + 1].Offset
                : _totalRecordSize;

            return ReadRecord(entry.Offset, endOffset);
        }

        #region Header

        private struct HeaderInfo
        {
            public int EngineVersion;
            public string Encoding;
            public string Format;
            public string Title;
            public bool Encrypted;
        }

        private HeaderInfo ReadHeader()
        {
            int headerLen = ReadInt32LE();
            if (headerLen <= 0 || headerLen > 1024 * 1024)
                throw new InvalidDataException("MDX 头部长度无效。");

            byte[] headerBytes = ReadBytes(headerLen);
            string headerXml = TryDecodeHeader(headerBytes);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(headerXml);
            }
            catch
            {
                headerXml = Encoding.Unicode.GetString(headerBytes).TrimEnd('\0');
                doc = XDocument.Parse(headerXml);
            }

            var root = doc.Root;
            string generatedBy = root?.Attribute("GeneratedByEngineVersion")?.Value;
            string encoding = root?.Attribute("Encoding")?.Value ?? "UTF-8";
            string format = root?.Attribute("Format")?.Value ?? "Html";
            string title = root?.Attribute("Title")?.Value;
            string encrypted = root?.Attribute("Encrypted")?.Value ?? "0";

            int version = 1;
            if (!string.IsNullOrEmpty(generatedBy))
            {
                var parts = generatedBy.Split('.');
                if (parts.Length > 0 && int.TryParse(parts[0], out int major))
                    version = major;
            }

            return new HeaderInfo
            {
                EngineVersion = version,
                Encoding = encoding,
                Format = format,
                Title = title,
                Encrypted = encrypted != "0"
            };
        }

        private static string TryDecodeHeader(byte[] bytes)
        {
            // 尝试 UTF-8（MDict 2.0 常见）
            string utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (utf8.Contains("<Dictionary", StringComparison.OrdinalIgnoreCase))
                return utf8;

            // 尝试带 BOM 的 UTF-16LE
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');

            return utf8;
        }

        #endregion

        #region Key Blocks

        private void ReadKeyBlocks()
        {
            if (_engineVersion < 1)
                throw new InvalidDataException("不支持的 MDict 引擎版本。");

            ulong keyBlockCount = ReadUInt64BE();
            /* number of entries */ ReadUInt64BE();
            /* key block info decompressed size */ ReadUInt64BE();
            ulong keyBlockInfoCompSize = ReadUInt64BE();

            byte[] keyBlockInfoComp = ReadBytes((int)keyBlockInfoCompSize);
            byte[] keyBlockInfo = Decompress(keyBlockInfoComp);

            var blockInfos = new List<(long firstOffset, long compSize, long decompSize)>();
            using (var infoStream = new MemoryStream(keyBlockInfo))
            {
                for (ulong i = 0; i < keyBlockCount; i++)
                {
                    long firstOffset = (long)ReadUInt64BE(infoStream);
                    long compSize = (long)ReadUInt64BE(infoStream);
                    long decompSize = (long)ReadUInt64BE(infoStream);
                    blockInfos.Add((firstOffset, compSize, decompSize));
                }
            }

            for (ulong i = 0; i < keyBlockCount; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var info = blockInfos[(int)i];
                byte[] compData = ReadBytes((int)info.compSize);
                byte[] decompData = Decompress(compData);
                ParseKeyBlock(decompData);
            }
        }

        private void ParseKeyBlock(byte[] data)
        {
            int pos = 0;
            int entryCount = 0;
            while (pos < data.Length)
            {
                if (++entryCount % 1000 == 0)
                    _cancellationToken.ThrowIfCancellationRequested();

                if (pos + 8 > data.Length)
                    break;

                long offset = (long)ReadUInt64BE(data, pos);
                pos += 8;

                string keyText;
                if (_engineVersion >= 2)
                {
                    if (pos >= data.Length)
                        break;

                    int keyLen = data[pos];
                    pos++;

                    if (pos + keyLen > data.Length)
                        break;

                    keyText = _encoding.GetString(data, pos, keyLen);
                    pos += keyLen;
                }
                else
                {
                    int start = pos;
                    while (pos < data.Length && data[pos] != 0)
                        pos++;

                    keyText = _encoding.GetString(data, start, pos - start);
                    if (pos < data.Length)
                        pos++; // skip null
                }

                int index = _entries.Count;
                _entries.Add(new KeyEntry { Key = keyText, Offset = offset });
                _keyIndex[keyText] = index;
            }
        }

        #endregion

        #region Record Blocks

        private void ReadRecordBlockInfo()
        {
            ulong recordBlockCount = ReadUInt64BE();
            /* record block info decompressed size */ ReadUInt64BE();
            ulong recordBlockInfoCompSize = ReadUInt64BE();

            byte[] infoComp = ReadBytes((int)recordBlockInfoCompSize);
            byte[] infoDecomp = Decompress(infoComp);

            long currentOffset = _stream.Position;
            using (var infoStream = new MemoryStream(infoDecomp))
            {
                _totalRecordSize = 0;
                for (ulong i = 0; i < recordBlockCount; i++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    long compSize = (long)ReadUInt64BE(infoStream);
                    long decompSize = (long)ReadUInt64BE(infoStream);

                    _recordBlocks.Add(new RecordBlockInfo
                    {
                        CompressedSize = compSize,
                        DecompressedSize = decompSize,
                        StreamOffset = currentOffset
                    });

                    currentOffset += compSize;
                    _totalRecordSize += decompSize;
                }
            }
        }

        private string ReadRecord(long startOffset, long endOffset)
        {
            if (startOffset < 0 || startOffset >= _totalRecordSize)
                return null;

            long cumulative = 0;
            int blockIndex = -1;
            for (int i = 0; i < _recordBlocks.Count; i++)
            {
                if (startOffset < cumulative + _recordBlocks[i].DecompressedSize)
                {
                    blockIndex = i;
                    break;
                }
                cumulative += _recordBlocks[i].DecompressedSize;
            }

            if (blockIndex < 0)
                return null;

            var block = _recordBlocks[blockIndex];
            long offsetInBlock = startOffset - cumulative;

            _stream.Position = block.StreamOffset;
            byte[] compData = ReadBytes((int)block.CompressedSize);
            byte[] decompData = Decompress(compData);

            long length = endOffset - startOffset;
            if (offsetInBlock + length > decompData.Length)
                length = decompData.Length - offsetInBlock;

            if (length <= 0)
                return null;

            return _encoding.GetString(decompData, (int)offsetInBlock, (int)length);
        }

        #endregion

        #region Helpers

        private int ReadInt32LE()
        {
            byte[] bytes = ReadBytes(4);
            return BitConverter.ToInt32(bytes, 0);
        }

        private ulong ReadUInt64BE()
        {
            byte[] bytes = ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private static ulong ReadUInt64BE(Stream stream)
        {
            byte[] bytes = new byte[8];
            stream.Read(bytes, 0, 8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private static ulong ReadUInt64BE(byte[] data, int offset)
        {
            byte[] bytes = new byte[8];
            Buffer.BlockCopy(data, offset, bytes, 0, 8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private byte[] ReadBytes(int count)
        {
            byte[] buffer = new byte[count];
            int read = _stream.Read(buffer, 0, count);
            if (read != count)
                throw new EndOfStreamException("MDX 文件意外结束。");
            return buffer;
        }

        private static byte[] Decompress(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }

        private static Encoding ResolveEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Encoding.UTF8;

            string normalized = name.ToUpperInvariant().Replace("-", "").Replace("_", "");
            if (normalized == "UTF8" || normalized == "UTF")
                return Encoding.UTF8;

            try
            {
                return Encoding.GetEncoding(name);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        #endregion

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}
