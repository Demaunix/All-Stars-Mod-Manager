using System.IO.Compression;

namespace AllStarsModManager;

public sealed class ArchiveException(string message) : Exception(message);

public enum ArchiveKind { Unknown, Zip, SevenZip, Rar, GZip, Tar, XZ, BZip2, Cab, Ole, Executable }

public static class ArchiveSniffer
{
    public static ArchiveKind Identify(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[512];
            int n = fs.Read(b, 0, b.Length);
            if (n >= 4 && b[0] == 'P' && b[1] == 'K' && (b[2] == 3 || b[2] == 5 || b[2] == 7)) return ArchiveKind.Zip;
            if (n >= 6 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF && b[4] == 0x27 && b[5] == 0x1C) return ArchiveKind.SevenZip;
            if (n >= 7 && b[0] == 'R' && b[1] == 'a' && b[2] == 'r' && b[3] == '!' && b[4] == 0x1A && b[5] == 0x07) return ArchiveKind.Rar;
            if (n >= 2 && b[0] == 0x1F && b[1] == 0x8B) return ArchiveKind.GZip;
            if (n >= 6 && b[0] == 0xFD && b[1] == '7' && b[2] == 'z' && b[3] == 'X' && b[4] == 'Z') return ArchiveKind.XZ;
            if (n >= 3 && b[0] == 'B' && b[1] == 'Z' && b[2] == 'h') return ArchiveKind.BZip2;
            if (n >= 4 && b[0] == 'M' && b[1] == 'S' && b[2] == 'C' && b[3] == 'F') return ArchiveKind.Cab;
            if (n >= 262 && b[257] == 'u' && b[258] == 's' && b[259] == 't' && b[260] == 'a' && b[261] == 'r') return ArchiveKind.Tar;
            if (n >= 8 && b[0] == 0xD0 && b[1] == 0xCF && b[2] == 0x11 && b[3] == 0xE0) return ArchiveKind.Ole;
            if (n >= 2 && b[0] == 'M' && b[1] == 'Z') return ArchiveKind.Executable;
        }
        catch (IOException) { }
        return ArchiveKind.Unknown;
    }

    public static string? UnsupportedMessage(string path, ArchiveKind kind)
    {
        var name = Path.GetFileName(path);
        return kind switch
        {
            ArchiveKind.SevenZip => $"\"{name}\" is a 7z archive. This version of the manager reads .zip files and folders only. Extract it with 7-Zip into a folder, then add that folder.",
            ArchiveKind.Rar => $"\"{name}\" is a RAR archive. This version of the manager reads .zip files and folders only. Extract it into a folder first, then add that folder.",
            ArchiveKind.GZip or ArchiveKind.Tar or ArchiveKind.XZ or ArchiveKind.BZip2 => $"\"{name}\" is a tar/gzip-style archive. Only .zip files and folders can be added; extract it first.",
            ArchiveKind.Cab or ArchiveKind.Ole => $"\"{name}\" is an installer package, not a mod folder or .zip. Mods are never run or installed from here.",
            ArchiveKind.Executable => $"\"{name}\" is a program. The manager never runs files from mods; add the mod's folder or .zip instead.",
            ArchiveKind.Unknown => $"\"{name}\" is not a folder or a .zip archive, so it cannot be added.",
            _ => null
        };
    }
}

public sealed class ZipLimits
{
    public int MaxEntries = 20_000;
    public long MaxEntryBytes = 4L * 1024 * 1024 * 1024;
    public long MaxTotalBytes = 6L * 1024 * 1024 * 1024;
    public int MaxPathChars = 200;     // inside the extraction folder; keeps results well under the 260 limit
}

/// <summary>
/// Extracts a .zip into a fresh folder without trusting it: every name is validated before anything is written,
/// sizes are enforced on the bytes actually produced (not on what headers claim), and nothing is executed or opened.
/// </summary>
public static class SafeZip
{
    static readonly char[] BadChars = "<>:\"|?*".ToCharArray();

