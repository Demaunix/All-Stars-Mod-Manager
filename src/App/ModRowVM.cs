using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AllStarsModManager.Transactions;

namespace AllStarsModManager.App;

/// <summary>One line in the mod list.</summary>
public sealed class ModRowVM : Obs
{
    readonly MainViewModel _vm;
    public ModEntry Entry { get; private set; }

    public ModRowVM(MainViewModel vm, ModEntry entry) { _vm = vm; Entry = entry; Rebuild(); }

    public string Key => Entry.Key;
    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "";
    public bool HasVersion => Version.Length > 0;
    public string SubLine { get; private set; } = "";
    public ObservableCollection<ChipVM> Chips { get; } = new();
    public string PendingText { get; private set; } = "";
    public bool HasPending => PendingText.Length > 0;
    public string SortKey { get; private set; } = "";
    public string SearchBlob { get; private set; } = "";
    public bool NeedsAttention { get; private set; }
    public string AutomationId { get; private set; } = "";
    public string CheckAutomationId => "Check_" + AutomationId;
    public string CheckTip { get; private set; } = "";

    // ---- table columns (the dense list shows state and file check in their own columns; everything else is a flag) ----
    public string IdText { get; private set; } = "";
    public string NameTip { get; private set; } = "";
    /// <summary>"On", "Off" or "Library" (not installed): drives the row's state icon and dimming.</summary>
    public string StateKey { get; private set; } = "Off";
    public string StateText { get; private set; } = "";
    public string StateTip { get; private set; } = "";
    public string CheckText { get; private set; } = "";
    public string CheckFull { get; private set; } = "";
    public string CheckTip2 { get; private set; } = "";
    public ChipKind CheckKind { get; private set; } = ChipKind.Muted;
    /// <summary>Chips that are not already shown in the state or file-check columns.</summary>
    public ObservableCollection<ChipVM> Flags { get; } = new();
    public bool HasFlagsLine => Flags.Count > 0 || HasPending;

    public bool IsChecked
    {
        get => _vm.Svc.IsWanted(Entry);
        set => _vm.SetWanted(this, value);
    }

    public bool CanToggle => !_vm.Busy && _vm.Svc.Game is { Exists: true } && Entry.Package.HasManifest && !Entry.Package.IsResearchCard && Entry.Package.Manifest?.Id is not null;

    static string Slug(string s) => Regex.Replace(s, "[^A-Za-z0-9]+", "_").Trim('_');

    void Rebuild()
    {
        var p = Entry.Package;
        Name = p.DisplayName;
        Version = p.Version ?? "";
        AutomationId = Slug((Entry.Id ?? p.FolderName) + "_" + (Entry.IsInstalled ? "inst" : "lib") + (Entry.IsInstalled || Entry.Version is null ? "" : "_" + Entry.Version));
        var sub = new List<string>();
        if (Entry.Id is { } id && !string.Equals(id, Name, StringComparison.OrdinalIgnoreCase)) sub.Add(id);
        if (Entry.IsInstalled) sub.Add("folder “" + p.FolderName + "”");
        else if (Entry.Library is { } l) sub.Add(l.Linked ? "linked folder" : "in your library");
        if (p.Sidecar?.Summary is { Length: > 0 } sum) sub.Add(sum.Length > 90 ? sum[..90] + "…" : sum);
        SubLine = string.Join("  ·  ", sub);
        IdText = Entry.Id ?? "";
        NameTip = Name + (SubLine.Length > 0 ? "\n" + SubLine : "");
        SortKey = (Entry.IsInstalled ? (Entry.IsActive ? "0" : "1") : "2") + Name.ToLowerInvariant();
        SearchBlob = (Name + " " + Entry.Id + " " + Version + " " + p.FolderName + " " + p.Sidecar?.Summary).ToLowerInvariant();
        RaiseAll();
    }

