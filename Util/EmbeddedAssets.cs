using System.Reflection;

namespace Batcomputer;

/// <summary>
/// Loads the tool's own artwork out of the assembly. <c>Assets\**\*.png</c> are compiled in as
/// embedded resources, so a published single-file exe carries its icons and part silhouettes with
/// no loose files to lose.
///
/// Falls back to reading from disk when a matching file exists next to the exe (or in the source
/// tree), which keeps a plain <c>dotnet run</c> working if art is added but not yet rebuilt.
/// </summary>
internal static class EmbeddedAssets
{
    private static readonly Assembly Asm = typeof(EmbeddedAssets).Assembly;
    private static readonly Lazy<string[]> Names = new(() => Asm.GetManifestResourceNames());

    /// <summary>
    /// Opens an asset by its logical path, e.g. <c>"Home.png"</c> or <c>"Parts/Head.png"</c>.
    /// Returns null when the asset doesn't exist anywhere.
    /// </summary>
    private static Stream? Open(string relativePath)
    {
        // Embedded names are "<RootNamespace>.<dir>.<dir>.<file>.png" - match on the tail so the
        // caller never has to know the namespace or the folder separator convention.
        var suffix = "." + relativePath.Replace('\\', '/').Replace('/', '.');
        var name = Array.Find(Names.Value, n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            var stream = Asm.GetManifestResourceStream(name);
            if (stream is not null)
            {
                return stream;
            }
        }

        foreach (var root in DiskRoots())
        {
            try
            {
                var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    return File.OpenRead(path);
                }
            }
            catch
            {
                // Unreadable candidate root - try the next.
            }
        }
        return null;
    }

    private static IEnumerable<string> DiskRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets");
        yield return Path.Combine(Application.StartupPath, "Assets");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "Assets");

        // Running from bin\Debug\... in the source tree: walk up to the project folder and use its
        // Assets. Found by looking for the folder, not by name, so renaming the project directory
        // doesn't break it.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "Assets");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
    }

    /// <summary>Reads an embedded/disk asset as raw bytes (for non-image assets like the viewer JS).</summary>
    public static byte[]? ReadBytes(string relativePath)
    {
        using var s = Open(relativePath);
        if (s is null)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>True when the asset exists (embedded or on disk).</summary>
    public static bool Exists(string relativePath)
    {
        using var s = Open(relativePath);
        return s is not null;
    }

    /// <summary>
    /// Loads an asset as a detached <see cref="Bitmap"/>. The caller owns it. Returns null when the
    /// asset is missing or unreadable - every caller has a text/blank fallback.
    /// </summary>
    public static Bitmap? Load(string relativePath)
    {
        try
        {
            using var stream = Open(relativePath);
            if (stream is null)
            {
                return null;
            }

            // Copy through a MemoryStream, then clone: a Bitmap built straight from a stream keeps
            // that stream alive for its lifetime, and the manifest stream is disposed on exit here.
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            using var decoded = new Bitmap(buffer);
            return new Bitmap(decoded);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads an .ico as a real multi-resolution <see cref="Icon"/>. Going through Bitmap would flatten
    /// it to one size and Windows would scale that for the taskbar instead of picking the right frame.
    /// </summary>
    public static Icon? LoadIcon(string relativePath)
    {
        try
        {
            using var stream = Open(relativePath);
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads an asset scaled to <paramref name="size"/> (used for the nav icons).</summary>
    public static Bitmap? Load(string relativePath, Size size)
    {
        using var source = Load(relativePath);
        if (source is null)
        {
            return null;
        }
        try
        {
            return new Bitmap(source, size);
        }
        catch
        {
            return null;
        }
    }
}
