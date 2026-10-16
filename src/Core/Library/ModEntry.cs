namespace AllStarsModManager;

/// <summary>A package the manager can install from: an imported library copy, or a read-only linked folder.</summary>
public sealed class LibraryItem
{
    public string EntryId = "";
    public string Origin = "Library";        // "Library" or "Linked folder"
    public bool Linked;
    public string DirPath = "";              // the mod folder
    public string PackageRoot = "";          // root that loose-asset sources are relative to
    public string? EntryDir;                 // managed library entry folder (not set for linked)
    public string? SourceNote;               // where it came from, for display
    public string FolderName = "";           // folder name used when installing into the mods folder
    public PackageInfo Package = null!;
}

/// <summary>One row in the mod list: an installed folder, a library package, or both when they are the same content.</summary>
public sealed class ModEntry
{
    public string Key = "";
    public PackageInfo? Installed;
    public LibraryItem? Library;
    public List<LibraryItem> OtherLibraryVersions = new();

    public PackageInfo Package => Installed ?? Library!.Package;
    public bool IsInstalled => Installed is not null;
    public string? InstalledFolder => Installed?.FolderName;
    public bool IsActive => Installed?.Manifest?.State == ManifestState.Active;
    public string? Id => Package.Id;
    public string? Version => Package.Version;
    public string Display => Package.DisplayName + (string.IsNullOrEmpty(Package.Version) ? "" : " " + Package.Version);
    public bool InLibrary => Library is not null;

    public static string InstalledKey(string folder) => "inst:" + folder;
    public static string LibraryKey(LibraryItem l) => "lib:" + l.EntryId + ":" + l.DirPath;
}
