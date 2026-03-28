using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Core.Tests
{
    public class ZipHandlerTests
    {
        [Fact]
        public async Task DetectsWinZipAesHeader_WhenPresent()
        {
            using var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(0x04034b50), 0, 4);
            var header = new byte[26];
            BitConverter.GetBytes((ushort)99).CopyTo(header, 4);
            ms.Write(header, 0, header.Length);
            ms.Seek(0, SeekOrigin.Begin);

            bool contains = await ZipHandler.ContainsWinZipAesAsync(ms);
            Assert.True(contains);
        }

        [Fact]
        public async Task ExtractPlainZip_UsesDotNetZipArchive()
        {
            using var ms = new MemoryStream();
            using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen:true))
            {
                var e = za.CreateEntry("a.txt");
                using var s = e.Open();
                using var w = new StreamWriter(s);
                w.Write("hello");
            }
            ms.Seek(0, SeekOrigin.Begin);
            var dest = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dest);
            await ZipHandler.ExtractAsync(ms, dest);
            Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        }
    }
}
