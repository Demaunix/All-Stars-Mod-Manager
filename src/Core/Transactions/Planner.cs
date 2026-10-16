using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllStarsModManager.Transactions;

/// <summary>Which extra files (outside any mod folder) the manager itself put in the game folder, so it only ever takes back its own.</summary>
public sealed class AssetRegistry
{
    public sealed class Entry
    {
        public string Sha { get; set; } = "";
        public bool CreatedByManager { get; set; }
        public List<string> Owners { get; set; } = new();
    }
    public int Version { get; set; } = 1;
    public Dictionary<string, Entry> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AssetRegistry Load(string gameRoot)
    {
        var p = TransactionStore.RegistryPath(gameRoot);
        if (!File.Exists(p)) return new AssetRegistry();
        try
        {
            var r = JsonSerializer.Deserialize<AssetRegistry>(File.ReadAllBytes(p), Opts) ?? new();
            r.Assets = new Dictionary<string, Entry>(r.Assets, StringComparer.OrdinalIgnoreCase);
            return r;
        }
        catch (JsonException) { return new AssetRegistry(); }
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Opts);

    public AssetRegistry Clone()
    {
        var c = new AssetRegistry();
        foreach (var (k, v) in Assets) c.Assets[k] = new Entry { Sha = v.Sha, CreatedByManager = v.CreatedByManager, Owners = v.Owners.ToList() };
        return c;
    }

    public static string Key(string relBackslash) => relBackslash.Replace('\\', '/');
}

