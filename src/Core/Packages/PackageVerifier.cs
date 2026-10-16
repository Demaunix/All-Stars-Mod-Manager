using System.Security.Cryptography;

namespace AllStarsModManager;

public enum GameFit { NotChecked, Matches, Mismatch, Unknown }

public sealed class VerifyOptions
{
    public HashCache Cache = new();
    public string? GameRoot;
    /// <summary>Re-hash everything instead of trusting the (length, time) cache.</summary>
    public bool Fresh;
    /// <summary>Also rebuild each archive the way the loader does and compare the resulting hash.</summary>
    public bool Deep;
    public IProgress<long>? Progress;
    public DeepCache? DeepCache;
}

public sealed class VerifyResult
{
    public Integrity Integrity;
    public GameFit Fit;
    public List<Issue> Issues = new();
    public long BytesHashed;
}

/// <summary>Remembers which composed archives were already proven for a given plan and base-file state.</summary>
public sealed class DeepCache
{
    readonly HashSet<string> _ok = new(StringComparer.Ordinal);
    readonly object _gate = new();
    readonly string? _file;
    public DeepCache(string? file = null)
    {
        _file = file;
        if (file is not null && File.Exists(file))
            try { foreach (var l in File.ReadAllLines(file)) if (l.Length > 0) _ok.Add(l); } catch { }
    }
    public static string Key(string planSha, string logicalKey, string baseSha, long baseLen, long baseMtime) => $"{planSha}|{logicalKey}|{baseSha}|{baseLen}|{baseMtime}";
    public bool Has(string k) { lock (_gate) return _ok.Contains(k); }
    public void Add(string k)
    {
        lock (_gate) _ok.Add(k);
        if (_file is null) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); File.AppendAllLines(_file, new[] { k }); } catch { }
    }
}

