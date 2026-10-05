using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
namespace MusicMachine.App.Updating;

public static class UpdateArchive
{
    public const long MaximumExpandedSize = 512L * 1024 * 1024;
    public const long MaximumEntrySize = 256L * 1024 * 1024;
    public const int MaximumEntries = 10000;
    public static void Extract(string archive, string destination, CancellationToken cancellation = default)
    {
        UpdatePaths.CreatePrivateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("Extraction requires an empty staging directory.");
        var root = UpdatePaths.Normalize(destination); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; var entries = 0;
        string PathFor(string name, long length, bool directory)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++entries > MaximumEntries || length < 0 || length > MaximumEntrySize || (expanded += length) > MaximumExpandedSize) throw new InvalidDataException("Update archive exceeds its size or entry limit.");
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/'); if (directory && name is "" or ".") return root;
            if (name.Length is 0 or > 512 || name.StartsWith('/') || name.Contains('\\') || name.Any(char.IsControl)) throw new InvalidDataException("Unsafe archive path.");
            var parts = name.Split('/');
            foreach (var part in parts)
            {
                var stem = part.Split('.')[0];
                if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])) throw new InvalidDataException("Unsafe archive filename.");
            }
            if (!ManagedRoot(parts[0])) throw new InvalidDataException("Unexpected file in update payload: " + parts[0]);
            if (!names.Add(name)) throw new InvalidDataException("Duplicate archive path.");
            var path = Path.GetFullPath(Path.Combine(root, name)); if (!UpdatePaths.Within(path, root)) throw new InvalidDataException("Archive path escapes staging.");
            UpdatePaths.EnsureNoLinks(path); return path;
        }
        UpdatePaths.EnsureNoLinks(archive); UpdatePaths.CheckOwnedEntry(archive); using var file = File.OpenRead(archive);
        if (file.Length > ReleaseClient.MaximumPackageSize) throw new InvalidDataException("Update archive exceeds its compressed size limit.");
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            PreflightZip(file);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                cancellation.ThrowIfCancellationRequested(); var type = (entry.ExternalAttributes >> 16) & 0xf000; var directory = entry.FullName.EndsWith('/');
                if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || type == 0x4000 && !directory || directory && entry.Length != 0) throw new InvalidDataException("Archives cannot contain links or special files.");
                var path = PathFor(entry.FullName, entry.Length, directory);
                if (directory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); Copy(input, output, entry.Length, cancellation);
            }
        }
        else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new GZipStream(file, CompressionMode.Decompress); using var limited = new BoundedReadStream(gzip, MaximumExpandedSize + 16 * 1024 * 1024, cancellation); using var tar = new TarReader(limited);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("Archives cannot contain links or special files.");
                var path = PathFor(entry.Name, entry.Length, entry.EntryType == TarEntryType.Directory);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    if (entry.DataStream is { } input) Copy(input, output, entry.Length, cancellation); else if (entry.Length != 0) throw new InvalidDataException("Truncated archive entry.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead | (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)));
            }
            // Consume the bounded stream to check gzip trailer integrity and reject excessive padding.
            var buffer = new byte[8192]; while (limited.Read(buffer) != 0) { }
        }
        else throw new InvalidDataException("Unsupported update archive format.");
    }
    private static void PreflightZip(FileStream file)
    {
        // ZipArchive creates its entire central-directory list before Entries can be bounded.
        // Our small release packages need neither ZIP64 nor split archives: reject both before allocation.
        var tail = new byte[(int)Math.Min(file.Length, 65557)]; file.Position = file.Length - tail.Length; file.ReadExactly(tail);
        var found = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length) { found = i; break; }
        if (found < 0) throw new InvalidDataException("Missing ZIP central directory.");
        var end = tail.AsSpan(found); var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]); var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count || count > MaximumEntries || size > 8 * 1024 * 1024 || (long)offset + size != file.Length - tail.Length + found) throw new InvalidDataException("Split, ZIP64 or oversized ZIP directory is unsupported.");
        file.Position = offset; Span<byte> header = stackalloc byte[46]; long expanded = 0;
        for (var i = 0; i < count; i++)
        {
            file.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]); var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50 || (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 1) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) is not (0 or 8) || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0 || length > MaximumEntrySize || (expanded += length) > MaximumExpandedSize || nameLength is 0 or > 2048 || extraLength > 4096 || commentLength > 4096 || BinaryPrimitives.ReadUInt32LittleEndian(header[42..]) >= offset) throw new InvalidDataException("Unsafe ZIP central-directory entry.");
            file.Position += nameLength + extraLength + commentLength;
            if (file.Position > (long)offset + size) throw new InvalidDataException("Truncated ZIP directory.");
        }
        if (file.Position != (long)offset + size) throw new InvalidDataException("ZIP entry counts do not match.");
        file.Position = 0;
    }
    private static bool ManagedRoot(string root) => root is "MusicMachine.Desktop" or "MusicMachine.Desktop.exe" or "release.json" or "LICENSE" or "README.md" or "musicmachine.ico" or "musicmachine.svg" or "docs" or "licenses" or "examples" || root.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || root.EndsWith(".so", StringComparison.Ordinal);
    internal static void Copy(Stream input, Stream output, long expected, CancellationToken cancellation)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { cancellation.ThrowIfCancellationRequested(); total += count; if (total > expected) throw new InvalidDataException("Archive entry exceeds its declared length."); output.Write(buffer, 0, count); }
        if (total != expected) throw new InvalidDataException("Truncated archive entry.");
    }
    private sealed class BoundedReadStream(Stream input, long limit, CancellationToken cancellation) : Stream
    {
        private long read;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { cancellation.ThrowIfCancellationRequested(); var count = input.Read(buffer); read += count; if (read > limit) throw new InvalidDataException("Expanded archive stream exceeds its limit."); return count; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