/// <summary>A cheap fingerprint of the parts of the game folder a plan depends on, used to refuse stale plans.</summary>
public static class SnapshotBuilder
{
    public static string Build(string gameRoot, IEnumerable<string> watched)
    {
        var lines = new List<string>();
        var mods = Path.Combine(gameRoot, "mods");
        var a = LoaderPath.TryAttributes(mods);
        lines.Add("mods|" + (a is null ? "missing" : ((a.Value & FileAttributes.ReparsePoint) != 0 ? "link" : "dir")));
        if (a is not null && (a.Value & FileAttributes.Directory) != 0 && (a.Value & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var e in new DirectoryInfo(mods).EnumerateFileSystemInfos())
            {
                if (e.Name.Equals(GameFolderInfo.ManagerDirName, StringComparison.OrdinalIgnoreCase)) continue;
                bool link = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                if (e is FileInfo f) { lines.Add($"f|{e.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"); continue; }
                lines.Add($"d|{e.Name}|{(link ? "link" : "dir")}");
                if (link) continue;
                foreach (var c in new DirectoryInfo(e.FullName).EnumerateFileSystemInfos())
                {
                    if (c is FileInfo cf) lines.Add($"  f|{e.Name}/{c.Name}|{cf.Length}|{cf.LastWriteTimeUtc.Ticks}");
                    else lines.Add($"  d|{e.Name}/{c.Name}|{((c.Attributes & FileAttributes.ReparsePoint) != 0 ? "link" : "dir")}");
                }
            }
        }
        foreach (var w in watched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(w);
            lines.Add(fi.Exists ? $"w|{w}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : $"w|{w}|absent");
        }
        lines.Add("registry|" + (File.Exists(TransactionStore.RegistryPath(gameRoot)) ? Sha.OfFile(TransactionStore.RegistryPath(gameRoot)) : "none"));
        lines.Add("journal|" + File.Exists(TransactionStore.CurrentPath(gameRoot)));
        return Sha.OfBytes(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }
}

public sealed class PlanRequest
{
    public GameFolderInfo Game = null!;
    public IReadOnlyList<ModEntry> Entries = Array.Empty<ModEntry>();
    /// <summary>Entry key → should be switched on after applying.</summary>
    public IReadOnlyDictionary<string, bool> Want = new Dictionary<string, bool>();
    /// <summary>Entry key → chosen switched-off manifest file name (only for folders with several).</summary>
    public IReadOnlyDictionary<string, string> ManifestChoice = new Dictionary<string, string>();
    public HashCache Cache = new();
    public DeepCache? DeepCache;
    public bool DeepVerify = true;
    public IProgress<long>? Progress;
    /// <summary>User-described loader builds (data\loader-profiles.json) used when the game folder is re-read after a change.</summary>
    public IReadOnlyList<LoaderProfile> ExtraProfiles = Array.Empty<LoaderProfile>();
}

public sealed class PlanResult
{
    public ChangePlan Plan = null!;
    public ValidationReport Validation = null!;
    public List<Candidate> Intended = new();
    public bool CanApply => !Validation.HasBlockers && Plan.HasChanges;
    public bool HasChanges => Plan.HasChanges;
    public List<string> Blockers => Validation.BlockerLines();
    public List<string> Warnings = new();
    public List<string> Notes => Plan.Notes;
}

/// <summary>Turns "this set of mods should be on" into a validated, previewable list of file steps.</summary>
public static class ChangePlanner
{
    const string DisabledByManager = "mod.ini.disabled-by-manager";

    // ---- switching mods on/off and installing from the library --------------------------

    public static PlanResult Build(PlanRequest r)
    {
        var g = r.Game;
        var plan = new ChangePlan { GameRoot = g.Root };
        var usedNames = new HashSet<string>(g.Folders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var intended = new List<Candidate>();
        var installs = new List<(Candidate C, ModEntry E)>();
        var enables = new List<(ModEntry E, string disabledFile)>();
        var disables = new List<ModEntry>();

        foreach (var e in r.Entries)
        {
            bool want = r.Want.TryGetValue(e.Key, out var w) && w;
            if (e.IsInstalled)
            {
                var pkg = e.Installed!;
                if (r.ManifestChoice.TryGetValue(e.Key, out var choice) && pkg.Manifest?.State == ManifestState.DisabledAmbiguous)
                {
                    var chosen = PackageInspector.Inspect(pkg.Dir, choice);
                    if (chosen.Fingerprint == pkg.Fingerprint) { chosen.Integrity = pkg.Integrity; chosen.Fit = pkg.Fit; chosen.VerifyIssues = pkg.VerifyIssues; }
                    else PackageVerifier.Apply(chosen, PackageVerifier.Verify(chosen, new VerifyOptions { Cache = r.Cache, GameRoot = g.Root, Deep = r.DeepVerify, DeepCache = r.DeepCache }));
                    pkg = chosen;
                }
                if (want)
                {
                    intended.Add(new Candidate { Key = e.Key, Pkg = pkg, FolderName = e.InstalledFolder!, Installed = true });
                    if (!e.IsActive) enables.Add((e, pkg.Manifest!.FileName));
                }
                else if (e.IsActive) disables.Add(e);
            }
            else if (want)
            {
                var lib = e.Library!;
                var name = UniqueName(LoaderPath.SafeFolderName(lib.FolderName.Length > 0 ? lib.FolderName : Path.GetFileName(lib.DirPath.TrimEnd('\\')), e.Package.Id ?? "Mod"), usedNames);
                usedNames.Add(name);
                var c = new Candidate { Key = e.Key, Pkg = lib.Package, FolderName = name, Installed = false, PackageRoot = lib.PackageRoot, LibraryEntry = lib.EntryId };
                intended.Add(c);
                installs.Add((c, e));
            }
        }
        // Folders in the mods folder that the user is not touching but that the loader will still read: every folder with mod.ini.
        foreach (var f in g.Folders)
        {
            if (f.Package is null) continue;
            var entry = r.Entries.FirstOrDefault(x => x.InstalledFolder == f.Name);
            if (entry is null && f.Package.IsActiveOnDisk)
                intended.Add(new Candidate { Key = ModEntry.InstalledKey(f.Name), Pkg = f.Package, FolderName = f.Name, Installed = true });
        }

        var validation = ActiveSetValidator.Validate(intended, g, r.Cache);
        var result = new PlanResult { Plan = plan, Validation = validation, Intended = intended };

        // ---- ops -------------------------------------------------------------------------
        var stage = plan.StageDir; var backup = plan.BackupDir;
        var mods = Path.Combine(g.Root, "mods");
        int seq = 0;
        JournalOp Op(OpKind k, string desc, string path, string? from = null) => new() { Seq = ++seq, Kind = k, Description = desc, Path = path, From = from };
        string UndoDir(string name) => Path.Combine(backup, "undo", $"{seq:00}-{name}");
        var registry = AssetRegistry.Load(g.Root);
        var newRegistry = registry.Clone();
        bool registryChanged = false;
        var watched = new List<string>();

        if (g.ModsDir == ModsDirState.Missing && (installs.Count > 0))
        {
            // creating the mods folder happens before the journal exists (see TransactionEngine.PrepareManagerDir)
        }

        foreach (var (c, e) in installs)
        {
            var dest = Path.Combine(mods, c.FolderName);
            var stagedRel = Path.Combine("pkg", c.FolderName);
            plan.StageItems.Add(new StageItem { SourcePath = c.Pkg.Dir, IsDirectory = true, DestinationRel = stagedRel, ActivateManifest = c.Pkg.Manifest!.State == ManifestState.Active ? null : c.Pkg.Manifest.FileName });
            var mv = Op(OpKind.MoveDir, $"Install {c.Display} into the mods folder as \"{c.FolderName}\"", dest, Path.Combine(stage, stagedRel));
            mv.UndoPath = UndoDir(c.FolderName);
            plan.Ops.Add(mv);
            plan.PreviewLines.Add($"Install “{c.Display}” into the mods folder (folder “{c.FolderName}”) and switch it on");
        }

        // Loose files outside the mod folders (created only if absent; identical existing files are adopted, never owned).
        var createDirs = new List<string>();
        var plannedCreates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in validation.Assets)
        {
            var rel = LooseAssetPolicy.ValidateTarget(ap.Asset.Target);
            var key = AssetRegistry.Key(rel);
            var owner = ap.Owner.FolderName;
            if (!newRegistry.Assets.TryGetValue(key, out var ent))
            {
                ent = new AssetRegistry.Entry { Sha = ap.Asset.Sha, CreatedByManager = ap.Action == AssetAction.Create };
                newRegistry.Assets[key] = ent;
            }
            if (!ent.Owners.Contains(owner, StringComparer.OrdinalIgnoreCase)) ent.Owners.Add(owner);
            watched.Add(ap.TargetFullPath);
            if (ap.Action == AssetAction.AlreadyPresent) { registryChanged |= !registry.Assets.ContainsKey(key) || !registry.Assets[key].Owners.Contains(owner, StringComparer.OrdinalIgnoreCase); continue; }
            // two selected mods may need the same (identical) file that is not there yet: stage and create it once, record every owner
            if (!plannedCreates.Add(key)) { registryChanged = true; plan.PreviewLines.Add($"{ap.Owner.Display} also uses the extra file {key}"); continue; }
            if (ap.Owner.Installed) { /* installed mod whose loose file went missing: restore it from the package if available */ }
            registryChanged = true;
            // parents that do not exist yet
            var dir = Path.GetDirectoryName(ap.TargetFullPath)!;
            var missing = new Stack<string>();
            while (!Directory.Exists(dir) && !LoaderPath.Fold(dir).Equals(LoaderPath.Fold(g.Root), StringComparison.Ordinal)) { missing.Push(dir); dir = Path.GetDirectoryName(dir)!; }
            foreach (var d in missing) if (!createDirs.Contains(d, StringComparer.OrdinalIgnoreCase)) { createDirs.Add(d); plan.Ops.Add(Op(OpKind.CreateDir, $"Create folder {Path.GetRelativePath(g.Root, d)}", d)); }
            var stagedAsset = Path.Combine("assets", rel);
            plan.StageItems.Add(new StageItem { SourcePath = ap.Asset.SourceFullPath!, IsDirectory = false, DestinationRel = stagedAsset });
            var op = Op(OpKind.MoveNewFile, $"Add required file {key}", ap.TargetFullPath, Path.Combine(stage, stagedAsset));
            op.UndoPath = UndoDir(Path.GetFileName(rel));
            plan.Ops.Add(op);
            plan.PreviewLines.Add($"Add required file {key} for {ap.Owner.Display}");
        }
        if (registryChanged) AddRegistryOp(plan, g.Root, newRegistry, ref seq);

        foreach (var e in disables)
        {
            var folder = e.Installed!.Dir;
            var name = UniqueManifestName(folder);
            var op = Op(OpKind.RenameFile, $"Switch off {e.Display} (rename mod.ini to {name})", Path.Combine(folder, name), Path.Combine(folder, "mod.ini"));
            op.PreSha = e.Installed.Manifest!.FileSha;
            plan.Ops.Add(op);
            plan.PreviewLines.Add($"Switch off “{e.Display}”");
            var note = DisableNote(e.Installed);
            if (note is not null) plan.Notes.Add($"{e.Display}: {note}");
        }
        foreach (var (e, chosenFile) in enables)
        {
            var folder = e.Installed!.Dir;
            var file = chosenFile;
            var op = Op(OpKind.RenameFile, $"Switch on {e.Display} (rename {file} to mod.ini)", Path.Combine(folder, "mod.ini"), Path.Combine(folder, file));
            op.PreSha = Sha.OfFile(Path.Combine(folder, file));
            plan.Ops.Add(op);
            plan.PreviewLines.Add($"Switch on “{e.Display}”");
        }

        // Notes from the validation that are useful in the confirmation step.
        foreach (var n in validation.Conflicts.Notes.Where(n => n.Title != "Startup order")) plan.Notes.Add(n.Title + ": " + n.Text);
        foreach (var c in intended)
            foreach (var w in validation.WarningsFor(c.Key)) result.Warnings.Add($"{c.Display}: {w.Message}");
        foreach (var gi in validation.GameIssues.Where(i => i.Severity == Severity.Warning)) result.Warnings.Add(gi.Message);

        plan.Summary = plan.PreviewLines.Count == 0 ? "No changes" : string.Join("; ", plan.PreviewLines);
        var expectedActive = intended.Select(c => c.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var o in plan.Ops.Where(o => o.Kind == OpKind.MoveNewFile)) watched.Add(o.Path);
        plan.ComputeSnapshot = () => SnapshotBuilder.Build(g.Root, watched);
        plan.SnapshotToken = plan.ComputeSnapshot();
        plan.VerifyStage = stageDir => VerifyStaged(r, installs, stageDir);
        plan.VerifyResult = () => VerifyOutcome(r, expectedActive, intended);
        return result;
    }

    static string UniqueName(string name, HashSet<string> used)
    {
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) { var n = $"{name} ({i})"; if (!used.Contains(n)) return n; }
    }

    static string UniqueManifestName(string folder)
    {
        var name = DisabledByManager;
        for (int i = 2; File.Exists(Path.Combine(folder, name)); i++) name = $"{DisabledByManager}-{i}";
        return name;
    }

    /// <summary>A short, concrete note the mod itself declares about what switching it off does not undo.</summary>
    public static string? DisableNote(PackageInfo p)
    {
        var sc = p.Sidecar;
        if (sc is null) return null;
        if (!string.IsNullOrWhiteSpace(sc.DisableNote)) return sc.DisableNote;
        if (string.IsNullOrWhiteSpace(sc.PersistenceStatement)) return null;
        var s = sc.PersistenceStatement!;
        var cut = s.Length > 320 ? s.LastIndexOf(". ", 320, StringComparison.Ordinal) : -1;
        s = cut > 60 ? s.Substring(0, cut + 1) : (s.Length > 320 ? s.Substring(0, 320) + "…" : s);
        return s;
    }

    static void AddRegistryOp(ChangePlan plan, string root, AssetRegistry newReg, ref int seq)
    {
        var path = TransactionStore.RegistryPath(root);
        var rel = Path.Combine("new", "assets.json");
        plan.NewFiles.Add(new StageNewFile { DestinationRel = rel, Content = newReg.ToBytes() });
        var staged = Path.Combine(plan.StageDir, rel);
        if (File.Exists(path))
            plan.Ops.Add(new JournalOp { Seq = ++seq, Kind = OpKind.ReplaceFile, Description = "Update the manager's list of files it added", Path = path, From = staged, BackupPath = Path.Combine(plan.BackupDir, "assets.json.before"), PreSha = Sha.OfFile(path), UndoPath = Path.Combine(plan.BackupDir, "undo", $"{seq:00}-assets.json") });
        else
            plan.Ops.Add(new JournalOp { Seq = ++seq, Kind = OpKind.MoveNewFile, Description = "Record the files the manager added", Path = path, From = staged, UndoPath = Path.Combine(plan.BackupDir, "undo", $"{seq:00}-assets.json") });
    }

    // ---- verification callbacks ---------------------------------------------------------

    static List<string> VerifyStaged(PlanRequest r, List<(Candidate C, ModEntry E)> installs, string stageDir)
    {
        var problems = new List<string>();
        foreach (var (c, _) in installs)
        {
            var dir = Path.Combine(stageDir, "pkg", c.FolderName);
            var pkg = PackageInspector.Inspect(dir);
            var res = PackageVerifier.Verify(pkg, new VerifyOptions { Cache = new HashCache(), GameRoot = null, Fresh = true, Deep = false });
            foreach (var i in pkg.AllIssues.Concat(res.Issues).Where(i => i.Severity == Severity.Blocker && !IsStageOnlyAssetIssue(i)))
                problems.Add($"{c.Display}: {i.Message}");
            if (pkg.Fingerprint != c.Pkg.Fingerprint) problems.Add($"{c.Display}: the copied files are not the same package that was selected.");
            if (c.Pkg.Sidecar is { } sc)
                foreach (var a in sc.LooseAssets)
                {
                    try
                    {
                        var rel = LooseAssetPolicy.ValidateTarget(a.Target);
                        var staged = Path.Combine(stageDir, "assets", rel);
                        if (File.Exists(staged) && Sha.OfFile(staged) != a.Sha) problems.Add($"{c.Display}: the copied file {a.Target} did not match its recorded hash.");
                    }
                    catch (PathRuleException) { }
                }
        }
        return problems;
    }

    // Loose-asset sources live outside the mod folder, so a staged mod folder alone can not see them.
    static bool IsStageOnlyAssetIssue(Issue i) => i.Code is "loose-size" or "loose-hash";

    static List<string> VerifyOutcome(PlanRequest r, HashSet<string> expectedActive, List<Candidate> intended)
    {
        var problems = new List<string>();
        var scan = GameFolderScanner.Scan(r.Game.Root, r.Cache, r.ExtraProfiles);
        scan.Issues.RemoveAll(i => i.Code == "unfinished-change");   // our own journal exists while this check runs
        var actual = scan.ActiveFolders.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var x in expectedActive.Except(actual, StringComparer.OrdinalIgnoreCase)) problems.Add($"\"{x}\" was expected to be switched on but is not.");
        foreach (var x in actual.Except(expectedActive, StringComparer.OrdinalIgnoreCase)) problems.Add($"\"{x}\" is switched on but was not expected to be.");
        foreach (var f in scan.Folders.Where(f => f.IsLink)) problems.Add($"\"{f.Name}\" is a link.");
        if (problems.Count > 0) return problems;
        // Re-validate what is really on disk the way the loader will see it.
        var candidates = new List<Candidate>();
        foreach (var f in scan.ActiveFolders)
        {
            var pkg = f.Package!;
            var vr = PackageVerifier.Verify(pkg, new VerifyOptions { Cache = r.Cache, GameRoot = scan.Root, Deep = r.DeepVerify, DeepCache = r.DeepCache });
            PackageVerifier.Apply(pkg, vr);
            candidates.Add(new Candidate { Key = ModEntry.InstalledKey(f.Name), Pkg = pkg, FolderName = f.Name, Installed = true });
        }
        var rep = ActiveSetValidator.Validate(candidates, scan, r.Cache);
        problems.AddRange(rep.BlockerLines());
        return problems;
    }
    // ---- uninstall ----------------------------------------------------------------------

