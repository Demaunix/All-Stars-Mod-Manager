using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AllStarsModManager.Transactions;

namespace AllStarsModManager.App;

public sealed class FactVM { public string Label { get; init; } = ""; public string Value { get; init; } = ""; }

public sealed class ConfigRowVM : Obs
{
    public ConfigRow Row { get; }
    public ConfigRowVM(ConfigRow row) { Row = row; }
    public string Key => Row.Key;
    public string Section => Row.Section;
    public string Label => Row.Schema is { Label.Length: > 0 } s ? s.Label : Row.Key;
    public string Description => Row.Schema?.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public string SectionText => Row.Section.Length > 0 ? "[" + Row.Section + "]" : "";
    public string Type => Row.Schema?.Type ?? "text";
    public string[] Choices => Row.Schema?.Choices ?? Array.Empty<string>();
    public bool IsBool => Type == "bool";
    public bool IsEnum => Type == "enum" && Choices.Length > 0;
    public bool IsText => !IsBool && !IsEnum;
    public string Value { get => Row.Value; set { if (Row.Value != value) { Row.Value = value; Raise(); Raise(nameof(Changed)); Raise(nameof(BoolValue)); } } }
    public bool BoolValue
    {
        get => Row.Value is "1" or "true" or "True";
        set { var style = Row.OriginalValue is "true" or "false" or "True" or "False"; Value = value ? (style ? "true" : "1") : (style ? "false" : "0"); }
    }
    public bool Changed => Row.Changed;
    public bool MarkEnabledZero => Row.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && Row.Value.Trim() == "0";
}

public sealed class DetailVM : Obs
{
    public string Title { get; init; } = "";
    public string SubTitle { get; init; } = "";
    public string StatusShort { get; init; } = "";
    public string StatusLong { get; init; } = "";
    public bool HasStatus => StatusShort.Length > 0;
    public string Summary { get; init; } = "";
    public bool HasSummary => Summary.Length > 0;
    public string WhereText { get; init; } = "";
    public string PersistenceNote { get; init; } = "";
    public bool HasPersistence => PersistenceNote.Length > 0;
    public ObservableCollection<ChipVM> Chips { get; } = new();
    public ObservableCollection<NoteVM> Problems { get; } = new();
    public bool HasProblems => Problems.Count > 0;
    public ObservableCollection<NoteVM> Compat { get; } = new();
    public ObservableCollection<FactVM> Facts { get; } = new();
    public ObservableCollection<string> Requirements { get; } = new();
    public bool HasRequirements => Requirements.Count > 0;
    public ObservableCollection<string> ManifestChoices { get; } = new();
    public bool NeedsManifestChoice => ManifestChoices.Count > 1;
    public ObservableCollection<FactVM> Targets { get; } = new();
    public string? InstalledDir { get; init; }
    public bool CanRemove { get; init; }
    public bool CanOpenFolder => InstalledDir is not null;
}

