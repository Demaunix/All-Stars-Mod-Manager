using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AllStarsModManager.App;

/// <summary>Edits the mod's own settings files in place. Nothing is assumed about what a setting means.</summary>
public sealed class ConfigEditorVM : Obs
{
    readonly MainViewModel _vm;
    readonly ModEntry _entry;
    ConfigFile? _file;

    public ConfigEditorVM(MainViewModel vm, ModEntry entry)
    {
        _vm = vm; _entry = entry;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(null), () => HasUnsaved && !_vm.Busy);
        ResetCommand = new RelayCommand(() => LoadFile(SelectedFile), () => HasUnsaved);
        EditTextCommand = new RelayCommand(EditText, () => _file is not null && !_vm.Busy);
        OpenInEditorCommand = new RelayCommand(OpenInEditor, () => _file is not null);
        Message = "";
        if (!entry.IsInstalled) { Message = "Install this mod (switch it on and apply) to change its settings. Library copies are never edited."; return; }
        foreach (var f in entry.Installed!.ConfigFiles) Files.Add(f);
        if (Files.Count == 0) { Message = "This mod has no settings file (.ini) of its own."; return; }
        var off = ConfigService.RuntimeSwitchedOff(entry.Installed!);
        if (off.Count > 0) RuntimeNote = "The mod's own “Enabled” setting is 0 in " + string.Join(", ", off) + ". The mod can be switched on here and still be turned off by its own setting; the two are independent.";
        _selected = Files[0];
        LoadFile(_selected);
    }

    /// <summary>Which mod row this editor belongs to (the editor survives refreshes while it holds unsaved edits).</summary>
    public string EntryKey => _entry.Key;
    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<ConfigRowVM> Rows { get; } = new();
    public string Message { get; private set; }
    public bool HasMessage => Message.Length > 0;
    public string RuntimeNote { get; private set; } = "";
    public bool HasRuntimeNote => RuntimeNote.Length > 0;
    public bool Available => Files.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public string EncodingText => _file is null ? "" : "Stored as " + _file.EncodingText + ". Comments and untouched lines are kept exactly.";
    public bool HasUnsaved => Rows.Any(r => r.Changed);
    string? _selected;
    public string? SelectedFile { get => _selected; set { if (Set(ref _selected, value) && value is not null) LoadFile(value); } }
    string _error = ""; public string Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand EditTextCommand { get; }
    public RelayCommand OpenInEditorCommand { get; }

    void LoadFile(string? name)
    {
        Rows.Clear(); Error = "";
        if (name is null || _entry.Installed is null) return;
        try
        {
            _file = ConfigService.Load(_entry.Installed, name);
            foreach (var r in _file.Rows)
            {
                var vm = new ConfigRowVM(r);
                vm.PropertyChanged += (_, _) => { Raise(nameof(HasUnsaved)); SaveCommand.RaiseCanExecuteChanged(); ResetCommand.RaiseCanExecuteChanged(); };
                Rows.Add(vm);
            }
            if (Rows.Count == 0) Message = "This file has no key=value settings. You can still edit it as text.";
            else Message = "";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or PathRuleException or UnauthorizedAccessException)
        {
            _file = null; Message = "This settings file cannot be edited here: " + e.Message;
        }
        Raise(nameof(Message)); Raise(nameof(HasMessage)); Raise(nameof(HasRows)); Raise(nameof(EncodingText)); Raise(nameof(HasUnsaved));
        SaveCommand.RaiseCanExecuteChanged(); ResetCommand.RaiseCanExecuteChanged(); EditTextCommand.RaiseCanExecuteChanged(); OpenInEditorCommand.RaiseCanExecuteChanged();
    }

    async Task SaveAsync(byte[]? raw)
    {
        if (_file is null || _entry.Installed is null) return;
        byte[] bytes;
        try { bytes = raw ?? ConfigService.Render(_file); }
        catch (InvalidDataException e) { Error = e.Message; return; }
        Error = "";
        var file = _file.FileName;
        var loadedSha = Sha.OfBytes(_file.OriginalBytes);
        bool done = _vm.ShowApply?.Invoke((p, ct) => _vm.Svc.PrepareConfigSave(_entry, file, bytes, loadedSha), "Save settings") ?? false;
        // Saved: the file on disk now holds these values, so reload it. That clears the unsaved marker and makes the next save compare against the new bytes.
        // Cancelled or failed: the typed values stay where they are.
        if (done) LoadFile(SelectedFile);
        await _vm.AfterChange(done);
    }

    void EditText()
    {
        if (_file is null) return;
        var edited = _vm.EditRawText?.Invoke(_vm.SelectedRow!, _file);
        if (edited is null) return;
        try { _ = SaveAsync(ConfigService.RenderRawText(_file, edited)); }
        catch (InvalidDataException e) { Error = e.Message; }
    }

    void OpenInEditor()
    {
        if (_file is null) return;
        try { ShellOpen.OpenFile(_file.FullPath); }
        catch (Exception e) { _vm.Inform?.Invoke("Cannot open the file", e.Message); }
    }
}
