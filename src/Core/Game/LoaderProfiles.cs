using System.Reflection;
using System.Text.Json;

namespace AllStarsModManager;

/// <summary>
/// What is known about one exact build of the mod loader (host + bootstrap proxy), keyed by the SHA-256 of the two files. A version label
/// written by a person is never used to decide what a build can do: only a hash match selects a profile.
/// </summary>
public sealed record LoaderProfile(string Id, string Description, string HostSha256, string ProxySha256, uint HostAbi, uint ModIniSchema, uint PlanSchema,
    IReadOnlyList<string> Capabilities, string Evidence, bool UserSupplied = false);

public static class LoaderProfiles
{
    public const string SchemaName = "allstars-loader-profiles/1";
    const string ResourceName = "AllStarsModManager.loader-profiles.json";
    static IReadOnlyList<LoaderProfile>? _builtIn;

    /// <summary>The profiles shipped with this build of the tools (embedded in AllStarsModManager.Core.dll).</summary>
    public static IReadOnlyList<LoaderProfile> BuiltIn => _builtIn ??= LoadBuiltIn();

    static IReadOnlyList<LoaderProfile> LoadBuiltIn()
    {
        using var s = typeof(LoaderProfiles).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The built-in loader profile table is missing from this build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var problems = new List<string>();
        var list = Parse(ms.ToArray(), userSupplied: false, problems);
        if (problems.Count > 0) throw new InvalidOperationException("The built-in loader profile table is invalid: " + problems[0]);
        return list;
    }

    public static LoaderProfile? Find(string id, IEnumerable<LoaderProfile>? extra = null) =>
        BuiltIn.Concat(extra ?? Enumerable.Empty<LoaderProfile>()).FirstOrDefault(p => p.Id.Equals(id, StringComparison.Ordinal));

    /// <summary>Selects a profile only when BOTH hashes equal the profile's pair.</summary>
    public static LoaderProfile? Match(string? hostSha, string? proxySha, IEnumerable<LoaderProfile>? extra = null)
    {
        if (hostSha is null || proxySha is null) return null;
        return BuiltIn.Concat(extra ?? Enumerable.Empty<LoaderProfile>())
            .FirstOrDefault(p => p.HostSha256 == hostSha && p.ProxySha256 == proxySha);
    }

    /// <summary>Reads a user-supplied profile file (same format as the built-in table). Entries are marked user-supplied.</summary>
    public static List<LoaderProfile> LoadFile(string path, List<string> problems)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { problems.Add("The loader profile file does not exist."); return new(); }
            if (fi.Length > 1_000_000) { problems.Add("The loader profile file is larger than 1 MB."); return new(); }
            return Parse(File.ReadAllBytes(path), userSupplied: true, problems);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add("The loader profile file cannot be read: " + e.Message); return new(); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { problems.Add("The loader profile file path is not a valid Windows path."); return new(); }
    }

    public static List<LoaderProfile> Parse(byte[] json, bool userSupplied, List<string> problems)
    {
        var list = new List<LoaderProfile>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var sch) || sch.ValueKind != JsonValueKind.String || sch.GetString() != SchemaName)
            { problems.Add($"Not a loader profile table (expected schema {SchemaName})."); return list; }
            if (!root.TryGetProperty("profiles", out var arr) || arr.ValueKind != JsonValueKind.Array) { problems.Add("The loader profile table has no \"profiles\" list."); return list; }
            int n = 0;
            foreach (var p in arr.EnumerateArray())
            {
                n++;
                string? Str(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                uint Num(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var u) ? u : 0;
                var id = Str("id"); var host = Str("host_sha256"); var proxy = Str("proxy_sha256");
                if (id is null || id.Length is 0 or > 64 || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))) { problems.Add($"Profile {n}: missing or invalid \"id\"."); continue; }
                if (!Sha.IsLoaderHash(host) || !Sha.IsLoaderHash(proxy)) { problems.Add($"Profile {id}: host_sha256 and proxy_sha256 must be 64 lowercase hexadecimal digits."); continue; }
                if (Num("host_abi") == 0) { problems.Add($"Profile {id}: host_abi must be a whole number above 0."); continue; }
                var caps = new List<string>();
                bool capsOk = p.TryGetProperty("capabilities", out var capArr) && capArr.ValueKind == JsonValueKind.Array;
                if (capsOk) foreach (var c in capArr.EnumerateArray()) { if (c.ValueKind == JsonValueKind.String) caps.Add(c.GetString()!); else capsOk = false; }
                if (!capsOk) { problems.Add($"Profile {id}: \"capabilities\" must be a list of names."); continue; }
                if (list.Any(x => x.Id == id) || list.Any(x => x.HostSha256 == host && x.ProxySha256 == proxy)) { problems.Add($"Profile {id}: duplicate id or hash pair."); continue; }
                list.Add(new LoaderProfile(id, Str("description") ?? "", host!, proxy!, Num("host_abi"), Num("mod_ini_schema"), Num("plan_schema"), caps, Str("evidence") ?? "", userSupplied));
            }
        }
        catch (JsonException e) { problems.Add("The loader profile table is not valid JSON: " + PackageInfo.Clean(e.Message, 120)); }
        return list;
    }
}

