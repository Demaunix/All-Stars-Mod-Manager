using System.Text.Json;
using AllStarsModManager.Transactions;

namespace AllStarsModManager;

public enum ImportKind { Folder, Zip }

public sealed class ImportMod
{
    public PackageInfo Package = null!;
    public string ModDir = "";            // absolute, inside WorkDir
    public string PackageRoot = "";       // absolute, inside WorkDir
    public string SuggestedFolder = "";
    public bool AlreadyInLibrary;
    public string? Duplicate;
    public bool Selected = true;
    /// <summary>Plain-language lines about problems and warnings, shared by the Add window and the tests.</summary>
    public List<string> NoteLines()
    {
        var p = Package;
        var lines = new List<string>();
        foreach (var b in p.AllIssues.Where(i => i.Severity == Severity.Blocker).DistinctBy(x => x.Message).Take(4)) lines.Add(b.Message);
        foreach (var w in p.AllIssues.Where(i => i.Severity == Severity.Warning).DistinctBy(x => x.Message).Take(2)) lines.Add(w.Message);
        if (p.Sidecar?.ModFolderAloneIsComplete == false && p.Sidecar.LooseAssets.Any(a => !a.PresentInPackage))
            lines.Add("This package is missing files it needs. It can be added to the library for safekeeping but cannot be switched on until they are present.");
        if (p.Sidecar?.Redistribution is { } r && r.StartsWith("PRIVATE", StringComparison.OrdinalIgnoreCase))
            lines.Add("Marked private by its author: it contains material derived from the game and must not be shared or uploaded.");
        return lines;
    }

    public bool CanAdd => Package.HasManifest && Package.Id is not null && !Package.IsResearchCard && !Package.Issues.Any(i => i.Code == "manifest-foreign");
}

public sealed class ImportPreview
{
    public string SourcePath = "";
    public ImportKind Kind;
    /// <summary>Folder the inspection ran on: the source folder itself, or a private extraction of the zip.</summary>
    public string WorkDir = "";
    public bool OwnsWorkDir;
    public List<ImportMod> Mods = new();
    public List<Issue> Issues = new();
    public int ExtractedFiles;
    public bool Usable => Mods.Any(m => m.CanAdd);
}

public sealed class LibraryEntryFile
{
    public int Version { get; set; } = 1;
    public string EntryId { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<LibraryEntryMod> Mods { get; set; } = new();
}

public sealed class LibraryEntryMod
{
    public string Rel { get; set; } = ".";
    public string FolderName { get; set; } = "";
    public string? Id { get; set; }
    public string? Version { get; set; }
    public string? Fingerprint { get; set; }
}

/// <summary>Everything about adding mods: inspect a folder or zip first (nothing is installed), then copy the chosen ones into the library.</summary>
public sealed class Importer
{
    readonly AppPaths _paths;
    readonly HashCache _cache;
    public ZipLimits Limits { get; } = new();

    public Importer(AppPaths paths, HashCache cache) { _paths = paths; _cache = cache; }

