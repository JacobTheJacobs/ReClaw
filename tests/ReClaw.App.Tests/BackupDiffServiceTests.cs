using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Execution;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class BackupDiffServiceTests
{
    [Fact]
    public async Task DiffDetectsWorkspaceAndConfigChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reclaw_diff_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var left = Path.Combine(tempDir, "left.tar");
        var right = Path.Combine(tempDir, "right.tar");

        try
        {
            CreateArchive(left, "root", "{\"token\":\"a\"}", new[] { "root/workspace/fileA.txt" });
            CreateArchive(right, "root", "{\"token\":\"b\"}", new[] { "root/workspace/fileA.txt", "root/workspace/fileB.txt" });

            var service = new BackupDiffService();
            var summary = await service.DiffAsync(left, right, redactSecrets: false, password: null, CancellationToken.None);

            Assert.Contains("changed: token", summary.ConfigDiff);
            Assert.Contains("root/workspace/fileB.txt", summary.WorkspaceAdded);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void CreateArchive(string path, string archiveRoot, string configJson, IEnumerable<string> workspaceEntries)
    {
        using var fs = File.Create(path);
        using var tar = new TarWriter(fs, leaveOpen: false);

        var manifest = new
        {
            archiveRoot,
            assets = new[]
            {
                new { kind = "config", sourcePath = @"C:\OpenClaw\openclaw.json", archivePath = "config/openclaw.json" },
                new { kind = "workspace", sourcePath = @"C:\OpenClaw\workspace", archivePath = "workspace" },
                new { kind = "credentials", sourcePath = @"C:\OpenClaw\credentials", archivePath = "credentials" }
            }
        };

        var manifestText = System.Text.Json.JsonSerializer.Serialize(manifest);
        WriteEntry(tar, $"{archiveRoot}/manifest.json", manifestText);
        WriteEntry(tar, $"{archiveRoot}/config/openclaw.json", configJson);
        foreach (var entry in workspaceEntries)
        {
            WriteEntry(tar, entry, "data");
        }
    }

    private static void WriteEntry(TarWriter tar, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = new MemoryStream(bytes)
        };
        tar.WriteEntry(entry);
    }
}