public sealed class MainViewModel : Obs
{
    public ManagerService Svc { get; }
    public Dispatcher Ui { get; } = Application.Current.Dispatcher;
    public bool FixtureMode { get; }
    public ObservableCollection<ModRowVM> Rows { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<BannerVM> Banners { get; } = new();
    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<string> FilterNames { get; } = new() { "All mods", "Switched on", "Switched off", "Not installed", "Needs attention", "Waiting to apply" };

    // hooks the window supplies (dialogs live in the window, not in the model)
    public Func<string, string, string?, string?>? Prompt;
    public Func<string, string, bool>? Confirm;
    public Action<string, string>? Inform;
    public Func<Func<IProgress<long>?, CancellationToken, PlanResult>, string, bool>? ShowApply;
    public Func<string, bool>? ShowImport;
    public Action? ShowSettings;
    public Func<ModRowVM, ConfigFile, string?>? EditRawText;

    Dictionary<string, List<string>> _hints = new();
    Dictionary<string, List<string>> _runtimeOff = new();
    IGameProcessProbe Probe => Svc.Env.Probe;
    ProbeResult _lastProbe = new(GameRunState.NotRunning, "");
    string? _detected;

    public MainViewModel(ManagerService svc, bool fixtureMode)
    {
        Svc = svc;
        FixtureMode = fixtureMode;
        var cvs = new ListCollectionView(Rows);
        cvs.SortDescriptions.Add(new SortDescription(nameof(ModRowVM.SortKey), ListSortDirection.Ascending));
        cvs.Filter = o => o is ModRowVM r && Matches(r);
        View = cvs;
        ApplyCommand = new RelayCommand(() => _ = ReviewAndApplyAsync(), () => CanApply);
        DiscardCommand = new RelayCommand(() => { Svc.DiscardChanges(); RefreshAll(); }, () => Svc.PendingCount > 0 || Svc.ProfileGaps.Count > 0);
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync(), () => !Busy);
        AddModCommand = new RelayCommand(() => AddModRequested?.Invoke(), () => !Busy);
        LaunchCommand = new RelayCommand(() => Launch(), () => CanLaunch);
        OpenGameFolderCommand = new RelayCommand(() => OpenGameFolder(), () => Svc.Game?.Exists == true);
        SettingsCommand = new RelayCommand(() => ShowSettings?.Invoke());
        SaveProfileCommand = new RelayCommand(() => SaveProfile(), () => Svc.Game is not null && !Busy);
        ReloadProfileCommand = new RelayCommand(() => { if (SelectedProfileName is { } n && n != NoProfile) UseProfile(n); }, () => SelectedProfileName is { } n && n != NoProfile && !Busy);
        RenameProfileCommand = new RelayCommand(() => RenameProfile(), () => SelectedProfileName is not null);
        DeleteProfileCommand = new RelayCommand(() => DeleteProfile(), () => SelectedProfileName is not null);
        UndoLastCommand = new RelayCommand(() => _ = UndoLastAsync(), () => Svc.Game is not null && !Busy);
        ClearFilterCommand = new RelayCommand(() => { SearchText = ""; FilterIndex = 0; });
    }

    public event Action? AddModRequested;

    // ---- commands --------------------------------------------------------------------
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddModCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenGameFolderCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand ReloadProfileCommand { get; }
    public RelayCommand RenameProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand UndoLastCommand { get; }