public static class PackageVerifier
{
    /// <summary>
    /// Hashes every file the plan depends on and (when a game folder is known) checks the plan's base hashes against
    /// the real game files. Does not modify the package and never loads any code.
    /// </summary>
    public static VerifyResult Verify(PackageInfo pkg, VerifyOptions o, CancellationToken ct = default)
    {
        var r = new VerifyResult { Integrity = Integrity.Verified, Fit = GameFit.NotChecked };
        var counting = o.Progress;
        void Block(string code, string msg, string? detail = null) { r.Issues.Add(new(Severity.Blocker, code, msg, detail)); }
        bool structural = pkg.Issues.Any(i => i.Severity == Severity.Blocker);
        if (structural) r.Integrity = Integrity.Failed;
        if (pkg.Plan is null) { if (pkg.HasManifest && !structural) r.Integrity = Integrity.NotChecked; r.Fit = GameFit.Unknown; return Finish(r); }

        // Package-internal files: sizes and hashes the plan pins.
        foreach (var g in pkg.Plan.Groups.Where(g => g.Valid))
            foreach (var p in g.Payloads)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(p.FullPath)) { Block("payload-missing", $"A file the plan needs is missing: {PackageInfo.Clean(p.Rel, 100)}"); continue; }
                var len = new FileInfo(p.FullPath).Length;
                if (p.ExpectedSize is { } es && es != len) { Block("payload-size", $"A file has a different size than the plan expects: {PackageInfo.Clean(p.Rel, 100)}", $"expected {es}, found {len}"); continue; }
                var actual = TryHash(o, p.FullPath, counting, ct, out var hashError);
                if (actual is null) { Block("payload-unreadable", $"A file the plan needs cannot be read, so it cannot be checked: {PackageInfo.Clean(p.Rel, 100)}", hashError); continue; }
                if (actual != p.Sha) Block("payload-hash", $"A file does not match the hash in the plan, so the loader would refuse it: {PackageInfo.Clean(p.Rel, 100)}", $"expected {p.Sha}, found {actual}");
            }
        if (pkg.Sidecar is { } sc)
            foreach (var a in sc.Artifacts)
            {
                // A listed file may live in the mod folder or (for a whole package) next to it at the package root; a file that is
                // simply not here is not a problem (it may not travel with this copy), but one that is here must match.
                foreach (var baseDir in new[] { pkg.Dir, pkg.PackageRoot }.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var full = LoaderPath.Child(baseDir!, a.Path);
                        LoaderPath.NoReparse(baseDir!, full);
                        if (!File.Exists(full)) continue;
                        if (Sha.IsLoaderHash(a.Sha))
                        {
                            var ah = TryHash(o, full, counting, ct, out var artifactError);
                            if (ah is null) r.Issues.Add(new(Severity.Warning, "artifact-unreadable", $"A file listed in the description cannot be read: {PackageInfo.Clean(a.Path, 80)}", artifactError));
                            else if (ah != a.Sha) r.Issues.Add(new(Severity.Warning, "artifact-hash", $"A file differs from the description's listing: {PackageInfo.Clean(a.Path, 80)}"));
                        }
                        break;
                    }
                    catch (PathRuleException) { r.Issues.Add(new(Severity.Warning, "artifact-path", $"The description lists an unsafe path: {PackageInfo.Clean(a.Path, 80)}")); break; }
                }
            }        VerifyLooseAssetSources(pkg, o, r, ct);

        if (r.Issues.Any(i => i.Severity == Severity.Blocker) || structural) r.Integrity = Integrity.Failed;

        if (o.GameRoot is not null) CheckAgainstGame(pkg, o, r, ct);
        return Finish(r);
    }

    static VerifyResult Finish(VerifyResult r) => r;

    /// <summary>Hashes a file, or returns null with the reason when it cannot be read (locked, access denied): a read failure is a finding, never a crash of the whole check.</summary>
    static string? TryHash(VerifyOptions o, string path, IProgress<long>? progress, CancellationToken ct, out string? error)
    {
        error = null;
        try { return o.Cache.Get(path, progress, ct, o.Fresh); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return null; }
    }

    /// <summary>Stores a verification outcome on the package, replacing any earlier one.</summary>
    public static void Apply(PackageInfo pkg, VerifyResult r)
    {
        pkg.Integrity = r.Integrity;
        pkg.Fit = r.Fit;
        pkg.VerifyIssues = r.Issues;
    }

    static void VerifyLooseAssetSources(PackageInfo pkg, VerifyOptions o, VerifyResult r, CancellationToken ct)
    {
        foreach (var a in pkg.RequiredLooseAssets)
        {
            if (a.SourceFullPath is null || !File.Exists(a.SourceFullPath)) continue;     // absence is reported by the completeness check
            ct.ThrowIfCancellationRequested();
            var len = new FileInfo(a.SourceFullPath).Length;
            if (a.Bytes >= 0 && len != a.Bytes)
            {
                r.Issues.Add(new(Severity.Blocker, "loose-size", $"Required file {PackageInfo.Clean(a.Target, 80)} has the wrong size.", $"expected {a.Bytes}, found {len}"));
                continue;
            }
            var lh = TryHash(o, a.SourceFullPath, o.Progress, ct, out var looseError);
            if (lh is null) { r.Issues.Add(new(Severity.Blocker, "loose-unreadable", $"Required file {PackageInfo.Clean(a.Target, 80)} cannot be read, so it cannot be checked.", looseError)); continue; }
            if (lh != a.Sha)
                r.Issues.Add(new(Severity.Blocker, "loose-hash", $"Required file {PackageInfo.Clean(a.Target, 80)} does not match its recorded hash.", Subject: a.Target));
        }
    }

    // ---- game fit -----------------------------------------------------------------------

    static void CheckAgainstGame(PackageInfo pkg, VerifyOptions o, VerifyResult r, CancellationToken ct)
    {
        r.Fit = GameFit.Matches;
        var plan = pkg.Plan!;
        foreach (var g in plan.Groups.Where(g => g.Valid && g.BaseSha is not null))
        {
            ct.ThrowIfCancellationRequested();
            string logical;
            try { logical = LoaderPath.Child(o.GameRoot!, g.LogicalRel); LoaderPath.NoReparse(o.GameRoot!, logical); }
            catch (PathRuleException e) { r.Issues.Add(new(Severity.Blocker, "game-path", $"{g.LogicalDisplay}: {e.Message}")); r.Fit = GameFit.Mismatch; continue; }
            if (!File.Exists(logical))
            {
                r.Issues.Add(new(Severity.Blocker, "base-missing", $"This mod changes {g.LogicalDisplay}, which was not found in your game folder."));
                r.Fit = GameFit.Mismatch; continue;
            }
            var baseHash = TryHash(o, logical, o.Progress, ct, out var baseError);
            if (baseHash is null)
            {
                r.Issues.Add(new(Severity.Blocker, "base-unreadable", $"{g.LogicalDisplay} in your game folder cannot be read, so this mod cannot be compared with it.", baseError));
                r.Fit = GameFit.Mismatch; continue;
            }
            if (baseHash != g.BaseSha)
            {
                r.Issues.Add(new(Severity.Blocker, "base-mismatch", $"{g.LogicalDisplay} in your game folder is not the version this mod was made for, so the loader would refuse it.", $"expected {g.BaseSha}, found {baseHash}"));
                r.Fit = GameFit.Mismatch; continue;
            }
            if (g.Format != "xpac") continue;
            try { CheckXpacGroup(pkg, g, logical, o, r, ct); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                r.Issues.Add(new(Severity.Blocker, "xpac-unreadable", $"{g.LogicalDisplay} could not be checked: {e.Message}"));
                r.Fit = GameFit.Mismatch;
            }
        }
        if (r.Issues.Any(i => i.Severity == Severity.Blocker) && r.Fit == GameFit.Matches) r.Fit = GameFit.Mismatch;
    }

    static void CheckXpacGroup(PackageInfo pkg, PlanGroup g, string basePath, VerifyOptions o, VerifyResult r, CancellationToken ct)
    {
        var idx = XpacIndex.Read(basePath);
        foreach (var op in g.Ops)
        {
            bool exists = idx.Members.TryGetValue(op.Key, out var m);
            if (op.Kind == "replace" && !exists) { r.Issues.Add(new(Severity.Blocker, "member-missing", $"{g.LogicalDisplay}: member {op.Key:X8} to replace is not in your game file.")); r.Fit = GameFit.Mismatch; continue; }
            if (op.Kind == "add" && exists) { r.Issues.Add(new(Severity.Blocker, "member-exists", $"{g.LogicalDisplay}: member {op.Key:X8} to add already exists in your game file.")); r.Fit = GameFit.Mismatch; continue; }
            if (op.Kind == "replace" && op.BaseStoredSha is not null)
            {
                using var fs = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (Sha.OfRange(fs, m.Offset, m.Stored, ct) != op.BaseStoredSha)
                {
                    r.Issues.Add(new(Severity.Blocker, "member-hash", $"{g.LogicalDisplay}: member {op.Key:X8} in your game file is not the one this mod expects."));
                    r.Fit = GameFit.Mismatch;
                }
            }
        }
        if (!o.Deep) return;
        var fi = new FileInfo(basePath);
        var key = DeepCache.Key(pkg.Manifest?.PlanSha ?? "", g.LogicalKey, g.BaseSha!, fi.Length, fi.LastWriteTimeUtc.Ticks);
        if (!o.Fresh && o.DeepCache?.Has(key) == true) return;
        var (sha, size, count) = XpacComposer.Compose(basePath, idx, g, o.Progress, ct);
        if (sha != g.ExpectedComposedSha || size != g.ExpectedComposedSize || count != g.ExpectedMemberCount)
        {
            r.Issues.Add(new(Severity.Blocker, "composed-mismatch", $"{g.LogicalDisplay}: the rebuilt archive does not match what the plan promises, so the loader would refuse it.", $"expected {g.ExpectedComposedSha} / {g.ExpectedComposedSize} bytes / {g.ExpectedMemberCount} members; rebuilt {sha} / {size} / {count}"));
            r.Fit = GameFit.Mismatch;
        }
        else o.DeepCache?.Add(key);
    }
}

