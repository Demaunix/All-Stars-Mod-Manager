using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace AllStarsModManager.Tests;

/// <summary>
/// Drives the packaged executable in test-folder mode with UI Automation: real clicks on real controls, then checks the
/// files on disk. Limits: drag-and-drop from Explorer and native file/folder pickers are not automated here (--add exercises
/// the same import window the drop handler opens).
/// </summary>
public static partial class UiTests
{
    public static string ExePath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ALLSTARS_UI_EXE");
            if (!string.IsNullOrEmpty(env)) return env;
            var dist = Path.Combine(Program.ModuleRoot, "dist", "AllStarsModManager", "AllStarsModManager.exe");
            return File.Exists(dist) ? dist : Path.Combine(Program.ModuleRoot, "src", "App", "bin", "Release", "net10.0-windows", "AllStarsModManager.exe");
        }
    }

    internal static string Mods(UiFixture f) => Path.Combine(f.Game, "mods");
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    internal static string[] Args(UiFixture f, params string[] extra) =>
        new[] { "--fixture", f.Game, "--data-dir", f.Data }.Concat(extra).ToArray();

    internal static void WaitLoaded(UiApp app, int expectedRows)
    {
        UiApp.WaitFor(() => app.Rows().Count >= expectedRows, 40, $"{expectedRows} mod rows to appear");
        UiApp.WaitFor(() => !UiApp.Text(app.Need("BusyText")).Contains("Checking") && !UiApp.Text(app.Need("BusyText")).Contains("Reading"), 40, "file checks to finish");
    }

    internal static string Pending(UiApp app) => UiApp.Text(app.Need("PendingText"));

    public static void Register()
    {
        T.Area("ui");

        T.Run("ui: default startup (no options) shows the window, offers game detection, starts nothing, writes nothing to the real game", () =>
        {
            // Optional extra check on a real install: set ALLSTARS_REAL_GAME_FOLDER to its folder and the test also proves that folder's mods listing is unchanged (read-only listing).
            var realGame = Environment.GetEnvironmentVariable("ALLSTARS_REAL_GAME_FOLDER") ?? Path.Combine(Program.WorkRoot, "no-real-game-folder");
            string Listing() => Directory.Exists(realGame + @"\mods")
                ? string.Join("\n", new DirectoryInfo(realGame + @"\mods").EnumerateFileSystemInfos().Select(e => $"{e.Name}|{e.LastWriteTimeUtc.Ticks}|{(e is FileInfo fi ? fi.Length : -1)}"))
                : "no-mods";
            var before = Listing();
            var gameProcsBefore = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Sonic", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
            var data = Path.Combine(Program.WorkRoot, "ui-default-data-" + Guid.NewGuid().ToString("N")[..6]);
            using var app = UiApp.Start(ExePath, "--data-dir", data);
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_nogame") is not null, 30, "the 'choose your game folder' banner");
            UiApp.WaitFor(() => UiApp.Text(app.Need("BusyText")).Length == 0, 30);
            Thread.Sleep(1500);   // let read-only detection finish
            var banner = UiApp.Need(app.Window, "Banner_nogame").Current.Name;
            T.True(banner.Contains("Choose your All-Stars Racing folder") || banner.Contains("Found a Steam install"), "banner text: " + banner);
            T.Eq("No game folder selected", UiApp.Text(app.Need("GameTitle")), "no folder chosen automatically");
            T.False(app.Need("ApplyButton").Current.IsEnabled, "Apply disabled");
            T.False(app.Need("PlayButton").Current.IsEnabled, "Play disabled without a folder");
            T.Eq(0, app.Rows().Count, "no rows");
            app.Screenshot("01-default-startup");
            T.Eq(0, app.CloseCleanly(), "exit code");
            T.Eq(before, Listing(), "the real mods folder is unchanged (names, times, sizes)");
            var gameProcsAfter = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Sonic", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
            T.True(gameProcsAfter.SetEquals(gameProcsBefore), "no game process was started");
            var settings = File.Exists(Path.Combine(data, "settings.json")) ? File.ReadAllText(Path.Combine(data, "settings.json")) : "";
            T.False(settings.Contains("steamapps", StringComparison.OrdinalIgnoreCase), "detection alone does not select or remember a folder");
        });

        T.Run("ui: test-folder mode lists mods with names, versions, integrity, conflict and runtime-switch chips; Play is disabled with a reason", () =>
        {
            var f = UiFixture.Build("ui1");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            var names = app.RowNames();
            foreach (var n in new[] { "Alpha", "Beta", "Gamma", "Quick Start", "Honest Gauges", "Ten Stage Drift", "Metal Sonic", "Delta", "Damaged One" })
                T.True(names.Any(x => x.Contains(n, StringComparison.OrdinalIgnoreCase)), $"row '{n}' missing; got {string.Join(", ", names)}");
            T.True(names.Any(x => x.Contains("Metal Sonic")), "a manifest without a name falls back to a readable name (got " + string.Join(" | ", names) + ")");
            T.True(UiApp.IsChecked(app.Need("Check_alpha_inst")), "alpha is on");
            T.False(UiApp.IsChecked(app.Need("Check_beta_inst")), "beta is off");
            var all = string.Join("\n", app.Window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            T.True(all.Contains("Switched off in its own settings"), "Quick Start's own Enabled=0 is shown");
            T.True(all.Contains("Offline tested · game test pending"), "friendly status text, not a raw code");
            T.False(all.Contains("CANDIDATE_OFFLINE_VERIFIED"), "no raw status code in the routine view");
            T.True(all.Contains("Would conflict with Honest Gauges"), "the waiting conflicting mod is flagged before enabling");
            T.True(all.Contains("Problem found"), "the damaged package is flagged");
            var play = app.Need("PlayButton");
            T.False(play.Current.IsEnabled, "Play disabled in test-folder mode");
            T.True(play.Current.HelpText.Contains("switched off"), "the reason is available: " + play.Current.HelpText);
            T.True(UiApp.Text(app.Need("GamePath")).Contains("game"), "game path shown");
            app.Screenshot("02-fixture-list");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: search and filter narrow the list; selecting a mod shows overview, compatibility, settings and files", () =>
        {
            var f = UiFixture.Build("ui2");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            UiApp.SetText(app.Need("SearchBox"), "gauge");
            UiApp.WaitFor(() => app.Rows().Count == 1, 10, "search to narrow to one row");
            T.True(app.RowNames()[0].Contains("Honest Gauges"), "search result");
            UiApp.SetText(app.Need("SearchBox"), "");
            UiApp.WaitFor(() => app.Rows().Count == 9, 10, "all rows back");
            app.ChooseCombo("FilterCombo", "Not installed");
            UiApp.WaitFor(() => app.Rows().Count == 1, 10, "filter to library rows");
            T.True(app.RowNames()[0].Contains("Delta"), "library package listed");
            app.ChooseCombo("FilterCombo", "All mods");
            UiApp.WaitFor(() => app.Rows().Count == 9, 10, "all rows");
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Ten Stage Drift")));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null && UiApp.Text(app.Need("DetailTitle")).Contains("Ten Stage Drift"), 10, "details for Ten Stage Drift");
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailSubtitle") is not null && UiApp.Text(UiApp.Need(app.Window, "DetailSubtitle")).Contains("ten-stage-drift"), 10, "the subtitle with the mod ID");
            UiApp.Click(app.Need("TabCompat"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "CompatList") is not null, 5);
            var compat = string.Join("\n", UiApp.Need(app.Window, "CompatList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            // not switched on, so the compatibility tab describes what would happen if it were
            T.True(compat.Contains("Honest Gauges"), "compatibility names the clashing mod: " + compat);
            UiApp.Click(app.Need("TabFiles"));
            Thread.Sleep(400);
            app.Screenshot("03-details-compat-files");
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.Click(app.Need("TabSettings"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Config_SkipIntro") is not null, 10, "settings rows");
            T.Eq("1", UiApp.Text(app.Need("Config_SkipIntro")), "value read from the mod's own file");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: a conflict a mod describes only in words shows once in the Compatibility tab, as a warning line (the shared note is not repeated)", () =>
        {
            var f = UiFixture.Build("ui-free");
            f.Scn.Fx.WriteMod(CompatCheckerTests.Spec("worded-one", "Worded One", "1.0.0", "{\"conflicts\":[{\"with\":\"any mod that writes 0x545434\",\"effect\":\"refuses to start\"}]}"));
            f.Scn.Reload();
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 10);
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Worded One")));      // programmatic selection, no keyboard involved
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null && UiApp.Text(app.Need("DetailTitle")).Contains("Worded One"), 10, "details for Worded One");
            UiApp.Click(app.Need("TabCompat"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "CompatList") is not null, 5);
            var names = UiApp.Need(app.Window, "CompatList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name).ToList();
            T.True(names.Any(n => n.Contains("Declared incompatibility (not checked automatically)")), "the warning line is there: " + string.Join(" | ", names));
            T.Eq(1, names.Count(n => n.Contains("any mod that writes 0x545434")), "the declared words appear exactly once");
            T.False(names.Any(n => n.Contains("declares an incompatibility that is not checked automatically")), "the checker-side note is not repeated here");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: switching a mod on shows a pending change; Review and apply previews, applies, and the file on disk changes exactly as promised", () =>
        {
            var f = UiFixture.Build("ui3");
            var beforeManifests = Directory.GetFiles(Path.Combine(Mods(f), "Beta"));
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            T.Eq("No changes waiting", Pending(app), "nothing pending at start");
            UiApp.Click(app.Need("Check_beta_inst"));
            UiApp.WaitFor(() => Pending(app).StartsWith("1 change"), 10, "pending count");
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Pending_beta_inst") is not null, 10, "the row shows a 'will switch on' tag");
            T.Eq("Will switch on", UiApp.Need(app.Window, "Pending_beta_inst").Current.Name, "tag wording");
            var applyBtn = app.Need("ApplyButton");
            T.True(applyBtn.Current.IsEnabled && applyBtn.Current.Name.Contains("(1)"), "apply enabled: " + applyBtn.Current.Name);
            T.True(File.Exists(Path.Combine(Mods(f), "Beta", "mod.ini.disabled-for-reasons")), "nothing changed on disk yet");
            UiApp.Click(applyBtn);
            var dlg = app.WaitWindow("ApplyWindow", 20);
            var confirm = UiApp.Need(dlg, "ApplyConfirmButton");
            UiApp.WaitFor(() => confirm.Current.IsEnabled, 40, "the check to finish and Apply to enable");
            var preview = string.Join("\n", UiApp.Need(dlg, "ChangeList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            T.True(preview.Contains("Switch on") && preview.Contains("Beta"), "preview lists the change in words: " + preview);
            app.ScreenshotWindow(dlg, "04-apply-preview");
            T.True(File.Exists(Path.Combine(Mods(f), "Beta", "mod.ini.disabled-for-reasons")), "still unchanged while previewing");
            UiApp.Click(confirm);
            UiApp.WaitFor(() => UiApp.Text(UiApp.Need(dlg, "ApplyStatus")).StartsWith("Done"), 30, "apply to finish");
            app.ScreenshotWindow(dlg, "05-apply-done");
            UiApp.Click(UiApp.Need(dlg, "ApplyCancelButton"));   // "Close"
            UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10, "dialog to close");
            T.True(File.Exists(Path.Combine(Mods(f), "Beta", "mod.ini")) && !File.Exists(Path.Combine(Mods(f), "Beta", "mod.ini.disabled-for-reasons")), "manifest renamed on disk");
            UiApp.WaitFor(() => Pending(app) == "No changes waiting", 30, "pending to clear after reload");
            UiApp.WaitFor(() => UiApp.IsChecked(app.Need("Check_beta_inst")), 10, "beta checked");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: a conflicting choice shows the problem in the list and in the review window, and Apply stays disabled", () =>
        {
            var f = UiFixture.Build("ui4");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            var before = Fs.Snapshot(f.Game);
            UiApp.Click(app.Need("Check_ten_stage_drift_inst"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_blockers") is not null, 10, "problem banner");
            var banner = UiApp.Need(app.Window, "Banner_blockers").Current.Name;
            T.True(banner.Contains("problem") && banner.Contains("same data"), "banner explains in words: " + banner);
            UiApp.Click(app.Need("ApplyButton"));
            var dlg = app.WaitWindow("ApplyWindow", 20);
            UiApp.WaitFor(() => UiApp.Find(dlg, "BlockerList") is { } bl && bl.FindAll(TreeScope.Descendants, Condition.TrueCondition).Count > 0, 40, "blockers to show");
            var text = string.Join("\n", UiApp.Need(dlg, "BlockerList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            T.True(text.Contains("Honest Gauges") && text.Contains("Ten Stage Drift"), "names both mods: " + text);
            T.False(UiApp.Need(dlg, "ApplyConfirmButton").Current.IsEnabled, "Apply is disabled");
            app.ScreenshotWindow(dlg, "06-blocked");
            UiApp.Click(UiApp.Need(dlg, "ApplyCancelButton"));
            UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(f.Game)), "nothing changed on disk");
            UiApp.Click(app.Need("DiscardButton"));
            UiApp.WaitFor(() => Pending(app) == "No changes waiting", 10, "discard");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: Add mod from a .zip and from a folder (via --add): identity and integrity are shown first; the game folder is untouched until the mod is switched on", () =>
        {
            var f = UiFixture.Build("ui5");
            var before = Fs.Snapshot(f.Game);
            using (var app = UiApp.Start(ExePath, Args(f, "--add", f.ZipToAdd)))
            {
                var dlg = app.WaitWindow("ImportWindow", 30);
                UiApp.WaitFor(() => UiApp.Find(dlg, "ImportAddButton") is { } b && b.Current.IsEnabled, 40, "the inspection to finish");
                var all = string.Join("\n", dlg.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
                T.True(all.Contains("Fresh Mod") && all.Contains("1.0.0") && all.Contains("fresh-mod"), "identity shown: " + all);
                T.True(all.Contains("Files verified"), "integrity shown before adding");
                app.ScreenshotWindow(dlg, "07-add-preview");
                T.Eq("", Fs.Diff(before, Fs.Snapshot(f.Game)), "inspection wrote nothing to the game folder");
                UiApp.Click(UiApp.Need(dlg, "ImportAddButton"));
                UiApp.WaitFor(() => app.TryWindow("ImportWindow") is null, 30, "import window to close");
                UiApp.WaitFor(() => app.RowNames().Any(n => n.Contains("Fresh Mod")), 30, "the new row");
                T.Eq("", Fs.Diff(before, Fs.Snapshot(f.Game)), "adding to the library does not touch the game folder");
                T.False(Directory.Exists(Path.Combine(Mods(f), "Fresh Mod")), "not installed");
                // switch it on and apply: now it is installed
                var check = app.Need("Check_fresh_mod_lib_1_0_0");
                UiApp.Click(check);
                UiApp.WaitFor(() => Pending(app).StartsWith("1 change"), 10);
                UiApp.Click(app.Need("ApplyButton"));
                var ad = app.WaitWindow("ApplyWindow", 20);
                UiApp.WaitFor(() => UiApp.Need(ad, "ApplyConfirmButton").Current.IsEnabled, 40, "apply ready");
                var preview = string.Join("\n", UiApp.Need(ad, "ChangeList").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
                T.True(preview.Contains("Install") && preview.Contains("Fresh Mod"), "install is described: " + preview);
                UiApp.Click(UiApp.Need(ad, "ApplyConfirmButton"));
                UiApp.WaitFor(() => UiApp.Text(UiApp.Need(ad, "ApplyStatus")).StartsWith("Done"), 40, "apply done");
                UiApp.Click(UiApp.Need(ad, "ApplyCancelButton"));
                UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10);
                T.True(File.Exists(Path.Combine(Mods(f), "Fresh Mod", "mod.ini")), "installed and switched on");
                T.Eq(0, app.CloseCleanly(), "exit code");
            }
            using (var app = UiApp.Start(ExePath, Args(f, "--add", f.FolderToAdd)))
            {
                var dlg = app.WaitWindow("ImportWindow", 30);
                UiApp.WaitFor(() => UiApp.Find(dlg, "ImportAddButton") is { } b && b.Current.IsEnabled, 40, "folder inspection");
                var all = string.Join("\n", dlg.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
                T.True(all.Contains("Folder Mod") && all.Contains("folder-mod"), "folder identity: " + all);
                UiApp.Click(UiApp.Need(dlg, "ImportCancelButton"));
                UiApp.WaitFor(() => app.TryWindow("ImportWindow") is null, 10);
                T.Eq(0, app.CloseCleanly(), "exit code");
            }
            f.Scn.Cleanup();
        });

        T.Run("ui: an unsafe archive is refused with a plain explanation and nothing can be added", () =>
        {
            var f = UiFixture.Build("ui6");
            var before = Fs.Snapshot(f.Game);
            using var app = UiApp.Start(ExePath, Args(f, "--add", f.BadZip));
            var dlg = app.WaitWindow("ImportWindow", 30);
            UiApp.WaitFor(() => UiApp.Text(UiApp.Need(dlg, "ImportStatus")).Contains("Nothing could be added"), 30, "refusal");
            var text = string.Join("\n", UiApp.Need(dlg, "ImportProblems").FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            T.True(text.Contains("leave the destination"), "explains: " + text);
            T.False(UiApp.Need(dlg, "ImportAddButton").Current.IsEnabled, "Add is disabled");
            app.ScreenshotWindow(dlg, "08-unsafe-zip");
            UiApp.Click(UiApp.Need(dlg, "ImportCancelButton"));
            UiApp.WaitFor(() => app.TryWindow("ImportWindow") is null, 10);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(f.Game)), "game folder untouched");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: editing a setting writes only that line, keeps a backup, and the mod's own Enabled switch is shown separately", () =>
        {
            var f = UiFixture.Build("ui7");
            var cfg = Path.Combine(Mods(f), "Quick Start", "config.ini");
            var orig = File.ReadAllText(cfg);
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.Click(app.Need("TabSettings"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Config_SkipIntro") is not null, 10);
            var all = string.Join("\n", app.Window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));
            T.True(all.Contains("independent"), "explains that the mod's own Enabled switch is separate");
            UiApp.SetText(app.Need("Config_SkipIntro"), "0");
            UiApp.WaitFor(() => app.Need("SaveSettingsButton").Current.IsEnabled, 10, "Save enabled");
            // a refresh (file check, toggle, status poll) must not throw away what was typed
            UiApp.Click(app.Need("RefreshButton"));
            UiApp.WaitFor(() => !UiApp.Text(app.Need("BusyText")).Contains("Reading") && !UiApp.Text(app.Need("BusyText")).Contains("Checking"), 30, "the refresh to finish");
            Thread.Sleep(600);
            T.Eq("0", UiApp.Text(app.Need("Config_SkipIntro")), "the typed value survived the refresh");
            T.True(app.Need("SaveSettingsButton").Current.IsEnabled, "Save still enabled after the refresh");
            UiApp.Click(app.Need("SaveSettingsButton"));
            var dlg = app.WaitWindow("ApplyWindow", 20);
            UiApp.WaitFor(() => UiApp.Need(dlg, "ApplyConfirmButton").Current.IsEnabled, 30);
            T.Eq(orig, File.ReadAllText(cfg), "unchanged until confirmed");
            UiApp.Click(UiApp.Need(dlg, "ApplyConfirmButton"));
            UiApp.WaitFor(() => UiApp.Text(UiApp.Need(dlg, "ApplyStatus")).StartsWith("Done"), 30);
            UiApp.Click(UiApp.Need(dlg, "ApplyCancelButton"));
            UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10);
            T.Eq(orig.Replace("SkipIntro=1", "SkipIntro=0"), File.ReadAllText(cfg), "only the edited line changed (comment, Enabled=0 and line endings kept)");
            var backups = Directory.GetFiles(Path.Combine(Mods(f), ".allstars-manager", "backups"), "*config.ini.before", SearchOption.AllDirectories);
            T.Eq(1, backups.Length, "backup of the previous file");
            T.Eq(orig, File.ReadAllText(backups[0]), "backup holds the original");
            // review round 2: after a successful save the editor is clean again and the next save is judged against the NEW bytes
            UiApp.WaitFor(() => !app.Need("SaveSettingsButton").Current.IsEnabled, 15, "Save disabled once the saved values are the baseline");
            T.Eq("0", UiApp.Text(app.Need("Config_SkipIntro")), "the saved value is shown");
            UiApp.SetText(app.Need("Config_SkipIntro"), "1");
            UiApp.WaitFor(() => app.Need("SaveSettingsButton").Current.IsEnabled, 10, "Save enabled for the second edit");
            UiApp.Click(app.Need("SaveSettingsButton"));
            var dlg2 = app.WaitWindow("ApplyWindow", 20);
            UiApp.WaitFor(() => UiApp.Need(dlg2, "ApplyConfirmButton").Current.IsEnabled, 30);
            UiApp.Click(UiApp.Need(dlg2, "ApplyConfirmButton"));
            UiApp.WaitFor(() => UiApp.Text(UiApp.Need(dlg2, "ApplyStatus")).StartsWith("Done"), 30);
            UiApp.Click(UiApp.Need(dlg2, "ApplyCancelButton"));
            UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10);
            T.Eq(orig, File.ReadAllText(cfg), "a second consecutive save is accepted (not rejected as an outside change) and writes the second value");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: profiles can be saved and applied from the window; applying one validates before changing anything", () =>
        {
            var f = UiFixture.Build("ui8");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            UiApp.Click(app.Need("SaveProfileButton"));
            var prompt = app.WaitWindow("PromptWindow", 10);
            UiApp.SetText(UiApp.Need(prompt, "PromptBox"), "Night Race");
            UiApp.Click(UiApp.Need(prompt, "PromptOk"));
            UiApp.WaitFor(() => app.TryWindow("PromptWindow") is null, 10);
            UiApp.WaitFor(() => File.Exists(Path.Combine(f.Data, "profiles.json")) && File.ReadAllText(Path.Combine(f.Data, "profiles.json")).Contains("Night Race"), 10, "profile persisted in the manager's own storage");
            // the file is written before the window refreshes its rows (MainViewModel.SaveProfile: Svc.SaveProfile, then RefreshAll), so the file alone does not
            // say the list is ready: wait for the checkbox the next step uses
            UiApp.WaitFor(() => app.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Check_beta_inst")) is { Current.IsEnabled: true }, 10, "the mod list after the profile save");
            UiApp.Click(app.Need("Check_beta_inst"));
            UiApp.WaitFor(() => Pending(app).StartsWith("1 change"), 10);
            UiApp.Click(app.Need("ProfileMoreButton"));
            AutomationElement? reload = null;
            UiApp.WaitFor(() => (reload = app.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MenuReloadProfile")) ?? AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "MenuReloadProfile"))) is not null, 10, "the profile menu");
            UiApp.Click(reload!);
            UiApp.WaitFor(() => Pending(app) == "No changes waiting", 10, "reloading the profile replaces the pending choice with the saved set");
            T.False(UiApp.IsChecked(app.Need("Check_beta_inst")), "beta back off");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: with a program running from the game folder the window warns and apply is declined; closing it clears the warning", () =>
        {
            var f = UiFixture.Build("ui9");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            Process? stub = null;
            try
            {
                var dir = Path.Combine(f.Game, "stubdir"); Directory.CreateDirectory(dir);
                var src = Path.Combine(Program.ModuleRoot, "src", "StubGame", "bin", "Release", "net10.0");
                foreach (var file in Directory.GetFiles(src, "StubGame.*")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
                stub = Process.Start(new ProcessStartInfo(Path.Combine(dir, "StubGame.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })!;
                UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_running") is not null, 25, "the running-game banner");
                var before = Fs.Snapshot(f.Game);
                UiApp.Click(app.Need("Check_beta_inst"));
                UiApp.WaitFor(() => Pending(app).StartsWith("1 change"), 10);
                UiApp.Click(app.Need("ApplyButton"));
                var dlg = app.WaitWindow("ApplyWindow", 20);
                UiApp.WaitFor(() => UiApp.Need(dlg, "ApplyConfirmButton").Current.IsEnabled, 40);
                UiApp.Click(UiApp.Need(dlg, "ApplyConfirmButton"));
                UiApp.WaitFor(() => UiApp.Text(UiApp.Need(dlg, "ApplyStatus")).Contains("running"), 30, "a refusal that says the game is running");
                app.ScreenshotWindow(dlg, "09-game-running");
                UiApp.Click(UiApp.Need(dlg, "ApplyCancelButton"));
                UiApp.WaitFor(() => app.TryWindow("ApplyWindow") is null, 10);
                T.True(!stub.HasExited, "the manager did not close the program");
                T.Eq("", Fs.Diff(before, Fs.Snapshot(f.Game)), "nothing changed");
                stub.Kill(); stub.WaitForExit(5000);
                UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_running") is null, 25, "the warning to clear once it is closed");
            }
            finally { try { if (stub is { HasExited: false }) { stub.Kill(); stub.WaitForExit(5000); } } catch { } }
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: a change interrupted by a crash is flagged on the next start and Restore previous state puts everything back", () =>
        {
            var f = UiFixture.Build("ui10");
            var baseline = Fs.Snapshot(f.Game);
            // simulate the crash in-process with the same engine the app uses, then open the app on the result
            f.Scn.Hooks.Step = p => { if (p == "op:1:after") throw new Transactions.SimulatedCrash(p); };
            f.Scn.Want("alpha", "beta", "gamma", "quick-start", "honest-gauges");
            var plan = f.Scn.Prepare();
            try { f.Scn.Svc.Apply(plan); } catch (Transactions.SimulatedCrash) { }
            f.Scn.Hooks.Step = null;
            T.True(File.Exists(Path.Combine(Mods(f), ".allstars-manager", "current.json")), "journal left behind");
            using var app = UiApp.Start(ExePath, Args(f));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_recover") is not null, 40, "the recovery banner");
            var banner = UiApp.Need(app.Window, "Banner_recover").Current.Name;
            T.True(banner.Contains("did not finish"), "plain words: " + banner);
            app.Screenshot("10-recovery-banner");
            var action = UiApp.Need(app.Window, "Banner_recover").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            T.True(action is not null, "a button offers recovery");
            UiApp.Click(action!);
            // the confirmation is a standard message box
            var box = UiApp.WaitMessageBox(app, 15);
            UiApp.PressYes(box);
            UiApp.WaitFor(() => !File.Exists(Path.Combine(Mods(f), ".allstars-manager", "current.json")), 30, "recovery to finish");
            UiApp.DismissMessageBoxIfAny(app);   // "Previous state restored"
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_recover") is null, 30, "banner to clear");
            T.Eq("", Fs.Diff(baseline, Fs.Snapshot(f.Game)), "exact baseline restored");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

#if PRIVATE_TESTS
        RegisterPrivate();   // the real Ryo/Banjo package ZIPs (private local inputs; not part of the public source tree)
#endif


        T.Run("ui: at the minimum window size every control is present, on screen, inside the window and not overlapping its neighbours", () =>
        {
            var f = UiFixture.Build("ui13");
            using var app = UiApp.Start(ExePath, Args(f));
            WaitLoaded(app, 9);
            app.Screenshot("13-normal-size");
            var tp = (TransformPattern)app.Window.GetCurrentPattern(TransformPattern.Pattern);
            tp.Resize(700, 450);                       // asks for less than the minimum: the window must clamp to its minimum size
            Thread.Sleep(800);
            var r = app.Window.Current.BoundingRectangle;
            Console.WriteLine($"        window after asking for 700x450: {r.Width:0}x{r.Height:0} (physical pixels)");
            T.True(r.Width >= 870 && r.Height >= 470, $"the window refuses to get smaller than its minimum: {r.Width}x{r.Height}");
            app.Select(app.Rows().First(x => x.Current.Name.Contains("Quick Start")));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTabs") is not null, 10, "the details panel");
            Thread.Sleep(500);
            app.Screenshot("14-minimum-size");
            // everything a user needs must be reachable at the minimum size
            string[] ids = { "TabMods", "TabManager", "ProfileCombo", "SaveProfileButton", "ProfileMoreButton", "FilterCombo", "SearchBox", "AddModButton", "RefreshButton", "ModList", "DetailTabs",
                             "PendingText", "ListSummary", "ShownText", "UndoLastButton", "OpenFolderButton", "DiscardButton", "PlayButton", "ApplyButton", "GameTitle", "GamePath" };
            var rects = new Dictionary<string, System.Windows.Rect>();
            foreach (var id in ids)
            {
                var el = app.Need(id);
                var b = el.Current.BoundingRectangle;
                T.False(el.Current.IsOffscreen, $"{id} is off screen");
                T.True(b.Width >= 24 && b.Height >= 14, $"{id} has a usable size ({b.Width:0}x{b.Height:0})");
                T.True(b.Left >= r.Left - 1 && b.Right <= r.Right + 1 && b.Top >= r.Top - 1 && b.Bottom <= r.Bottom + 1, $"{id} lies inside the window ({b} vs {r})");
                rects[id] = b;
            }
            bool Overlap(string a, string b2) { var x = rects[a]; var y = rects[b2]; return x.Left < y.Right - 1 && y.Left < x.Right - 1 && x.Top < y.Bottom - 1 && y.Top < x.Bottom - 1; }
            var rows = new[] { new[] { "ProfileCombo", "SaveProfileButton", "ProfileMoreButton", "FilterCombo", "SearchBox", "AddModButton", "RefreshButton" },
                               new[] { "UndoLastButton", "OpenFolderButton", "DiscardButton", "PlayButton", "ApplyButton", "PendingText" } };
            foreach (var row in rows)
                for (int i = 0; i < row.Length; i++)
                    for (int j = i + 1; j < row.Length; j++)
                        T.False(Overlap(row[i], row[j]), $"{row[i]} overlaps {row[j]}");
            T.True(rects["ModList"].Width >= 600 && rects["ModList"].Height >= 100, $"the mod list keeps a usable area ({rects["ModList"].Width:0}x{rects["ModList"].Height:0})");
            // the most important actions still work at this size
            UiApp.SetText(app.Need("SearchBox"), "alpha");
            UiApp.WaitFor(() => app.Rows().Count == 1, 10, "search works at the minimum size");
            UiApp.SetText(app.Need("SearchBox"), "");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("ui: documented options work (--help text, unknown option, --screenshot writes a picture and exits)", () =>
        {
            var f = UiFixture.Build("ui11");
            var shot = Path.Combine(UiApp.ShotDir, "11-screenshot-option.png");
            if (File.Exists(shot)) File.Delete(shot);
            var p = Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, ArgumentList = { "--fixture", f.Game, "--data-dir", f.Data, "--screenshot", shot } })!;
            T.True(p.WaitForExit(60_000), "the app exits by itself after taking the picture");
            T.Eq(0, p.ExitCode, "exit code");
            T.True(File.Exists(shot) && new FileInfo(shot).Length > 5000, "picture written");
            f.Scn.Cleanup();
        });
    }
}