    // ---- state shown in the window ---------------------------------------------------
    bool _busy; public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(NotBusy)); RaiseCommands(); foreach (var r in Rows) r.Raise(nameof(ModRowVM.CanToggle)); } } }
    public bool NotBusy => !Busy;
    string _busyText = ""; public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    string _search = ""; public string SearchText { get => _search; set { if (Set(ref _search, value)) { View.Refresh(); RaiseShown(); } } }
    int _filter; public int FilterIndex { get => _filter; set { if (Set(ref _filter, value)) { View.Refresh(); RaiseShown(); } } }

    // ---- presentation state (which page is open, table width class, "N shown" line) ----
    int _page; public int SelectedPage { get => _page; set => Set(ref _page, value); }
    bool _showId = true; public bool ShowIdColumn { get => _showId; set => Set(ref _showId, value); }
    public string ShownText => Rows.Count == 0 ? "" : ((ListCollectionView)View).Count == Rows.Count ? $"{Rows.Count} mods" : $"{((ListCollectionView)View).Count} of {Rows.Count} mods shown";
    public bool NoMatches => Rows.Count > 0 && ((ListCollectionView)View).Count == 0;
    public RelayCommand ClearFilterCommand { get; private set; } = null!;
    void RaiseShown() { Raise(nameof(ShownText)); Raise(nameof(NoMatches)); }
    ModRowVM? _selected;
    public ModRowVM? SelectedRow { get => _selected; set { if (Set(ref _selected, value)) { BuildDetails(); } } }
    DetailVM? _details; public DetailVM? Details { get => _details; private set { Set(ref _details, value); Raise(nameof(HasDetails)); Raise(nameof(NoDetails)); } }
    public bool HasDetails => Details is not null;
    public bool NoDetails => Details is null;
    string? _selectedProfile; public string? SelectedProfileName { get => _selectedProfile; set { if (Set(ref _selectedProfile, value)) { RaiseCommands(); if (!_suppressProfile && value is not null && value != NoProfile) UseProfile(value); } } }
    bool _suppressProfile;
    public const string NoProfile = "(no profile)";

    public string GameTitle { get; private set; } = "No game folder selected";
    public string GamePath { get; private set; } = "";
    public ObservableCollection<ChipVM> HeaderChips { get; } = new();
    public string PendingText { get; private set; } = "No changes waiting";
    public string ApplyText { get; private set; } = "Review and apply";
    public bool CanApply => !Busy && Svc.Game is { Exists: true } && (Svc.PendingCount > 0);
    public string LaunchTip { get; private set; } = "";
    public bool CanLaunch => !Busy && Svc.GetLaunchPlan().CanLaunch;
    public string EmptyText { get; private set; } = "";
    public bool ShowEmpty => Rows.Count == 0;
    public bool EmptyIsNoGame => Svc.Game is null;
    public bool HasPendingChanges => Svc.PendingCount > 0;
    public string ListSummary { get; private set; } = "";
    public bool ShowTechnical { get => Svc.Settings.ShowTechnicalDetails; }
    public string ProfileHint { get; private set; } = "";

    void RaiseCommands()
    {
        foreach (var c in new[] { ApplyCommand, DiscardCommand, RefreshCommand, AddModCommand, LaunchCommand, OpenGameFolderCommand, SaveProfileCommand, ReloadProfileCommand, RenameProfileCommand, DeleteProfileCommand, UndoLastCommand })
            c.RaiseCanExecuteChanged();
        Raise(nameof(CanApply)); Raise(nameof(CanLaunch));
    }

    // ---- loading ---------------------------------------------------------------------
    public async Task StartAsync()
    {
        await ReloadAsync(verify: true);
        await Task.Run(() => _detected = Svc.Game is null ? Svc.DetectGame() : null);
        RefreshAll();
    }

    long _hashed;
    public async Task ReloadAsync(bool verify = true)
    {
        if (Busy) return;
        Busy = true; BusyText = "Reading your game folder…";
        try
        {
            await Task.Run(() => Svc.Refresh(keepPending: true));
            SyncRows();
            RefreshAll();
            if (verify && Svc.Game is not null)
            {
                _hashed = 0;
                var progress = new Progress<long>(n => { _hashed += n; BusyText = $"Checking mod files… {Fmt.Bytes(_hashed)} read"; });
                BusyText = "Checking mod files…";
                await Task.Run(() => Svc.VerifyAll(progress));
                _runtimeOff = await Task.Run(() => Svc.Entries.ToDictionary(e => e.Key, e => e.Installed is { } p ? ConfigService.RuntimeSwitchedOff(p) : new List<string>()));
            }
        }
        catch (Exception e) { Inform?.Invoke("Could not read the folder", e.Message); }
        finally { Busy = false; BusyText = ""; SyncRows(); RefreshAll(); }
    }

    void SyncRows()
    {
        var selectedKey = SelectedRow?.Key;
        var byKey = Rows.ToDictionary(r => r.Key);
        var seen = new HashSet<string>();
        foreach (var e in Svc.Entries)
        {
            seen.Add(e.Key);
            if (byKey.TryGetValue(e.Key, out var row)) row.UpdateEntry(e);
            else Rows.Add(new ModRowVM(this, e));
        }
        foreach (var gone in Rows.Where(r => !seen.Contains(r.Key)).ToList()) Rows.Remove(gone);
        View.Refresh();
        _suppressSelect = true;
        SelectedRow = Rows.FirstOrDefault(r => r.Key == selectedKey);
        _suppressSelect = false;
        Raise(nameof(ShowEmpty)); RaiseShown();
    }
    bool _suppressSelect;

    /// <summary>Recompute everything derived from the service state.</summary>
    public void RefreshAll()
    {
        _hints = Svc.Game is null ? new() : Svc.ConflictHints();
        foreach (var r in Rows) r.Refresh(_hints, _runtimeOff);
        // game header
        var g = Svc.Game;
        HeaderChips.Clear();
        if (g is null) { GameTitle = "No game folder selected"; GamePath = ""; }
        else
        {
            GameTitle = Path.GetFileName(g.Root.TrimEnd('\\'));
            GamePath = g.Root;
            if (!g.Exists || !g.HasGameExe) HeaderChips.Add(new("Not an All-Stars Racing folder", ChipKind.Danger, g.Issues.FirstOrDefault(i => i.Severity == Severity.Blocker)?.Message));
            else if (g.LoaderRecognized) HeaderChips.Add(new("Mod loader recognized", ChipKind.Ok, "ChaoGarage.dll and dinput8.dll match a loader build this program knows (compared by file hash, not by name)." + (g.LoaderProfile?.UserSupplied == true ? " The build is described by a profile file you supplied (data\\loader-profiles.json)." : "")));
            else if (g.LoaderPresent) HeaderChips.Add(new("Mod loader: different build", ChipKind.Warn, "This loader build is not one this program knows, so results may differ, and mods that declare loader needs cannot be confirmed."));
            else HeaderChips.Add(new("Mod loader not found", ChipKind.Warn, "Mods will not load until the loader is installed."));
            if (Svc.UnmanagedFolders is { Count: > 0 } other)
                HeaderChips.Add(new(other.Count == 1 ? "1 other folder in mods" : $"{other.Count} other folders in mods", ChipKind.Info,
                    "Not loader mods (no mod.ini), so the loader ignores them and they are not listed here. The manager does not examine, change or convert them: " + string.Join(", ", other.Take(8)) + (other.Count > 8 ? ", …" : "")));
            HeaderChips.Add(_lastProbe.State switch
            {
                GameRunState.Running => new ChipVM("Game is running", ChipKind.Warn, _lastProbe.Message),
                GameRunState.Unknown => new ChipVM("Cannot tell if the game is running", ChipKind.Warn, _lastProbe.Message),
                _ => new ChipVM("Game is not running", ChipKind.Ok)
            });
            if (FixtureMode) HeaderChips.Add(new("Test folder", ChipKind.Info, "Test-folder mode: launching the game is switched off."));
        }
        // profiles
        var names = Svc.Profiles.Profiles.Select(p => p.Name).ToList();
        _suppressProfile = true;
        ProfileNames.Clear(); ProfileNames.Add(NoProfile); foreach (var n in names) ProfileNames.Add(n);
        _selectedProfile = Svc.ActiveProfileName is { } ap && names.Contains(ap, StringComparer.OrdinalIgnoreCase) ? names.First(n => n.Equals(ap, StringComparison.OrdinalIgnoreCase)) : NoProfile;
        Raise(nameof(SelectedProfileName));
        _suppressProfile = false;
        // footer
        int pend = Svc.PendingCount;
        PendingText = pend == 0 ? (Svc.ProfileGaps.Count > 0 ? "A profile needs mods you do not have" : "No changes waiting") : pend == 1 ? "1 change waiting to be applied" : $"{pend} changes waiting to be applied";
        ApplyText = pend > 0 ? $"Review and apply ({pend})" : "Review and apply";
        var lp = Svc.GetLaunchPlan();
        LaunchTip = lp.CanLaunch ? (pend > 0 ? "Starts the game through Steam. Apply your changes first." : "Starts the game through Steam.") : lp.Reason;
        EmptyText = Svc.Game is null ? "Choose your All-Stars Racing folder to see your mods." : "No mods were found in this folder yet. Use “Add mod” to add a folder or .zip.";
        ListSummary = $"{Rows.Count(r => r.Entry.IsActive)} switched on · {Rows.Count(r => r.Entry.IsInstalled && !r.Entry.IsActive)} switched off · {Rows.Count(r => !r.Entry.IsInstalled)} in library";
        BuildBanners();
        foreach (var n in new[] { nameof(GameTitle), nameof(GamePath), nameof(PendingText), nameof(ApplyText), nameof(LaunchTip), nameof(EmptyText), nameof(ShowEmpty), nameof(ListSummary), nameof(ShowTechnical), nameof(HasPendingChanges), nameof(EmptyIsNoGame) }) Raise(n);
        RaiseCommands();
        View.Refresh();
        RaiseShown();
        BuildDetails();
    }

    bool Matches(ModRowVM r)
    {
        if (SearchText.Length > 0 && !SearchText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => r.SearchBlob.Contains(t))) return false;
        return FilterIndex switch
        {
            1 => r.Entry.IsActive,
            2 => r.Entry.IsInstalled && !r.Entry.IsActive,
            3 => !r.Entry.IsInstalled,
            4 => r.NeedsAttention,
            5 => r.HasPending,
            _ => true
        };
    }

    // ---- banners ---------------------------------------------------------------------
    void BuildBanners()
    {
        Banners.Clear();
        var g = Svc.Game;
        if (FixtureMode) Banners.Add(new BannerVM { Id = "fixture", Kind = BannerKind.Info, Text = "Test folder mode: you are working on a practice copy. Launching the game is switched off." });
        if (g is null)
        {
            var b = new BannerVM { Id = "nogame", Kind = BannerKind.Warn, Text = "Choose your All-Stars Racing folder to get started." };
            if (_detected is not null)
            {
                b.Text = $"Found a Steam install of the game at {_detected}. Use it?";
                b.ActionText = "Use this folder"; b.Action = new RelayCommand(() => { Svc.SelectGame(_detected); _ = ReloadAsync(); });
                b.SecondActionText = "Choose another…"; b.SecondAction = SettingsCommand;
            }
            else { b.ActionText = "Choose folder…"; b.Action = SettingsCommand; }
            Banners.Add(b);
            return;
        }
        if (g.PendingJournal)
            Banners.Add(new BannerVM { Id = "recover", Kind = BannerKind.Danger, Text = "A change did not finish earlier. Restore the previous state before making new changes. Nothing is deleted.", ActionText = "Restore previous state", Action = new RelayCommand(() => _ = RecoverAsync()) });
        foreach (var i in g.Issues.Where(i => i.Severity == Severity.Blocker && i.Code is "folder-link" or "mods-link" or "mods-not-folder" or "game-exe-missing" or "game-protected" or "game-missing"))
            Banners.Add(new BannerVM { Id = "game_" + i.Code, Kind = BannerKind.Danger, Text = i.Message });
        if (_lastProbe.State == GameRunState.Running) Banners.Add(new BannerVM { Id = "running", Kind = BannerKind.Warn, Text = "The game is running. Close it before applying changes; the manager never closes programs for you." });
        else if (_lastProbe.State == GameRunState.Unknown) Banners.Add(new BannerVM { Id = "unknown", Kind = BannerKind.Warn, Text = _lastProbe.Message });
        foreach (var gap in Svc.ProfileGaps)
            Banners.Add(new BannerVM { Id = "gap", Kind = BannerKind.Warn, Text = gap + ".", ActionText = "Ignore missing", Action = new RelayCommand(() => { Svc.DismissProfileGaps(); RefreshAll(); }) });
        if (Svc.PendingCount > 0 && !g.PendingJournal)
        {
            var ev = Svc.Evaluate();
            var blockers = ev.Blockers.Where(b => !b.Contains("did not finish")).ToList();
            if (blockers.Count > 0)
                Banners.Add(new BannerVM { Id = "blockers", Kind = BannerKind.Danger, Text = blockers.Count == 1 ? "1 problem with the current choice: " + Shorten(blockers[0]) : $"{blockers.Count} problems with the current choice: " + Shorten(blockers[0]) });
        }
    }

    static string Shorten(string s) => s.Length > 220 ? s[..220] + "…" : s;

    // ---- selection and toggles -------------------------------------------------------
    public void SetWanted(ModRowVM row, bool on)
    {
        if (Busy) return;
        Svc.SetWanted(row.Entry, on);
        RefreshAll();
    }

    // ---- details panel ---------------------------------------------------------------
    public ConfigEditorVM? Config { get; private set; }

    void BuildDetails()
    {
        if (_suppressSelect) return;
        var row = SelectedRow;
        if (row is null)
        {
            Details = null;
            // the list briefly reports "nothing selected" while it is rebuilt; unsaved typing must outlive that
            if (Config is not { HasUnsaved: true }) { Config = null; Raise(nameof(Config)); }
            return;
        }
        var e = row.Entry; var p = e.Package;
        var stage = StageText.Describe(p.Sidecar?.Stage);
        string where = e.IsInstalled ? $"Installed in the mods folder as “{p.FolderName}”" + (e.Library is not null ? ", and also saved in your library." : ".")
            : e.Library!.Linked ? $"Available from a linked folder: {e.Library.SourceNote}. Not installed." : "Saved in your library. Not installed.";
        var note = e.IsInstalled && e.IsActive ? ChangePlanner.DisableNote(p) : null;
        var d = new DetailVM
        {
            Title = row.Name, SubTitle = string.Join("  ·  ", new[] { row.Version.Length > 0 ? "Version " + row.Version : null, e.Id }.Where(s => s is not null)!),
            StatusShort = stage.Short, StatusLong = stage.Long, Summary = p.Sidecar?.Summary ?? "", WhereText = where,
            PersistenceNote = note is null ? "" : "If you switch this off: " + note,
            InstalledDir = e.Installed?.Dir, CanRemove = e.IsInstalled
        };
        foreach (var c in row.Chips) d.Chips.Add(c);
        foreach (var i in p.AllIssues.Where(i => i.Severity >= Severity.Warning).DistinctBy(i => i.Message))
            d.Problems.Add(new NoteVM { Title = i.Severity == Severity.Blocker ? "Problem" : "Note", Text = i.Message, Kind = i.Severity == Severity.Blocker ? ChipKind.Danger : ChipKind.Warn });
        if (p.Sidecar?.ModFolderAloneIsComplete == false && p.Sidecar.CompletenessNote is { Length: > 0 } cn)
            d.Problems.Add(new NoteVM { Title = "Needs extra files", Text = cn, Kind = ChipKind.Info });
        if (p.Sidecar?.Redistribution is { } redis && redis.StartsWith("PRIVATE", StringComparison.OrdinalIgnoreCase))
            d.Problems.Add(new NoteVM { Title = "Private package", Text = "Marked private by its author: it contains material derived from the game and must not be shared or uploaded.", Kind = ChipKind.Info });
        foreach (var r in p.Sidecar?.HumanGameplayRequirements ?? new()) d.Requirements.Add(r);
        if (p.Manifest?.State == ManifestState.DisabledAmbiguous) foreach (var a in p.Manifest.Alternates) d.ManifestChoices.Add(a);

        // compatibility
        if (Svc.Game is not null)
        {
            var wanted = Svc.Entries.Where(Svc.IsWanted).Select(Svc.CandidateFor).ToList();
            var withMe = wanted.Where(w => w.Key != e.Key).ToList();
            if (!Svc.IsWanted(e) && e.Id is not null) { withMe = withMe.Where(w => w.Id != e.Id).ToList(); withMe.Add(Svc.CandidateFor(e)); }
            else if (!withMe.Any(w => w.Key == e.Key)) withMe.Add(Svc.CandidateFor(e));
            var rep = ConflictAnalyzer.Analyze(withMe, LoaderEvidence.FromGame(Svc.Game, Svc.Cache));
            foreach (var c in rep.Conflicts.Where(c => c.Parties.Contains(e.Key)))
                d.Compat.Add(new NoteVM { Title = c.Title, Text = c.Explanation + (c.LoaderEnforced ? "" : " (declared by the mod, not enforced by the loader)"), Kind = c.Certainty == Certainty.Unknown ? ChipKind.Warn : ChipKind.Danger });
            foreach (var n in rep.Notes.Where(n => n.Parties.Contains(e.Key) && n.Title != "Startup order" && n.Code != "free-text-conflict"))      // (free-text conflicts are listed just below, as warnings)
                d.Compat.Add(new NoteVM { Title = n.Title, Text = n.Text, Kind = ChipKind.Info });
            var order = rep.Notes.FirstOrDefault(n => n.Title == "Startup order");
            if (order is not null) d.Compat.Add(new NoteVM { Title = order.Title, Text = order.Text, Kind = ChipKind.Muted });
            foreach (var f in p.Sidecar?.FreeTextConflicts ?? new()) d.Compat.Add(new NoteVM { Title = "Declared incompatibility (not checked automatically)", Text = f, Kind = ChipKind.Warn });
            if (d.Compat.Count == 0)
                d.Compat.Add(new NoteVM { Title = "No clashes found", Text = "Nothing in the current choice changes the same game data as this mod. That says nothing about how its native code behaves next to other mods' native code; the manager cannot test that.", Kind = ChipKind.Muted });
        }

        // files
        d.Facts.Add(new FactVM { Label = "Manifest", Value = p.Manifest is null ? "none" : p.Manifest.FileName + (p.Manifest.State == ManifestState.Active ? "" : "  (switched off)") });
        if (p.Plan is not null)
        {
            d.Facts.Add(new FactVM { Label = "Plan", Value = $"{p.Plan.Groups.Count} {(p.Plan.Groups.Count == 1 ? "entry" : "entries")} · " + string.Join(", ", p.Plan.Groups.GroupBy(g => g.Format).Select(g => $"{g.Count()} {FormatName(g.Key)}")) });
            foreach (var g in p.Plan.Groups.Take(60)) d.Targets.Add(new FactVM { Label = FormatName(g.Format), Value = g.LogicalDisplay + (g.Keys.Count > 0 ? $"  ({g.Keys.Count} item{(g.Keys.Count == 1 ? "" : "s")})" : "") });
        }
        if (p.RuntimeBytes is { } rb) d.Facts.Add(new FactVM { Label = "Native code", Value = $"{p.Manifest?.RuntimeRel}  ({Fmt.Bytes(rb)})  — hashed, never run by the manager" });
        d.Facts.Add(new FactVM { Label = "Files", Value = p.Integrity switch { Integrity.Verified => "All files match the hashes in the manifest and plan", Integrity.Failed => "Problem found (see above)", _ => "Not checked yet" } });
        d.Facts.Add(new FactVM { Label = "Your game files", Value = p.Fit switch { GameFit.Matches => "The plan's original-file hashes match your game files", GameFit.Mismatch => "Does not match your game files", GameFit.Unknown => "Not applicable", _ => "Not checked yet" } });
        if (ShowTechnical && p.Manifest is { } m)
        {
            d.Facts.Add(new FactVM { Label = "Raw status", Value = p.Sidecar?.Stage ?? "—" });
            d.Facts.Add(new FactVM { Label = "Plan hash", Value = m.PlanSha ?? "—" });
            d.Facts.Add(new FactVM { Label = "Runtime hash", Value = m.RuntimeSha ?? "—" });
            d.Facts.Add(new FactVM { Label = "Manifest hash", Value = m.FileSha });
            d.Facts.Add(new FactVM { Label = "Manifest encoding", Value = m.Encoding });
            d.Facts.Add(new FactVM { Label = "Folder", Value = p.Dir });
        }
        Details = d;
        // Keep the open editor while it holds unsaved edits for this same mod: a refresh (file check finishing, a toggle, the game-status poll) must not discard what the user typed.
        if (!(Config is { HasUnsaved: true } open && open.EntryKey == e.Key)) Config = new ConfigEditorVM(this, e);
        Raise(nameof(Config));
    }

    static string FormatName(string f) => f switch { "xpac" => "archive changes", "file" => "file replacement", "rtsc_merge" => "shader cache", "copy" => "copied file", _ => f };

    public void ChooseManifest(string? name)
    {
        if (SelectedRow is null || name is null) return;
        Svc.ChooseManifest(SelectedRow.Entry, name);
        RefreshAll();
    }

    // ---- actions ---------------------------------------------------------------------
    async Task ReviewAndApplyAsync()
    {
        if (Svc.Game is null) return;
        bool applied = ShowApply?.Invoke((p, ct) => Svc.Prepare(p, ct), "Apply changes") ?? false;
        await AfterChange(applied);
    }

    public async Task AfterChange(bool changed)
    {
        await ReloadAsync(verify: true);
    }

    async Task RecoverAsync()
    {
        if (Confirm?.Invoke("Restore previous state", "This undoes the unfinished change using the manager's record of what it did. Nothing is deleted; anything it cannot safely undo is left exactly as it is.") != true) return;
        Busy = true; BusyText = "Restoring…";
        ApplyResult r;
        try { r = await Task.Run(() => Svc.Recover()); }
        finally { Busy = false; BusyText = ""; }
        Inform?.Invoke(r.Success || r.RolledBack ? "Previous state restored" : "Could not finish restoring", r.Message);
        await ReloadAsync();
    }

    async Task UndoLastAsync()
    {
        if (Confirm?.Invoke("Undo the last change", "Go back to how the mods folder was before the last applied change? This only works if nothing has changed since.") != true) return;
        Busy = true; BusyText = "Undoing…";
        ApplyResult r;
        try { r = await Task.Run(() => Svc.UndoLast()); }
        finally { Busy = false; BusyText = ""; }
        Inform?.Invoke(r.Success ? "Undone" : "Could not undo", r.Message);
        await ReloadAsync();
    }

    public async Task UninstallSelectedAsync()
    {
        var row = SelectedRow; if (row is null || !row.Entry.IsInstalled) return;
        bool applied = ShowApply?.Invoke((p, ct) => Svc.PrepareUninstall(row.Entry, p, ct), "Remove from the game folder") ?? false;
        await AfterChange(applied);
    }

    public async Task DeepCheckSelectedAsync()
    {
        var row = SelectedRow; if (row is null || Busy) return;
        Busy = true; _hashed = 0;
        var progress = new Progress<long>(n => { _hashed += n; BusyText = $"Rebuilding archives to check them… {Fmt.Bytes(_hashed)}"; });
        try { await Task.Run(() => Svc.VerifyEntry(row.Entry, progress, default, deep: true, fresh: true)); }
        catch (Exception ex) { Inform?.Invoke("Check stopped", ex.Message); }
        finally { Busy = false; BusyText = ""; }
        RefreshAll();
        Inform?.Invoke("Check finished", row.Entry.Package.HasBlockers ? "Problems were found. See the details panel." : "This mod passed the loader-style check against your game files.");
    }

    public async Task PollProbeAsync()
    {
        if (Svc.Game is null || Busy) return;
        var root = Svc.Game.Root;
        var r = await Task.Run(() => Probe.Check(root));
        if (r.State != _lastProbe.State || r.Message != _lastProbe.Message) { _lastProbe = r; RefreshAll(); }
    }

    void Launch()
    {
        if (Svc.PendingCount > 0 && Confirm?.Invoke("Unapplied changes", "You have changes that have not been applied yet, so the game would start without them. Start the game anyway?") != true) return;
        try { Svc.Launch(); }
        catch (Exception e) { Inform?.Invoke("Cannot start the game", e.Message); }
    }

    void OpenGameFolder()
    {
        try { if (Svc.Game is { } g) ShellOpen.OpenFolder(g.Root); }
        catch (Exception e) { Inform?.Invoke("Cannot open the folder", e.Message); }
    }

    // ---- profiles --------------------------------------------------------------------
    void SaveProfile()
    {
        var name = Prompt?.Invoke("Save profile", "Name for this set of switched-on mods:", SelectedProfileName is { } s && s != NoProfile ? s : "");
        if (name is null) return;
        try { Svc.SaveProfile(name.Trim()); }
        catch (Exception e) { Inform?.Invoke("Cannot save the profile", e.Message); return; }
        RefreshAll();
    }

    void RenameProfile()
    {
        if (SelectedProfileName is not { } old || old == NoProfile) return;
        var name = Prompt?.Invoke("Rename profile", "New name:", old);
        if (name is null) return;
        var err = ProfileStore.ValidateName(name);
        try { if (err is not null) throw new InvalidOperationException(err); Svc.Profiles.Rename(old, name.Trim()); Svc.Settings.ActiveProfile = name.Trim(); }
        catch (Exception e) { Inform?.Invoke("Cannot rename the profile", e.Message); return; }
        RefreshAll();
    }

    void DeleteProfile()
    {
        if (SelectedProfileName is not { } name || name == NoProfile) return;
        if (Confirm?.Invoke("Delete profile", $"Delete the profile “{name}”? Your mods are not affected.") != true) return;
        Svc.Profiles.Delete(name);
        RefreshAll();
    }

    void UseProfile(string name)
    {
        try
        {
            var res = Svc.UseProfile(name);
            ProfileHint = res.Approximate.Count > 0 ? string.Join(" ", res.Approximate) : "";
        }
        catch (Exception e) { Inform?.Invoke("Cannot use the profile", e.Message); }
        RefreshAll();
    }

    public Task<bool> ImportAsync(string path) => Task.FromResult(ShowImport?.Invoke(path) ?? false);

    /// <summary>Marks the manager busy (no toggles, no background polling) while a dialog works on the game folder.</summary>
    public IDisposable Exclusive(string text)
    {
        Busy = true; BusyText = text;
        return new Scope(() => { Busy = false; BusyText = ""; });
    }

    sealed class Scope(Action done) : IDisposable { public void Dispose() => done(); }
}
