using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Threading;
using System.Threading.Tasks;

namespace ReClaw.Core.IO
{
    public static class TarHandler
    {
        public static async Task ExtractTarGzAsync(Stream compressedStream, string destination, CancellationToken ct = default)
        {
            using var gz = new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
            using var tr = new TarReader(gz, leaveOpen: true);
            TarEntry? entry;
            while ((entry = tr.GetNextEntry()) != null)
            {
                var outPath = Path.Combine(destination, entry.Name);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(outPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? destination);
                using var outFs = File.Create(outPath);
                if (entry.DataStream is null)
                {
                    throw new InvalidDataException($"Missing data stream for entry '{entry.Name}'.");
                }
                await entry.DataStream.CopyToAsync(outFs, 16 * 1024, ct).ConfigureAwait(false);
            }
        }

        public static async Task CreateTarGzFromDirectoryAsync(string sourceDir, Stream outputGzStream, CancellationToken ct = default)
        {
            using var gz = new GZipStream(outputGzStream, CompressionLevel.Optimal, leaveOpen: true);
            using var tw = new TarWriter(gz, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string entryName = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                tw.WriteEntry(file, entryName);
            }
            await gz.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
