using System;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace TigerClaw.Shared
{
    public enum EncryptedModelKind : byte
    {
        SentenceNgram = 1,
        SentenceTransformer = 2,
        SentenceVocabulary = 3
    }

    public static class EncryptedModelReader
    {
        private const string Magic = "TCMODEL1";
        private const int HeaderSize = 36;
        private const int IvSize = 16;
        private const int MacSize = 32;
        private const byte DeflateFlag = 1;
        private const long MaximumPlaintextLength = 1024L * 1024L * 1024L;

        public static T Read<T>(string path, EncryptedModelKind expectedKind, Func<Stream, T> reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            ContainerInfo info = ReadAndVerify(path, expectedKind);
            return ReadPayload(path, expectedKind, info, reader);
        }

        public static byte[] ReadAllBytes(string path, EncryptedModelKind expectedKind)
        {
            ContainerInfo info = ReadAndVerify(path, expectedKind);
            if (info.PlaintextLength > int.MaxValue)
            {
                throw new InvalidDataException("Encrypted model is too large for an in-memory load.");
            }

            return ReadPayload(
                path,
                expectedKind,
                info,
                stream => ReadExactly(stream, (int)info.PlaintextLength));
        }

        public static MemoryMappedFile ReadToMemoryMappedFile(
            string path,
            EncryptedModelKind expectedKind,
            out long plaintextLength)
        {
            ContainerInfo info = ReadAndVerify(path, expectedKind);
            if (info.PlaintextLength <= 0)
            {
                throw new InvalidDataException("Encrypted model payload is empty.");
            }

            MemoryMappedFile mapping = MemoryMappedFile.CreateNew(
                null,
                info.PlaintextLength,
                MemoryMappedFileAccess.ReadWrite);
            try
            {
                ReadPayload(
                    path,
                    expectedKind,
                    info,
                    stream =>
                    {
                        using (MemoryMappedViewStream output = mapping.CreateViewStream(
                            0,
                            info.PlaintextLength,
                            MemoryMappedFileAccess.Write))
                        {
                            CopyExactly(stream, output, info.PlaintextLength);
                            output.Flush();
                        }
                        return true;
                    });
                plaintextLength = info.PlaintextLength;
                return mapping;
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }

        private static T ReadPayload<T>(
            string path,
            EncryptedModelKind expectedKind,
            ContainerInfo info,
            Func<Stream, T> reader)
        {
            byte[] encryptionKey = DeriveKey(expectedKind, "enc");
            try
            {
                using (var file = File.OpenRead(path))
                {
                    file.Position = HeaderSize;
                    using (var ciphertext = new BoundedReadStream(file, info.CiphertextLength, leaveOpen: true))
                    using (Aes aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.BlockSize = 128;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = encryptionKey;
                        aes.IV = info.Iv;
                        using (ICryptoTransform decryptor = aes.CreateDecryptor())
                        using (var crypto = new CryptoStream(ciphertext, decryptor, CryptoStreamMode.Read))
                        using (var deflate = new DeflateStream(crypto, CompressionMode.Decompress))
                        {
                            T result = reader(deflate);
                            if (deflate.ReadByte() != -1)
                            {
                                throw new InvalidDataException("Encrypted model contains trailing plaintext data.");
                            }
                            return result;
                        }
                    }
                }
            }
            finally
            {
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
            }
        }

        private static ContainerInfo ReadAndVerify(string path, EncryptedModelKind expectedKind)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Encrypted model path is required.", nameof(path));
            }

            using (var file = File.OpenRead(path))
            {
                if (file.Length < HeaderSize + 16 + MacSize)
                {
                    throw new InvalidDataException("Encrypted model is truncated.");
                }

                byte[] header = ReadExactly(file, HeaderSize);
                string magic = Encoding.ASCII.GetString(header, 0, Magic.Length);
                if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Invalid encrypted model magic.");
                }
                if (header[8] != (byte)expectedKind)
                {
                    throw new InvalidDataException("Encrypted model kind does not match the requested asset.");
                }
                if (header[9] != DeflateFlag || header[10] != 0 || header[11] != 0)
                {
                    throw new InvalidDataException("Unsupported encrypted model flags.");
                }

                long plaintextLength = BitConverter.ToInt64(header, 12);
                if (plaintextLength < 0 || plaintextLength > MaximumPlaintextLength)
                {
                    throw new InvalidDataException("Invalid encrypted model plaintext length.");
                }

                long ciphertextLength = file.Length - HeaderSize - MacSize;
                if (ciphertextLength <= 0 || ciphertextLength % 16 != 0)
                {
                    throw new InvalidDataException("Invalid encrypted model ciphertext length.");
                }

                var iv = new byte[IvSize];
                Buffer.BlockCopy(header, 20, iv, 0, iv.Length);
                byte[] macKey = DeriveKey(expectedKind, "mac");
                try
                {
                    file.Position = 0;
                    byte[] actualMac;
                    using (var authenticatedData = new BoundedReadStream(file, file.Length - MacSize, leaveOpen: true))
                    using (var hmac = new HMACSHA256(macKey))
                    {
                        actualMac = hmac.ComputeHash(authenticatedData);
                    }

                    byte[] expectedMac = ReadExactly(file, MacSize);
                    bool valid = FixedTimeEquals(actualMac, expectedMac);
                    Array.Clear(actualMac, 0, actualMac.Length);
                    Array.Clear(expectedMac, 0, expectedMac.Length);
                    if (!valid)
                    {
                        throw new InvalidDataException("Encrypted model integrity verification failed.");
                    }
                }
                finally
                {
                    Array.Clear(macKey, 0, macKey.Length);
                }

                return new ContainerInfo(plaintextLength, ciphertextLength, iv);
            }
        }

        private static byte[] DeriveKey(EncryptedModelKind kind, string purpose)
        {
            byte[] masterKey = BuildInfo.GetModelProtectionKey();
            try
            {
                byte[] context = Encoding.ASCII.GetBytes(
                    "TigerClaw.Model.v1|" + ((byte)kind).ToString() + "|" + purpose);
                using (var hmac = new HMACSHA256(masterKey))
                {
                    return hmac.ComputeHash(context);
                }
            }
            finally
            {
                Array.Clear(masterKey, 0, masterKey.Length);
            }
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(result, offset, count - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Encrypted model ended unexpectedly.");
                }
                offset += read;
            }
            return result;
        }

        private static void CopyExactly(Stream input, Stream output, long count)
        {
            var buffer = new byte[1024 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Encrypted model ended unexpectedly.");
                }
                output.Write(buffer, 0, read);
                remaining -= read;
            }
            Array.Clear(buffer, 0, buffer.Length);
        }

        private static bool FixedTimeEquals(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            int difference = 0;
            for (int index = 0; index < first.Length; index++)
            {
                difference |= first[index] ^ second[index];
            }
            return difference == 0;
        }

        private sealed class ContainerInfo
        {
            public ContainerInfo(long plaintextLength, long ciphertextLength, byte[] iv)
            {
                PlaintextLength = plaintextLength;
                CiphertextLength = ciphertextLength;
                Iv = iv;
            }

            public long PlaintextLength { get; }
            public long CiphertextLength { get; }
            public byte[] Iv { get; }
        }

        private sealed class BoundedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly bool _leaveOpen;
            private long _remaining;

            public BoundedReadStream(Stream inner, long length, bool leaveOpen)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                if (length < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(length));
                }
                _remaining = length;
                _leaveOpen = leaveOpen;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0)
                {
                    return 0;
                }

                int requested = (int)Math.Min(count, _remaining);
                int read = _inner.Read(buffer, offset, requested);
                _remaining -= read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_leaveOpen)
                {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
