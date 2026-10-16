using AllStarsModManager.Transactions;

namespace AllStarsModManager;

public sealed class ManagerEnvironment
{
    public IGameProcessProbe Probe = new SystemGameProcessProbe();
    public IGameLauncher Launcher = new SteamUriLauncher();
    public SteamLocator Steam = new();
    /// <summary>Test-folder mode: the launch path is disabled entirely.</summary>
    public bool NoLaunch;
    public TransactionHooks? Hooks;

    /// <summary>A mode that cannot start anything: recording launcher, no Steam lookup, launching disabled.</summary>
    public static ManagerEnvironment Fixture(IGameProcessProbe? probe = null) => new()
    {
        Probe = probe ?? new SystemGameProcessProbe(),
        Launcher = new RecordingLauncher(),
        Steam = new SteamLocator(Array.Empty<string>()),
        NoLaunch = true
    };
}

/// <summary>
/// The manager's working model, shared by the window and the tests: the game folder, the rows (installed and library),
/// the user's pending on/off choices, profiles, and the operations that validate and apply them.
/// </summary>
public sealed class ManagerService
{
    public AppPaths Paths { get; }
    public Settings Settings { get; }
    public HashCache Cache { get; }
    public DeepCache Deep { get; }
    public ProfileStore Profiles { get; }
    public Importer Importer { get; }
    public ManagerEnvironment Env { get; }

    public GameFolderInfo? Game { get; private set; }
    public List<ModEntry> Entries { get; private set; } = new();
    public List<LibraryItem> Library { get; private set; } = new();
    /// <summary>Loader builds the user has described in data\loader-profiles.json (same format as the built-in table). Empty when the file is absent.</summary>
    public List<LoaderProfile> ExtraProfiles { get; private set; } = new();
    public List<string> ProfileFileProblems { get; } = new();
    public Dictionary<string, bool> Want { get; } = new();
    public Dictionary<string, string> ManifestChoice { get; } = new();
    /// <summary>Things a chosen profile needs that cannot be found. They block applying until dismissed or fixed.</summary>
    public List<string> ProfileGaps { get; } = new();
    public string? ActiveProfileName { get; private set; }

    public event Action? Changed;

    public ManagerService(AppPaths paths, ManagerEnvironment? env = null)
    {
        Paths = paths;
        paths.EnsureCreated();
        Env = env ?? new ManagerEnvironment();
        Settings = Settings.Load(paths);
        Cache = new HashCache(Path.Combine(paths.CacheDir, "hashes.json"));
        Deep = new DeepCache(Path.Combine(paths.CacheDir, "deep-ok.txt"));
        Profiles = new ProfileStore(paths);
        Importer = new Importer(paths, Cache);
        Importer.PurgeStaleIncoming();
        ActiveProfileName = Settings.ActiveProfile;
        _rememberedRoot = Settings.GameRoot;
        LoadExtraProfiles();
    }

    string? _rememberedRoot;

    /// <summary>Reads the optional data\loader-profiles.json (builds the user vouches for). A broken file is reported and ignored: nothing is trusted from it.</summary>
    void LoadExtraProfiles()
    {
        ExtraProfiles = new();
        ProfileFileProblems.Clear();
        var file = Path.Combine(Paths.DataDir, "loader-profiles.json");
        if (File.Exists(file)) ExtraProfiles = LoaderProfiles.LoadFile(file, ProfileFileProblems);
    }

    /// <summary>Saves preference changes made in the window (never records a test folder).</summary>
    public void PersistSettings() => SaveSettings();

    /// <summary>Saves settings; a game folder chosen only for this run (test folders) is never written to the file.</summary>
    void SaveSettings()
    {
        var current = Settings.GameRoot;
        Settings.GameRoot = _rememberedRoot;
        try { Settings.Save(Paths); } finally { Settings.GameRoot = current; }
    }

    void Raise() => Changed?.Invoke();

    // ---- game folder ---------------------------------------------------------------------

    /// <summary>Chooses the game folder. With <paramref name="remember"/> false (test folders) the choice is not stored in the settings file.</summary>
    public void SelectGame(string? root, bool remember = true)
    {
        Settings.GameRoot = string.IsNullOrWhiteSpace(root) ? null : LoaderPath.Full(root);
        if (remember) { _rememberedRoot = Settings.GameRoot; SaveSettings(); }
        Refresh(keepPending: false);
    }