    /// <summary>Recomputes chips and pending text from the current service state.</summary>
    public void Refresh(Dictionary<string, List<string>> hints, Dictionary<string, List<string>> runtimeOff)
    {
        var svc = _vm.Svc;
        var p = Entry.Package;
        Chips.Clear();
        NeedsAttention = false;
        bool wanted = svc.IsWanted(Entry);

        if (Entry.IsInstalled) Chips.Add(Entry.IsActive ? new("Switched on", ChipKind.Ok, "The game's loader will read this mod.") : new("Switched off", ChipKind.Muted, "Installed in the mods folder but not loaded."));
        else Chips.Add(new(Entry.Library!.Linked ? "Linked folder · not installed" : "In library · not installed", ChipKind.Info, "Not in the game folder yet. Switch it on to install it."));

        if (p.IsResearchCard) Chips.Add(new("Research note", ChipKind.Muted, "Not an installable mod."));
        else if (!p.HasManifest) { Chips.Add(new("No manifest", ChipKind.Muted, "The loader ignores folders without a mod.ini.")); }

        // integrity
        var blockers = p.AllIssues.Where(i => i.Severity == Severity.Blocker).ToList();
        if (p.HasManifest && !p.IsResearchCard)
        {
            if (p.Integrity == Integrity.Verified && p.Fit != GameFit.Mismatch && blockers.Count == 0) Chips.Add(new("Files verified", ChipKind.Ok, "Every file matches the hashes its manifest and plan promise."));
            else if (p.Integrity == Integrity.Failed || blockers.Count > 0) { Chips.Add(new("Problem found", ChipKind.Danger, blockers.FirstOrDefault()?.Message ?? "Some files do not match.")); NeedsAttention = true; }
            else if (p.Plan is not null) Chips.Add(new("Checking files…", ChipKind.Muted, "Hashing the mod's files."));
        }
        if (p.Fit == GameFit.Mismatch && !blockers.Any(b => b.Code is "base-mismatch")) { Chips.Add(new("Not for your game files", ChipKind.Danger)); NeedsAttention = true; }
        else if (p.Fit == GameFit.Mismatch) { /* already covered by the problem chip */ }

        if (hints.TryGetValue(Key, out var names) && names.Count > 0)
        {
            var list = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
            if (wanted) { Chips.Add(new("Conflicts with " + list, ChipKind.Danger, "The loader refuses this combination. Open the Compatibility tab for details.")); NeedsAttention = true; }
            else Chips.Add(new("Would conflict with " + list, ChipKind.Warn, "Switching this on would clash with a mod that is switched on."));
        }
        if (runtimeOff.TryGetValue(Key, out var off) && off.Count > 0)
            Chips.Add(new("Switched off in its own settings", ChipKind.Warn, "Its settings file says Enabled=0 (" + string.Join(", ", off) + "). That is separate from switching the mod on here."));
        var stage = StageText.Describe(p.Sidecar?.Stage);
        if (stage.Short.Length > 0) Chips.Add(new(stage.Short, stage.GameTested ? ChipKind.Ok : ChipKind.Info, stage.Long));
        if (p.Sidecar is { } sc && sc.LooseAssets.Count > 0 && p.Sidecar.ModFolderAloneIsComplete == false)
            Chips.Add(new("Needs extra files", ChipKind.Info, "Also adds " + sc.LooseAssets.Count + " file(s) outside its folder."));
        if (Entry.IsInstalled && Entry.Library is not null) Chips.Add(new("Also in library", ChipKind.Muted));
        if (Entry.Id is not null)
        {
            var others = _vm.Svc.Entries.Where(o => o != Entry && o.Id == Entry.Id).Select(o => o.Package.Version).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
            if (others.Count > 0) Chips.Add(new("Other version: " + string.Join(", ", others.Take(2)), ChipKind.Muted, "Only one version of a mod can be switched on."));
        }
        if (p.Manifest?.State == ManifestState.DisabledAmbiguous && !_vm.Svc.ManifestChoice.ContainsKey(Key)) Chips.Add(new("Choose a manifest", ChipKind.Warn, "Several switched-off manifests exist; pick one in the details."));

        // pending
        if (wanted == Entry.IsActive) PendingText = "";
        else if (wanted) PendingText = Entry.IsInstalled ? "Will switch on" : "Will install and switch on";
        else PendingText = "Will switch off";
        CheckTip = !CanToggle
            ? (p.IsResearchCard ? "Research notes cannot be switched on." : !p.HasManifest ? "This folder has no manifest, so it cannot be switched on." : "Unavailable right now.")
            : (wanted ? "Switch off" : "Switch on");
        BuildColumns();
        Raise(nameof(PendingText)); Raise(nameof(HasPending)); Raise(nameof(IsChecked)); Raise(nameof(CanToggle)); Raise(nameof(CheckTip)); Raise(nameof(NeedsAttention));
    }

    /// <summary>Splits the chips into the state column, the file-check column and the remaining flags. Presentation only: the chips themselves are unchanged.</summary>
    void BuildColumns()
    {
        var stateChip = Chips[0];
        StateKey = Entry.IsInstalled ? (Entry.IsActive ? "On" : "Off") : "Library";
        StateText = Entry.IsInstalled ? (Entry.IsActive ? "Switched on" : "Switched off") : "Not installed";
        StateTip = stateChip.Tip ?? stateChip.Text;
        var checkChip = Chips.FirstOrDefault(c => c.Text is "Files verified" or "Problem found" or "Checking files…" or "Research note" or "No manifest");
        CheckFull = checkChip?.Text ?? "";
        CheckKind = checkChip?.Kind ?? ChipKind.Muted;
        CheckText = CheckFull switch { "Files verified" => "Verified", "Problem found" => "Problem", "Checking files…" => "Checking", "Research note" => "Research", _ => CheckFull };
        CheckTip2 = checkChip?.Tip ?? "";
        Flags.Clear();
        foreach (var c in Chips) if (!ReferenceEquals(c, stateChip) && !ReferenceEquals(c, checkChip)) Flags.Add(c);
        Raise(nameof(StateKey)); Raise(nameof(StateText)); Raise(nameof(StateTip)); Raise(nameof(CheckText)); Raise(nameof(CheckFull)); Raise(nameof(CheckKind)); Raise(nameof(CheckTip2)); Raise(nameof(HasFlagsLine));
    }

    public void UpdateEntry(ModEntry entry) { Entry = entry; Rebuild(); }
}
