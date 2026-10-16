using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AllStarsModManager.App;

namespace AllStarsModManager.Tests;

/// <summary>
/// Behavioural check of the Manager-settings page against the REAL SettingsPage and MainViewModel (hosted in this process on a WPF
/// dispatcher, disposable fixture folders only; no native file pickers, no real game). It drives the transition review 1 found:
/// a settings change (link / unlink / technical-details switch) attempted while a reload of the game folder is in flight.
/// The reload is held "in flight" deterministically: ReloadAsync sets Busy synchronously and its completion can only run when this
/// thread pumps the dispatcher, so Busy stays true for the whole attempt. Attempts are made through the page's own Click/Checked
/// handlers (bypassing the disabled-control UI), which is what a reentrant or racing caller would do.
/// The same test file can be built against the preserved pre-fix source (review/run-prefix-regression.ps1) to show it fails there.
/// </summary>
public static class SettingsInProcTests
{
    public static void Register()
    {
        T.Area("settings-inproc");
        T.Run("settings page (in process, real page and view model): changes attempted during a reload are refused and leave folders, game root and verification state intact; controls recover; the next change and a failed change behave", () => RunOnSta(Body));
    }

    // ---- infrastructure --------------------------------------------------------------------------
    static void RunOnSta(Action<Application> body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? err = null;
        bool entered = false, completed = false, startupRaised = false;
        var t = new Thread(() =>
        {
            // A test-only Application that loads the SHIPPED theme dictionaries (the same Theme\Controls.xaml the program merges) but whose startup does nothing.
            // Why not the production App class: an Application whose dispatcher is pumped raises Startup even though Run() is never called (independent WPF
            // control, coordinator note; observed here too), and App.OnStartup would then parse THIS process's command line ("settings-inproc") as an unknown
            // option, show a notice and shut down. No production startup, command-line handling or main window runs in this host; startupRaised below only
            // records that the test host's own (empty) startup was the one that ran.
            var app = new TestHostApp(() => startupRaised = true);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AllStarsModManager;component/Theme/Controls.xaml", UriKind.Absolute) });
            var d = Dispatcher.CurrentDispatcher;
            // the body is dispatched into a normal Dispatcher loop (after any startup operation), so the pump frames used below are nested frames, as in the real program
            d.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                entered = true;
                try { body(app); completed = true; } catch (Exception e) { err = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
                finally { d.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        err?.Throw();
        if (!entered) throw new TestFailure("the test body never started: the dispatcher ended before running it");
        if (!completed) throw new TestFailure("the test body did not run to its end");
        if (!startupRaised) Console.WriteLine("        NOTE: the host's startup did not run before the body (harmless)");
    }

    /// <summary>Test host application: shipped theme, no production startup (see RunOnSta).</summary>
    sealed class TestHostApp : Application
    {
        readonly Action _started;
        public TestHostApp(Action started) { _started = started; }
        protected override void OnStartup(StartupEventArgs e) { _started(); }   // deliberately does not call base or parse the command line
    }
    static void DoEvents()
    {
        var f = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
        Dispatcher.PushFrame(f);
    }

    static void Pump(Func<bool> until, double seconds, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!until()) { if (sw.Elapsed.TotalSeconds > seconds) throw new TestFailure("timed out waiting for: " + what); DoEvents(); Thread.Sleep(10); }
    }

    static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Visuals(c)) yield return d;
        }
    }

