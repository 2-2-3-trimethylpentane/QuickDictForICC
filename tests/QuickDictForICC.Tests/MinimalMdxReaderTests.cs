using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using QuickDictForICC.Services;
using Xunit;

namespace QuickDictForICC.Tests
{
    public class MinimalMdxReaderTests
    {
        private static readonly Type ReaderType = typeof(MDictService).Assembly.GetType("QuickDictForICC.Services.MinimalMdxReader", throwOnError: true);

        [Fact]
        public void ParseHeader_EncryptedTwo_SetsKeyInfoEncryptionOnly()
        {
            object reader = FormatterServices.GetUninitializedObject(ReaderType);
            byte[] header = Encoding.UTF8.GetBytes("<Dictionary GeneratedByEngineVersion=\"2.0\" Encoding=\"UTF-8\" Encrypted=\"2\" />");

            object result = Invoke(reader, "ParseHeaderXml", header);

            Assert.True((bool)GetField(result, "KeyInfoEncrypted"));
            Assert.False((bool)GetField(result, "RecordBlocksEncrypted"));
            Assert.Equal(2, (int)GetField(result, "EngineVersion"));
        }

        [Fact]
        public void Decompress_SupportsUncompressedAndZlibBlocks()
        {
            byte[] payload = Encoding.UTF8.GetBytes("mdx key metadata");
            byte[] typeZero = BuildBlock(0, payload);
            byte[] typeTwo = BuildZlibBlock(payload);

            Assert.Equal(payload, (byte[])InvokeStatic("Decompress", typeZero));
            Assert.Equal(payload, (byte[])InvokeStatic("Decompress", typeTwo));
        }

        [Fact]
        public void ValidateKeyBlockInfo_RequiresMatchingWrapperChecksumAndLength()
        {
            byte[] payload = Encoding.UTF8.GetBytes("validated metadata");
            byte[] block = BuildZlibBlock(payload);
            byte[] checksum = UInt32Be(Adler32(payload));
            object[] args = { block, checksum, (ulong)payload.Length, null, null };

            bool valid = (bool)InvokeStatic("TryValidateKeyBlockInfo", args);
            Assert.True(valid);
            Assert.Null(args[3]);
            Assert.Equal(payload, (byte[])args[4]);

            byte[] badChecksum = (byte[])block.Clone();
            badChecksum[4] ^= 0x01;
            args = new object[] { badChecksum, checksum, (ulong)payload.Length, null, null };
            Assert.False((bool)InvokeStatic("TryValidateKeyBlockInfo", args));
            Assert.Contains("Adler-32", (string)args[3]);

            args = new object[] { block, checksum, (ulong)payload.Length + 1, null, null };
            Assert.False((bool)InvokeStatic("TryValidateKeyBlockInfo", args));
            Assert.Contains("长度不匹配", (string)args[3]);
        }

        [Fact]
        public void DecryptKeyBlockInfo_AcceptsMdxEncryptedMetadataAndRejectsCorruption()
        {
            byte[] payload = Encoding.UTF8.GetBytes("encrypted key metadata");
            byte[] plainBlock = BuildZlibBlock(payload);
            byte[] encrypted = EncryptKeyBlockInfo(plainBlock);
            object reader = FormatterServices.GetUninitializedObject(ReaderType);

            Assert.Equal(plainBlock, (byte[])Invoke(reader, "DecryptKeyBlockInfo", encrypted, UInt32Be(0), (ulong)payload.Length));

            encrypted[8] ^= 0x01;
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Invoke(reader, "DecryptKeyBlockInfo", encrypted, UInt32Be(0), (ulong)payload.Length));
            Assert.IsType<InvalidDataException>(exception.InnerException);
        }

        private static byte[] EncryptKeyBlockInfo(byte[] plainBlock)
        {
            byte[] encrypted = (byte[])plainBlock.Clone();
            byte[] keyMaterial = new byte[8];
            Buffer.BlockCopy(plainBlock, 4, keyMaterial, 0, 4);
            keyMaterial[4] = 0x95;
            keyMaterial[5] = 0x36;
            byte[] key = (byte[])InvokeStatic("RipeMd128", keyMaterial);
            byte previous = 0x36;
            for (int i = 8; i < encrypted.Length; i++)
            {
                byte value = (byte)(plainBlock[i] ^ previous ^ ((i - 8) & 0xFF) ^ key[(i - 8) % key.Length]);
                encrypted[i] = (byte)((value >> 4) | (value << 4));
                previous = encrypted[i];
            }
            return encrypted;
        }

        private static byte[] BuildBlock(uint compressionType, byte[] payload)
        {
            return BitConverter.GetBytes(compressionType)
                .Concat(UInt32Be(Adler32(payload)))
                .Concat(payload)
                .ToArray();
        }

        private static byte[] BuildZlibBlock(byte[] payload)
        {
            using (var output = new MemoryStream())
            {
                output.Write(BitConverter.GetBytes(2), 0, 4);
                byte[] checksum = UInt32Be(Adler32(payload));
                output.Write(checksum, 0, checksum.Length);
                using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
                    zlib.Write(payload, 0, payload.Length);
                return output.ToArray();
            }
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

        private static byte[] UInt32Be(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static object InvokeStatic(string name, params object[] arguments)
        {
            return Invoke(null, name, arguments);
        }

        private static object Invoke(object target, string name, params object[] arguments)
        {
            MethodInfo method = ReaderType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
            return method.Invoke(target, arguments);
        }

        private static object GetField(object value, string name)
        {
            return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value);
        }
    }
}
