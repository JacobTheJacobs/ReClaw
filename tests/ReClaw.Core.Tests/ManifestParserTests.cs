using System;
using System.IO;
using ReClaw.Core.Models;
using ReClaw.Core.Parsing;
using Xunit;

namespace ReClaw.Core.Tests
{
    public class ManifestParserTests
    {
        private static string FixturePath =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-manifest.json");

        [Fact]
        public void ParseFromString_ValidJson_ReturnsManifest()
        {
            var json = File.ReadAllText(FixturePath);
            var manifest = ManifestParser.ParseFromString(json);
            Assert.Equal("1", manifest.SchemaVersion);
            Assert.Equal("test", manifest.Author);
            Assert.Equal(2, manifest.Payload.Count);
            Assert.Equal("etc/config.json", manifest.Payload[0].Path);
        }

        [Fact]
        public void ParseFromFile_LoadsSuccessfully()
        {
            var manifest = ManifestParser.ParseFromFile(FixturePath);
            Assert.NotNull(manifest);
        }
    }
}