    public ImportPreview Inspect(string source, IReadOnlyList<LibraryItem>? existing = null, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var pv = new ImportPreview { SourcePath = LoaderPath.Full(source) };
        if (Directory.Exists(pv.SourcePath))
        {
            pv.Kind = ImportKind.Folder;
            if (LoaderPath.IsReparse(pv.SourcePath)) { pv.Issues.Add(new(Severity.Blocker, "import-link", "That folder is a link. The manager does not follow links when adding mods.")); return pv; }
            pv.WorkDir = pv.SourcePath;
            var link = FindLink(pv.SourcePath);
            if (link is not null) { pv.Issues.Add(new(Severity.Blocker, "import-link", $"The folder contains a link (\"{PackageInfo.Clean(Path.GetRelativePath(pv.SourcePath, link), 100)}\"). Links are never followed or copied, so nothing was added.")); return pv; }
        }
        else if (File.Exists(pv.SourcePath))
        {
            var kind = ArchiveSniffer.Identify(pv.SourcePath);
            if (kind != ArchiveKind.Zip)
            {
                pv.Kind = ImportKind.Zip;
                pv.Issues.Add(new(Severity.Blocker, "import-unsupported", ArchiveSniffer.UnsupportedMessage(pv.SourcePath, kind) ?? "This file type cannot be added."));
                return pv;
            }
            pv.Kind = ImportKind.Zip;
            _paths.EnsureCreated();
            var work = Path.Combine(_paths.IncomingDir, "x-" + Guid.NewGuid().ToString("N")[..10]);
            try
            {
                var written = SafeZip.Extract(pv.SourcePath, work, Limits, progress, ct);
                pv.ExtractedFiles = written.Count;
            }
            catch (ArchiveException e)
            {
                TryDelete(work);
                pv.Issues.Add(new(Severity.Blocker, "import-archive", e.Message));
                return pv;
            }
            catch (OperationCanceledException) { TryDelete(work); throw; }
            pv.WorkDir = work;
            pv.OwnsWorkDir = true;
        }
        else
        {
            pv.Issues.Add(new(Severity.Blocker, "import-missing", "That path does not exist."));
            return pv;
        }

        var roots = FindModDirs(pv.WorkDir, ct);
        if (roots.Count == 0)
        {
            pv.Issues.Add(new(Severity.Blocker, "import-no-mod", NoModMessage(pv.WorkDir)));
            Discard(pv);
            return pv;
        }
        var stem = pv.Kind == ImportKind.Zip ? Path.GetFileNameWithoutExtension(pv.SourcePath) : Path.GetFileName(pv.SourcePath.TrimEnd('\\'));
        foreach (var (modDir, packageRoot) in roots)
        {
            ct.ThrowIfCancellationRequested();
            var pkg = PackageInspector.Inspect(modDir, packageRoot: packageRoot);
            LooseAssets.Resolve(pkg, packageRoot);
            var vr = PackageVerifier.Verify(pkg, new VerifyOptions { Cache = _cache, Progress = progress }, ct);
            PackageVerifier.Apply(pkg, vr);
            var folder = LoaderPath.SafeFolderName(modDir.TrimEnd('\\') == pv.WorkDir.TrimEnd('\\') ? stem : Path.GetFileName(modDir.TrimEnd('\\')), pkg.Id ?? "Mod");
            var m = new ImportMod { Package = pkg, ModDir = modDir, PackageRoot = packageRoot, SuggestedFolder = folder };
            var dup = existing?.FirstOrDefault(l => l.Package.Fingerprint is not null && l.Package.Fingerprint == pkg.Fingerprint);
            if (dup is not null) { m.AlreadyInLibrary = true; m.Duplicate = dup.Origin; m.Selected = false; }
            pv.Mods.Add(m);
        }
        _cache.Save();
        return pv;
    }

    // ---- locating mods inside a folder ---------------------------------------------------

    static bool IsModDir(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).StartsWith("mod.ini", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Finds mod folders at the root or up to three levels down (single wrapper folders, "mods/&lt;name&gt;" packages, or folders of many mods).</summary>
    public static List<(string ModDir, string PackageRoot)> FindModDirs(string root, CancellationToken ct = default)
    {
        var found = new List<(string, string)>();
        int visited = 0;
        void Walk(string dir, int depth)
        {
            if (visited++ > 400) return;
            ct.ThrowIfCancellationRequested();
            if (IsModDir(dir))
            {
                var parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
                string pkgRoot = dir;
                if (parent is not null && Path.GetFileName(parent).Equals("mods", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } gp
                    && (LoaderPath.Fold(gp.TrimEnd('\\')) == LoaderPath.Fold(root.TrimEnd('\\')) || LoaderPath.IsInside(root, gp)))
                    pkgRoot = gp;                found.Add((dir, pkgRoot));
                return;
            }
            if (depth >= 3) return;
            IEnumerable<string> kids;
            try { kids = Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var k in kids)
            {
                if (LoaderPath.IsReparse(k)) continue;
                Walk(k, depth + 1);
            }
        }
        Walk(LoaderPath.Full(root).TrimEnd('\\'), 0);
        return found;
    }

    static string NoModMessage(string dir)
    {
        var names = new List<string>();
        try { names.AddRange(Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).Take(8)); } catch { }
        var hasPlan = names.Any(n => n.Equals("plan.json", StringComparison.OrdinalIgnoreCase));
        var hasRuntime = names.Any(n => n.Equals("runtime", StringComparison.OrdinalIgnoreCase));
        var hasDll = names.Any(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var hasMain = false;
        try { hasMain = Directory.EnumerateFiles(dir, "mod.ini*", SearchOption.AllDirectories).Any(); } catch { }
        string found = names.Count == 0 ? "The folder is empty." : "Found: " + string.Join(", ", names.Select(n => PackageInfo.Clean(n, 40))) + (names.Count >= 8 ? ", …" : "") + ".";
        if (hasPlan || hasRuntime)
            return "No mod.ini was found, but this has a plan or runtime folder. It looks like an incomplete or older-format package; it was not added. " + found;
        if (hasDll)
            return "This contains a DLL but no mod.ini and plan. A single DLL is not a package this manager can use, and it is never run from here. " + found;
        return "No mod was found in here. A mod folder has a mod.ini (or a switched-off mod.ini.disabled…) file. " + found;
    }

    static string? FindLink(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0 && n++ < 100_000)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> kids;
            try { kids = new DirectoryInfo(d).EnumerateFileSystemInfos().ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var k in kids)
            {
                if ((k.Attributes & FileAttributes.ReparsePoint) != 0) return k.FullName;
                if (k is DirectoryInfo) stack.Push(k.FullName);
            }
        }
        return null;
    }

