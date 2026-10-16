using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AllStarsModManager.App;

/// <summary>The manager's own settings (game folder, linked folders, display, files, about), shown as the second page of the main window.</summary>
public partial class SettingsPage : UserControl
{
    MainViewModel? _vm;
    bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        // The tab strip creates the page lazily and can show it again later: read the current state every time it appears.
        Loaded += (_, _) => { _vm = DataContext as MainViewModel; if (_vm is not null) Fill(); };
    }

    Window? Owner => Window.GetWindow(this);

    void Fill()
    {
        if (_vm is null) return;
        _loading = true;
        var s = _vm.Svc.Settings;
        GameBox.Text = s.GameRoot ?? "";
        var g = _vm.Svc.Game;
        GameNote.Text = g is null ? "No folder chosen yet." : !g.Exists ? "That folder does not exist." : !g.HasGameExe ? "“Sonic & SEGA All-Stars Racing.exe” was not found here." : g.LoaderRecognized ? "Looks right. The mod loader is the build this manager knows." : g.LoaderPresent ? "Looks right, but the mod loader is a different build." : "Looks right, but the mod loader files are missing.";
        if (_vm.FixtureMode) GameNote.Text = "Test folder mode: this folder was chosen on the command line and is not remembered.";
        LinkedList.ItemsSource = s.LinkedFolders.ToList();
        var suggestions = g is null ? new List<string>() : LibraryScanner.SuggestFolders(g.Root).Where(x => !s.LinkedFolders.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        SuggestList.ItemsSource = suggestions;
        SuggestPanel.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TechBox.IsChecked = s.ShowTechnicalDetails;
        DataText.Text = _vm.Svc.Paths.DataDir;
        var asm = Assembly.GetEntryAssembly()!;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        AboutText.Text = $"All-Stars Mod Manager {info}\nReads and writes only the game's mods folder, Resource\\Movie for required extra files, and its own data folder. It never runs mod code, starts the game by itself, or touches saves.\nThere is no update service: new mod versions are added by hand with “Add mod…”.\nMods do not need this manager: copying a mod folder into the game's mods folder yourself works too. AllStarsModCheck.exe, in the same folder as this program, checks a game or mods folder offline without opening the manager.";
        _loading = false;
    }

    /// <summary>
    /// The game folder or the linked folders changed: show the new list and read the folder again (what closing the old, modal Settings window used to trigger).
    /// The controls that change these settings are disabled while the manager is busy and every handler re-checks it, so a change can never overlap a
    /// background read of the game folder (the reload sets Busy before this method returns to the dispatcher).
    /// </summary>
    async Task ChangedAsync()
    {
        Fill();
        if (_vm is not null) await _vm.ReloadAsync();
        Fill();
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Busy) return;
        var dlg = new OpenFolderDialog { Title = "Choose the folder that contains Sonic & SEGA All-Stars Racing.exe" };
        if (dlg.ShowDialog(Owner) != true || _vm.Busy) return;
        _vm.Svc.SelectGame(dlg.FolderName, remember: !_vm.FixtureMode);
        await ChangedAsync();
    }

    async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Busy) return;
        var found = _vm.Svc.DetectGame();
        if (found is null) { MessageDialog.Tell(Owner, "Find automatically", "No Steam install of the game was found. Use “Choose…” to pick the folder yourself."); return; }
        if (!MessageDialog.Ask(Owner, "Find automatically", $"Found the Steam install at:\n{found}\n\nUse this folder?") || _vm.Busy) return;
        _vm.Svc.SelectGame(found, remember: !_vm.FixtureMode);
        await ChangedAsync();
    }

    async void AddLinked_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Busy) return;
        var dlg = new OpenFolderDialog { Title = "Choose a folder that contains mods" };
        if (dlg.ShowDialog(Owner) != true || _vm.Busy) return;
        try { _vm.Svc.AddLinkedFolder(dlg.FolderName); } catch (Exception ex) { MessageDialog.Tell(Owner, "Link a folder", ex.Message, DialogKind.Warning); }
        await ChangedAsync();
    }

    async void RemoveLinked_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Busy) return;
        if (LinkedList.SelectedItem is string s) _vm.Svc.RemoveLinkedFolder(s);
        await ChangedAsync();
    }

    async void LinkSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Busy) return;
        if (sender is FrameworkElement { Tag: string path }) { try { _vm.Svc.AddLinkedFolder(path); } catch (Exception ex) { MessageDialog.Tell(Owner, "Link a folder", ex.Message, DialogKind.Warning); } }
        await ChangedAsync();
    }

    void Tech_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _vm is null) return;
        if (_vm.Busy)
        {
            // refused while the manager is busy: show the saved value again so the box never disagrees with the setting
            _loading = true; TechBox.IsChecked = _vm.Svc.Settings.ShowTechnicalDetails; _loading = false;
            return;
        }
        _vm.Svc.Settings.ShowTechnicalDetails = TechBox.IsChecked == true;
        _vm.Svc.PersistSettings();
        _vm.RefreshAll();
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try { ShellOpen.OpenFolder(_vm.Svc.Paths.DataDir); } catch (Exception ex) { MessageDialog.Tell(Owner, "Open folder", ex.Message, DialogKind.Warning); }
    }
}