    public static List<string> Extract(string zipPath, string destDir, ZipLimits limits, IProgress<long>? progress, CancellationToken ct)
    {
        if (ZipHeaders.HasEncryptedEntry(zipPath))
            throw new ArchiveException("This .zip is password-protected (or uses encryption), which the manager cannot read. Unpack it yourself with the password, then add the folder.");
        Directory.CreateDirectory(destDir);
        var destFull = LoaderPath.Full(destDir).TrimEnd('\\');
        var written = new List<string>();
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            throw new ArchiveException("The .zip file could not be opened (it may be damaged, split across several files, or password-protected): " + e.Message);
        }
        using (zip)
        {
            if (zip.Entries.Count > limits.MaxEntries) throw new ArchiveException($"The archive contains {zip.Entries.Count:N0} items, more than the {limits.MaxEntries:N0} the manager accepts.");
            var plan = new List<(ZipArchiveEntry E, string Rel, bool IsDir)>();
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // folded path -> isDir
            long total = 0;
            foreach (var e in zip.Entries)
            {
                var (rel, isDir) = ValidateName(e.FullName, limits);
                if (rel.Length == 0) continue;
                if (IsLink(e)) throw new ArchiveException($"\"{Clean(e.FullName)}\" is a link inside the archive. Links are never extracted.");
                if (!isDir)
                {
                    if (e.Length > limits.MaxEntryBytes) throw new ArchiveException($"\"{Clean(e.FullName)}\" is larger than the {limits.MaxEntryBytes >> 30} GB per-file limit.");
                    total += e.Length;
                    if (total > limits.MaxTotalBytes) throw new ArchiveException($"The archive would expand to more than {limits.MaxTotalBytes >> 30} GB, which is more than the manager accepts.");
                }
                if (seen.TryGetValue(rel, out var prevDir)) throw new ArchiveException($"\"{Clean(e.FullName)}\" appears twice in the archive (names are compared ignoring capitals, as Windows does).");
                seen[rel] = isDir;
                plan.Add((e, rel, isDir));
            }
            // A name cannot be both a file and the parent folder of another entry.
            foreach (var (rel, isDir) in seen.Where(kv => !kv.Value))
            {
                var prefix = rel + "\\";
                if (seen.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new ArchiveException($"\"{Clean(rel)}\" is used as both a file and a folder in the archive.");
            }
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(destFull)!);
                if (drive.AvailableFreeSpace < total + (64L << 20)) throw new ArchiveException("There is not enough free disk space to unpack this archive.");
            }
            catch (ArgumentException) { }

            long produced = 0;
            foreach (var (e, rel, isDir) in plan)
            {
                ct.ThrowIfCancellationRequested();
                var target = LoaderPath.Full(Path.Combine(destFull, rel));
                if (!LoaderPath.IsInside(destFull, target)) throw new ArchiveException($"\"{Clean(e.FullName)}\" would be written outside the destination and was refused.");
                LoaderPath.NoReparse(destFull, target);
                if (isDir) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    using var input = e.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    var buf = new byte[1 << 20];
                    long entryBytes = 0; int n; uint crc = 0;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        crc = Crc32.Update(crc, buf, n);
                        entryBytes += n; produced += n;
                        if (entryBytes > e.Length || entryBytes > limits.MaxEntryBytes)
                            throw new ArchiveException($"\"{Clean(e.FullName)}\" produced more data than the archive announced; it was rejected.");
                        if (produced > limits.MaxTotalBytes) throw new ArchiveException("The archive expanded beyond the allowed total size and was stopped.");
                        output.Write(buf, 0, n);
                        progress?.Report(n);
                    }
                    if (entryBytes != e.Length) throw new ArchiveException($"\"{Clean(e.FullName)}\" is shorter than the archive announced; the archive is damaged.");
                    if (crc != e.Crc32) throw new ArchiveException($"\"{Clean(e.FullName)}\" does not match its checksum; the archive is damaged or its headers are wrong.");
                    output.Flush(true);
                }
                catch (InvalidDataException x) { throw new ArchiveException($"\"{Clean(e.FullName)}\" could not be unpacked (damaged, password-protected or an unsupported compression method): {x.Message}"); }
                catch (NotSupportedException x) { throw new ArchiveException($"\"{Clean(e.FullName)}\" uses a compression method the manager cannot read: {x.Message}"); }
                try { File.SetLastWriteTimeUtc(target, e.LastWriteTime.UtcDateTime); } catch (ArgumentOutOfRangeException) { }
                written.Add(rel);
            }
        }
        return written;
    }

    static string Clean(string s) => PackageInfo.Clean(s, 120);

    /// <summary>Normalises an entry name to a safe relative path, or throws with a plain explanation.</summary>
    public static (string Rel, bool IsDir) ValidateName(string name, ZipLimits limits)
    {
        if (name.Length == 0) return ("", false);
        bool isDir = name.EndsWith('/') || name.EndsWith('\\');
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/')) throw new ArchiveException($"\"{Clean(name)}\" is an absolute path.");
        if (n.Length >= 2 && n[1] == ':') throw new ArchiveException($"\"{Clean(name)}\" names a drive.");
        if (n.Any(c => c < 32)) throw new ArchiveException($"\"{Clean(name)}\" contains control characters.");
        var segs = n.TrimEnd('/').Split('/');
        foreach (var s in segs)
        {
            if (s.Length == 0) throw new ArchiveException($"\"{Clean(name)}\" has an empty folder name.");
            if (s == "." || s == "..") throw new ArchiveException($"\"{Clean(name)}\" tries to leave the destination folder.");
            if (s.IndexOfAny(BadChars) >= 0) throw new ArchiveException($"\"{Clean(name)}\" contains characters Windows does not allow in names (or an alternate data stream).");
            if (s[^1] == '.' || s[^1] == ' ') throw new ArchiveException($"\"{Clean(name)}\" has a name ending in a dot or space, which Windows silently rewrites.");
            if (LoaderPath.IsReservedDeviceName(s)) throw new ArchiveException($"\"{Clean(name)}\" uses a reserved Windows device name.");
        }
        var rel = string.Join('\\', segs);
        if (rel.Length > limits.MaxPathChars) throw new ArchiveException($"\"{Clean(name)}\" has a path that is too long.");
        return (rel, isDir);
    }

    static bool IsLink(ZipArchiveEntry e)
    {
        uint attrs = (uint)e.ExternalAttributes;
        // Unix mode lives in the high 16 bits when the archive was made on a Unix-like system.
        uint mode = attrs >> 16;
        if ((mode & 0xF000) == 0xA000) return true;
        // Windows reparse-point attribute in the low word.
        return (attrs & 0x400) != 0;
    }
}

