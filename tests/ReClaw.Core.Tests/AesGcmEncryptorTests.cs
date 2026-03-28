using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ReClaw.Core.Security;
using Xunit;

namespace ReClaw.Core.Tests
{
    public class AesGcmEncryptorTests
    {
        [Fact]
        public void EncryptDecrypt_Roundtrip()
        {
            var encryptor = new AesGcmEncryptor();
            var password = "correct horse battery staple";

            var plainText = new byte[1024];
            RandomNumberGenerator.Fill(plainText);

            using var plainMs = new MemoryStream(plainText);
            var encrypted = encryptor.Encrypt(plainMs, password);

            using var encMs = new MemoryStream(encrypted);
            using var decStream = encryptor.Decrypt(encMs, password);
            using var outMs = new MemoryStream();
            decStream.CopyTo(outMs);

            var result = outMs.ToArray();
            Assert.Equal(plainText.Length, result.Length);
            Assert.Equal(plainText, result);
        }

        [Fact]
        public void Decrypt_Fails_WhenTagTampered()
        {
            var encryptor = new AesGcmEncryptor();
            var password = "s3cr3t";

            var plainText = Encoding.UTF8.GetBytes("Hello, AES-GCM test");
            using var plainMs = new MemoryStream(plainText);
            var encrypted = encryptor.Encrypt(plainMs, password);

            // tamper last byte (part of tag)
            encrypted[encrypted.Length - 1] ^= 0xFF;

            using var encMs = new MemoryStream(encrypted);
            Assert.ThrowsAny<CryptographicException>(() =>
            {
                using var dec = encryptor.Decrypt(encMs, password);
                using var r = new MemoryStream();
                dec.CopyTo(r);
            });
        }
    }
}
