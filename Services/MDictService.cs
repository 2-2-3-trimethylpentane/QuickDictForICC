using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto.Digests;

namespace QuickDictForICC.Services
{
    internal static class MdxEncryptedKeyBlockInfo
    {
        internal static byte[] Decrypt(byte[] encryptedData)
        {
            if (encryptedData == null || encryptedData.Length < 8)
                throw new InvalidDataException("MDX 加密 key block metadata 长度无效。");

            byte[] keyMaterial = new byte[8];
            Buffer.BlockCopy(encryptedData, 4, keyMaterial, 0, 4);
            keyMaterial[4] = 0x95;
            keyMaterial[5] = 0x36;
            byte[] key = RipeMd128(keyMaterial);
            byte[] result = (byte[])encryptedData.Clone();
            byte previous = 0x36;
            for (int i = 8; i < result.Length; i++)
            {
                byte encrypted = result[i];
                result[i] = (byte)(((encrypted >> 4) | (encrypted << 4)) ^ previous ^ ((i - 8) & 0xFF) ^ key[(i - 8) % key.Length]);
                previous = encrypted;
            }
            return result;
        }

        private static byte[] RipeMd128(byte[] data)
        {
            var digest = new RipeMD128Digest();
            digest.BlockUpdate(data, 0, data.Length);
            byte[] result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }
    }

    /// <summary>
    /// MDict 词典服务。
    /// </summary>
    public class MDictService : IDictionaryService
    {
        private readonly string _mdxPath;
        private readonly Action<string> _log;
        private MinimalMdxReader _reader;

        public bool IsLoaded { get; private set; }

        public MDictService(string mdxPath, Action<string> log = null)
            : this(mdxPath, null, log)
        {
        }

        public MDictService(string mdxPath, string mddPath, Action<string> log = null)
        {
            _mdxPath = mdxPath;
            _log = log;
        }

