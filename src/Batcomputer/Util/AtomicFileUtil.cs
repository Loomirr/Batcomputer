using System.Text;

namespace Batcomputer;

/// <summary>
/// Writes small tool-owned files through a sibling temporary file, then replaces the destination.
/// A process interruption can therefore leave either the previous complete file or the new complete
/// file, but never a partially serialized settings/project document.
/// </summary>
internal static class AtomicFileUtil
{
    public static void WriteAllText(string path, string contents)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not resolve the parent folder for '{path}'.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { /* best-effort orphan cleanup */ }
            }
        }
    }
}
