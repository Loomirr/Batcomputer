namespace Batcomputer;

/// <summary>
/// Carries a known transient sharing/lock failure across service-result and UI-layer boundaries.
/// The original OS exception is retained when one is available, but callers never need to infer
/// retryability from localized exception text.
/// </summary>
internal sealed class TransientFileLockException : IOException
{
    internal TransientFileLockException(string message)
        : base(message)
    {
    }

    internal TransientFileLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class FileLockUtil
{
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>
    /// Identifies the Windows sharing/lock violations that can clear after an asset viewer or a
    /// concurrent staging pass releases a generated file. Other IO failures (missing files,
    /// invalid paths, permissions) are deterministic and should fail immediately instead of being
    /// hidden behind retries.
    /// </summary>
    internal static bool IsTransient(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is TransientFileLockException)
            {
                return true;
            }

            if (current is not IOException io)
            {
                continue;
            }

            var win32Code = io.HResult & 0xFFFF;
            if (win32Code is SharingViolation or LockViolation)
            {
                return true;
            }
        }
        return false;
    }
}