/// <summary>Read-only view of the member table of an .xpac archive, with the same sanity rules as the loader.</summary>
public sealed class XpacIndex
{
    public readonly record struct Member(uint Key, uint Offset, uint Stored);
    public uint DataStart;
    public long FileLength;
    public SortedDictionary<uint, Member> Members { get; } = new();

    public static XpacIndex Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length < 24 || length > uint.MaxValue) throw new InvalidDataException("Invalid archive length");
        Span<byte> head = stackalloc byte[24];
        fs.ReadExactly(head);
        uint h0 = BitConverter.ToUInt32(head[0..]), h1 = BitConverter.ToUInt32(head[4..]), h2 = BitConverter.ToUInt32(head[8..]),
             count = BitConverter.ToUInt32(head[12..]), h4 = BitConverter.ToUInt32(head[16..]), h5 = BitConverter.ToUInt32(head[20..]);
        if (h0 != 0 || h1 != 0 || h4 != 0 || h5 != 0 || count == 0 || count >= 1_000_000 || 24UL + 20UL * count > h2 || h2 > length)
            throw new InvalidDataException("Invalid archive header");
        var idx = new XpacIndex { DataStart = h2, FileLength = length };
        var rows = new byte[count * 20];
        fs.ReadExactly(rows);
        var ranges = new List<(ulong a, ulong b)>();
        for (int i = 0; i < count; i++)
        {
            var s = rows.AsSpan(i * 20, 20);
            uint key = BitConverter.ToUInt32(s[0..]), off = BitConverter.ToUInt32(s[4..]), size2 = BitConverter.ToUInt32(s[8..]), stored = BitConverter.ToUInt32(s[12..]), flags = BitConverter.ToUInt32(s[16..]);
            if (off < h2 || (ulong)off + stored > (ulong)length || size2 != stored || flags != 0) throw new InvalidDataException("Unsupported archive record");
            if (!idx.Members.TryAdd(key, new Member(key, off, stored))) throw new InvalidDataException("Duplicate archive key");
            if (stored > 0) ranges.Add((off, (ulong)off + stored));
        }
        ranges.Sort();
        for (int i = 1; i < ranges.Count; i++) if (ranges[i].a < ranges[i - 1].b) throw new InvalidDataException("Overlapping archive records");
        return idx;
    }
}

