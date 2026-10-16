using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllStarsModManager;

/// <summary>Where the manager keeps its own files. Portable: next to the program unless told otherwise. Never inside a game folder.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    public string LibraryDir => Path.Combine(DataDir, "library");
    public string IncomingDir => Path.Combine(DataDir, "incoming");
    public string CacheDir => Path.Combine(DataDir, "cache");
    public string LogFile => Path.Combine(DataDir, "manager.log");

    public AppPaths(string dataDir) => DataDir = LoaderPath.Full(dataDir);

    public static AppPaths Portable(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return new AppPaths(overrideDir);
        var exeDir = AppContext.BaseDirectory;
        var portable = Path.Combine(exeDir, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new AppPaths(portable);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Program folder is read-only (for example Program Files): fall back to the user's own application data.
            return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllStarsModManager"));
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
    }
}

public sealed class Settings
{
    public int Version { get; set; } = 1;
    public string? GameRoot { get; set; }
    public List<string> LinkedFolders { get; set; } = new();
    public string? ActiveProfile { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool ShowTechnicalDetails { get; set; }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile)) return JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(paths.SettingsFile), Opts) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new Settings();
    }

    public void Save(AppPaths paths)
    {
        paths.EnsureCreated();
        AtomicFile.WriteAllBytes(paths.SettingsFile, JsonSerializer.SerializeToUtf8Bytes(this, Opts));
    }
}

public sealed class ProfileEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? PlanSha256 { get; set; }
    public string? RuntimeSha256 { get; set; }
    public string? FolderHint { get; set; }
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public List<ProfileEntry> Mods { get; set; } = new();
}

public sealed class ProfileStore
{
    readonly AppPaths _paths;
    public List<Profile> Profiles { get; private set; } = new();
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ProfileStore(AppPaths paths)
    {
        _paths = paths;
        try
        {
            if (File.Exists(paths.ProfilesFile)) Profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllBytes(paths.ProfilesFile), Opts) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException) { Profiles = new(); }
    }

    public Profile? Find(string? name) => name is null ? null : Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Save(Profile p)
    {
        var existing = Find(p.Name);
        if (existing is not null) Profiles.Remove(existing);
        Profiles.Add(p);
        Profiles = Profiles.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Persist();
    }

    public void Delete(string name)
    {
        var p = Find(name);
        if (p is null) return;
        Profiles.Remove(p);
        Persist();
    }

    public void Rename(string oldName, string newName)
    {
        var p = Find(oldName) ?? throw new InvalidOperationException("No such profile.");
        if (Find(newName) is not null && !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A profile with that name already exists.");
        p.Name = newName;
        Profiles = Profiles.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Persist();
    }

    void Persist()
    {
        _paths.EnsureCreated();
        AtomicFile.WriteAllBytes(_paths.ProfilesFile, JsonSerializer.SerializeToUtf8Bytes(Profiles, Opts));
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the profile a name.";
        if (name.Trim().Length > 60) return "Use 60 characters or fewer.";
        if (name.Any(char.IsControl)) return "Use plain text for the name.";
        return null;
    }

    /// <summary>Entries identify content (ID plus the two hashes the loader pins), not folder names or labels.</summary>
    public static Profile FromSelection(string name, IEnumerable<ModEntry> active) => new()
    {
        Name = name.Trim(),
        Mods = active.Where(e => e.Id is not null).Select(e => new ProfileEntry
        {
            Id = e.Id!, Name = e.Package.DisplayName, Version = e.Package.Version,
            PlanSha256 = e.Package.Manifest?.PlanSha, RuntimeSha256 = e.Package.Manifest?.RuntimeSha, FolderHint = e.InstalledFolder
        }).ToList()
    };

    public sealed class Resolution
    {
        public Dictionary<string, bool> Want = new();
        public List<string> Missing = new();
        public List<string> Approximate = new();
    }

    /// <summary>Maps a profile onto the known mods. Exact content wins; same ID and version next; otherwise the entry is reported as missing — never silently substituted.</summary>
    public static Resolution Resolve(Profile p, IReadOnlyList<ModEntry> entries)
    {
        var r = new Resolution();
        foreach (var e in entries) r.Want[e.Key] = false;
        foreach (var m in p.Mods)
        {
            var exact = entries.FirstOrDefault(e => e.Id == m.Id && e.Package.Manifest?.PlanSha == m.PlanSha256 && e.Package.Manifest?.RuntimeSha == m.RuntimeSha256);
            if (exact is not null) { r.Want[exact.Key] = true; continue; }
            var sameVersion = entries.Where(e => e.Id == m.Id && e.Package.Version == m.Version).ToList();
            if (sameVersion.Count > 0)
            {
                r.Want[sameVersion[0].Key] = true;
                r.Approximate.Add($"{m.Name ?? m.Id} {m.Version}: the files differ from the ones saved in this profile (same ID and version).");
                continue;
            }
            var label = $"{m.Name ?? m.Id}{(string.IsNullOrEmpty(m.Version) ? "" : " " + m.Version)}";
            var other = entries.FirstOrDefault(e => e.Id == m.Id);
            r.Missing.Add(other is null ? $"{label} is not installed or in your library." : $"{label} is not available; only {other.Package.Version ?? "another version"} was found.");
        }
        return r;
    }
}
