using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AllStarsModManager;

public static class Sha
{
    const int BufferSize = 1 << 20;

    public static string OfBytes(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string OfFile(string path, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return OfStream(fs, progress, ct);
    }

    public static string OfStream(Stream s, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int n;
            while ((n = s.Read(buf, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                h.AppendData(buf, 0, n);
                progress?.Report(n);
            }
            return Convert.ToHexStringLower(h.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>Hash of a byte range of an open, seekable stream (used for archive members).</summary>
    public static string OfRange(Stream s, long offset, long length, CancellationToken ct = default)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        s.Seek(offset, SeekOrigin.Begin);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (length > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(length, BufferSize);
                int n = s.Read(buf, 0, want);
                if (n <= 0) throw new EndOfStreamException("File ended inside a hashed range.");
                h.AppendData(buf, 0, n);
                length -= n;
            }
            return Convert.ToHexStringLower(h.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>The loader only accepts exactly 64 lowercase hex digits (it compares against its own lowercase output).</summary>
    public static bool IsLoaderHash(string? s)
    {
        if (s is not { Length: 64 }) return false;
        foreach (var c in s) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    public static bool LooksLikeHashIgnoringCase(string? s)
    {
        if (s is not { Length: 64 }) return false;
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}

/// <summary>
/// Remembers file hashes by (path, length, last-write time). Used only to avoid rehashing multi-hundred-MB
/// files on every refresh; operations that mutate the game folder re-hash anything they change.
/// </summary>
public sealed class HashCache
{
    sealed record Entry(long Length, long MtimeTicks, string Sha);
    readonly Dictionary<string, Entry> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    readonly string? _file;
    bool _dirty;

    public HashCache(string? persistPath = null)
    {
        _file = persistPath;
        if (_file is not null && File.Exists(_file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var e = p.Value;
                    _items[p.Name] = new Entry(e.GetProperty("len").GetInt64(), e.GetProperty("mtime").GetInt64(), e.GetProperty("sha").GetString() ?? "");
                }
            }
            catch { _items.Clear(); }   // a damaged cache is only a cache
        }
    }

    public string Get(string path, IProgress<long>? progress = null, CancellationToken ct = default, bool forceFresh = false)
    {
        var fi = new FileInfo(path);
        var key = fi.FullName;
        long len = fi.Length, mt = fi.LastWriteTimeUtc.Ticks;
        if (!forceFresh)
        {
            lock (_gate)
                if (_items.TryGetValue(key, out var e) && e.Length == len && e.MtimeTicks == mt)
                {
                    progress?.Report(len);
                    return e.Sha;
                }
        }
        var sha = Sha.OfFile(key, progress, ct);
        // Re-stat after hashing so a file that changed while being read is not cached under stale metadata.
        var after = new FileInfo(key);
        if (after.Length == len && after.LastWriteTimeUtc.Ticks == mt)
            lock (_gate) { _items[key] = new Entry(len, mt, sha); _dirty = true; }
        return sha;
    }

    public void Save()
    {
        if (_file is null) return;
        Dictionary<string, Entry> copy;
        lock (_gate)
        {
            if (!_dirty) return;
            copy = new(_items);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartObject();
                foreach (var (k, e) in copy)
                {
                    w.WriteStartObject(k);
                    w.WriteNumber("len", e.Length);
                    w.WriteNumber("mtime", e.MtimeTicks);
                    w.WriteString("sha", e.Sha);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            AtomicFile.WriteAllBytes(_file, ms.ToArray());
        }
        catch { /* cache persistence is best effort */ }
    }
}