/// <summary>
/// Rebuilds an archive exactly as the loader's virtual layout presents it (members sorted by key, 2048-byte
/// alignment, rewritten directory) and hashes the result, without writing it anywhere.
/// </summary>
public static class XpacComposer
{
    const uint Align = 2048;
    static ulong Up(ulong n) { var a = (n + Align - 1) & ~(ulong)(Align - 1); if (a > uint.MaxValue) throw new InvalidDataException("Archive would exceed 32-bit offsets"); return a; }

    sealed record Src(string? PayloadPath, uint BaseOffset, uint Length);

    public static (string Sha, uint Size, uint Count) Compose(string basePath, XpacIndex idx, PlanGroup g, IProgress<long>? progress, CancellationToken ct)
    {
        var members = new SortedDictionary<uint, Src>();
        foreach (var m in idx.Members.Values) members[m.Key] = new Src(null, m.Offset, m.Stored);
        int payloadIndex = 0;
        foreach (var op in g.Ops)
        {
            var payload = g.Payloads[payloadIndex++];
            members[op.Key] = new Src(payload.FullPath, 0, (uint)new FileInfo(payload.FullPath).Length);
        }
        uint count = (uint)members.Count;
        uint dirEnd = (uint)Up(24UL + 20UL * count);

        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var meta = new byte[dirEnd];
        BitConverter.GetBytes(0u).CopyTo(meta, 0);
        BitConverter.GetBytes(0u).CopyTo(meta, 4);
        BitConverter.GetBytes(dirEnd).CopyTo(meta, 8);
        BitConverter.GetBytes(count).CopyTo(meta, 12);
        ulong cursor = dirEnd;
        int i = 0;
        foreach (var (key, s) in members)
        {
            var row = meta.AsSpan(24 + 20 * i++, 20);
            BitConverter.GetBytes(key).CopyTo(row[0..]);
            BitConverter.GetBytes((uint)cursor).CopyTo(row[4..]);
            BitConverter.GetBytes(s.Length).CopyTo(row[8..]);
            BitConverter.GetBytes(s.Length).CopyTo(row[12..]);
            BitConverter.GetBytes(0u).CopyTo(row[16..]);
            cursor = Up(cursor + s.Length);
        }
        h.AppendData(meta);
        progress?.Report(meta.Length);

        using var baseStream = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var zeros = new byte[Align];
        var buf = new byte[1 << 20];
        ulong pos = dirEnd;
        foreach (var (key, s) in members)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Length > 0)
            {
                if (s.PayloadPath is null)
                {
                    baseStream.Seek(s.BaseOffset, SeekOrigin.Begin);
                    Copy(baseStream, h, s.Length, buf, progress);
                }
                else
                {
                    using var pf = new FileStream(s.PayloadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    Copy(pf, h, s.Length, buf, progress);
                }
            }
            ulong end = Up(pos + s.Length);
            ulong pad = end - pos - s.Length;
            while (pad > 0) { int n = (int)Math.Min(pad, (ulong)zeros.Length); h.AppendData(zeros, 0, n); pad -= (ulong)n; }
            pos = end;
        }
        return (Convert.ToHexStringLower(h.GetHashAndReset()), (uint)pos, count);
    }

    static void Copy(Stream from, IncrementalHash h, uint length, byte[] buf, IProgress<long>? progress)
    {
        ulong left = length;
        while (left > 0)
        {
            int n = from.Read(buf, 0, (int)Math.Min(left, (ulong)buf.Length));
            if (n <= 0) throw new EndOfStreamException("A file ended earlier than expected.");
            h.AppendData(buf, 0, n);
            progress?.Report(n);
            left -= (ulong)n;
        }
    }
}
