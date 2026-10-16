using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AllStarsModManager.App;

public partial class MainWindow : ThemedWindow
{
    readonly MainViewModel _vm;
    readonly StartupOptions _opts;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };

    public MainWindow(MainViewModel vm, StartupOptions opts)
    {
        InitializeComponent();
        _vm = vm; _opts = opts;
        DataContext = vm;
        // Never open (or insist on a minimum) larger than the usable screen: at high Windows scaling the screen holds fewer device-independent pixels.
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
        double w = Width, h = Height;
        if (vm.Svc.Settings.WindowWidth is >= 880 and < 4000 && vm.Svc.Settings.WindowHeight is >= 480 and < 3000)
        { w = vm.Svc.Settings.WindowWidth!.Value; h = vm.Svc.Settings.WindowHeight!.Value; }
        Width = Math.Min(w, area.Width); Height = Math.Min(h, area.Height);

        vm.Prompt = (title, msg, initial) => PromptWindow.Ask(this, title, msg, initial);
        vm.Confirm = (title, msg) => MessageDialog.Ask(this, title, msg);
        vm.Inform = (title, msg) => MessageDialog.Tell(this, title, msg);
        vm.ShowApply = (prepare, title) =>
        {
            var w = new ApplyWindow(vm, prepare, title) { Owner = this };
            w.ShowDialog();
            return w.Applied;
        };
        vm.ShowImport = path =>
        {
            var w = new ImportWindow(vm, path) { Owner = this };
            w.ShowDialog();
            return w.Added;
        };
        vm.ShowSettings = () => vm.SelectedPage = 1;
        vm.EditRawText = (row, cf) => TextEditWindow.Edit(this, "Edit " + cf.FileName,
            "You are editing the raw text of the mod's own settings file. Special characters are only allowed if the file already has a byte-order mark. You will review the change before it is saved, and the old file is kept.", ConfigText(cf));
        vm.AddModRequested += ShowAddMenu;
        // Switching a mod rebuilds the rows; when that was done from the keyboard, put keyboard focus back on the same row (or its box).
        ModList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is CheckBox { DataContext: ModRowVM r } && InputManager.Current.MostRecentInputDevice is KeyboardDevice) RestoreFocus(r, onBox: true);
        }));

        Loaded += async (_, _) =>
        {
            _timer.Tick += async (_, _) => await vm.PollProbeAsync();
            _timer.Start();
            await vm.StartAsync();
            await vm.PollProbeAsync();
            if (opts.CaptureDir is { } capDir) StartCaptureWatcher(capDir);
            foreach (var p in opts.AddPaths) await AddPathAsync(p);
            if (opts.ScreenshotPath is { } shot)
            {
                await Task.Delay(400);
                Screenshots.Save(this, shot);
                Close();
            }
        };
        Closing += OnClosing;
        DragEnter += (_, e) => { Trace("drag entered");  if (e.Data.GetDataPresent(DataFormats.FileDrop) && !vm.Busy) { DropOverlay.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Copy; } else e.Effects = DragDropEffects.None; e.Handled = true; };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !vm.Busy ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += async (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            Trace("drop received");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                foreach (var p in DropPaths.Normalize(paths)) await AddPathAsync(p);
        };
    }

    /// <summary>Testing aid: when &lt;dir&gt;\capture-request.txt appears, saves pictures of this program's own windows and removes the request.
    /// The request text is a file-name tag, optionally followed by "@" and a scale in percent ("shot@150" renders at 150% pixel density).</summary>
    void StartCaptureWatcher(string dir)
    {
        Directory.CreateDirectory(dir);
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        watcher.Tick += (_, _) =>
        {
            var req = Path.Combine(dir, "capture-request.txt");
            if (!File.Exists(req)) return;
            string name; double scale = 1.0;
            try
            {
                var text = File.ReadAllText(req).Trim(); File.Delete(req);
                var parts = text.Split('@', 2);
                name = new string(parts[0].Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                if (parts.Length == 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct is >= 100 and <= 400) scale = pct / 100.0;
            }
            catch (IOException) { return; }
            if (name.Length == 0) name = "capture";
            try { Screenshots.SaveAll(dir, name, scale); File.WriteAllText(Path.Combine(dir, name + ".done"), "ok"); }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, name + ".done"), "failed: " + e.Message); }
        };
        watcher.Start();
    }

    void Trace(string what)
    {
        try { File.AppendAllText(Path.Combine(_vm.Svc.Paths.DataDir, "manager.log"), $"{DateTime.UtcNow:o}  window: {what}\n"); } catch (IOException) { }
    }

    static string ConfigText(ConfigFile cf) => ConfigService.RawText(cf);

    void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        try
        {
            if (WindowState == WindowState.Normal && !_opts.IsTestRun)
            {
                _vm.Svc.Settings.WindowWidth = Width; _vm.Svc.Settings.WindowHeight = Height;
                _vm.Svc.PersistSettings();
            }
        }
        catch (IOException) { }
    }

    async Task AddPathAsync(string path)
    {
        if (!await _vm.ImportAsync(path)) return;
        await _vm.ReloadAsync();
    }

    // ---- keyboard ---------------------------------------------------------------------
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && _vm.RefreshCommand.CanExecute(null)) { _vm.RefreshCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.SelectedPage = 0;
            // the Mods page may have to be put back into the window first, so focus the box once layout has run
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { SearchBox.Focus(); SearchBox.SelectAll(); });
            e.Handled = true;
        }
    }

    /// <summary>Space on a focused row switches that mod on or off (same as clicking its box), like the other mod managers.</summary>
    void ModList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.OriginalSource is ListBoxItem && _vm.SelectedRow is { CanToggle: true } row)
        {
            row.IsChecked = !row.IsChecked;
            RestoreFocus(row, onBox: false);
            e.Handled = true;
        }
    }

    void RestoreFocus(ModRowVM row, bool onBox)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ModList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
            if (onBox && FindBox(item) is { } box) box.Focus(); else item.Focus();
        });
    }

    static CheckBox? FindBox(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is CheckBox cb) return cb;
            if (FindBox(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>A narrow table drops the Mod ID column so names keep room.</summary>
    void ModList_SizeChanged(object sender, SizeChangedEventArgs e) => _vm.ShowIdColumn = e.NewSize.Width >= 640;

    // ---- menus ---------------------------------------------------------------------
    void ShowAddMenu()
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var folder = new MenuItem { Header = "Add a folder…" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(folder, "MenuAddFolder");
        folder.Click += async (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a mod folder (or a folder of mods)" };
            if (dlg.ShowDialog(this) == true) await AddPathAsync(dlg.FolderName);
        };
        var zip = new MenuItem { Header = "Add a .zip file…" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(zip, "MenuAddZip");
        zip.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Choose a mod .zip", Filter = "Zip archives (*.zip)|*.zip|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) await AddPathAsync(dlg.FileName);
        };
        menu.Items.Add(folder); menu.Items.Add(zip);
        menu.IsOpen = true;
    }

    void ProfileMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProfileMoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, System.Windows.Input.ICommand cmd, string id)
        {
            var mi = new MenuItem { Header = header, Command = cmd };
            System.Windows.Automation.AutomationProperties.SetAutomationId(mi, id);
            return mi;
        }
        menu.Items.Add(Item("Reload this profile", _vm.ReloadProfileCommand, "MenuReloadProfile"));
        menu.Items.Add(Item("Rename…", _vm.RenameProfileCommand, "MenuRenameProfile"));
        menu.Items.Add(Item("Delete…", _vm.DeleteProfileCommand, "MenuDeleteProfile"));
        menu.IsOpen = true;
    }

    void ManifestChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is string s) _vm.ChooseManifest(s);
    }

    void OpenModFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (_vm.Details?.InstalledDir is { } d) ShellOpen.OpenFolder(d); }
        catch (Exception ex) { _vm.Inform?.Invoke("Cannot open the folder", ex.Message); }
    }

    async void DeepCheck_Click(object sender, RoutedEventArgs e) => await _vm.DeepCheckSelectedAsync();

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var row = _vm.SelectedRow; if (row is null) return;
        var note = Transactions.ChangePlanner.DisableNote(row.Entry.Package);
        var msg = $"Move “{row.Name}” out of the game's mods folder?\n\nIt is not deleted: the folder goes to the manager's backup area and can be put back with “Undo last change”." + (note is null ? "" : "\n\n" + note);
        if (!(_vm.Confirm?.Invoke("Remove from game folder", msg) ?? false)) return;
        await _vm.UninstallSelectedAsync();
    }
}
