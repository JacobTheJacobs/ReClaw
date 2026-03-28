using System;
using System.IO;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ReClaw.Core
{
    public static class CryptoHelpers
    {
        private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("RCLAWENC1");
        private const int SaltBytes = 16;
        private const int IvBytes = 12;
        private const int TagBytes = 16;
        private const int PBKDF2Rounds = 310000;

        public static byte[] DeriveKey(string password, byte[] salt)
        {
            return System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                PBKDF2Rounds,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                32);
        }

        // Streaming AES-GCM encryption using BouncyCastle GcmBlockCipher to avoid large managed allocations.
        public static void EncryptFileWithPassword(string inputFile, string outputFile, string password)
        {
            var salt = new byte[SaltBytes];
            var iv = new byte[IvBytes];
            SecureRandom random = new SecureRandom();
            random.NextBytes(salt);
            random.NextBytes(iv);

            var key = DeriveKey(password, salt);

            using var inFs = File.OpenRead(inputFile);
            using var outFs = File.Create(outputFile);

            // header
            outFs.Write(Magic, 0, Magic.Length);
            outFs.Write(salt, 0, salt.Length);
            outFs.Write(iv, 0, iv.Length);

            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), TagBytes * 8, iv, null);
            cipher.Init(true, parameters);

            var buffer = new byte[64 * 1024];
            var outBuffer = new byte[buffer.Length + TagBytes];

            int read;
            while ((read = inFs.Read(buffer, 0, buffer.Length)) > 0)
            {
                int outLen = cipher.ProcessBytes(buffer, 0, read, outBuffer, 0);
                if (outLen > 0) outFs.Write(outBuffer, 0, outLen);
            }

            // finalize and get tag
            var finalBuffer = new byte[cipher.GetOutputSize(0) + TagBytes];
            int finalLen = cipher.DoFinal(finalBuffer, 0);
            if (finalLen > 0) outFs.Write(finalBuffer, 0, finalLen);

            // BouncyCastle's DoFinal already appends tag as part of output for GCM in this API; ensure tag length is present.
        }

        public static void DecryptFileWithPassword(string inputFile, string outputFile, string password)
        {
            using var inFs = File.OpenRead(inputFile);
            var header = new byte[Magic.Length + SaltBytes + IvBytes];
            int read = inFs.Read(header, 0, header.Length);
            if (read != header.Length) throw new InvalidDataException("Encrypted archive is too small or corrupted.");

            for (int i = 0; i < Magic.Length; i++) if (header[i] != Magic[i]) throw new InvalidDataException("Encrypted archive header invalid.");

            var salt = new byte[SaltBytes];
            Array.Copy(header, Magic.Length, salt, 0, SaltBytes);
            var iv = new byte[IvBytes];
            Array.Copy(header, Magic.Length + SaltBytes, iv, 0, IvBytes);

            var key = DeriveKey(password, salt);

            // Prepare cipher
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), TagBytes * 8, iv, null);
            cipher.Init(false, parameters);

            using var outFs = File.Create(outputFile);
            var buffer = new byte[64 * 1024];
            var outBuffer = new byte[buffer.Length + TagBytes];

            int cnt;
            while ((cnt = inFs.Read(buffer, 0, buffer.Length)) > 0)
            {
                int outLen = cipher.ProcessBytes(buffer, 0, cnt, outBuffer, 0);
                if (outLen > 0) outFs.Write(outBuffer, 0, outLen);
            }

            try
            {
                var finalBuffer = new byte[cipher.GetOutputSize(0) + TagBytes];
                int finalLen = cipher.DoFinal(finalBuffer, 0);
                if (finalLen > 0) outFs.Write(finalBuffer, 0, finalLen);
            }
            catch (InvalidCipherTextException ex)
            {
                throw new InvalidDataException("Could not decrypt archive. Check password and archive integrity.", ex);
            }
        }
    }
}
