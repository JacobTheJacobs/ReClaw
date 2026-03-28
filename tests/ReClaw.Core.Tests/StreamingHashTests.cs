using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Core.Tests
{
    public class StreamingHashTests
    {
        [Fact]
        public async Task StreamingHash_Equals_BulkHash()
        {
            var data = new byte[100_000];
            new Random(42).NextBytes(data);

            byte[] expected;
            using (var sha = SHA256.Create())
                expected = sha.ComputeHash(data);

            using var ms = new MemoryStream(data);
            var sh = new StreamingHash();
            await sh.AppendAsync(ms);
            var result = sh.GetHashAndReset();

            Assert.Equal(expected, result);
        }
    }
}
