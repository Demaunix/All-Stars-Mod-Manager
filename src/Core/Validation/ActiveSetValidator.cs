namespace AllStarsModManager;

public enum AssetAction { AlreadyPresent, Create }

public sealed class AssetPlan
{
    public Candidate Owner = null!;
    public LooseAsset Asset = null!;
    public AssetAction Action;
    public string TargetFullPath = "";
}

public sealed class ValidationReport
{
    public List<Issue> GameIssues = new();
    public Dictionary<string, List<Issue>> ByCandidate = new();
    public ConflictReport Conflicts = new();
    public List<AssetPlan> Assets = new();
    public List<Candidate> Active = new();

    public bool HasBlockers =>
        GameIssues.Any(i => i.Severity == Severity.Blocker) ||
        ByCandidate.Values.Any(l => l.Any(i => i.Severity == Severity.Blocker)) ||
        Conflicts.HasBlockers;

    /// <summary>Plain sentences explaining every blocker, in a stable order.</summary>
    public List<string> BlockerLines()
    {
        var lines = new List<string>();
        foreach (var i in GameIssues.Where(i => i.Severity == Severity.Blocker)) lines.Add(i.Message);
        foreach (var c in Active)
            if (ByCandidate.TryGetValue(c.Key, out var l))
                foreach (var i in l.Where(i => i.Severity == Severity.Blocker)) lines.Add($"{c.Display}: {i.Message}");
        foreach (var c in Conflicts.Conflicts.Where(c => c.Severity == Severity.Blocker)) lines.Add(c.Title + ". " + c.Explanation);
        return lines;
    }

    public IEnumerable<Issue> WarningsFor(string key) => ByCandidate.TryGetValue(key, out var l) ? l.Where(i => i.Severity == Severity.Warning) : Enumerable.Empty<Issue>();
}

public static class LooseAssetPolicy
{
    static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".lnk", ".sys", ".jar", ".py", ".hta", ".wsf", ".reg", ".xpac", ".sbn" };

    /// <summary>Returns the game-relative path (backslashes) or throws PathRuleException.</summary>
    public static string ValidateTarget(string target)
    {
        var rel = LoaderPath.Relative(target);
        var folded = LoaderPath.Fold(rel);
        if (!folded.StartsWith("resource\\", StringComparison.Ordinal)) throw new PathRuleException("extra files may only be placed under Resource");
        foreach (var seg in rel.Split('\\'))
            if (LoaderPath.IsReservedDeviceName(seg)) throw new PathRuleException("reserved device name in path");
        if (Denied.Contains(Path.GetExtension(rel))) throw new PathRuleException("this file type cannot be placed outside the mod folder");
        return rel;
    }
}

public static class ActiveSetValidator
{
    /// <summary>
    /// Validates the whole intended active set against the actual game folder. Pure evaluation of already-gathered facts
    /// (package inspection and verification results); it writes nothing.
    /// </summary>
    public static ValidationReport Validate(IReadOnlyList<Candidate> intended, GameFolderInfo game, HashCache cache, IEnumerable<Candidate>? alsoInstalledInactive = null, LoaderEvidence? loader = null)
        => Validate(intended, game, cache, loader, new ValidationOptions());

    /// <summary>Choices that only the read-only checker makes; the manager always uses the defaults.</summary>
    public sealed class ValidationOptions
    {
        /// <summary>True (manager): a package whose files have not been hashed yet is a blocker. False (checker --quick): it is reported as not checked instead.</summary>
        public bool RequireVerification = true;
    }