    public static PlanResult BuildUninstall(PlanRequest r, ModEntry entry)
    {
        var g = r.Game;
        var plan = new ChangePlan { GameRoot = g.Root };
        var pkg = entry.Installed ?? throw new InvalidOperationException("Not installed.");
        int seq = 0;
        var mods = Path.Combine(g.Root, "mods");
        var removedDir = Path.Combine(plan.BackupDir, "removed", pkg.FolderName);
        var mv = new JournalOp { Seq = ++seq, Kind = OpKind.MoveDir, Description = $"Move {entry.Display} out of the mods folder into the manager's backup folder", Path = removedDir, From = pkg.Dir, Fingerprint = Fingerprint.OfTree(pkg.Dir) };
        plan.Ops.Add(mv);
        plan.PreviewLines.Add($"Remove “{entry.Display}” from the mods folder (kept in the backup folder; nothing is deleted)");

        var registry = AssetRegistry.Load(g.Root);
        var nr = registry.Clone();
        bool changed = false;
        foreach (var (key, ent) in registry.Assets.ToList())
        {
            if (!ent.Owners.Contains(pkg.FolderName, StringComparer.OrdinalIgnoreCase)) continue;
            // the list is a plain file anyone can edit: only ever act on entries that still satisfy the loose-file policy
            try { LooseAssetPolicy.ValidateTarget(key); }
            catch (PathRuleException) { plan.Notes.Add($"An entry in the manager's list of added files ({PackageInfo.Clean(key, 80)}) is not a path the manager would ever add, so it was ignored."); continue; }
            var e2 = nr.Assets[key];
            e2.Owners.RemoveAll(o => o.Equals(pkg.FolderName, StringComparison.OrdinalIgnoreCase));
            changed = true;
            if (e2.Owners.Count == 0)
            {
                var full = LoaderPath.Child(g.Root, key);
                if (e2.CreatedByManager && File.Exists(full) && Sha.OfFile(full) == e2.Sha)
                {
                    plan.Ops.Add(new JournalOp { Seq = ++seq, Kind = OpKind.RenameFile, Description = $"Take back the file the manager added: {key}", Path = Path.Combine(plan.BackupDir, "removed-files", key.Replace('/', '\\')), From = full, PreSha = e2.Sha });
                    plan.PreviewLines.Add($"Take back the extra file {key} that the manager added (kept in the backup folder)");
                }
                else plan.Notes.Add($"{key} was left in place (it was not added by the manager or has been changed).");
                nr.Assets.Remove(key);
            }
        }
        if (changed) AddRegistryOp(plan, g.Root, nr, ref seq);
        var note = DisableNote(pkg);
        if (note is not null) plan.Notes.Add($"{entry.Display}: {note}");

        // Remaining active set must still be acceptable to the loader.
        var remaining = g.Folders.Where(f => f.Package is not null && f.Package.IsActiveOnDisk && f.Name != pkg.FolderName)
            .Select(f => new Candidate { Key = ModEntry.InstalledKey(f.Name), Pkg = f.Package!, FolderName = f.Name, Installed = true }).ToList();
        var validation = ActiveSetValidator.Validate(remaining, g, r.Cache);
        var res = new PlanResult { Plan = plan, Validation = validation, Intended = remaining };
        plan.Summary = string.Join("; ", plan.PreviewLines);
        var watched = new List<string>();
        plan.ComputeSnapshot = () => SnapshotBuilder.Build(g.Root, watched);
        plan.SnapshotToken = plan.ComputeSnapshot();
        var expected = remaining.Select(c => c.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.VerifyResult = () => VerifyOutcome(r, expected, remaining);
        return res;
    }

    // ---- configuration edits ------------------------------------------------------------

    /// <param name="loadedSha">SHA-256 of the file as it was when the editor opened it. If the file on disk differs now, saving would silently discard the other program's edit, so the save is refused.</param>
    public static PlanResult BuildConfigEdit(PlanRequest r, ModEntry entry, string fileName, byte[] newBytes, string? loadedSha = null)
    {
        var g = r.Game;
        var plan = new ChangePlan { GameRoot = g.Root };
        var pkg = entry.Installed ?? throw new InvalidOperationException("Settings can only be changed for mods in the game folder.");
        var validation = new ValidationReport();
        var res = new PlanResult { Plan = plan, Validation = validation };
        if (!PackageInspector.ListConfigFiles(pkg.Dir).Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            validation.GameIssues.Add(new(Severity.Blocker, "config-not-listed", $"\"{PackageInfo.Clean(fileName, 60)}\" is not a settings file of this mod."));
            return res;
        }
        var path = Path.Combine(pkg.Dir, fileName);
        if (!LoaderPath.IsPlainInside(g.Root, path)) { validation.GameIssues.Add(new(Severity.Blocker, "config-link", "The settings file is reached through a link.")); return res; }
        var pre = Sha.OfFile(path);
        if (loadedSha is not null && pre != loadedSha)
        {
            validation.GameIssues.Add(new(Severity.Blocker, "config-changed", $"\"{PackageInfo.Clean(fileName, 60)}\" was changed by another program after it was opened here. Close this and reopen the settings so nothing is lost, then make your change again."));
            return res;
        }
        var post = Sha.OfBytes(newBytes);
        if (pre == post) return res;
        plan.NewFiles.Add(new StageNewFile { DestinationRel = Path.Combine("new", fileName), Content = newBytes });
        plan.Ops.Add(new JournalOp
        {
            Seq = 1, Kind = OpKind.ReplaceFile, Description = $"Save settings in {fileName} of {entry.Display}", Path = path, From = Path.Combine(plan.StageDir, "new", fileName),
            BackupPath = Path.Combine(plan.BackupDir, $"{pkg.FolderName}-{fileName}.before"), UndoPath = Path.Combine(plan.BackupDir, "undo", $"01-{fileName}"), PreSha = pre
        });
        plan.PreviewLines.Add($"Save settings in {fileName} of “{entry.Display}” (the previous file is kept in the backup folder)");
        plan.Summary = plan.PreviewLines[0];
        plan.ComputeSnapshot = () => SnapshotBuilder.Build(g.Root, new[] { path });
        plan.SnapshotToken = plan.ComputeSnapshot();
        plan.VerifyResult = () => File.Exists(path) && Sha.OfFile(path) == post ? new() : new() { "The settings file does not contain what was saved." };
        return res;
    }
}
