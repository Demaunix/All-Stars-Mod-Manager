using System.Collections.ObjectModel;
using System.Windows;

namespace AllStarsModManager.App;

public sealed class ImportItemVM : Obs
{
    public ImportMod Mod { get; }
    public ImportItemVM(ImportMod m, int index)
    {
        Mod = m;
        var p = m.Package;
        Name = p.DisplayName; Version = p.Version ?? "";
        CheckId = "ImportCheck_" + index;
        var parts = new List<string>();
        if (p.Id is { } id) parts.Add(id);
        if (p.Sidecar?.Summary is { Length: > 0 } s) parts.Add(s.Length > 120 ? s[..120] + "…" : s);
        Line = string.Join("  ·  ", parts);
        var blockers = p.AllIssues.Where(i => i.Severity == Severity.Blocker).ToList();
        if (!p.HasManifest || p.Id is null) Chips.Add(new("Cannot be added", ChipKind.Danger));
        else if (blockers.Count == 0 && p.Integrity == Integrity.Verified) Chips.Add(new("Files verified", ChipKind.Ok, "Every file matches the hashes its manifest and plan promise."));
        else Chips.Add(new("Problem found", ChipKind.Danger, blockers.FirstOrDefault()?.Message));
        if (m.AlreadyInLibrary) Chips.Add(new("Already in your library", ChipKind.Muted));
        var stage = StageText.Describe(p.Sidecar?.Stage);
        if (stage.Short.Length > 0) Chips.Add(new(stage.Short, ChipKind.Info, stage.Long));
        if (p.Sidecar is { LooseAssets.Count: > 0 } sc) Chips.Add(new($"Includes {sc.LooseAssets.Count} extra file(s)", ChipKind.Info, "Files that must be placed outside the mod's folder."));
        if (p.Plan is { } plan) Chips.Add(new($"{plan.Groups.Count} change{(plan.Groups.Count == 1 ? "" : "s")} to game data", ChipKind.Muted));
        foreach (var line in m.NoteLines()) Notes.Add(line);
        Selected = m.Selected;
    }
    public string Name { get; }
    public string Version { get; }
    public string Line { get; }
    public string CheckId { get; }
    public ObservableCollection<ChipVM> Chips { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    bool _sel; public bool Selected { get => _sel; set { if (Set(ref _sel, value)) Mod.Selected = value; } }
    public bool CanSelect => Mod.CanAdd;
}

public partial class ImportWindow : ThemedWindow
{
    readonly MainViewModel _vm;
    readonly string _path;
    readonly CancellationTokenSource _cts = new();
    ImportPreview? _pv;
    bool _added;

    public bool Added { get; private set; }

    public ImportWindow(MainViewModel vm, string path)
    {
        InitializeComponent();
        _vm = vm; _path = path;
        SourceText.Text = path;
        Loaded += async (_, _) => await InspectAsync();
        Closing += (_, _) => { _cts.Cancel(); if (_pv is not null && !_added) _vm.Svc.Importer.Discard(_pv); };
    }

    async Task InspectAsync()
    {
        long bytes = 0;
        StatusText.Text = "Reading it. Nothing is installed or run; files are only read and checked.";
        var progress = new Progress<long>(n => { bytes += n; StatusText.Text = $"Reading it… {Fmt.Bytes(bytes)} unpacked. Nothing is installed or run; files are only read and checked."; });
        try { _pv = await Task.Run(() => _vm.Svc.Importer.Inspect(_path, _vm.Svc.Library, progress, _cts.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Bar.Visibility = Visibility.Collapsed; StatusText.Text = "Could not read it: " + e.Message; return; }
        Bar.Visibility = Visibility.Collapsed;
        ProblemList.ItemsSource = _pv.Issues.Select(i => new NoteVM { Title = "", Text = i.Message, Kind = i.Severity == Severity.Blocker ? ChipKind.Danger : ChipKind.Warn }).ToList();
        ModList.ItemsSource = _pv.Mods.Select((m, i) => new ImportItemVM(m, i)).ToList();
        int n = _pv.Mods.Count;
        StatusText.Text = n == 0 ? "Nothing could be added from this." : n == 1 ? "Found 1 mod. Check its details, then add it to your library. Adding does not change the game folder." : $"Found {n} mods. Choose which to add to your library. Adding does not change the game folder.";
        AddButton.IsEnabled = _pv.Usable && _pv.Mods.Any(m => m.Selected && m.CanAdd);
        foreach (var vm in (IEnumerable<ImportItemVM>)ModList.ItemsSource) vm.PropertyChanged += (_, _) => AddButton.IsEnabled = _pv.Mods.Any(m => m.Selected && m.CanAdd);
    }

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_pv is null) return;
        AddButton.IsEnabled = false; Bar.Visibility = Visibility.Visible;
        StatusText.Text = "Copying into your library…";
        try
        {
            using (_vm.Exclusive("Adding to library…"))
                await Task.Run(() => _vm.Svc.Importer.AddToLibrary(_pv, new Progress<string>(s => { }), _cts.Token));
            _added = true; Added = true;
            Close();
        }
        catch (Exception ex)
        {
            Bar.Visibility = Visibility.Collapsed;
            StatusText.Text = "Could not add it: " + ex.Message + " Nothing was changed in the game folder.";
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