    static TElement ById<TElement>(DependencyObject root, string id) where TElement : FrameworkElement
        => Visuals(root).OfType<TElement>().FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id) ?? throw new TestFailure($"control '{id}' not found");

    static List<Button> LinkButtons(DependencyObject root) => Visuals(root).OfType<Button>().Where(b => b.Content as string == "Link").ToList();

    static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));   // runs the page's Click handler even if the button is disabled

    // ---- the test --------------------------------------------------------------------------------
    static void Body(Application app)
    {
        var u = UiFixture.Build("uiinproc");
        var fx = u.Scn.Fx;
        // an older mod in a dot-folder inside the practice game folder: the page offers it as a "found next to your game" suggestion
        var old = PackageTests.Basic("old-timer", "Old Timer", "0.9.0"); old.Groups.Clear();
        old.Groups.Add(GroupSpec.File("Scripts/FrontEndScripts.sbn", System.Text.Encoding.ASCII.GetBytes("old timer script")));
        fx.WriteMod(old, Path.Combine(fx.Game, ".oldmods"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var informs = new List<string>();
        int dialogsClosed = 0;
        var closer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        closer.Tick += (_, _) => { foreach (var w in app.Windows.OfType<MessageDialog>().ToList()) { dialogsClosed++; w.DialogResult = true; } };
        closer.Start();
        MainViewModel? vmRef = null;
        try
        {
            var svc = u.Scn.Svc;
            var vm = new MainViewModel(svc, fixtureMode: true) { Inform = (t, m) => informs.Add(t + ": " + m), Confirm = (t, m) => true };
            vmRef = vm;
            var page = new SettingsPage();
            var win = new ThemedWindow { Width = 900, Height = 760, Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, DataContext = vm, Content = page };
            win.Show();
            var start = vm.StartAsync();
            Pump(() => start.IsCompleted && !vm.Busy, 90, "startup read and verification");
            Pump(() => LinkButtons(page).Count > 0, 10, "the suggestion for the older-mods folder");

            string[] folderControls = { "BrowseGameButton", "DetectGameButton", "AddLinkedButton", "RemoveLinkedButton", "TechnicalCheck" };
            // Every phase runs even if an earlier one found a problem, and all problems are reported together.
            var problems = new List<string>();
            void Phase(string name, Action a) { try { a(); } catch (TestFailure f) { problems.Add($"[{name}] {f.Message}"); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException) { problems.Add($"[{name}] {e.GetType().Name}: {e.Message}"); } }
            void AssertControls(bool enabled, string when)
            {
                foreach (var id in folderControls) T.Eq(enabled, ById<Control>(page, id).IsEnabled, $"{when}: {id} IsEnabled");
                foreach (var l in LinkButtons(page)) T.Eq(enabled, l.IsEnabled, $"{when}: a suggestion's Link button IsEnabled");
            }
            string RootNow() => svc.Game?.Root ?? "";
            Dictionary<string, Integrity> Integrities() => svc.Entries.ToDictionary(e => e.Key, e => e.Package.Integrity);
            string Folders() => string.Join("|", svc.Settings.LinkedFolders);
            string IntegrityText(Dictionary<string, Integrity> d) => string.Join(",", d.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value));
            // A controlled worker barrier: the reload's background work needs the hash cache's lock (VerifyAll saves through it), so while this thread
            // holds that lock the reload is genuinely in flight (its worker is held), however fast the machine is. Releasing it lets the reload finish.
            var gate = typeof(HashCache).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(svc.Cache) ?? throw new TestFailure("the hash cache lock (the barrier) was not found");
            Task StartHeldReload() { Monitor.Enter(gate); var r = vm.ReloadAsync(); return r; }
            void ReleaseAndFinish(Task r, string what) { Monitor.Exit(gate); Pump(() => r.IsCompleted && !vm.Busy, 90, what); }
            void RefreshPage() => page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, page));   // the page re-reads the settings and suggestions when it is (re)loaded
            var linkedBefore = svc.Settings.LinkedFolders.ToList();
            var root0 = RootNow(); var integ0 = Integrities();
            T.Eq(0, linkedBefore.Count, "no linked folder at the start");
            T.True(integ0.Values.All(i => i != Integrity.NotChecked) && integ0.Values.Contains(Integrity.Failed) && integ0.Values.Contains(Integrity.Verified), "the fixture's verification state is complete (verified and one failed package) before the test");
            AssertControls(true, "idle");

            // ---- A. changes attempted while a reload is in flight (its worker is held at the barrier) -------------
            var reload = StartHeldReload();
            try
            {
                T.True(vm.Busy && !reload.IsCompleted, "the reload is in flight: Busy is set and its worker is held at the barrier");
                Phase("A1 controls during the reload", () => AssertControls(false, "during the reload"));
                Click(LinkButtons(page)[0]);                                         // reentrant "link this folder"
                ById<CheckBox>(page, "TechnicalCheck").IsChecked = true;             // reentrant "show technical details"
                Phase("A2 refusals", () =>
                {
                    T.Eq(string.Join("|", linkedBefore), Folders(), "no folder was linked while the reload was in flight");
                    T.False(svc.Settings.ShowTechnicalDetails, "the technical-details setting was not changed while the reload was in flight");
                    T.Eq(false, ById<CheckBox>(page, "TechnicalCheck").IsChecked, "the technical-details box shows the unchanged setting after the refused switch (UI and setting agree)");
                    T.Eq(root0, RootNow(), "the game root is unchanged while the reload is in flight");
                    T.True(vm.Busy && !reload.IsCompleted, "still in flight: the refused attempts did not end or restart the reload");
                });
            }
            finally { ReleaseAndFinish(reload, "the reload to finish"); }
            Phase("A3 state after the reload", () =>
            {
                AssertControls(true, "after the reload");
                T.Eq(string.Join("|", linkedBefore), Folders(), "folder set intact after the reload");
                T.Eq(root0, RootNow(), "game root intact after the reload");
                T.Eq(IntegrityText(integ0), IntegrityText(Integrities()), "verification state is the same complete state after the reload");
                T.Eq(0, informs.Count, "no error was reported");
            });
            // Return to a known state whatever happened above (a pre-fix page may have accepted the change), and make the page re-read it,
            // so the later phases test the later behaviour rather than cascading from phase A.
            if (svc.Settings.LinkedFolders.Count > 0) { foreach (var f0 in svc.Settings.LinkedFolders.ToList()) svc.RemoveLinkedFolder(f0); var cleanup = vm.ReloadAsync(); Pump(() => cleanup.IsCompleted && !vm.Busy, 90, "state clean-up"); }
            svc.Settings.ShowTechnicalDetails = false; ById<CheckBox>(page, "TechnicalCheck").IsChecked = false; informs.Clear();
            RefreshPage();

            // ---- B. the next change works (healthy operation after the refusal) ------------------------------
            Phase("B healthy change", () =>
            {
                Pump(() => LinkButtons(page).Count > 0, 10, "the suggestion");
                ById<CheckBox>(page, "TechnicalCheck").IsChecked = true;
                T.True(svc.Settings.ShowTechnicalDetails, "technical details can be switched on when idle");
                ById<CheckBox>(page, "TechnicalCheck").IsChecked = false;
                Click(LinkButtons(page)[0]);
                Pump(() => !vm.Busy && svc.Settings.LinkedFolders.Count == 1, 90, "the link and its reload");
                var oldEntry = svc.Entries.FirstOrDefault(e => e.Id == "old-timer") ?? throw new TestFailure("the older mod is listed after linking");
                T.Eq(Integrity.Verified, oldEntry.Package.Integrity, "the newly linked mod was verified by the reload that followed the change");
                T.True(oldEntry.Library is { Linked: true }, "listed as coming from a linked folder");
                AssertControls(true, "after the healthy link");
                T.Eq(1, ById<ListBox>(page, "LinkedFolders").Items.Count, "the page lists the linked folder");
            });

            // ---- C. an unlink attempted during a reload is refused; a later unlink works ---------------------
            Phase("C unlink during a reload", () =>
            {
                ById<ListBox>(page, "LinkedFolders").SelectedIndex = 0;
                var linked1 = svc.Settings.LinkedFolders.ToList();
                var reload2 = StartHeldReload();
                bool removedWhileBusy;
                try
                {
                    T.True(vm.Busy && !reload2.IsCompleted, "in flight again (worker held)");
                    Click(ById<Button>(page, "RemoveLinkedButton"));
                    removedWhileBusy = Folders() != string.Join("|", linked1);
                }
                finally { ReleaseAndFinish(reload2, "the second reload"); }
                T.False(removedWhileBusy, "the linked folder was not removed while the reload was in flight");
                T.Eq(Integrity.Verified, svc.Entries.First(e => e.Id == "old-timer").Package.Integrity, "the linked mod is still verified after the refused unlink");
            });
            Phase("C healthy unlink", () =>
            {
                Pump(() => !vm.Busy, 90, "idle");
                if (svc.Settings.LinkedFolders.Count == 0) throw new TestFailure("nothing left to unlink");
                ById<ListBox>(page, "LinkedFolders").SelectedIndex = 0;
                Click(ById<Button>(page, "RemoveLinkedButton"));
                Pump(() => !vm.Busy && svc.Settings.LinkedFolders.Count == 0, 90, "the unlink and its reload");
                T.False(svc.Entries.Any(e => e.Id == "old-timer"), "the unlinked mod is gone from the list");
                AssertControls(true, "after the healthy unlink");
            });

            // ---- D. failure boundary 1: a change that fails (folder does not exist) -------------------------
            Phase("D failed change", () =>
            {
                Pump(() => LinkButtons(page).Count > 0, 10, "the suggestion again");
                dialogsClosed = 0;
                // deliberately stale verification state: only a reload that re-verifies can bring it back, so this proves the recovery reload actually ran
                svc.Entries.First(e => e.Id == "alpha").Package.Integrity = Integrity.NotChecked;
                LinkButtons(page)[0].Tag = Path.Combine(fx.Root, "no-such-folder");
                Click(LinkButtons(page)[0]);
                Pump(() => !vm.Busy && dialogsClosed >= 1, 60, "the failure notice and the recovery reload");
                T.Eq(0, svc.Settings.LinkedFolders.Count, "the failed link left the folder set unchanged");
                T.True(dialogsClosed >= 1, "the user was told (themed notice shown)");
                T.Eq(Integrity.Verified, svc.Entries.First(e => e.Id == "alpha").Package.Integrity, "the recovery reload ran and re-verified (the stale state is gone)");
                AssertControls(true, "after the failed change");
                Pump(() => LinkButtons(page).Count > 0, 10, "the suggestion once more");
                Click(LinkButtons(page)[0]);
                Pump(() => !vm.Busy && svc.Settings.LinkedFolders.Count == 1, 90, "a healthy link after the failure");
                T.Eq(Integrity.Verified, svc.Entries.First(e => e.Id == "old-timer").Package.Integrity, "verified again");
            });

            // ---- E1. an unreadable game root: the scanner turns it into a blocker, which must be visible, then recover ----
            Phase("E1 unreadable game root", () =>
            {
                var goodRoot = svc.Settings.GameRoot;
                svc.Settings.GameRoot = "Z:\\bad|path<>";
                var r1 = vm.ReloadAsync();
                Pump(() => r1.IsCompleted && !vm.Busy, 60, "the reload with the bad root to end");
                T.True(svc.Game is not null && svc.Game.Issues.Any(i => i.Code == "game-missing"), "the bad root is reported as a game-missing blocker (the change really took effect)");
                T.True(vm.Banners.Any(b => b.Id == "game_game-missing"), "the manager shows the problem to the user");
                AssertControls(true, "with the bad root");
                svc.Settings.GameRoot = goodRoot;
                var r2 = vm.ReloadAsync();
                Pump(() => r2.IsCompleted && !vm.Busy, 90, "the recovery reload");
                T.Eq(root0, RootNow(), "the game root is back and readable");
                T.False(vm.Banners.Any(b => b.Id == "game_game-missing"), "the problem banner is gone");
                T.True(svc.Entries.Any(e => e.Id == "alpha"), "the normal mods are listed again");
                T.Eq(Integrity.Verified, svc.Entries.First(e => e.Id == "old-timer").Package.Integrity, "the linked mod is verified after recovery");
            });

            // ---- E2. a reload that really throws (a library entry file is locked): Busy and the controls recover, the user is told ----
            Phase("E2 reload exception", () =>
            {
                var entryFile = Directory.EnumerateFiles(svc.Paths.LibraryDir, "entry.json", SearchOption.AllDirectories).FirstOrDefault() ?? throw new TestFailure("the fixture has no library entry to lock");
                informs.Clear();
                Task failing;
                using (var locked = new FileStream(entryFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    failing = vm.ReloadAsync();
                    Pump(() => failing.IsCompleted && !vm.Busy, 60, "the failing reload to end");
                }
                T.True(informs.Any(s => s.StartsWith("Could not read the folder")), "the failed reload was reported to the user: " + string.Join(" | ", informs));
                T.True(!vm.Busy, "Busy is cleared although the reload threw");
                AssertControls(true, "after the throwing reload");
                // the failed reload left the old rows in place; plant stale state on them so only a SUCCESSFUL recovery (which replaces the rows and re-verifies) can clear it
                informs.Clear();
                svc.Entries.First(e => e.Id == "alpha").Package.Integrity = Integrity.NotChecked;
                var again = vm.ReloadAsync();
                Pump(() => again.IsCompleted && !vm.Busy, 90, "the reload with the file released");
                T.Eq(0, informs.Count, "the recovery reload reported no error: " + string.Join(" | ", informs));
                T.Eq(Integrity.Verified, svc.Entries.First(e => e.Id == "alpha").Package.Integrity, "a normal reload works again: it replaced the rows and re-verified (the stale state is gone)");
                T.True(svc.Entries.Any(e => e.Id == "old-timer"), "the linked mod is listed again");
            });
            closer.Stop();
            win.Close();
            if (problems.Count > 0) throw new TestFailure(problems.Count + " problem(s): " + string.Join("  ||  ", problems));
        }
        finally
        {
            closer.Stop();
            // let any outstanding reload finish before the fixture folders are removed
            if (vmRef is { Busy: true }) { try { Pump(() => !vmRef.Busy, 30, "the manager to become idle before clean-up"); } catch (TestFailure) { } }
            u.Scn.Cleanup();
        }
    }
}
