using System.Security.Cryptography;

namespace GmailWatcher.Archive.Storage;

/// <summary>
/// Writes archive files so that a crash or a full disk can never leave a half-written file under its final name:
/// data goes to a temporary file in the same folder, is flushed to the physical disk, and only then renamed.
/// The database row that points at a file is committed after this returns, so a row never refers to a missing file.
/// </summary>
internal static class FileStore
{
    private const int BufferSize = 81_920;

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> (replacing a file left by an earlier attempt) and returns its SHA-256 as lowercase hex.</summary>
    public static string WriteAtomic(string path, ReadOnlySpan<byte> data)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var temp = TempName(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return hash;
    }

    /// <summary>
    /// Streams content produced by <paramref name="write"/> into <paramref name="path"/> while counting and hashing it,
    /// so a large attachment is never held in memory twice.
    /// </summary>
    public static (long Size, string Sha256) WriteAtomic(string path, Action<Stream> write)
    {
        var temp = TempName(path);
        try
        {
            long size;
            string hash;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                using var measuring = new HashingStream(stream);
                write(measuring);
                measuring.Flush();
                stream.Flush(flushToDisk: true);
                (size, hash) = (measuring.Length, measuring.Finish());
            }

            File.Move(temp, path, overwrite: true);
            return (size, hash);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Counts and hashes content without keeping it (an attachment that is not saved as a separate file).</summary>
    public static (long Size, string Sha256) Measure(Action<Stream> write)
    {
        using var measuring = new HashingStream(Stream.Null);
        write(measuring);
        measuring.Flush();
        return (measuring.Length, measuring.Finish());
    }

    public static string Sha256Hex(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Marks a file as coming from the internet (NTFS "Zone.Identifier"), so Windows asks before running it and Office
    /// opens documents in Protected View. Mail attachments are untrusted by nature. A no-op where unsupported.
    /// </summary>
    public static void MarkAsFromInternet(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not an NTFS volume (or the stream can't be created): the file is still saved.
        }
    }

    /// <summary>True for "the disk is full" (ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL).</summary>
    public static bool IsDiskFull(Exception exception) =>
        exception is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27;

    private static string TempName(string path) => $"{path}.part-{Guid.NewGuid():N}";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp files are harmless and are overwritten or ignored later.
        }
    }

    /// <summary>A write-only pass-through that counts and hashes what goes through it.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
            _length += buffer.Length;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
