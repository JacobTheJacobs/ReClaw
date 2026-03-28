using System;
using System.IO;
using System.Runtime.InteropServices;
using ReClaw.Core.IO;
using Xunit;

namespace ReClaw.Core.Tests;

public sealed class ZipExtractionSafetyTests
{
    [Fact]
    public void ValidateEntryNames_RejectsTraversalAndAbsolutePaths()
    {
        var entries = new[]
        {
            new ZipEntryInfo("../evil.txt", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(entries, "test"));

        var absEntries = new[]
        {
            new ZipEntryInfo("/etc/passwd", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(absEntries, "test"));

        var driveEntries = new[]
        {
            new ZipEntryInfo("C:\\evil.txt", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(driveEntries, "test"));

        var driveRelative = new[]
        {
            new ZipEntryInfo("C:evil.txt", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(driveRelative, "test"));

        var uncEntries = new[]
        {
            new ZipEntryInfo("\\\\server\\share\\evil.txt", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(uncEntries, "test"));
    }

    [Fact]
    public void ValidateEntryNames_RejectsMixedSeparators_And_CaseCollisions()
    {
        var mixed = new[]
        {
            new ZipEntryInfo("foo\\bar/baz.txt", false, false),
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(mixed, "test"));

        var caseCollide = new[]
        {
            new ZipEntryInfo("Config/one.txt", false, false),
            new ZipEntryInfo("config/one.txt", false, false)
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(caseCollide, "test"));
    }

    [Fact]
    public void ValidateEntryNames_RejectsLinks()
    {
        var entries = new[]
        {
            new ZipEntryInfo("linked.txt", false, true)
        };
        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateEntryNames(entries, "test"));
    }

    [Fact]
    public void ValidateExtractedTree_RejectsSymlink()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "real.txt");
        File.WriteAllText(target, "ok");
        var linkPath = Path.Combine(temp.Path, "link.txt");

        if (!TryCreateSymlink(linkPath, target))
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateExtractedTree(temp.Path));
    }

    [Fact]
    public void ValidateExtractedTree_RejectsHardlinksOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "source.txt");
        File.WriteAllText(source, "ok");
        var link = Path.Combine(temp.Path, "hardlink.txt");

        if (!TryCreateHardLink(link, source))
        {
            return;
        }

        Assert.Throws<InvalidDataException>(() => ZipHandler.ValidateExtractedTree(temp.Path));
    }

    private static bool TryCreateSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        try
        {
            return CreateHardLink(linkPath, targetPath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reclaw-test-{Guid.NewGuid():N}");

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
