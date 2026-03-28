using System.IO;
using Xunit;
using ReClaw.Core;

namespace ReClaw.Core.Tests
{
    public class EncryptionTests
    {
        [Fact]
        public void EncryptDecrypt_Roundtrip_Works()
        {
            var temp = Path.GetTempFileName();
            var src = Path.ChangeExtension(temp, ".bin");
            File.Delete(temp);

            var data = new byte[1024];
            new System.Random(42).NextBytes(data);
            File.WriteAllBytes(src, data);

            var enc = Path.ChangeExtension(src, ".enc");
            var dec = Path.ChangeExtension(src, ".dec");

            var password = "test-password";
            CryptoHelpers.EncryptFileWithPassword(src, enc, password);
            CryptoHelpers.DecryptFileWithPassword(enc, dec, password);

            var outData = File.ReadAllBytes(dec);
            Assert.Equal(data.Length, outData.Length);
            Assert.Equal(data, outData);

            File.Delete(src);
            File.Delete(enc);
            File.Delete(dec);
        }
    }
}