    /// <summary>
    /// The one implementation of the rules, used by the manager (with its game folder) and by the manager-free checker (with or without one).
    /// <paramref name="game"/> may be null when only a package or a plain mods folder was given: game-folder facts are then not evaluated.
    /// </summary>
    public static ValidationReport Validate(IReadOnlyList<Candidate> intended, GameFolderInfo? game, HashCache cache, LoaderEvidence? loader, ValidationOptions? options)
    {
        options ??= new ValidationOptions();
        // A report is keyed by candidate; two candidates sharing a key would silently overwrite each other's findings.
        if (intended.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != intended.Count) throw new ArgumentException("Candidate keys must be unique.", nameof(intended));
        var rep = new ValidationReport { Active = intended.ToList() };
        // Game-level problems. Only those that matter to whether the loader will accept the folder are blockers.
        if (game is not null)
            foreach (var i in game.Issues)
            {
                if (i.Severity == Severity.Blocker && i.Code == "folder-link") rep.GameIssues.Add(i);
                else if (i.Severity == Severity.Blocker && i.Code is "game-missing" or "game-exe-missing" or "game-protected" or "mods-link" or "mods-not-folder" or "unfinished-change") rep.GameIssues.Add(i);
                else if (i.Severity == Severity.Warning) rep.GameIssues.Add(i);
            }

        foreach (var c in intended)
        {
            var list = new List<Issue>();
            rep.ByCandidate[c.Key] = list;
            foreach (var i in c.Pkg.AllIssues.Where(i => i.Severity >= Severity.Warning)) list.Add(i);
            if (c.Pkg.Manifest is null || c.Pkg.Manifest.State == ManifestState.NoManifest)
                list.Add(new(Severity.Blocker, "no-manifest", "This folder has no manifest the loader can read."));
            if (c.Pkg.IsResearchCard) list.Add(new(Severity.Blocker, "research-card", "This is a research note and cannot be switched on."));
            if (c.Pkg.Manifest is { State: ManifestState.DisabledAmbiguous, ChoiceMade: false })
                list.Add(new(Severity.Blocker, "manifest-ambiguous", "Several switched-off manifests exist; choose which one to use."));
            if (options.RequireVerification)
            {
                if (c.Pkg.Plan is not null && c.Pkg.Integrity == Integrity.NotChecked)
                    list.Add(new(Severity.Blocker, "not-verified", "Its files have not been checked yet."));
                if (game is not null && c.Pkg.Plan is not null && c.Pkg.Fit == GameFit.NotChecked && c.Pkg.Plan.Groups.Any(g => g.BaseSha is not null))
                    list.Add(new(Severity.Blocker, "not-fit-checked", "It has not been checked against your game files yet."));
            }
            if (c.Pkg.Integrity == Integrity.Failed && !list.Any(i => i.Severity == Severity.Blocker))
                list.Add(new(Severity.Blocker, "integrity", "Its files are damaged or do not match its manifest."));
            if (c.Pkg.Plan is null && c.Pkg.Manifest?.Id is not null && !list.Any(i => i.Severity == Severity.Blocker))
                list.Add(new(Severity.Blocker, "no-plan", "Its plan could not be read."));
            // Path length headroom for files the loader writes inside the mod folder at startup (shader cache seeds).
            if (game is not null && c.Pkg.Plan?.Groups.Any(g => g.Format == "rtsc_merge") == true)
            {
                int est = Path.Combine(game.ModsPath, c.FolderName).Length + 1 + @".cache\shaders\".Length + 64 + @"\data.rtsc.tmp-".Length + 24;
                if (est >= 259) list.Add(new(Severity.Warning, "long-path", "The folder location is long enough that files the loader creates at startup may exceed the Windows path limit. Move the game to a shorter path if the mod fails to start."));
            }
        }

        if (game is not null) AssetsFor(intended, game, cache, rep);
        rep.Conflicts = ConflictAnalyzer.Analyze(intended, loader ?? (game is null ? null : LoaderEvidence.FromGame(game, cache)));
        return rep;
    }
    static void AssetsFor(IReadOnlyList<Candidate> intended, GameFolderInfo game, HashCache cache, ValidationReport rep)
    {
        var claimed = new Dictionary<string, (Candidate C, LooseAsset A)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in intended)
        {
            var sc = c.Pkg.Sidecar;
            if (sc is null || sc.LooseAssets.Count == 0) continue;
            var issues = rep.ByCandidate[c.Key];
            if (sc.Id is { Length: > 0 } sid && c.Id is not null && sid != c.Id) continue;   // description for another mod is ignored
            foreach (var a in sc.LooseAssets)
            {
                string rel;
                try { rel = LooseAssetPolicy.ValidateTarget(a.Target); }
                catch (PathRuleException e) { issues.Add(new(Severity.Blocker, "loose-path", $"Required file \"{PackageInfo.Clean(a.Target, 80)}\" has an unsafe location: {e.Message}.")); continue; }
                if (!Sha.IsLoaderHash(a.Sha)) { issues.Add(new(Severity.Blocker, "loose-hash-format", $"Required file {rel.Replace('\\', '/')} has no valid recorded hash.")); continue; }
                string full = LoaderPath.Child(game.Root, rel);
                if (!LoaderPath.IsPlainInside(game.Root, full)) { issues.Add(new(Severity.Blocker, "loose-link", $"The location of {rel.Replace('\\', '/')} passes through a link; the manager will not use it.")); continue; }
                if (claimed.TryGetValue(rel, out var prior) && prior.C.Key != c.Key && prior.A.Sha != a.Sha) continue;   // reported as a collision by the conflict rules
                claimed[rel] = (c, a);

                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    var same = (a.Bytes < 0 || fi.Length == a.Bytes) && cache.Get(full) == a.Sha;
                    if (same) { rep.Assets.Add(new AssetPlan { Owner = c, Asset = a, Action = AssetAction.AlreadyPresent, TargetFullPath = full }); continue; }
                    issues.Add(new(Severity.Blocker, "loose-different", $"A different file already exists at {rel.Replace('\\', '/')}. The manager never overwrites existing files, so this mod cannot be completed."));
                    continue;
                }
                if (Directory.Exists(full)) { issues.Add(new(Severity.Blocker, "loose-different", $"A folder exists where {rel.Replace('\\', '/')} should go.")); continue; }
                if (a.SourceFullPath is null || !File.Exists(a.SourceFullPath))
                {
                    issues.Add(new(Severity.Blocker, "loose-incomplete", $"Incomplete package: {rel.Replace('\\', '/')} is required but is neither in the package nor in the game folder."));
                    continue;
                }
                rep.Assets.Add(new AssetPlan { Owner = c, Asset = a, Action = AssetAction.Create, TargetFullPath = full });
            }
        }
    }
}
