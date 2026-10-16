namespace AllStarsModManager;

public sealed class InstalledFolder
{
    public string Name = "";
    public string Path = "";
    public bool IsLink;
    public PackageInfo? Package;     // null for links
}

public enum ModsDirState { Missing, Ok, Link, NotADirectory }

public sealed class GameFolderInfo
{
    public const string ExeName = "Sonic & SEGA All-Stars Racing.exe";
    public const string ManagerDirName = ".allstars-manager";
    /// <summary>Loader builds the conflict rules in this manager were written against (source-bound by the project).</summary>
    public const string KnownHostSha = "b66b5ab2b0ef5db82448d35f048be7ed56d626b463cf898187bdc98f61fe1952";
    public const string KnownProxySha = "17a78e1eab0645c63b58ed7b90895787b62a79bbbb9413fb95c4ff54f931a6e2";

    public string Root = "";
    public bool Exists;
    public bool HasGameExe;
    public bool HasHost, HasProxy;
    public string? HostSha, ProxySha;
    /// <summary>The known loader build whose host AND proxy hashes both match, or null.</summary>
    public LoaderProfile? LoaderProfile;
    public bool LoaderRecognized => LoaderProfile is not null;
    public bool LoaderPresent => HasHost && HasProxy;
    public ModsDirState ModsDir;
    public string ModsPath => System.IO.Path.Combine(Root, "mods");
    public string ManagerPath => System.IO.Path.Combine(ModsPath, ManagerDirName);
    public List<InstalledFolder> Folders = new();
    public List<Issue> Issues = new();
    public bool PendingJournal;

    public IEnumerable<InstalledFolder> ActiveFolders => Folders.Where(f => f.Package?.Manifest?.State == ManifestState.Active);
}

/// <summary>Read-only inspection of a game folder. Nothing here writes, launches, or loads code.</summary>
public static class GameFolderScanner
{
    public static GameFolderInfo Scan(string root, HashCache? cache = null, IEnumerable<LoaderProfile>? extraProfiles = null)
    {
        var info = new GameFolderInfo { Root = root };
        try { info.Root = LoaderPath.Full(root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            info.Exists = false;
            info.Issues.Add(new(Severity.Blocker, "game-missing", "The game folder path is not a valid Windows path."));
            return info;
        }
        info.Exists = Directory.Exists(info.Root);
        if (!info.Exists) { info.Issues.Add(new(Severity.Blocker, "game-missing", "The game folder does not exist.")); return info; }
        info.HasGameExe = File.Exists(Path.Combine(info.Root, GameFolderInfo.ExeName));
        if (!info.HasGameExe)
            info.Issues.Add(new(Severity.Blocker, "game-exe-missing", $"\"{GameFolderInfo.ExeName}\" was not found here, so this does not look like an All-Stars Racing folder."));
        if (IsDriveRootOrSystem(info.Root))
            info.Issues.Add(new(Severity.Blocker, "game-protected", "This location is a drive root or a Windows system folder; the manager will not change it."));
        var host = Path.Combine(info.Root, "ChaoGarage.dll");
        var proxy = Path.Combine(info.Root, "dinput8.dll");
        info.HasHost = File.Exists(host);
        info.HasProxy = File.Exists(proxy);
        try { if (info.HasHost) info.HostSha = cache?.Get(host) ?? Sha.OfFile(host); if (info.HasProxy) info.ProxySha = cache?.Get(proxy) ?? Sha.OfFile(proxy); } catch (IOException) { }
        info.LoaderProfile = LoaderProfiles.Match(info.HostSha, info.ProxySha, extraProfiles);
        if (!info.LoaderPresent)
            info.Issues.Add(new(Severity.Warning, "loader-missing", "The mod loader files (ChaoGarage.dll and dinput8.dll) were not both found. Mods will not load until the loader is installed; the manager does not install it."));
        else if (!info.LoaderRecognized)
            info.Issues.Add(new(Severity.Warning, "loader-unrecognized", "This loader build is not one of the builds this program knows (matched by file hash, not by name). The conflict rules were written against a known build, so results may differ, and any mod that declares loader requirements cannot be confirmed."));

        var mods = info.ModsPath;
        var attr = LoaderPath.TryAttributes(mods);
        if (attr is null) info.ModsDir = ModsDirState.Missing;
        else if ((attr.Value & FileAttributes.ReparsePoint) != 0)
        {
            info.ModsDir = ModsDirState.Link;
            info.Issues.Add(new(Severity.Blocker, "mods-link", "The mods folder is a link. The loader requires a real folder, and the manager will not follow links."));
        }
        else if ((attr.Value & FileAttributes.Directory) == 0)
        {
            info.ModsDir = ModsDirState.NotADirectory;
            info.Issues.Add(new(Severity.Blocker, "mods-not-folder", "\"mods\" exists but is not a folder."));
        }
        else info.ModsDir = ModsDirState.Ok;

        if (info.ModsDir == ModsDirState.Ok)
        {
            info.Folders.AddRange(ReadModFolders(mods, info.Issues));
            info.PendingJournal = Transactions.TransactionStore.HasUnfinished(info);
            if (info.PendingJournal)
                info.Issues.Add(new(Severity.Blocker, "unfinished-change", "An earlier change did not finish. Restore the previous state before making new changes."));
        }
        return info;
    }

    /// <summary>
    /// Reads every folder directly inside a mods folder (the only level the loader looks at), in the loader's own order. Links are reported
    /// but not followed. Read-only; used by the game-folder scan and by the manager-free checker so both see the same thing.
    /// </summary>
    public static List<InstalledFolder> ReadModFolders(string modsPath, List<Issue> issues)
    {
        var result = new List<InstalledFolder>();
        foreach (var d in Directory.EnumerateDirectories(modsPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(d);
            if (name.Equals(GameFolderInfo.ManagerDirName, StringComparison.OrdinalIgnoreCase)) continue;
            var f = new InstalledFolder { Name = name, Path = d };
            if (LoaderPath.IsReparse(d))
            {
                f.IsLink = true;
                issues.Add(new(Severity.Blocker, "folder-link", $"\"{name}\" in the mods folder is a link. The loader refuses links, so the game would start with no mods until it is removed.", Subject: name));
            }
            else f.Package = PackageInspector.Inspect(d);
            result.Add(f);
        }
        return result;
    }

    static bool IsDriveRootOrSystem(string root)
    {
        var full = root.TrimEnd('\\');
        if (full.Length <= 2) return true;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        foreach (var p in new[] { win, pf, pfx86 })
            if (p.Length > 0 && string.Equals(full, p, StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrEmpty(win) && LoaderPath.IsInside(win, full);
    }
}

/// <summary>The order in which the installed loader starts runtimes: folder names sorted ordinally, then metal-sonic first.</summary>
public static class StartupOrder
{
    public static List<T> Compute<T>(IEnumerable<T> items, Func<T, string> folderName, Func<T, string?> id)
    {
        var sorted = items.OrderBy(folderName, StringComparer.Ordinal).ToList();
        return sorted.Where(i => id(i) == "metal-sonic").Concat(sorted.Where(i => id(i) != "metal-sonic")).ToList();
    }
}
