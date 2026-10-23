namespace AllStarsModManager;

public enum Severity { Info = 0, Warning = 1, Blocker = 2 }

/// <summary>A finding about a package or a change. <see cref="Code"/> is stable for tests; <see cref="Message"/> is user text.</summary>
public sealed record Issue(Severity Severity, string Code, string Message, string? Detail = null, string? Subject = null)
{
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}

/// <summary>Classifies stable issue codes for reports: which rules the game's loader itself enforces, and which findings mean "could not be confirmed".</summary>
public static class IssueInfo
{
    static readonly HashSet<string> Unknown = new(StringComparer.Ordinal)
    {
        "sidecar-unreadable", "sidecar-schema", "sidecar-feature", "sidecar-unevaluable", "sidecar-footprint", "sidecar-id", "runtime-exports-unreadable"
    };
    static readonly HashSet<string> Loader = new(StringComparer.Ordinal)
    {
        "manifest-unreadable", "manifest-field", "manifest-schema", "manifest-hash-case", "manifest-hash-format", "manifest-foreign", "link-in-folder", "folder-link",
        "plan-path", "plan-missing", "plan-large", "plan-hash-mismatch", "plan-bom", "plan-invalid", "plan-schema", "plan-group", "plan-unreadable",
        "runtime-path", "runtime-missing", "runtime-unreadable", "runtime-hash-mismatch", "runtime-not-pe", "runtime-wrong-machine", "runtime-not-dll", "runtime-exports-missing",
        "payload-missing", "payload-size", "payload-hash", "payload-unreadable"
    };
    /// <summary>True when the issue says something could not be confirmed (unreadable or unsupported declarations), rather than stating a fact.</summary>
    public static bool IsUnknown(Issue i) => Unknown.Contains(i.Code);
    /// <summary>True when the game's own loader performs the same check, so a failure means it would refuse the whole mod set.</summary>
    public static bool LoaderEnforced(Issue i) => Loader.Contains(i.Code);
}

public enum Integrity { NotChecked, Verified, Failed }

public enum ManifestState
{
    /// <summary>mod.ini exists: the loader will read this folder.</summary>
    Active,
    /// <summary>No mod.ini, exactly one mod.ini.disabled* file.</summary>
    Disabled,
    /// <summary>No mod.ini and several candidate manifests: the user must choose.</summary>
    DisabledAmbiguous,
    /// <summary>No manifest at all: the loader ignores the folder.</summary>
    NoManifest
}

public sealed class PlanPayload
{
    public string Rel = "";
    public string FullPath = "";
    public string Sha = "";
    public long? ExpectedSize;
    public string Role = "";
}

public sealed class PlanOp
{
    public uint Key;
    public string Kind = "";
    public string? BaseStoredSha;
    public string? Name;
    public PlanPayload Payload = null!;
    /// <summary>ChaoGarage 1.2.0 "optional": the change gives way when another mod changes the same member and no variant fits.</summary>
    public bool Optional;
    /// <summary>ChaoGarage 1.2.0 "variants": this mod's versions of the member, each made on top of another mod's exact content.</summary>
    public List<PlanVariant> Variants = new();
}

public sealed class PlanVariant
{
    /// <summary>SHA-256 of the other mod's content for the member that this variant was made on top of.</summary>
    public string OverSha = "";
    public string? OverMod;
    public PlanPayload Payload = null!;
}

public sealed class PlanGroup
{
    public string Format = "";
    public string PathAsWritten = "";
    public string LogicalRel = "";       // loader-normalised, backslashes
    public string LogicalKey = "";       // case-folded, used to detect shared targets
    public string? BaseSha;
    public SortedSet<uint> Keys = new();
    public List<PlanOp> Ops = new();
    public List<PlanPayload> Payloads = new();
    public string? ExpectedComposedSha;
    public uint? ExpectedComposedSize;
    public uint? ExpectedMemberCount;
    public uint? RtscKeySize;
    public bool Valid = true;