        public void Load()
        {
            if (string.IsNullOrWhiteSpace(_mdxPath) || !File.Exists(_mdxPath))
                return;

            _reader?.Dispose();
            _reader = new MinimalMdxReader(_mdxPath, _log);
            IsLoaded = true;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Load();
            }, cancellationToken);
        }

        public IWordEntry Lookup(string word)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(word))
                return null;

            string html = _reader.Lookup(word);
            if (html == null)
                return null;

            return new WordEntry
            {
                Word = word,
                HtmlDefinition = html,
                Definition = html,
                Source = "MDict"
            };
        }

        public IReadOnlyList<string> Suggest(string prefix, int maxResults = 10)
        {
            return GetSuggestions(prefix, maxResults).ToList();
        }

        public IEnumerable<string> GetSuggestions(string prefix, int maxCount)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(prefix) || maxCount <= 0)
                return Array.Empty<string>();
            return _reader.Suggest(prefix, maxCount);
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
        private readonly Dictionary<string, int> _keyIndexIgnoreCase;
        private readonly List<RecordBlockInfo> _recordBlocks;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<string> _log;
        private readonly bool _keyInfoEncrypted;
        private readonly bool _recordBlocksEncrypted;
        private readonly string _registrationCode;
        private readonly string _userId;
        private long _totalRecordSize;

        private sealed class KeyEntry
        {
            public string Key { get; set; }
            public long Offset { get; set; }
        }

        private sealed class RecordBlockInfo
        {
            public long CompressedSize { get; set; }
            public long DecompressedSize { get; set; }
            public long Offset { get; set; }
        }

        private void Log(string message) => _log?.Invoke($"[MinimalMdxReader] {message}");

        public MinimalMdxReader(string path, Action<string> log = null, CancellationToken cancellationToken = default)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _log = log;
            _cancellationToken = cancellationToken;
            _entries = new List<KeyEntry>();
            _keyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            _keyIndexIgnoreCase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _recordBlocks = new List<RecordBlockInfo>();

            var swTotal = Stopwatch.StartNew();
            Log("=== MinimalMdxReader 构造开始 ===");
            Log($"文件路径 = '{path}'");
            Log($"文件大小 = {_stream.Length:N0} bytes ({_stream.Length / 1024.0 / 1024.0:F2} MB)");

            _cancellationToken.ThrowIfCancellationRequested();

            Log("阶段 1: ReadHeader()");
            var sw = Stopwatch.StartNew();
            HeaderInfo header = ReadHeader();
            sw.Stop();
            Log($"ReadHeader 完成, 耗时 {sw.ElapsedMilliseconds}ms");
            Log($"  EngineVersion = {header.EngineVersion}");
            Log($"  Encoding      = '{header.Encoding}'");
            Log($"  Format        = '{header.Format}'");
            Log($"  Title         = '{header.Title}'");
            Log($"  Encrypted     = '{header.EncryptedValue}' (key metadata={header.KeyInfoEncrypted}, record blocks={header.RecordBlocksEncrypted})");
            Log($"  Registration  = RegisterBy/RegCode present={!string.IsNullOrEmpty(header.RegistrationCode)}, UserID present={!string.IsNullOrEmpty(header.UserId)}");

            _engineVersion = header.EngineVersion;
            _keyInfoEncrypted = header.KeyInfoEncrypted;
            _recordBlocksEncrypted = header.RecordBlocksEncrypted;
            _registrationCode = header.RegistrationCode;
            _userId = header.UserId;
            _encodingName = header.Encoding;
            _encoding = ResolveEncoding(_encodingName);
            Log($"  解析后 Encoding = {_encoding.EncodingName}");

            _cancellationToken.ThrowIfCancellationRequested();

            Log("阶段 2: ReadKeyBlocks()");
            sw.Restart();
            ReadKeyBlocks();
            sw.Stop();
            Log($"ReadKeyBlocks 完成, 耗时 {sw.ElapsedMilliseconds}ms, 共解析 {_entries.Count} 个词条");

            _cancellationToken.ThrowIfCancellationRequested();

            Log("阶段 3: ReadRecordBlockInfo()");
            sw.Restart();
            ReadRecordBlockInfo();
            sw.Stop();
            Log($"ReadRecordBlockInfo 完成, 耗时 {sw.ElapsedMilliseconds}ms, 记录块数={_recordBlocks.Count}, 总解压大小={_totalRecordSize}");

            swTotal.Stop();
            Log($"=== MinimalMdxReader 构造结束, 总耗时 {swTotal.ElapsedMilliseconds}ms ===");
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }

        public IReadOnlyList<string> Suggest(string prefix, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(prefix) || maxResults <= 0)
                return Array.Empty<string>();

            return _entries
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .Select(entry => entry.Key)
                .ToList();
        }

        public string Lookup(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return null;

            string currentWord = word;
            for (int redirectDepth = 0; redirectDepth < 8; redirectDepth++)
            {
                if (!_keyIndexIgnoreCase.TryGetValue(currentWord, out int index))
                    return null;

                long startOffset = _entries[index].Offset;
                long endOffset = index + 1 < _entries.Count ? _entries[index + 1].Offset : _totalRecordSize;
                string record = ReadRecord(startOffset, endOffset);
                if (record == null)
                    return null;

                const string linkPrefix = "@@@LINK=";
                if (!record.StartsWith(linkPrefix, StringComparison.OrdinalIgnoreCase))
                    return record;

                currentWord = record.Substring(linkPrefix.Length)
                    .TrimEnd('\0', '\r', '\n')
                    .Trim();
                Log($"Lookup('{word}'): 解析到 MDict 重定向 -> '{currentWord}'");
                if (string.IsNullOrWhiteSpace(currentWord))
                    return null;
            }

            Log($"Lookup('{word}'): 重定向层级超过上限");
            return null;
        }

        #region Header

        private struct HeaderInfo
        {
            public int EngineVersion;
            public string Encoding;
            public string Format;
            public string Title;
            public string EncryptedValue;
            public bool KeyInfoEncrypted;
            public bool RecordBlocksEncrypted;
            public string RegistrationCode;
            public string UserId;
        }

        private HeaderInfo ReadHeader()
        {
            byte[] first8 = PeekBytes(8);
            Log($"ReadHeader: 文件开头前 8 字节: {BitConverter.ToString(first8)}");

            int headerLen = ReadInt32BE();
            Log($"ReadHeader: headerLen (BE) = {headerLen} (0x{headerLen:X8})");
            if (headerLen <= 0 || headerLen > 1024 * 1024 || headerLen > _stream.Length - 8)
                throw new InvalidDataException("MDX 头部长度无效。");

            byte[] headerBytes = ReadBytes(headerLen);
            byte[] headerChecksum = ReadBytes(4);
            Log($"ReadHeader: 已读取 {headerLen} 字节 XML 头部和 Adler-32 校验和 {BitConverter.ToString(headerChecksum)}；后续块起始偏移 {_stream.Position}");
            return ParseHeaderXml(headerBytes);
        }

        private HeaderInfo ParseHeaderXml(byte[] headerBytes)
        {
            string headerXml = TryDecodeHeader(headerBytes);
            Log($"ParseHeaderXml: 解码后 (前 300 字符):");
            Log($"  '{headerXml.Substring(0, Math.Min(300, headerXml.Length))}'");

            XDocument doc;
            try
            {
                doc = XDocument.Parse(headerXml);
                Log("ParseHeaderXml: XDocument.Parse 成功");
            }
            catch (Exception ex)
            {
                Log($"ParseHeaderXml: XDocument.Parse 失败: {ex.Message}");

                try
                {
                    string utf16 = Encoding.Unicode.GetString(headerBytes).TrimEnd('\0');
                    Log($"ParseHeaderXml: 尝试 UTF-16LE: '{utf16.Substring(0, Math.Min(300, utf16.Length))}'");
                    doc = XDocument.Parse(utf16);
                    Log("ParseHeaderXml: UTF-16LE XDocument.Parse 成功");
                }
                catch (Exception ex2)
                {
                    Log($"ParseHeaderXml: UTF-16LE 也失败: {ex2.Message}");
                    throw new InvalidDataException("MDX 头部 XML 解析失败。", ex);
                }
            }

            var root = doc.Root;
            string generatedBy = root?.Attribute("GeneratedByEngineVersion")?.Value;
            string encoding = root?.Attribute("Encoding")?.Value ?? "UTF-8";
            string format = root?.Attribute("Format")?.Value ?? "Html";
            string title = root?.Attribute("Title")?.Value;
            string encrypted = root?.Attribute("Encrypted")?.Value ?? "0";
            string registrationCode = root?.Attribute("RegisterBy")?.Value ?? root?.Attribute("RegCode")?.Value ?? string.Empty;
            string userId = root?.Attribute("UserID")?.Value ?? string.Empty;
            int encryptedFlags;
            if (encrypted.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                encryptedFlags = 1;
            else if (!int.TryParse(encrypted, out encryptedFlags))
                encryptedFlags = 0;

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
                EncryptedValue = encrypted,
                KeyInfoEncrypted = (encryptedFlags & 2) != 0,
                RecordBlocksEncrypted = (encryptedFlags & 1) != 0,
                RegistrationCode = registrationCode,
                UserId = userId
            };
        }

        private static string TryDecodeHeader(byte[] headerBytes)
        {
            string utf8 = Encoding.UTF8.GetString(headerBytes).TrimEnd('\0');
            if (utf8.IndexOf("<Dictionary", StringComparison.OrdinalIgnoreCase) >= 0)
                return utf8;
            return Encoding.Unicode.GetString(headerBytes).TrimEnd('\0');
        }

        #endregion

        #region Key Blocks

        private void ReadKeyBlocks()
        {
            if (_engineVersion < 1)
            {
                Log($"ReadKeyBlocks: 不支持的引擎版本 {_engineVersion}, 抛出异常");
                throw new InvalidDataException("不支持的 MDict 引擎版本。");
            }

            long posBefore = _stream.Position;
            Log($"ReadKeyBlocks: 标准格式, 开始位置 {posBefore}");
            byte[] peekAfterHeader = PeekBytes(64);
            Log($"ReadKeyBlocks: HEADER 后前 64 字节: {BitConverter.ToString(peekAfterHeader)}");

            ulong keyBlockCount = ReadUInt64BE();
            ulong numEntries = ReadUInt64BE();
            ulong keyBlockInfoDecompSize = ReadUInt64BE();
            ulong keyBlockInfoCompSize = ReadUInt64BE();
            ulong keyBlockSize = ReadUInt64BE();

            Log($"ReadKeyBlocks: keyBlockCount={keyBlockCount}, numEntries={numEntries}");
            Log($"ReadKeyBlocks: keyBlockInfoDecompSize={keyBlockInfoDecompSize}, keyBlockInfoCompSize={keyBlockInfoCompSize}, keyBlockSize={keyBlockSize}");

            if (keyBlockCount == 0 || keyBlockCount > 100000 ||
                numEntries == 0 || numEntries > 10000000 ||
                keyBlockInfoCompSize > (ulong)(_stream.Length - _stream.Position - 4) ||
                keyBlockSize > (ulong)(_stream.Length - _stream.Position - 4 - (long)keyBlockInfoCompSize))
            {
                throw new InvalidDataException("MDX key block 元数据无效，已停止解析以避免异常内存分配。");
            }

            byte[] keyBlockInfoChecksum = ReadBytes(4);
            byte[] keyBlockInfoComp = ReadBytes((int)keyBlockInfoCompSize);
            Log($"ReadKeyBlocks: 已读取 keyBlockInfo 校验和 {BitConverter.ToString(keyBlockInfoChecksum)} 和压缩数据, 大小={keyBlockInfoComp.Length}");

            if (_keyInfoEncrypted)
            {
                Log("ReadKeyBlocks: 对 Encrypted=2/3 的 key block metadata 执行 MDX 解密。");
                keyBlockInfoComp = DecryptKeyBlockInfo(keyBlockInfoComp, keyBlockInfoChecksum, keyBlockInfoDecompSize);
            }

            byte[] keyBlockInfo;
            try
            {
                keyBlockInfo = DecompressKeyBlockInfo(keyBlockInfoComp, keyBlockInfoChecksum, keyBlockInfoDecompSize);
                Log($"ReadKeyBlocks: keyBlockInfo 解压并验证成功, 解压后大小={keyBlockInfo.Length}");
            }
            catch (Exception ex)
            {
                Log($"ReadKeyBlocks: keyBlockInfo 解压或验证失败: {ex.Message}");
                throw;
            }

            var blockInfos = new List<(long compSize, long decompSize)>();
            using (var infoStream = new MemoryStream(keyBlockInfo))
            {
                for (ulong i = 0; i < keyBlockCount; i++)
                {
                    ReadNumberBE(infoStream);
                    SkipKeyText(infoStream);
                    SkipKeyText(infoStream);
                    long compSize = (long)ReadUInt64BE(infoStream);
                    long decompSize = (long)ReadUInt64BE(infoStream);
                    if (compSize <= 8 || decompSize <= 0)
                        throw new InvalidDataException($"MDX key block metadata 第 {i + 1} 项的长度无效。");
                    blockInfos.Add((compSize, decompSize));
                }

                if (infoStream.Position != infoStream.Length)
                    throw new InvalidDataException($"MDX key block metadata 结构长度不匹配: 已读取 {infoStream.Position}, 总长度 {infoStream.Length}。");
            }

            ulong declaredKeyBlockSize = 0;
            foreach (var blockInfo in blockInfos)
                declaredKeyBlockSize += (ulong)blockInfo.compSize;
            if (declaredKeyBlockSize != keyBlockSize)
                throw new InvalidDataException($"MDX key block metadata 数据长度不匹配: 元信息合计 {declaredKeyBlockSize}, 头部声明 {keyBlockSize}。");

            Log($"ReadKeyBlocks: 解析并验证了 {blockInfos.Count} 个 key block 元信息");

            for (ulong i = 0; i < keyBlockCount; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var info = blockInfos[(int)i];

                Log($"ReadKeyBlocks: 读取第 {i + 1}/{keyBlockCount} 个 key block, compSize={info.compSize}, decompSize={info.decompSize}");
                if (info.compSize <= 8 || info.compSize > _stream.Length - _stream.Position)
                    throw new InvalidDataException("MDX key block 大小无效。");
                byte[] compData = ReadBytes((int)info.compSize);

                byte[] decompData;
                try
                {
                    decompData = Decompress(compData);
                }
                catch (Exception ex)
                {
                    Log($"ReadKeyBlocks: 第 {i + 1} 个 key block 解压失败: {ex.Message}");
                    throw;
                }

                int beforeCount = _entries.Count;
                ParseKeyBlock(decompData);
                int parsedInBlock = _entries.Count - beforeCount;
                Log($"ReadKeyBlocks: 第 {i + 1} 个 key block 解析出 {parsedInBlock} 个词条, 累计 {_entries.Count}");
            }

            Log($"ReadKeyBlocks: 全部完成, 共 {_entries.Count} 个词条");
        }

        private void ParseKeyBlock(byte[] data)
        {
            int pos = 0;
            int entryCount = 0;
            while (pos < data.Length)
            {
                if (++entryCount % 1000 == 0)
                    _cancellationToken.ThrowIfCancellationRequested();

                int numberWidth = _engineVersion >= 2 ? 8 : 4;
                if (pos + numberWidth > data.Length)
                    break;

                long offset = _engineVersion >= 2
                    ? (long)ReadUInt64BE(data, pos)
                    : ReadUInt32BE(data, pos);
                pos += numberWidth;

                int keyStart = pos;
                int terminatorWidth = _encoding.CodePage == Encoding.Unicode.CodePage ? 2 : 1;
                while (pos + terminatorWidth <= data.Length)
                {
                    bool terminator = true;
                    for (int i = 0; i < terminatorWidth; i++)
                    {
                        if (data[pos + i] != 0)
                        {
                            terminator = false;
                            break;
                        }
                    }

                    if (terminator)
                        break;

                    pos++;
                }

                if (pos + terminatorWidth > data.Length)
                    break;

                string keyText = _encoding.GetString(data, keyStart, pos - keyStart);
                pos += terminatorWidth;

                int index = _entries.Count;
                _entries.Add(new KeyEntry { Key = keyText, Offset = offset });
                _keyIndex[keyText] = index;
                _keyIndexIgnoreCase[keyText] = index;
            }
        }

        #endregion

        #region Record Blocks

        private void ReadRecordBlockInfo()
        {
            long posBefore = _stream.Position;
            Log($"ReadRecordBlockInfo: 开始位置 {posBefore}");
            ulong recordBlockCount = ReadUInt64BE();
            ulong entryCount = ReadUInt64BE();
            ulong recordBlockInfoSize = ReadUInt64BE();
            ulong recordBlockSize = ReadUInt64BE();
            Log($"ReadRecordBlockInfo: recordBlockCount={recordBlockCount}, entryCount={entryCount}, infoSize={recordBlockInfoSize}, dataSize={recordBlockSize}");

            if (recordBlockCount == 0 || recordBlockCount > 100000 ||
                recordBlockInfoSize > (ulong)(_stream.Length - _stream.Position) ||
                recordBlockSize > (ulong)(_stream.Length - _stream.Position - (long)recordBlockInfoSize))
            {
                throw new InvalidDataException("MDX record block 元数据无效，已停止解析以避免异常内存分配。");
            }

            for (ulong i = 0; i < recordBlockCount; i++)
            {
                long compressedSize = (long)ReadUInt64BE();
                long decompressedSize = (long)ReadUInt64BE();
                if (compressedSize <= 8 || decompressedSize <= 0)
                    throw new InvalidDataException($"MDX record block metadata 第 {i + 1} 项的长度无效。");
                _recordBlocks.Add(new RecordBlockInfo
                {
                    CompressedSize = compressedSize,
                    DecompressedSize = decompressedSize,
                    Offset = 0
                });
                _totalRecordSize += decompressedSize;
            }

            long dataOffset = _stream.Position;
            foreach (RecordBlockInfo block in _recordBlocks)
            {
                block.Offset = dataOffset;
                dataOffset += block.CompressedSize;
            }

            if (dataOffset > _stream.Length)
                throw new InvalidDataException("MDX record block 数据长度不足。");
        }

        private string ReadRecord(long startOffset, long endOffset)
        {
            if (_recordBlocksEncrypted)
            {
                Log("ReadRecord: Header Encrypted 标志要求先解密 record block；当前版本尚未实现该解密算法。");
                return null;
            }

            if (startOffset < 0 || startOffset >= _totalRecordSize)
            {
                Log($"ReadRecord: startOffset={startOffset} 越界 (totalRecordSize={_totalRecordSize}), 返回 null");
                return null;
            }

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
            {
                Log($"ReadRecord: 未找到对应记录块, startOffset={startOffset}, 返回 null");
                return null;
            }

            var block = _recordBlocks[blockIndex];
            long offsetInBlock = startOffset - cumulative;
            long length = endOffset - startOffset;
            if (length <= 0)
                return string.Empty;

            Log($"ReadRecord: blockIndex={blockIndex}, offsetInBlock={offsetInBlock}, length={length}");
            _stream.Position = block.Offset;
            byte[] compData = ReadBytes((int)block.CompressedSize);
            byte[] decompData = Decompress(compData);

            if (offsetInBlock + length > decompData.Length)
            {
                Log($"ReadRecord: 请求范围超出记录块, offset={offsetInBlock}, length={length}, blockLength={decompData.Length}");
                return null;
            }

            return _encoding.GetString(decompData, (int)offsetInBlock, (int)length).TrimEnd('\0');
        }

        #endregion

        #region Binary Helpers

        private byte[] PeekBytes(int length)
        {
            long position = _stream.Position;
            byte[] buffer = ReadBytes(Math.Min(length, (int)(_stream.Length - position)));
            _stream.Position = position;
            return buffer;
        }

        private byte[] ReadBytes(int length)
        {
            if (length < 0 || _stream.Position + length > _stream.Length)
                throw new EndOfStreamException("MDX 文件数据不足。");

            byte[] buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int count = _stream.Read(buffer, read, length - read);
                if (count == 0)
                    throw new EndOfStreamException("MDX 文件数据不足。");
                read += count;
            }
            return buffer;
        }

        private int ReadInt32BE()
        {
            return (int)ReadUInt32BE(ReadBytes(4), 0);
        }

        private ulong ReadUInt64BE()
        {
            return ReadUInt64BE(ReadBytes(8), 0);
        }

        private static ulong ReadUInt64BE(Stream stream)
        {
            byte[] bytes = new byte[8];
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read != bytes.Length)
                throw new EndOfStreamException("MDX 元数据数据不足。");
            return ReadUInt64BE(bytes, 0);
        }

        private static ulong ReadUInt64BE(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 8 > data.Length)
                throw new InvalidDataException("MDX UInt64 数据无效。");
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | data[offset + i];
            return value;
        }

        private static uint ReadUInt32BE(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
                throw new InvalidDataException("MDX UInt32 数据无效。");
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static uint ReadUInt32BE(byte[] data, int offset, bool littleEndian)
        {
            if (!littleEndian)
                return ReadUInt32BE(data, offset);
            if (data == null || offset < 0 || offset + 4 > data.Length)
                throw new InvalidDataException("MDX UInt32 数据无效。");
            return BitConverter.ToUInt32(data, offset);
        }

        private static uint ReadUInt32BE(Stream stream)
        {
            byte[] bytes = new byte[4];
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read != bytes.Length)
                throw new EndOfStreamException("MDX 元数据数据不足。");
            return ReadUInt32BE(bytes, 0);
        }

        private static uint ReadUInt32BE(byte[] data, int offset, int length)
        {
            return ReadUInt32BE(data, offset);
        }

        private static uint ReadUInt32BE(byte[] data)
        {
            return ReadUInt32BE(data, 0);
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
                throw new InvalidDataException("MDX UInt32 数据无效。");
            return BitConverter.ToUInt32(data, offset);
        }

        private static ulong ReadNumberBE(Stream stream)
        {
            return _dummyEngineVersion >= 2 ? ReadUInt64BE(stream) : ReadUInt32BE(stream);
        }

        private static int _dummyEngineVersion = 2;

        private void SkipKeyText(Stream stream)
        {
            int length = _engineVersion >= 2 ? (int)ReadUInt16BE(stream) : (int)ReadUInt8(stream);
            int characterWidth = _encoding.CodePage == Encoding.Unicode.CodePage ? 2 : 1;
            int byteLength = checked(length * characterWidth);
            int terminatorLength = characterWidth;
            if (byteLength < 0 || stream.Position + byteLength + terminatorLength > stream.Length)
                throw new InvalidDataException("MDX key block metadata 文本长度无效。");
            stream.Position += byteLength + terminatorLength;
        }

        private static ushort ReadUInt16BE(Stream stream)
        {
            int high = stream.ReadByte();
            int low = stream.ReadByte();
            if (high < 0 || low < 0)
                throw new EndOfStreamException("MDX 元数据数据不足。");
            return (ushort)((high << 8) | low);
        }

        private static byte ReadUInt8(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0)
                throw new EndOfStreamException("MDX 元数据数据不足。");
            return (byte)value;
        }

        private static byte[] Decompress(byte[] data)
        {
            if (data.Length < 8)
                throw new InvalidDataException("MDX 压缩块长度无效。");

            uint compressionType = BitConverter.ToUInt32(data, 0);
            if (compressionType == 0)
            {
                byte[] uncompressed = new byte[data.Length - 8];
                Buffer.BlockCopy(data, 8, uncompressed, 0, uncompressed.Length);
                return uncompressed;
            }

            if (compressionType != 2)
                throw new InvalidDataException($"不支持的 MDX 压缩类型: {compressionType}。");

            using var input = new MemoryStream(data, 8, data.Length - 8, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] DecryptKeyBlockInfo(byte[] encryptedData, byte[] keyBlockInfoChecksum, ulong expectedDecompressedSize)
        {
            if (encryptedData == null || keyBlockInfoChecksum == null)
                throw new InvalidDataException("MDX 加密 key block metadata 输入无效。");

            byte[] decryptedData = MdxEncryptedKeyBlockInfo.Decrypt(encryptedData);
            if (!TryValidateKeyBlockInfo(decryptedData, keyBlockInfoChecksum, expectedDecompressedSize, out string reason))
                throw new InvalidDataException($"MDX 加密 key block metadata 解密后验证失败: {reason}");
            return decryptedData;
        }

        private static byte[] RipeMd128(byte[] data)
        {
            var digest = new RipeMD128Digest();
            digest.BlockUpdate(data, 0, data.Length);
            byte[] result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }

        private byte[] DecompressKeyBlockInfo(byte[] data, byte[] keyBlockInfoChecksum, ulong expectedDecompressedSize)
        {
            if (!TryValidateKeyBlockInfo(data, keyBlockInfoChecksum, expectedDecompressedSize, out string reason, out byte[] decompressed))
                throw new InvalidDataException($"MDX key block metadata 验证失败: {reason}");
            return decompressed;
        }

        private static bool TryValidateKeyBlockInfo(byte[] data, byte[] expectedChecksum, ulong expectedDecompressedSize, out string reason)
        {
            return TryValidateKeyBlockInfo(data, expectedChecksum, expectedDecompressedSize, out reason, out _);
        }

        private static bool TryValidateKeyBlockInfo(byte[] data, byte[] expectedChecksum, ulong expectedDecompressedSize, out string reason, out byte[] decompressed)
        {
            decompressed = null;
            if (data == null || data.Length < 8)
            {
                reason = "压缩块长度不足 8 字节";
                return false;
            }

            uint compressionType = BitConverter.ToUInt32(data, 0);
            if (compressionType != 0 && compressionType != 2)
            {
                reason = $"压缩类型无效: {compressionType} (0x{compressionType:X8})";
                return false;
            }

            try
            {
                decompressed = Decompress(data);
            }
            catch (Exception ex)
            {
                reason = $"解压失败: {ex.Message}";
                return false;
            }

            if ((ulong)decompressed.Length != expectedDecompressedSize)
            {
                reason = $"解压长度不匹配: 实际 {decompressed.Length}, 头部声明 {expectedDecompressedSize}";
                return false;
            }

            uint actualChecksum = Adler32(decompressed);
            uint expectedChecksumValue = ReadUInt32BE(data, 4);
            if (actualChecksum != expectedChecksumValue)
            {
                reason = $"Adler-32 不匹配: 实际 0x{actualChecksum:X8}, 压缩包装声明 0x{expectedChecksumValue:X8}";
                return false;
            }

            reason = null;
            return true;
        }

        private static uint Adler32(byte[] data)
        {
            const uint modulus = 65521;
            uint a = 1;
            uint b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % modulus;
                b = (b + a) % modulus;
            }
            return (b << 16) | a;
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
    }
}
