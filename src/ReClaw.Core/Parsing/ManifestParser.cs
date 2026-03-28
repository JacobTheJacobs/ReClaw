using System;
using System.IO;
using System.Text.Json;
using ReClaw.Core.Models;

namespace ReClaw.Core.Parsing
{
    public static class ManifestParser
    {
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static BackupManifest ParseFromString(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("json is empty", nameof(json));
            return JsonSerializer.Deserialize<BackupManifest>(json, _opts)
                ?? throw new InvalidDataException("Unable to deserialize manifest");
        }

        public static BackupManifest ParseFromFile(string path)
        {
            var txt = File.ReadAllText(path);
            return ParseFromString(txt);
        }
    }
}
