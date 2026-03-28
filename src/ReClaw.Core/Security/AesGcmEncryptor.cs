using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ReClaw.Core.Security
{
    public class AesGcmEncryptor
    {
        private const string Magic = "RCLAWENC1"; // 8 bytes
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);
        private const int SaltSize = 16;
        private const int IvSize = 12;
        private const int TagSize = 16;
        private const int KeySize = 32;
        private const int Iterations = 310000;

        public byte[] Encrypt(Stream plaintext, string password)
        {
            if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
            if (password is null) throw new ArgumentNullException(nameof(password));

            using var ms = new MemoryStream();
            plaintext.CopyTo(ms);
            var plainBytes = ms.ToArray();

            var salt = new byte[SaltSize];
            var iv = new byte[IvSize];
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(iv);

            var key = DeriveKey(password, salt);

            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(iv, plainBytes, cipher, tag);
            }

            using var outMs = new MemoryStream();
            outMs.Write(MagicBytes, 0, MagicBytes.Length);
            outMs.Write(salt, 0, salt.Length);
            outMs.Write(iv, 0, iv.Length);
            outMs.Write(cipher, 0, cipher.Length);
            outMs.Write(tag, 0, tag.Length);

            return outMs.ToArray();
        }

        public async Task<byte[]> EncryptAsync(Stream plaintext, string password)
        {
            if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
            if (password is null) throw new ArgumentNullException(nameof(password));

            using var ms = new MemoryStream();
            await plaintext.CopyToAsync(ms).ConfigureAwait(false);
            ms.Seek(0, SeekOrigin.Begin);
            return Encrypt(ms, password);
        }

        public Stream Decrypt(Stream encryptedStream, string password)
        {
            if (encryptedStream is null) throw new ArgumentNullException(nameof(encryptedStream));
            if (password is null) throw new ArgumentNullException(nameof(password));

            var header = new byte[MagicBytes.Length];
            ReadExact(encryptedStream, header, 0, header.Length);
            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (header[i] != MagicBytes[i]) throw new InvalidDataException("Invalid magic header");
            }

            var salt = new byte[SaltSize];
            ReadExact(encryptedStream, salt, 0, salt.Length);

            var iv = new byte[IvSize];
            ReadExact(encryptedStream, iv, 0, iv.Length);

            using var rest = new MemoryStream();
            encryptedStream.CopyTo(rest);
            var restBytes = rest.ToArray();
            if (restBytes.Length < TagSize) throw new InvalidDataException("Ciphertext too short");

            var tag = new byte[TagSize];
            Array.Copy(restBytes, restBytes.Length - TagSize, tag, 0, TagSize);
            var cipherLen = restBytes.Length - TagSize;
            var cipher = new byte[cipherLen];
            Array.Copy(restBytes, 0, cipher, 0, cipherLen);

            var key = DeriveKey(password, salt);

            var plain = new byte[cipherLen];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(iv, cipher, tag, plain);
            }

            return new MemoryStream(plain, writable: false);
        }

        public async Task<Stream> DecryptAsync(Stream encryptedStream, string password)
        {
            if (encryptedStream is null) throw new ArgumentNullException(nameof(encryptedStream));
            if (password is null) throw new ArgumentNullException(nameof(password));

            using var ms = new MemoryStream();
            await encryptedStream.CopyToAsync(ms).ConfigureAwait(false);
            ms.Seek(0, SeekOrigin.Begin);
            return Decrypt(ms, password);
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        }

        private static void ReadExact(Stream s, byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buffer, offset + read, count - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }
    }
}