    static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Removes extractions left behind by an interrupted session (older than a few hours; another window may still be using newer ones).</summary>
    public void PurgeStaleIncoming()
    {
        try
        {
            if (!Directory.Exists(_paths.IncomingDir)) return;
            foreach (var d in Directory.EnumerateDirectories(_paths.IncomingDir))
                if (!LoaderPath.IsReparse(d) && (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalHours > 6) TryDelete(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Discard the private extraction of an inspected zip (the manager's own temporary files).</summary>
    public void Discard(ImportPreview pv)
    {
        if (pv.OwnsWorkDir && LoaderPath.IsInside(_paths.IncomingDir, pv.WorkDir)) TryDelete(pv.WorkDir);
    }

    // ---- adding to the library -----------------------------------------------------------

    /// <summary>Copies the selected mods (with their whole package) into the manager's library. Never touches any game folder.</summary>
    public string AddToLibrary(ImportPreview pv, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var chosen = pv.Mods.Where(m => m.Selected && m.CanAdd).ToList();
        if (chosen.Count == 0) throw new InvalidOperationException("Nothing is selected to add.");
        _paths.EnsureCreated();
        Directory.CreateDirectory(_paths.LibraryDir);
        // One library entry per distinct package root so loose files stay next to the mods that need them.
        string lastEntry = "";
        foreach (var group in chosen.GroupBy(m => LoaderPath.Fold(m.PackageRoot)))
        {
            ct.ThrowIfCancellationRequested();
            var first = group.First();
            var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            var entryDir = Path.Combine(_paths.LibraryDir, id);
            var content = Path.Combine(entryDir, "content");
            Directory.CreateDirectory(entryDir);
            try
            {
                progress?.Report("Copying " + first.Package.DisplayName + "…");
                if (pv.OwnsWorkDir && chosen.GroupBy(m => LoaderPath.Fold(m.PackageRoot)).Count() == 1)
                    Directory.Move(first.PackageRoot, content);          // the extraction is ours: move, don't copy
                else
                    SafeCopy.Tree(first.PackageRoot, content, ct);
                var entry = new LibraryEntryFile
                {
                    EntryId = id, ImportedUtc = DateTime.UtcNow, Source = pv.SourcePath, Kind = pv.Kind.ToString(),
                    Mods = group.Select(m => new LibraryEntryMod
                    {
                        Rel = Path.GetRelativePath(first.PackageRoot, m.ModDir),
                        FolderName = m.SuggestedFolder, Id = m.Package.Id, Version = m.Package.Version, Fingerprint = m.Package.Fingerprint
                    }).ToList()
                };
                AtomicFile.WriteAllBytes(Path.Combine(entryDir, "entry.json"), JsonSerializer.SerializeToUtf8Bytes(entry, new JsonSerializerOptions { WriteIndented = true }));
                lastEntry = id;
            }
            catch
            {
                // Remove only the half-built entry this call created.
                TryDelete(entryDir);
                throw;
            }
        }
        if (pv.OwnsWorkDir) Discard(pv);
        return lastEntry;
    }
}

/// <summary>Library scan: managed entries (imported copies) and linked read-only folders.</summary>
public static class LibraryScanner
{
    public static List<LibraryItem> Scan(AppPaths paths, IEnumerable<string> linkedFolders)
    {
        var items = new List<LibraryItem>();
        if (Directory.Exists(paths.LibraryDir))
            foreach (var entryDir in Directory.EnumerateDirectories(paths.LibraryDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var ef = Path.Combine(entryDir, "entry.json");
                if (!File.Exists(ef)) continue;
                LibraryEntryFile? entry;
                try { entry = JsonSerializer.Deserialize<LibraryEntryFile>(File.ReadAllBytes(ef)); } catch (JsonException) { continue; }
                if (entry is null) continue;
                var content = Path.Combine(entryDir, "content");
                foreach (var m in entry.Mods)
                {
                    var dir = m.Rel == "." ? content : Path.Combine(content, m.Rel);
                    if (!Directory.Exists(dir)) continue;
                    var pkg = PackageInspector.Inspect(dir, packageRoot: content);
                    pkg.NameHint = m.FolderName;
                    LooseAssets.Resolve(pkg, content);
                    items.Add(new LibraryItem { EntryId = entry.EntryId, Origin = "Library", DirPath = dir, PackageRoot = content, EntryDir = entryDir, FolderName = m.FolderName, SourceNote = entry.Source, Package = pkg });
                }
            }
        foreach (var linked in linkedFolders)
        {
            if (!Directory.Exists(linked) || LoaderPath.IsReparse(linked)) continue;
            foreach (var (modDir, pkgRoot) in Importer.FindModDirs(linked))
            {
                var pkg = PackageInspector.Inspect(modDir, packageRoot: pkgRoot);
                pkg.NameHint = Path.GetFileName(modDir.TrimEnd('\\'));
                LooseAssets.Resolve(pkg, pkgRoot);
                items.Add(new LibraryItem
                {
                    EntryId = "linked-" + Sha.OfBytes(System.Text.Encoding.UTF8.GetBytes(LoaderPath.Fold(modDir)))[..10],
                    Origin = "Linked folder", Linked = true, DirPath = modDir, PackageRoot = pkgRoot, SourceNote = linked,
                    FolderName = LoaderPath.SafeFolderName(Path.GetFileName(modDir.TrimEnd('\\')), pkg.Id ?? "Mod"), Package = pkg
                });
            }
        }
        return items;
    }

    /// <summary>Hidden folders next to the game that contain several older mods, offered as links the user can accept. Read-only.</summary>
    public static List<string> SuggestFolders(string gameRoot)
    {
        var res = new List<string>();
        if (!Directory.Exists(gameRoot)) return res;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(gameRoot))
            {
                var name = Path.GetFileName(d);
                if (!name.StartsWith('.') || name.Equals(".git", StringComparison.OrdinalIgnoreCase) || LoaderPath.IsReparse(d)) continue;
                foreach (var cand in new[] { d }.Concat(SafeDirs(d)))
                {
                    if (LoaderPath.IsReparse(cand)) continue;
                    var kids = SafeDirs(cand).Take(60).ToList();
                    int n = kids.Count(k => !LoaderPath.IsReparse(k) && SafeFiles(k).Any(f => Path.GetFileName(f).StartsWith("mod.ini", StringComparison.OrdinalIgnoreCase)));
                    if (n >= 1 && !res.Contains(cand, StringComparer.OrdinalIgnoreCase)) res.Add(cand);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return res;
    }

    static IEnumerable<string> SafeDirs(string d) { try { return Directory.EnumerateDirectories(d).ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); } }
    static IEnumerable<string> SafeFiles(string d) { try { return Directory.EnumerateFiles(d).ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); } }
}

/// <summary>Resolves loose-file sources for a package against its package root.</summary>
public static class LooseAssets
{
    public static void Resolve(PackageInfo pkg, string packageRoot)
    {
        if (pkg.Sidecar is null) return;
        foreach (var a in pkg.Sidecar.LooseAssets)
        {
            try
            {
                var full = LoaderPath.Child(packageRoot, a.SourceInPackage);
                LoaderPath.NoReparse(packageRoot, full);
                a.SourceFullPath = File.Exists(full) ? full : null;
                a.PresentInPackage = a.SourceFullPath is not null;
            }
            catch (PathRuleException)
            {
                a.SourceFullPath = null;
                pkg.Issues.Add(new(Severity.Blocker, "loose-source-path", $"The package lists an unsafe source path for {PackageInfo.Clean(a.Target, 80)}."));
            }
        }
    }
}