/// <summary>Plain CRC-32 (the zip checksum), so extracted data can be compared with what the archive recorded.</summary>
static class Crc32
{
    static readonly uint[] Table = Build();
    static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    public static uint Update(uint crc, byte[] data, int count)
    {
        uint c = ~crc;
        for (int i = 0; i < count; i++) c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
/// <summary>Reads the zip central directory flags directly: the framework reader ignores the "encrypted" bit and would hand back unreadable bytes.</summary>
static class ZipHeaders
{
    public static bool HasEncryptedEntry(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len < 22) return false;
            int tail = (int)Math.Min(len, 65_557);
            var buf = new byte[tail];
            fs.Seek(len - tail, SeekOrigin.Begin);
            fs.ReadExactly(buf);
            int eocd = -1;
            for (int i = tail - 22; i >= 0; i--)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4B && buf[i + 2] == 0x05 && buf[i + 3] == 0x06) { eocd = i; break; }
            if (eocd < 0) return false;
            ulong count = BitConverter.ToUInt16(buf, eocd + 10);
            ulong cdOffset = BitConverter.ToUInt32(buf, eocd + 16);
            if (count == 0xFFFF || cdOffset == 0xFFFFFFFF)
            {
                // zip64: the locator sits right before the end record and points at the zip64 end record
                int loc = eocd - 20;
                if (loc >= 0 && BitConverter.ToUInt32(buf, loc) == 0x07064b50)
                {
                    long z64 = (long)BitConverter.ToUInt64(buf, loc + 8);
                    var rec = new byte[56];
                    fs.Seek(z64, SeekOrigin.Begin);
                    fs.ReadExactly(rec);
                    if (BitConverter.ToUInt32(rec, 0) == 0x06064b50) { count = BitConverter.ToUInt64(rec, 32); cdOffset = BitConverter.ToUInt64(rec, 48); }
                }
            }
            fs.Seek((long)cdOffset, SeekOrigin.Begin);
            var head = new byte[46];
            for (ulong n = 0; n < count && n < 1_000_000; n++)
            {
                if (fs.Read(head, 0, 46) < 46 || BitConverter.ToUInt32(head, 0) != 0x02014b50) return false;
                ushort flags = BitConverter.ToUInt16(head, 8);
                if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0) return true;
                int skip = BitConverter.ToUInt16(head, 28) + BitConverter.ToUInt16(head, 30) + BitConverter.ToUInt16(head, 32);
                fs.Seek(skip, SeekOrigin.Current);
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException) { }
        return false;
    }
}