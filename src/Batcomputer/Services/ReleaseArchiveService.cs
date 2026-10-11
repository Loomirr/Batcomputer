using System.IO.Compression;

namespace Batcomputer;

internal sealed record ReleaseArchiveProgress(string Stage, string Entry, int FileIndex, int FileCount, long Bytes, long TotalBytes);

internal static class ReleaseArchiveService
{
    internal static async Task CreateAsync(string destination, IReadOnlyList<(string Source, string ArchivePath)> files,
        IProgress<ReleaseArchiveProgress>? progress = null)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var lengths = files.Select(f => new FileInfo(f.Source).Length).ToArray();
            var total = lengths.Sum();
            long completed = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var buffer = new byte[131072];
                for (var i = 0; i < files.Count; i++)
                {
                    var (source, entryName) = files[i];
                    progress?.Report(new("Compressing", entryName, i + 1, files.Count, completed, total));
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
                    if (input.Length != lengths[i]) throw new IOException("A release file changed while preparing the ZIP: " + source);
                    using var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                    var lastReport = Environment.TickCount64;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) != 0)
                    {
                        await entry.WriteAsync(buffer.AsMemory(0, read));
                        completed += read;
                        if (Environment.TickCount64 - lastReport >= 150)
                        {
                            progress?.Report(new("Compressing", entryName, i + 1, files.Count, completed, total));
                            lastReport = Environment.TickCount64;
                        }
                    }
                }
                progress?.Report(new("Finalizing ZIP", "", files.Count, files.Count, completed, total));
            }
            // Replace only after the ZIP central directory has been written successfully.
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(new("Archive ready", "", files.Count, files.Count, completed, total));
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* preserve the original error */ }
        }
    }
}