/// <summary>How much is known about the loader that a check refers to.</summary>
public enum LoaderKnowledge
{
    /// <summary>No game folder was given and no profile was assumed.</summary>
    NotInspected,
    /// <summary>A game folder was inspected but the loader files are not both there.</summary>
    Absent,
    /// <summary>Both loader files are there but their hashes match no known profile.</summary>
    Unrecognized,
    /// <summary>Both loader files are there and their hashes match a known profile.</summary>
    Verified,
    /// <summary>No loader was inspected; the caller chose to evaluate against a named profile.</summary>
    Assumed
}

public sealed class LoaderEvidence
{
    public LoaderKnowledge Knowledge { get; init; } = LoaderKnowledge.NotInspected;
    public LoaderProfile? Profile { get; init; }
    public string? HostSha { get; init; }
    public string? ProxySha { get; init; }
    /// <summary>Computes the SHA-256 of the game executable on first use (null when no game folder was inspected).</summary>
    public Func<string?>? ExeShaProvider { get; init; }
    public bool GameInspected { get; init; }

    string? _exe; bool _exeDone;
    public string? GameExecutableSha
    {
        get
        {
            if (!_exeDone) { _exeDone = true; try { _exe = ExeShaProvider?.Invoke(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _exe = null; } }
            return _exe;
        }
    }

    /// <summary>The profile came from a file the user supplied, not from the table built into the program.</summary>
    public bool ProfileIsUserSupplied => Profile?.UserSupplied == true;

    /// <summary>A profile selected by hash (Verified) or by the caller (Assumed) can answer capability questions; nothing else can.</summary>
    public bool CanAnswerCapabilities => Knowledge is LoaderKnowledge.Verified or LoaderKnowledge.Assumed;

    public static LoaderEvidence NotInspected { get; } = new();

    public static LoaderEvidence Assume(LoaderProfile profile) => new() { Knowledge = LoaderKnowledge.Assumed, Profile = profile, HostSha = profile.HostSha256, ProxySha = profile.ProxySha256 };

    public static LoaderEvidence FromGame(GameFolderInfo game, HashCache? cache = null) => new()
    {
        GameInspected = true,
        HostSha = game.HostSha,
        ProxySha = game.ProxySha,
        Profile = game.LoaderProfile,
        Knowledge = !game.LoaderPresent || game.HostSha is null || game.ProxySha is null ? LoaderKnowledge.Absent
            : game.LoaderProfile is not null ? LoaderKnowledge.Verified : LoaderKnowledge.Unrecognized,
        ExeShaProvider = game.HasGameExe ? () =>
        {
            var exe = Path.Combine(game.Root, GameFolderInfo.ExeName);
            return cache is not null ? cache.Get(exe) : Sha.OfFile(exe);
        } : null
    };
}
