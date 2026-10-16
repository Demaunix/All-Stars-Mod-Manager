using System.Reflection;

namespace AllStarsModManager.Compat;

public sealed class CheckOptions
{
    public string Path = ".";
    /// <summary>Packages that are not installed yet, checked as if they were added to the set (--add).</summary>
    public List<string> Add = new();
    /// <summary>game, mods or package; null = detect.</summary>
    public string? InputAs;
    /// <summary>Evaluate against this named loader profile when no game folder is involved (--loader).</summary>
    public string? LoaderProfileId;
    public string? ProfileFile;
    /// <summary>Skip hashing package payloads and game files; structure, pins and declarations are still checked.</summary>
    public bool Quick;
    /// <summary>Also rebuild each changed archive the way the loader does (reads whole archives; slow).</summary>
    public bool Deep;
    public Action<string>? Progress;
}

/// <summary>Raised for problems with the request itself (missing folder, unknown option value); the CLI turns it into exit code 3.</summary>
public sealed class CheckInputException(string message) : Exception(message);

/// <summary>
/// The manager-free compatibility check. It reads a game folder, a mods folder or a single package; it never loads or runs a mod DLL, never
/// writes anywhere and needs no network. It uses exactly the same <see cref="PackageInspector"/>, <see cref="PackageVerifier"/> and
/// <see cref="ActiveSetValidator"/> rules as the manager.
/// </summary>
public static class CheckRunner
{
    public static string ToolVersion =>
        typeof(CheckRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";

    sealed class Entry
    {
        public Candidate Cand = null!;
        public string State = "active";
        /// <summary>Name shown in reports; differs from the folder name only when several entries share that name.</summary>
        public string? Label;
        public string Name => Label ?? Cand.FolderName;
    }

    public static CheckReport Run(CheckOptions o)
    {
        var extra = new List<LoaderProfile>();
        if (o.ProfileFile is not null)
        {
            var problems = new List<string>();
            extra = LoaderProfiles.LoadFile(o.ProfileFile, problems);
            if (problems.Count > 0) throw new CheckInputException("Loader profile file: " + problems[0]);
        }
        LoaderProfile? assumed = null;
        if (o.LoaderProfileId is not null)
        {
            assumed = LoaderProfiles.Find(o.LoaderProfileId, extra)
                ?? throw new CheckInputException($"Unknown loader profile \"{o.LoaderProfileId}\". Use --list-loaders to see the known ones.");
        }

        var root = ResolveDirectory(o.Path);
        var cache = new HashCache();                    // memory only: the checker never writes a cache file
        var entries = new List<Entry>();
        var report = new CheckReport { Tool = "AllStarsModCheck " + ToolVersion };
        var unmanaged = new List<CheckedPackage>();
        var findings = report.Findings;
        GameFolderInfo? game = null;

        var kind = o.InputAs ?? Detect(root);
        switch (kind)
        {
            case "game":
            {
                game = GameFolderScanner.Scan(root, cache, extra);
                report.InputKind = "game-folder";
                foreach (var f in game.Folders) Classify(f, entries, unmanaged, findings);
                break;
            }
            case "mods":
            {
                report.InputKind = "mods-folder";
                var issues = new List<Issue>();
                foreach (var f in GameFolderScanner.ReadModFolders(root, issues)) Classify(f, entries, unmanaged, findings);
                foreach (var i in issues) findings.Add(FromIssue(i, null, "loader"));
                break;
            }
            case "package":
            {
                report.InputKind = "package";
                var pkg = PackageInspector.Inspect(root);
                var f = new InstalledFolder { Name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/')), Path = root, Package = pkg };
                Classify(f, entries, unmanaged, findings, force: true);
                break;
            }
            default: throw new CheckInputException($"Unknown input kind \"{kind}\" (use game, mods or package).");
        }

        // Packages to add are keyed by position, never by folder name: two downloads can both be called "Mod", and a shared key would let one hide the other.
        var addedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string> { root };
        int addNo = 0;
        foreach (var add in o.Add)
        {
            var addRoot = ResolveDirectory(add);
            roots.Add(addRoot);
            if (!addedRoots.Add(addRoot.TrimEnd('\\', '/'))) { report.NotChecked.Add("A folder given more than once with --add was counted once."); continue; }
            foreach (var pkg in ReadAdded(addRoot))
            {
                if (!addedDirs.Add(pkg.Dir.TrimEnd('\\', '/'))) continue;
                entries.Add(new Entry { Cand = new Candidate { Key = $"add:{++addNo}", Pkg = pkg, FolderName = pkg.FolderName, Installed = false }, State = "would-be-added" });
            }
        }
        // One folder can hold one mod (Windows folder names ignore letter case): two packages that would be installed under the same name cannot both exist.
        foreach (var grp in entries.GroupBy(e => e.Cand.FolderName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Any(e => e.State == "would-be-added")).ToList())
        {
            int k = 0;
            foreach (var e in grp.Where(e => e.State == "would-be-added")) e.Label = $"{e.Cand.FolderName} (to add #{++k})";
            findings.Add(new CheckFinding
            {
                Impact = "blocker", Code = "folder-name-collision", EnforcedBy = "tools", Title = $"More than one mod would be installed into the folder name \"{grp.Key}\"",
                Detail = $"A folder holds one mod, so these cannot all exist side by side: {string.Join(", ", grp.Select(e => e.Label ?? e.Cand.FolderName + " (installed)"))}. Give them different folder names (the folder name also decides the start order).",
                Packages = grp.Select(e => e.Label ?? e.Cand.FolderName).ToList()
            });
        }

        if (kind == "game" && game is { ModsDir: ModsDirState.Missing } && entries.Count == 0)
            findings.Add(new CheckFinding { Impact = "note", Code = "no-mods-folder", Title = "No mods folder", Detail = "This game folder has no \"mods\" folder, so the loader has nothing to start." });
        if (entries.Count == 0 && unmanaged.Count == 0 && kind != "game")
            throw new CheckInputException("No mod folders were found there. Point the checker at a game folder, a mods folder, or one mod folder (a folder holding mod.ini).");

        // ---- evidence: what is known about the loader ----------------------------------
        LoaderEvidence loader;
        if (game is not null) loader = LoaderEvidence.FromGame(game, cache);
        else if (assumed is not null) loader = LoaderEvidence.Assume(assumed);
        else loader = LoaderEvidence.NotInspected;
        if (game is not null && assumed is not null)
            report.NotChecked.Add("--loader was ignored because a game folder was given; the real loader files were used.");

        // ---- file hashes ---------------------------------------------------------------------
        var active = entries.Select(e => e.Cand).ToList();
        // Where a package's required extra files would come from (cheap, so also done in --quick mode: otherwise a complete package would look incomplete).
        foreach (var c in active)
        {
            if (c.Pkg.Manifest is null || c.Pkg.Manifest.State == ManifestState.NoManifest) continue;
            if (!c.Installed && c.PackageRoot is null) c.PackageRoot = DefaultPackageRoot(c.Pkg.Dir);
            if (c.PackageRoot is not null) LooseAssets.Resolve(c.Pkg, c.PackageRoot);
        }
        if (!o.Quick)
        {
            o.Progress?.Invoke("Checking file hashes (this reads every mod file" + (game is not null ? " and the game files it replaces" : "") + "; --quick skips it)...");
            foreach (var c in active)
            {
                if (c.Pkg.Manifest is null || c.Pkg.Manifest.State == ManifestState.NoManifest) continue;
                var vr = PackageVerifier.Verify(c.Pkg, new VerifyOptions { Cache = cache, GameRoot = game?.Exists == true ? game.Root : null, Deep = o.Deep, DeepCache = new DeepCache() });
                PackageVerifier.Apply(c.Pkg, vr);
            }
            report.FileHashes = o.Deep ? "full-with-archive-rebuild" : "full";
        }
        else report.FileHashes = "skipped";

        // ---- the shared rules --------------------------------------------------------------------
        var val = ActiveSetValidator.Validate(active, game, cache, loader, new ActiveSetValidator.ValidationOptions { RequireVerification = false });

        foreach (var i in val.GameIssues) AddGameIssue(i, findings);
        foreach (var e in entries)
        {
            var c = e.Cand;
            if (val.ByCandidate.TryGetValue(c.Key, out var list))
                foreach (var i in list.Where(x => x.Severity >= Severity.Warning)) findings.Add(FromIssue(i, e.Name, IssueInfo.LoaderEnforced(i) ? "loader" : "tools"));
        }
        string FolderOf(string key) => entries.FirstOrDefault(x => x.Cand.Key == key)?.Name ?? key;
        foreach (var cf in val.Conflicts.Conflicts)
            findings.Add(new CheckFinding
            {
                Impact = cf.Certainty == Certainty.Unknown ? "unknown" : cf.Severity == Severity.Blocker ? "blocker" : "note",
                Code = cf.Code, Title = cf.Title, Detail = cf.Explanation,
                EnforcedBy = cf.LoaderEnforced ? "loader" : "tools",
                Packages = cf.Parties.Select(FolderOf).Distinct().ToList()
            });
        foreach (var n in val.Conflicts.Notes)
            findings.Add(new CheckFinding
            {
                Impact = n.Code is "native-unknown" or "free-text-conflict" ? "unknown" : "note",
                Code = n.Code, Title = n.Title, Detail = n.Text, EnforcedBy = n.Code is "id-case" ? "loader" : "tools",
                Packages = n.Parties.Select(FolderOf).Distinct().ToList()
            });

        // ---- loader knowledge ----------------------------------------------------------------------
        report.Loader = new CheckLoader
        {
            Knowledge = loader.Knowledge switch
            {
                LoaderKnowledge.Verified => "verified", LoaderKnowledge.Assumed => "assumed", LoaderKnowledge.Unrecognized => "unrecognized",
                LoaderKnowledge.Absent => "absent", _ => "not-inspected"
            },
            Profile = loader.Profile?.Id, ProfileSource = loader.Profile is null ? null : loader.ProfileIsUserSupplied ? "user-supplied" : "built-in",
            HostSha256 = loader.HostSha, ProxySha256 = loader.ProxySha
        };
        if (entries.Count > 0 && loader.Knowledge is not (LoaderKnowledge.Verified or LoaderKnowledge.Assumed))
        {
            var (code, title, detail) = loader.Knowledge switch
            {
                LoaderKnowledge.Unrecognized => ("loader-unrecognized", "The mod loader build is not one this tool knows",
                    "ChaoGarage.dll and dinput8.dll are present but their SHA-256 values match no known loader build (builds are recognised by hash, never by a version label). The rules here were written against a known build, so compatibility cannot be confirmed."),
                LoaderKnowledge.Absent => ("loader-missing", "The mod loader is not installed in this folder",
                    "ChaoGarage.dll and dinput8.dll are not both present, so the game will not load any of these mods as things stand. This tool does not install the loader."),
                _ => ("loader-not-inspected", "No loader was inspected",
                    "Only the mod files were checked. Give the game folder (the folder containing the game's .exe) to compare with the real loader, or use --loader <profile> to check against a named loader build you know you have.")
            };
            findings.RemoveAll(f => f.Code == "loader-unrecognized" || f.Code == "loader-missing");   // the game scan reports these too; keep one
            findings.Add(new CheckFinding { Impact = "unknown", Code = code, Title = title, Detail = detail, EnforcedBy = "tools" });
        }
        if (loader.GameInspected && entries.Any(e => e.Cand.Pkg.Sidecar?.TargetExecutableSha256.Count > 0))
            report.Loader.GameExecutableSha256 = loader.GameExecutableSha;

        // ---- packages and order ------------------------------------------------------------------------
        var order = StartupOrder.Compute(entries.Select(e => e.Cand).ToList(), c => c.FolderName, c => c.Id);
        report.StartOrder = order.Select(c => entries.First(e => e.Cand == c).Name).ToList();
        foreach (var e in entries)
        {
            var p = e.Cand.Pkg;
            report.Packages.Add(new CheckedPackage
            {
                Folder = e.Name, State = e.State, Id = p.Id, Name = string.IsNullOrEmpty(p.Manifest?.Name) ? p.Sidecar?.Name : p.Manifest?.Name,
                Version = p.Version, Homepage = p.Sidecar?.Homepage,
                DescriptionFile = p.Sidecar is null ? (p.Issues.Any(i => i.Code is "sidecar-unreadable") ? "unreadable" : "none")
                    : !p.Sidecar.Interpretable ? "not understood" : "read (" + (p.Sidecar.SchemaText ?? "no format stated") + ")",
                Runtime = p.Runtime is { IsPe: true } pe ? $"{PeSummary.MachineName(pe.Machine)} {(pe.IsDll ? "DLL" : "program")}" + (pe.ExportsRead ? (pe.ExportNamesSorted && PackageInspector.RequiredRuntimeExports.All(n => pe.ExportKinds.TryGetValue(n, out var k) && k == ExportKind.Code) ? ", required entry points present" : ", required entry points NOT confirmed") : "") : p.Runtime is not null ? "not a valid DLL" : null
            });
        }
        report.Packages.AddRange(unmanaged);

        // ---- what this check does not cover (always stated) -----------------------------------------------------
        report.NotChecked.Add("Nothing was run or tested in the game: mod DLLs were only read as bytes, never loaded.");
        report.NotChecked.Add("Only what mods declare in their own files can be compared; a clash a mod does not declare (especially between native DLLs) cannot be seen.");
        report.NotChecked.Add("Hash equality shows files are intact and unchanged; it does not show who made them.");
        if (o.Quick) report.NotChecked.Add("File hashes were skipped (--quick): damaged or swapped payload files, and whether the plans fit your game files, were not checked.");
        else if (game is null) report.NotChecked.Add("No game folder was given, so the plans' original-file hashes and required extra files were not compared with a real game install.");
        if (!o.Deep && !o.Quick) report.NotChecked.Add("Archive rebuilds were not performed (--deep).");
        if (kind == "game") report.NotChecked.Add("Files outside the mods folder (other injectors, plug-ins, a different dinput8.dll) are not examined beyond the loader file hashes.");

        // ---- no installation paths or account names in anything that can be shared --------------------------------------------------------
        // Every string that comes from the machine or from a mod (folder names, ids, names, versions, messages) goes through the same scrubber; the internal
        // identities used above are not affected, and the same function is applied to both sides of every label lookup the renderers make.
        if (game is not null) roots.Add(game.Root);
        string? Sc(string? s) => s is null ? null : CheckOutput.ScrubPaths(s, roots);
        foreach (var f in findings) { f.Title = Sc(f.Title)!; f.Detail = Sc(f.Detail)!; f.Packages = f.Packages.Select(x => Sc(x)!).ToList(); }
        report.StartOrder = report.StartOrder.Select(x => Sc(x)!).ToList();
        foreach (var p in report.Packages)
        {
            p.Folder = Sc(p.Folder)!; p.Id = Sc(p.Id); p.Name = Sc(p.Name); p.Version = Sc(p.Version); p.DescriptionFile = Sc(p.DescriptionFile);
            p.Runtime = Sc(p.Runtime); p.Homepage = Sc(p.Homepage); p.Note = Sc(p.Note);
        }
        report.Loader.Profile = Sc(report.Loader.Profile);
        report.NotChecked = report.NotChecked.Select(x => Sc(x)!).ToList();

        // ---- verdict ------------------------------------------------------------------------------------------
        var verdict = report.Blockers > 0 ? CheckVerdict.Incompatible : report.Unknowns > 0 ? CheckVerdict.InsufficientInformation : CheckVerdict.DeclaredCompatible;
        report.VerdictValue = verdict;
        report.Verdict = CheckExit.Name(verdict);
        report.ExitCode = CheckExit.For(verdict);
        report.Summary = Summarize(report, entries.Count);
        return report;
    }

    static string Summarize(CheckReport r, int mods)
    {
        string what = mods == 0 ? "No mod would be started" : mods == 1 ? "1 mod checked" : $"{mods} mods checked";
        return r.VerdictValue switch
        {
            CheckVerdict.Incompatible => $"{what}: {r.Blockers} definite problem{(r.Blockers == 1 ? "" : "s")}" + (r.Unknowns > 0 ? $" and {r.Unknowns} thing{(r.Unknowns == 1 ? "" : "s")} that could not be confirmed" : "") + ". The listed combination will not work as it stands.",
            CheckVerdict.InsufficientInformation => $"{what}: no definite problem found, but {r.Unknowns} thing{(r.Unknowns == 1 ? "" : "s")} could not be confirmed, so this is not a promise that it works.",
            _ => $"{what}: every rule the mods declare is met. This is not a test in the game."
        };
    }

    // ---- input handling -------------------------------------------------------------------------------

    static string ResolveDirectory(string path)
    {
        string full;
        try { full = LoaderPath.Full(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new CheckInputException("That path is not a valid Windows path."); }
        if (!Directory.Exists(full)) throw new CheckInputException(File.Exists(full)
            ? "That is a file. Unzip the mod first, then give the checker the folder that contains mod.ini (or the game folder)."
            : "That folder does not exist.");
        return full;
    }

    static bool LooksLikePackage(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Select(System.IO.Path.GetFileName).Any(n => n is not null &&
                (n.Equals("mod.ini", StringComparison.OrdinalIgnoreCase) || n.StartsWith("mod.ini.", StringComparison.OrdinalIgnoreCase) || n.Equals(SidecarReader.FileName, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string Detect(string root)
    {
        if (File.Exists(System.IO.Path.Combine(root, GameFolderInfo.ExeName))) return "game";
        if (LooksLikePackage(root)) return "package";
        try
        {
            if (Directory.EnumerateDirectories(root).Any(LooksLikePackage)) return "mods";
            if (Directory.Exists(System.IO.Path.Combine(root, "mods"))) return "game";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        throw new CheckInputException("Nothing to check there: no game executable, no mods folder and no folder containing mod.ini was found.");
    }

    static void Classify(InstalledFolder f, List<Entry> entries, List<CheckedPackage> unmanaged, List<CheckFinding> findings, bool force = false)
    {
        if (f.IsLink)
        {
            unmanaged.Add(new CheckedPackage { Folder = f.Name, State = "unmanaged", Note = "A link (junction or symbolic link). The loader refuses links inside the mods folder; this tool does not follow it." });
            return;
        }
        var p = f.Package!;
        switch (p.Manifest?.State)
        {
            case ManifestState.Active:
                entries.Add(new Entry { Cand = new Candidate { Key = "folder:" + f.Name, Pkg = p, FolderName = f.Name, Installed = true } });
                break;
            case ManifestState.Disabled or ManifestState.DisabledAmbiguous:
                unmanaged.Add(new CheckedPackage { Folder = f.Name, State = "switched-off", Id = p.Id, Name = p.Manifest?.Name, Version = p.Version, Note = $"Its manifest is stored as \"{p.Manifest!.FileName}\", so the loader ignores this folder. Not checked as part of the active set." });
                break;
            default:
                if (force)
                {
                    // A single folder was given explicitly: report why it has no manifest instead of skipping it silently.
                    entries.Add(new Entry { Cand = new Candidate { Key = "folder:" + f.Name, Pkg = p, FolderName = f.Name, Installed = true } });
                }
                else
                    unmanaged.Add(new CheckedPackage { Folder = f.Name, State = "unmanaged", Name = p.Sidecar?.Name, Note = p.IsResearchCard
                        ? "A research note, not an installable mod."
                        : "No mod.ini here, so the loader ignores this folder. It may belong to another tool or be a mod for another loader; this tool does not examine or change it." });
                break;
        }
    }

    static IEnumerable<PackageInfo> ReadAdded(string dir)
    {
        var self = PackageInspector.Inspect(dir);
        if (self.HasManifest) { yield return self; yield break; }
        bool any = false;
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (LoaderPath.IsReparse(d)) continue;
            var p = PackageInspector.Inspect(d);
            if (!p.HasManifest) continue;
            any = true;
            yield return p;
        }
        if (!any) throw new CheckInputException("--add: no mod.ini was found in that folder or directly inside it.");
    }

    static string DefaultPackageRoot(string packageDir)
    {
        var parent = System.IO.Path.GetDirectoryName(packageDir.TrimEnd('\\', '/'));
        return parent is not null && System.IO.Path.GetFileName(parent).Equals("mods", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetDirectoryName(parent) ?? packageDir : packageDir;
    }

    // ---- finding conversion -----------------------------------------------------------------------------

    static CheckFinding FromIssue(Issue i, string? folder, string enforcedBy) => new()
    {
        Impact = IssueInfo.IsUnknown(i) ? "unknown" : i.Severity == Severity.Blocker ? "blocker" : "note",
        Code = i.Code, Title = i.Message, Detail = i.Detail ?? "", EnforcedBy = enforcedBy,
        Packages = folder is null ? new() : new() { folder }
    };

    static void AddGameIssue(Issue i, List<CheckFinding> findings)
    {
        if (i.Code is "game-protected") return;        // a manager-only precaution about where it may write; the checker writes nothing
        if (i.Code == "unfinished-change")
        {
            findings.Add(new CheckFinding { Impact = "unknown", Code = i.Code, Title = "An earlier change by the manager did not finish", Detail = "The mods folder may be in a half-changed state, so what is on disk cannot be relied on. Open the manager and use \"Restore previous state\"." });
            return;
        }
        findings.Add(FromIssue(i, null, i.Code is "mods-link" or "mods-not-folder" or "folder-link" ? "loader" : "tools"));
    }
}