    public string LogicalDisplay => LogicalRel.Replace('\\', '/');
}

public sealed class PlanInfo
{
    public uint Schema;
    public string? ModId;
    public List<PlanGroup> Groups = new();
    public int Bytes;
}

public sealed class LooseAsset
{
    public string Target = "";           // relative to game root, forward slashes
    public string Sha = "";
    public long Bytes;
    public string SourceInPackage = "";
    public string Role = "";
    public string? SourceFullPath;       // resolved inside the package, null when the file is not present
    public bool PresentInPackage;
}

public sealed class NativePatch
{
    public string Id = "";
    public uint Va;
    public uint Length;
}

public sealed class Relation
{
    public string Id = "";
    public string Versions = "";
    public string Note = "";
    public string Evidence = "";
    /// <summary>The entry could not be read as written (for example a version range that is not text). It is never treated as satisfied.</summary>
    public string? Invalid;
}

/// <summary>
/// A problem found while reading allstars-mod.json. <see cref="Safety"/> problems affect compatibility claims: they are never read as "no rule".
/// With <see cref="Blocking"/> (requirements, loader needs, load order, schema: things that must hold) the manager refuses to switch the mod on; without it
/// (a mod's own footprint declarations such as native patch ranges) the finding is an unconfirmed warning.
/// </summary>
public sealed record SidecarProblem(string Field, string Message, bool Safety, bool Blocking = true);

/// <summary>Shared-runtime (mod loader) requirements declared by a package. Nothing is assumed about fields that are absent.</summary>
public sealed class LoaderRequirement
{
    public bool Declared;
    public uint? HostAbi, ModIniSchema, PlanSchema;
    public List<string> Capabilities = new();
    public List<string> HostSha256 = new();
    public List<string> ProxySha256 = new();
}

/// <summary>Another mod named in a load-order constraint.</summary>
public sealed class OrderRef
{
    public string Id = "";
}

public sealed class ConfigSchemaEntry
{
    public string File = "", Section = "", Key = "", Label = "", Description = "", Type = "text";
    public string[] Choices = Array.Empty<string>();
    public double? Min, Max;
}

public sealed class ArtifactRef
{
    public string Path = "", Sha = "";
    public long Bytes;
}

/// <summary>Optional descriptive metadata (allstars-mod.json). It is declarative text and numbers only.</summary>
public sealed class Sidecar
{
    public string? FilePath;
    public string? Id, Name, Version, Summary, Kind, Stage, Redistribution;
    /// <summary>Where the mod's page is (an https:// address, display text only: no tool opens or contacts it).</summary>
    public string? Homepage;
    public bool? Installable;
    public string? PersistenceStatement, DisableNote;
    public bool? ModFolderAloneIsComplete;
    public string? CompletenessNote;
    public List<LooseAsset> LooseAssets = new();
    public List<Relation> Requires = new();
    public List<Relation> ConflictsWith = new();
    public List<Relation> ComposesWith = new();
    public List<string> FreeTextConflicts = new();
    public List<string> CapabilitiesExclusive = new();
    public List<NativePatch> NativePatches = new();
    public List<ConfigSchemaEntry> ConfigSchema = new();
    public List<ArtifactRef> Artifacts = new();
    public List<string> HumanGameplayRequirements = new();
    public string? StageRaw => Stage;

    // ---- stable format 1: schema, loader requirements, target build, load order ----
    /// <summary>The manifest_schema text as written, or null when the file does not declare one (legacy file, read as schema 1).</summary>
    public string? SchemaText;
    public int? SchemaMajor, SchemaMinor;
    /// <summary>False when the file is not understood well enough to act on its compatibility declarations (future major schema, unsupported must_understand feature, not an object).</summary>
    public bool Interpretable = true;
    public List<SidecarProblem> Problems = new();
    public List<string> MustUnderstand = new();
    public LoaderRequirement Loader = new();
    /// <summary>Game executable builds the package says it was made and tested for (lowercase SHA-256). Descriptive, not a hard requirement.</summary>
    public List<string> TargetExecutableSha256 = new();
    public List<OrderRef> LoadAfter = new();
    public List<OrderRef> LoadBefore = new();