    /// <summary>Read-only detection of the Steam-registered install.</summary>
    public string? DetectGame() => Env.Steam.FirstRegistered();

    public void AddLinkedFolder(string folder)
    {
        var full = LoaderPath.Full(folder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if (!Settings.LinkedFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) Settings.LinkedFolders.Add(full);
        SaveSettings();
        Refresh(keepPending: true);
    }

    public void RemoveLinkedFolder(string folder)
    {
        Settings.LinkedFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        Refresh(keepPending: true);
    }

    // ---- scanning ------------------------------------------------------------------------

    /// <summary>Re-reads the game folder and library (structure and pins only; use <see cref="VerifyAll"/> for the slow hashing).</summary>
    public void Refresh(bool keepPending)
    {
        var oldWant = new Dictionary<string, bool>(Want);
        Game = Settings.GameRoot is { } r ? GameFolderScanner.Scan(r, Cache, ExtraProfiles) : null;
        Library = LibraryScanner.Scan(Paths, Settings.LinkedFolders);
        Entries = BuildEntries(Game, Library);
        Want.Clear();
        foreach (var e in Entries) Want[e.Key] = keepPending && oldWant.TryGetValue(e.Key, out var w) ? w : e.IsActive;
        if (!keepPending) { ManifestChoice.Clear(); ProfileGaps.Clear(); }
        Raise();
    }

    static bool PackageLike(PackageInfo p) => p.HasManifest || File.Exists(Path.Combine(p.Dir, "plan.json")) || Directory.Exists(Path.Combine(p.Dir, "runtime"));

    /// <summary>
    /// Folders inside the game's mods folder that are not loader mods (no mod.ini and no plan or runtime: another tool's files, notes, anything else).
    /// The loader ignores them, they are not listed as mods, and the manager never examines, changes or converts them.
    /// </summary>
    public IReadOnlyList<string> UnmanagedFolders => Game is null ? Array.Empty<string>()
        : Game.Folders.Where(f => !f.IsLink && f.Package is { } p && !PackageLike(p)).Select(f => f.Name).ToList();

    public static List<ModEntry> BuildEntries(GameFolderInfo? game, IReadOnlyList<LibraryItem> library)
    {
        var entries = new List<ModEntry>();
        if (game is not null)
            foreach (var f in game.Folders)
            {
                var p = f.Package;
                if (p is null) continue;
                if (!PackageLike(p)) continue;
                entries.Add(new ModEntry { Key = ModEntry.InstalledKey(f.Name), Installed = p });
            }
        foreach (var lib in library)
        {
            var fp = lib.Package.Fingerprint;
            var match = fp is null ? null : entries.FirstOrDefault(e => e.Installed?.Fingerprint == fp && e.Library is null);
            if (match is not null) { match.Library = lib; continue; }
            var sameLib = fp is null ? null : entries.FirstOrDefault(e => e.Installed is null && e.Library?.Package.Fingerprint == fp);
            if (sameLib is not null) { sameLib.OtherLibraryVersions.Add(lib); continue; }
            entries.Add(new ModEntry { Key = ModEntry.LibraryKey(lib), Library = lib });
        }
        return entries;
    }

    /// <summary>Hashes all package files and checks them against the game files. Slow for large mods; reports bytes processed.</summary>
    public void VerifyAll(IProgress<long>? progress = null, CancellationToken ct = default, bool deep = false)
    {
        foreach (var e in Entries) VerifyEntry(e, progress, ct, deep);
        Cache.Save();
        Raise();
    }

    public void VerifyEntry(ModEntry e, IProgress<long>? progress, CancellationToken ct, bool deep, bool fresh = false)
    {
        foreach (var pkg in new[] { e.Installed, e.Library?.Package }.Where(p => p is not null))
        {
            var vr = PackageVerifier.Verify(pkg!, new VerifyOptions { Cache = Cache, GameRoot = Game?.Exists == true ? Game.Root : null, Deep = deep, DeepCache = Deep, Progress = progress, Fresh = fresh }, ct);
            PackageVerifier.Apply(pkg!, vr);
        }
    }

    // ---- choices -------------------------------------------------------------------------

    public bool IsWanted(ModEntry e) => Want.TryGetValue(e.Key, out var w) && w;
    public bool IsPending(ModEntry e) => IsWanted(e) != e.IsActive;
    public int PendingCount => Entries.Count(IsPending);

    public void SetWanted(ModEntry e, bool on)
    {
        Want[e.Key] = on;
        if (on && e.Id is not null)
            foreach (var o in Entries.Where(o => o != e && o.Id == e.Id)) Want[o.Key] = false;   // one version per mod ID
        Raise();
    }

    public void DiscardChanges()
    {
        foreach (var e in Entries) Want[e.Key] = e.IsActive;
        ManifestChoice.Clear();
        ProfileGaps.Clear();
        Raise();
    }

    public void ChooseManifest(ModEntry e, string fileName) { ManifestChoice[e.Key] = fileName; Raise(); }

    // ---- live hints ----------------------------------------------------------------------

    public Candidate CandidateFor(ModEntry e) => e.IsInstalled
        ? new Candidate { Key = e.Key, Pkg = e.Installed!, FolderName = e.InstalledFolder!, Installed = true }
        : new Candidate { Key = e.Key, Pkg = e.Library!.Package, FolderName = LoaderPath.SafeFolderName(e.Library.FolderName, e.Id ?? "Mod"), Installed = false, PackageRoot = e.Library.PackageRoot };

    /// <summary>
    /// For every row: the names of switched-on mods it would clash with if it were switched on too (rows that are on
    /// show their actual clashes). Pure and quick: uses the plans already read, no hashing.
    /// </summary>
    public Dictionary<string, List<string>> ConflictHints()
    {
        var hints = new Dictionary<string, List<string>>();
        var wanted = Entries.Where(IsWanted).Select(CandidateFor).ToList();
        var evidence = Game is null ? null : LoaderEvidence.FromGame(Game, Cache);
        var current = ConflictAnalyzer.Analyze(wanted, evidence);
        foreach (var e in Entries)
        {
            var names = new List<string>();
            if (IsWanted(e))
            {
                foreach (var c in current.Conflicts.Where(c => c.Parties.Contains(e.Key)))
                    foreach (var p in c.Parties.Where(p => p != e.Key)) { var other = wanted.FirstOrDefault(w => w.Key == p); if (other is not null) names.Add(other.Pkg.DisplayName); }
            }
            else if (e.Package.Plan is not null || e.Package.Sidecar is not null)
            {
                var trial = wanted.Where(w => w.Id != e.Id).Append(CandidateFor(e)).ToList();
                foreach (var c in ConflictAnalyzer.Analyze(trial, evidence).Conflicts.Where(c => c.Parties.Contains(e.Key)))
                    foreach (var p in c.Parties.Where(p => p != e.Key)) { var other = trial.FirstOrDefault(w => w.Key == p); if (other is not null) names.Add(other.Pkg.DisplayName); }
            }
            hints[e.Key] = names.Distinct().ToList();
        }
        return hints;
    }

    // ---- planning and applying -----------------------------------------------------------

    PlanRequest Request() => new()
    {
        Game = Game ?? throw new InvalidOperationException("Choose the game folder first."),
        Entries = Entries, Want = Want, ManifestChoice = ManifestChoice, Cache = Cache, DeepCache = Deep, ExtraProfiles = ExtraProfiles
    };

    /// <summary>Quick evaluation with whatever verification results exist; no hashing.</summary>
    public PlanResult Evaluate()
    {
        var res = ChangePlanner.Build(Request());
        foreach (var gap in ProfileGaps) res.Validation.GameIssues.Add(new(Severity.Blocker, "profile-gap", gap));
        return res;
    }

    /// <summary>Verifies everything the change depends on (including a rebuild of each changed archive) and returns the final plan.</summary>
    public PlanResult Prepare(IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (Game is null) throw new InvalidOperationException("Choose the game folder first.");
        Refresh(keepPending: true);   // re-read every manifest, plan and runtime hash: the files may have changed since the last scan
        foreach (var e in Entries.Where(x => IsWanted(x) || IsPending(x)))
            VerifyEntry(e, progress, ct, deep: true);
        Cache.Save();
        return Evaluate();
    }

    public ApplyResult Apply(PlanResult plan, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        if (Game is null) return new ApplyResult { Declined = true, Message = "Choose the game folder first." };
        if (!plan.CanApply) return new ApplyResult { Declined = true, Message = plan.Blockers.FirstOrDefault() ?? "There is nothing to apply." };
        var engine = new TransactionEngine(Game.Root, Env.Probe, Env.Hooks);
        var result = engine.Apply(plan.Plan, ct, progress);
        Refresh(keepPending: !result.Success);
        return result;
    }

    public ApplyResult Recover()
    {
        if (Game is null) return new ApplyResult { Declined = true, Message = "Choose the game folder first." };
        var r = new TransactionEngine(Game.Root, Env.Probe, Env.Hooks).Recover();
        Refresh(keepPending: false);
        return r;
    }

    public ApplyResult UndoLast()
    {
        if (Game is null) return new ApplyResult { Declined = true, Message = "Choose the game folder first." };
        var last = TransactionStore.History(Game.Root).LastOrDefault(j => j.Status == TxnStatus.Committed && !File.Exists(Path.Combine(TransactionStore.HistoryDir(Game.Root), j.Txn, "journal-undone.json")));
        if (last is null) return new ApplyResult { Declined = true, Message = "There is no finished change to undo." };
        var r = new TransactionEngine(Game.Root, Env.Probe, Env.Hooks).UndoCommitted(last.Txn);
        Refresh(keepPending: false);
        return r;
    }

    public PlanResult PrepareUninstall(ModEntry e, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        foreach (var other in Entries.Where(x => x.IsActive && x != e)) VerifyEntry(other, progress, ct, deep: true);
        return ChangePlanner.BuildUninstall(Request(), e);
    }

    // ---- settings files ------------------------------------------------------------------

    public PlanResult PrepareConfigSave(ModEntry e, string fileName, byte[] bytes, string? loadedSha = null) => ChangePlanner.BuildConfigEdit(Request(), e, fileName, bytes, loadedSha);

    // ---- profiles ------------------------------------------------------------------------

    public Profile SaveProfile(string name)
    {
        var err = ProfileStore.ValidateName(name);
        if (err is not null) throw new InvalidOperationException(err);
        var p = ProfileStore.FromSelection(name, Entries.Where(IsWanted));
        Profiles.Save(p);
        ActiveProfileName = p.Name;
        Settings.ActiveProfile = p.Name;
        SaveSettings();
        Raise();
        return p;
    }

    /// <summary>Selects the profile's mods as the pending choice. Nothing is changed in the game folder until Apply.</summary>
    public ProfileStore.Resolution UseProfile(string name)
    {
        var p = Profiles.Find(name) ?? throw new InvalidOperationException("No such profile.");
        var res = ProfileStore.Resolve(p, Entries);
        foreach (var (k, v) in res.Want) Want[k] = v;
        // keep the one-version-per-ID rule even if a profile (edited by hand) lists two versions
        foreach (var g in Entries.Where(e => e.Id is not null && Want[e.Key]).GroupBy(e => e.Id!).Where(g => g.Count() > 1))
            foreach (var extra in g.Skip(1)) Want[extra.Key] = false;
        ProfileGaps.Clear();
        ProfileGaps.AddRange(res.Missing.Select(m => $"Profile \"{p.Name}\" needs {m}"));
        ActiveProfileName = p.Name;
        Settings.ActiveProfile = p.Name;
        SaveSettings();
        Raise();
        return res;
    }

    public void DismissProfileGaps() { ProfileGaps.Clear(); Raise(); }

    // ---- launching -----------------------------------------------------------------------

    public LaunchPlan GetLaunchPlan() => LaunchPlanner.Plan(Game, Env.Steam, Env.NoLaunch);

    /// <summary>Starts the game through Steam if (and only if) the selected folder is the registered install.</summary>
    public LaunchPlan Launch()
    {
        var plan = GetLaunchPlan();
        if (!plan.CanLaunch) throw new InvalidOperationException(plan.Reason);
        var probe = Env.Probe.Check(Game!.Root);
        if (probe.State == GameRunState.Running) throw new InvalidOperationException("The game is already running.");
        if (Game.PendingJournal) throw new InvalidOperationException("An earlier change did not finish. Restore the previous state first.");
        Env.Launcher.Launch(plan);
        return plan;
    }
}