    /// <summary>True when the file states anything that can make a combination unsafe or incomplete.</summary>
    public bool HasSafetyDeclarations =>
        Requires.Count + ConflictsWith.Count + FreeTextConflicts.Count + CapabilitiesExclusive.Count + NativePatches.Count + LooseAssets.Count
        + LoadAfter.Count + LoadBefore.Count > 0 || Loader.Declared || Installable == false || ModFolderAloneIsComplete == false;
}

public sealed class ManifestInfo
{
    public string FileName = "";
    public string FullPath = "";
    public string FileSha = "";
    public ManifestState State;
    public List<string> Alternates = new();      // other disabled manifest candidates (names only)
    public List<string> OtherManifestLike = new();
    public string? Schema, Id, Name, Version, PlanRel, PlanSha, RuntimeRel, RuntimeSha;
    public string Encoding = "";
    /// <summary>True when the user picked one of several switched-off manifests.</summary>
    public bool ChoiceMade;
}

/// <summary>Everything the manager knows about one mod folder (installed, in the library, or being imported).</summary>
public sealed class PackageInfo
{
    public string Dir = "";
    public string FolderName => Path.GetFileName(Dir.TrimEnd('\\', '/'));
    public ManifestInfo? Manifest;
    public PlanInfo? Plan;
    public Sidecar? Sidecar;
    public string? RuntimeFullPath;
    public long? RuntimeBytes;
    /// <summary>Static facts read from the runtime DLL's headers (never loaded). Null until the runtime has been inspected.</summary>
    public PeSummary? Runtime;
    public string? PlanFullPath;
    public List<string> ConfigFiles = new();      // top-level .ini files, names only
    public List<Issue> Issues = new();                // found while reading the folder
    public List<Issue> VerifyIssues = new();          // found while hashing files / checking the game (replaced on every verification)
    public IEnumerable<Issue> AllIssues => Issues.Concat(VerifyIssues);
    public Integrity Integrity = Integrity.NotChecked;
    public GameFit Fit = GameFit.NotChecked;
    public string? PackageRoot;                   // library/import: root that loose assets are relative to
    public bool IsResearchCard;

    public string? Id => Manifest?.Id;
    public string? Version => Sidecar?.Version is { Length: > 0 } sv && string.IsNullOrEmpty(Manifest?.Version) ? sv : Manifest?.Version;
    /// <summary>Friendlier name to show when the manifest has none (for example the folder a library copy will install into).</summary>
    public string? NameHint;
    public string DisplayName => Clean(FirstNonEmpty(Manifest?.Name, Sidecar?.Name, NameHint, FolderName, Manifest?.Id));
    public bool HasManifest => Manifest is not null && Manifest.State != ManifestState.NoManifest;
    public bool IsActiveOnDisk => Manifest?.State == ManifestState.Active;
    public bool HasBlockers => AllIssues.Any(i => i.Severity == Severity.Blocker);

    /// <summary>Content identity: id plus the two hashes the loader pins. Not the folder name, not the version text.</summary>
    public string? Fingerprint =>
        Manifest is { Id: { Length: > 0 } id, PlanSha: { } p, RuntimeSha: { } r } ? $"{id}|{p}|{r}" : null;

    public IEnumerable<LooseAsset> RequiredLooseAssets => Sidecar?.LooseAssets ?? Enumerable.Empty<LooseAsset>();

    public static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>Strips control characters and caps length; metadata text comes from untrusted files.</summary>
    public static string Clean(string? s, int max = 200)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.Where(c => !char.IsControl(c) || c == ' ').ToArray();
        var r = new string(chars);
        return r.Length > max ? r.Substring(0, max) + "…" : r;
    }
}
