using System.Diagnostics;
using System.Text;

namespace AllStarsModManager.Tests;

public static class MiscTests
{
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    static string StubDir()
    {
        // the StubGame project is built next to the tests; copy its files to wherever a test needs a "running program"
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        var candidate = Path.Combine(Program.ModuleRoot, "src", "StubGame", "bin", "Release", "net10.0");
        if (!File.Exists(Path.Combine(candidate, "StubGame.exe"))) throw new TestFailure("StubGame is not built: " + candidate);
        return candidate;
    }

    static Process StartStub(string dir, string exeName)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(StubDir(), "StubGame.*")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        var exe = Path.Combine(dir, exeName);
        if (exeName != "StubGame.exe") File.Copy(Path.Combine(StubDir(), "StubGame.exe"), exe, true);   // the host finds StubGame.dll next to it
        var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })!;
        for (int i = 0; i < 50 && !p.HasExited; i++) { Thread.Sleep(100); try { if (p.MainModule is not null) break; } catch { } }
        return p;
    }
    public static void Register()
    {
        T.Area("guards-launch-profiles");

        T.Run("process guard: a program running from the game folder blocks changes; closing it clears the block (real probe, test-owned stub)", () =>
        {
            using var fx = new Fx("gd");
            var probe = new SystemGameProcessProbe();
            T.Eq(GameRunState.NotRunning, probe.Check(fx.Game).State, "idle folder");
            var p = StartStub(Path.Combine(fx.Game, "stubdir"), "StubGame.exe");
            try
            {
                var r = probe.Check(fx.Game);
                T.Eq(GameRunState.Running, r.State, "running from the folder: " + r.Message);
                T.Has(new[] { r.Message }, "never closes", "says the manager won't close it");
                // the engine honours it
                var eng = new Transactions.TransactionEngine(fx.Game, probe);
                var plan = new Transactions.ChangePlan { GameRoot = fx.Game, Summary = "x", Ops = { new Transactions.JournalOp { Seq = 1, Kind = Transactions.OpKind.CreateDir, Path = Path.Combine(fx.Mods, "zzz"), Description = "x" } } };
                var before = Fs.Snapshot(fx.Game, false);
                var res = eng.Apply(plan);
                T.True(res.Declined, "apply declined while the program runs");
                T.Eq("", Fs.Diff(before, Fs.Snapshot(fx.Game, false)), "nothing written");
                T.True(!p.HasExited, "the manager did not terminate it");
            }
            finally { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            T.Eq(GameRunState.NotRunning, probe.Check(fx.Game).State, "after it exits");
            fx.Cleanup();
        });

        T.Run("process guard: the same program name running from a different install does not block this folder", () =>
        {
            using var fx = new Fx("gd");
            var elsewhere = Path.Combine(fx.Root, "other-install");
            var p = StartStub(elsewhere, "Sonic & SEGA All-Stars Racing.exe");
            try
            {
                T.Eq(GameRunState.NotRunning, new SystemGameProcessProbe().Check(fx.Game).State, "another install's process is not this folder's game");
                T.Eq(GameRunState.Running, new SystemGameProcessProbe().Check(elsewhere).State, "but it blocks its own folder");
            }
            finally { try { p.Kill(); p.WaitForExit(5000); } catch { } }
            fx.Cleanup();
        });

        T.Run("launch planning: only the Steam-registered install can be launched; custom folders get an explanation; no-launch mode disables it", () =>
        {
            using var fx = new Fx("ln");
            var lib = Path.Combine(fx.Root, "SteamLib");
            var common = Path.Combine(lib, "steamapps", "common");
            Directory.CreateDirectory(common);
            var installDir = "Sonic and SEGA All Stars Racing";
            // register fx.Game as the install by moving the fixture under steamapps\common\<installdir>
            Directory.Move(fx.Game, Path.Combine(common, installDir));
            File.WriteAllText(Path.Combine(lib, "steamapps", "appmanifest_34190.acf"), "\"AppState\"\r\n{\r\n\t\"appid\"\t\t\"34190\"\r\n\t\"LastOwner\"\t\t\"76561190000000000\"\r\n\t\"name\"\t\t\"Sonic & SEGA All-Stars Racing\"\r\n\t\"installdir\"\t\t\"" + installDir + "\"\r\n}\r\n");
            var registered = Path.Combine(common, installDir);
            var steam = new SteamLocator(new[] { lib });
            T.True(steam.IsRegistered(registered), "registered install recognised");
            var g = GameFolderScanner.Scan(registered);
            var plan = LaunchPlanner.Plan(g, steam, noLaunch: false);
            T.True(plan.CanLaunch && plan.Uri == "steam://rungameid/34190", "Steam launch planned: " + plan.Reason);
            var other = Path.Combine(fx.Root, "lab-copy"); Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, GameFolderInfo.ExeName), Bytes("x"));
            var og = GameFolderScanner.Scan(other);
            var p2 = LaunchPlanner.Plan(og, steam, false);
            T.False(p2.CanLaunch, "custom folder: no silent launch of a different install");
            T.Has(new[] { p2.Reason }, "registered folder", "explains Steam starts its own install");
            T.False(LaunchPlanner.Plan(g, steam, noLaunch: true).CanLaunch, "no-launch mode");
            T.False(LaunchPlanner.Plan(null, steam, false).CanLaunch, "no folder chosen");
            // the real launcher refuses anything but its fixed address, before starting anything
            T.Throws<InvalidOperationException>(() => new SteamUriLauncher().Launch(new LaunchPlan(LaunchMode.SteamRegisteredInstall, "x", "steam://run/evil")), "tampered address");
            T.Throws<InvalidOperationException>(() => new SteamUriLauncher().Launch(new LaunchPlan(LaunchMode.Unavailable, "x", null)), "unavailable mode");
            fx.Cleanup();
        });

        T.Run("launch intent is verified against a stub; nothing in scan/preview/startup ever launches", () =>
        {
            var s = Scn.Rich("la");
            var rec = (RecordingLauncher)s.Svc.Env.Launcher;
            s.Reload(); s.Want("beta"); s.Svc.Evaluate(); s.Prepare(); s.Svc.GetLaunchPlan();
            T.Eq(0, rec.Requests.Count, "no launch from scan/preview");
            T.Throws<InvalidOperationException>(() => s.Svc.Launch(), "fixture mode refuses to launch");
            T.Eq(0, rec.Requests.Count, "still none");
            // an environment that does allow launching (stubbed): exactly one request with the fixed address
            var env = new ManagerEnvironment { Probe = s.Holder, Launcher = rec, Steam = new SteamLocator(Array.Empty<string>()), NoLaunch = false };
            var lib = Path.Combine(s.Fx.Root, "SteamLib2", "steamapps", "common"); Directory.CreateDirectory(lib);
            var dest = Path.Combine(lib, "Sonic and SEGA All Stars Racing");
            var root2 = new Fx("la2"); Directory.Move(root2.Game, dest);
            File.WriteAllText(Path.Combine(lib, "..", "appmanifest_34190.acf"), "\"AppState\"\r\n{\r\n\t\"appid\"\t\t\"34190\"\r\n\t\"installdir\"\t\t\"Sonic and SEGA All Stars Racing\"\r\n}\r\n");
            env.Steam = new SteamLocator(new[] { Path.GetFullPath(Path.Combine(lib, "..", "..")) });
            var svc = new ManagerService(new AppPaths(Path.Combine(s.Fx.Root, "data2")), env);
            svc.SelectGame(dest);
            var plan = svc.Launch();
            T.Eq(1, rec.Requests.Count, "one launch request");
            T.Eq("steam://rungameid/34190", rec.Requests[0].Uri, "fixed address");
            // game already running blocks it
            s.Holder.State = GameRunState.Running;
            T.Throws<InvalidOperationException>(() => svc.Launch(), "already running");
            T.Eq(1, rec.Requests.Count, "not repeated");
            // metadata can't influence launching: the plan is built from the app id only
            T.False(typeof(LaunchPlan).GetProperties().Any(p => p.Name.Contains("Command") || p.Name.Contains("Args")), "no command/arguments in a launch plan");
            s.Cleanup(); root2.Cleanup();
        });

        T.Run("only the two launch/open entry points may start processes (source audit)", () =>
        {
            var offenders = new List<string>();
            foreach (var dir in new[] { Path.Combine(Program.ModuleRoot, "src", "Core"), Path.Combine(Program.ModuleRoot, "src", "App") })
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
                {
                    var text = File.ReadAllText(f);
                    if ((text.Contains("Process.Start") || text.Contains("ShellExecute") || text.Contains("CreateProcess")) && !f.EndsWith("Launching.cs"))
                        offenders.Add(Path.GetFileName(f));
                }
            T.True(offenders.Count == 0, "process-starting code outside Launching.cs: " + string.Join(",", offenders));
            var launching = File.ReadAllText(Path.Combine(Program.ModuleRoot, "src", "Core", "Game", "Launching.cs"));
            T.Eq(3, System.Text.RegularExpressions.Regex.Matches(launching, @"Process\.Start").Count, "exactly: steam uri, explorer folder, open text file");
            var assemblyLoads = new[] { "Assembly.Load", "LoadLibrary", "NativeLibrary.Load", "AppDomain" };
            foreach (var dir in new[] { Path.Combine(Program.ModuleRoot, "src", "Core"), Path.Combine(Program.ModuleRoot, "src", "App") })
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
                {
                    var text = File.ReadAllText(f);
                    foreach (var a in assemblyLoads) T.False(text.Contains(a), $"{Path.GetFileName(f)} must not load code ({a})");
                }
        });

        T.Run("profiles: stored in manager storage only, survive restart, validate names, rename and delete", () =>
        {
            using var fx = new Fx("pf");
            var paths = new AppPaths(Path.Combine(fx.Root, "data"));
            var store = new ProfileStore(paths);
            store.Save(new Profile { Name = "Race night", Mods = { new ProfileEntry { Id = "a", Version = "1" } } });
            store.Save(new Profile { Name = "Solo" });
            var again = new ProfileStore(paths);
            T.Eq(2, again.Profiles.Count, "persisted");
            T.True(again.Find("race NIGHT") is not null, "case-insensitive lookup");
            again.Rename("Solo", "Single player");
            T.True(new ProfileStore(paths).Find("Single player") is not null, "renamed persisted");
            T.Throws<InvalidOperationException>(() => again.Rename("Race night", "single PLAYER"), "duplicate names refused");
            again.Delete("Single player");
            T.Eq(1, new ProfileStore(paths).Profiles.Count, "deleted");
            T.False(ProfileStore.ValidateName("x\u0007y") is null, "control chars refused");
            T.False(ProfileStore.ValidateName(new string('x', 61)) is null, "too long refused");
            T.True(ProfileStore.ValidateName("Fine name") is null, "ok");
            T.False(Directory.GetFiles(fx.Game, "*profile*", SearchOption.AllDirectories).Any(), "nothing about profiles in the game folder");
            File.WriteAllText(paths.ProfilesFile, "{ damaged");
            T.Eq(0, new ProfileStore(paths).Profiles.Count, "a damaged profile file does not crash the manager");
            fx.Cleanup();
        });

        T.Run("profile resolution: exact content, same-version-different-files (flagged), same ID other version (missing)", () =>
        {
            var s = Scn.Rich("pr");
            var a = s.E("alpha").Package.Manifest!;
            var p = new Profile
            {
                Name = "mix",
                Mods =
                {
                    new ProfileEntry { Id = "alpha", Version = "1.0.0", PlanSha256 = a.PlanSha, RuntimeSha256 = a.RuntimeSha },
                    new ProfileEntry { Id = "gamma", Name = "Gamma", Version = "1.0.0", PlanSha256 = new string('0', 64), RuntimeSha256 = new string('1', 64) },
                    new ProfileEntry { Id = "beta", Name = "Beta", Version = "9.9.9", PlanSha256 = new string('0', 64), RuntimeSha256 = new string('1', 64) },
                }
            };
            var res = ProfileStore.Resolve(p, s.Svc.Entries);
            T.True(res.Want[s.E("alpha").Key], "exact");
            T.True(res.Want[s.E("gamma").Key] && res.Approximate.Count == 1, "same version, different files is applied but flagged");
            T.False(res.Want[s.E("beta").Key], "different version is not silently substituted");
            T.Eq(1, res.Missing.Count, "reported as missing");
            T.Has(res.Missing, "only 1.0.0", "tells which version exists");
            s.Cleanup();
        });

        T.Run("settings: persisted next to the program/data folder; a corrupt file falls back to defaults; nothing is written to game folders", () =>
        {
            using var fx = new Fx("st");
            var paths = new AppPaths(Path.Combine(fx.Root, "mgr-data"));
            var s = new Settings { GameRoot = fx.Game, ShowTechnicalDetails = true }; s.LinkedFolders.Add(fx.Root);
            var gameBefore = Fs.Snapshot(fx.Game, false);
            s.Save(paths);
            var l = Settings.Load(paths);
            T.Eq(fx.Game, l.GameRoot, "game root remembered"); T.True(l.ShowTechnicalDetails, "flag"); T.Eq(1, l.LinkedFolders.Count, "linked");
            File.WriteAllText(paths.SettingsFile, "][");
            T.True(Settings.Load(paths).GameRoot is null, "corrupt -> defaults");
            T.Eq("", Fs.Diff(gameBefore, Fs.Snapshot(fx.Game, false)), "game folder untouched");
            var portable = AppPaths.Portable(Path.Combine(fx.Root, "override"));
            T.Eq(LoaderPath.Full(Path.Combine(fx.Root, "override")), portable.DataDir, "explicit data dir honoured");
            fx.Cleanup();
        });

        T.Run("a malformed or missing game folder setting never crashes a scan; it is reported as a blocker", () =>
        {
            foreach (var bad in new[] { "bad|path<>", "C:\\no\\such\\folder-" + Guid.NewGuid().ToString("N"), "", new string('x', 400) })
            {
                var g = GameFolderScanner.Scan(bad);
                T.False(g.Exists && g.HasGameExe, "not a game folder: " + bad);
                T.True(g.Issues.Any(i => i.Severity == Severity.Blocker), "a blocker is reported for '" + (bad.Length > 20 ? bad[..20] : bad) + "'");
            }
            using var fx = new Fx("bg");
            var paths = new AppPaths(Path.Combine(fx.Root, "data"));
            var svc = new ManagerService(paths, ManagerEnvironment.Fixture());
            svc.SelectGame("bad|path<>", remember: false);
            T.True(svc.Game is not null && !svc.Game.Exists, "the service keeps running with an invalid path");
            T.Eq(0, svc.Entries.Count, "no rows");
            fx.Cleanup();
        });

        T.Run("hash cache: reused only while length and time match; a damaged cache file is ignored", () =>
        {
            using var fx = new Fx("hc");
            var file = Path.Combine(fx.Root, "f.bin"); File.WriteAllBytes(file, Bytes("abc"));
            var cacheFile = Path.Combine(fx.Root, "c.json");
            var c = new HashCache(cacheFile);
            var h1 = c.Get(file);
            long reported = 0;
            c.Get(file, new Progress<long>(n => reported += n));
            c.Save();
            var c2 = new HashCache(cacheFile);
            T.Eq(h1, c2.Get(file), "persisted entry reused");
            File.WriteAllBytes(file, Bytes("abd")); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
            T.Eq(Sha.OfBytes(Bytes("abd")), c2.Get(file), "content change (same length) with new time is rehashed");
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllBytes(file, Bytes("xyz")); File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var forced = c2.Get(file, forceFresh: true);
            T.Eq(Sha.OfBytes(Bytes("xyz")), forced, "forceFresh bypasses the cache");
            File.WriteAllText(cacheFile, "garbage");
            T.Eq(Sha.OfBytes(Bytes("xyz")), new HashCache(cacheFile).Get(file), "damaged cache ignored");
            fx.Cleanup();
        });

        T.Run("typed settings only come from the mod's own description; unknown settings stay plain text", () =>
        {
            using var fx = new Fx("cs");
            var spec = PackageTests.Basic("cfg", "Cfg"); spec.ConfigFiles["config.ini"] = "[S]\r\nSpeed=5\r\nMode=fast\r\nFlag=1\r\nFree=hello\r\n";
            spec.SidecarJson = "{\"id\":\"cfg\",\"config_schema\":[{\"file\":\"config.ini\",\"section\":\"S\",\"key\":\"Speed\",\"type\":\"int\",\"min\":1,\"max\":10,\"label\":\"Speed\"},{\"file\":\"config.ini\",\"section\":\"S\",\"key\":\"Mode\",\"type\":\"enum\",\"choices\":[\"fast\",\"slow\"]},{\"file\":\"config.ini\",\"section\":\"S\",\"key\":\"Flag\",\"type\":\"bool\"}]}";
            var m = fx.WriteMod(spec);
            var pkg = PackageInspector.Inspect(m.Dir);
            var cf = ConfigService.Load(pkg, "config.ini");
            T.True(cf.Rows.Single(r => r.Key == "Speed").Schema is not null, "described setting is typed");
            T.True(cf.Rows.Single(r => r.Key == "Free").Schema is null, "undescribed setting has no invented schema");
            cf.Rows.Single(r => r.Key == "Speed").Value = "99";
            T.Throws<InvalidDataException>(() => ConfigService.Render(cf), "out of range");
            cf.Rows.Single(r => r.Key == "Speed").Value = "7";
            cf.Rows.Single(r => r.Key == "Mode").Value = "medium";
            T.Throws<InvalidDataException>(() => ConfigService.Render(cf), "not a listed choice");
            cf.Rows.Single(r => r.Key == "Mode").Value = "slow";
            cf.Rows.Single(r => r.Key == "Free").Value = "anything goes";
            var text = Encoding.UTF8.GetString(ConfigService.Render(cf));
            T.Eq("[S]\r\nSpeed=7\r\nMode=slow\r\nFlag=1\r\nFree=anything goes\r\n", text, "only edited lines changed");
            var raw = ConfigService.RenderRawText(cf, "[S]\r\nSpeed=1\r\n");
            T.Eq("[S]\r\nSpeed=1\r\n", Encoding.UTF8.GetString(raw), "raw text editing");
            T.Throws<InvalidDataException>(() => ConfigService.RenderRawText(cf, "[S]\r\nName=é\r\n"), "special characters in a file with no byte-order mark");
            fx.Cleanup();
        });

        T.Run("persistence notes: a mod's concrete saved-game statement is shown short and only where declared", () =>
        {
            var withNote = PackageTests.Basic("n1", "N1");
            using var fx = new Fx("pn");
            withNote.SidecarJson = "{\"id\":\"n1\",\"persistence\":{\"statement\":\"Disabling the runtime reverts the code patches only. The forklift record stays in the save and could become visible again if another roster mod raises the registered count past its slot. Nothing here edits saves. " + new string('x', 300) + "\"}}";
            var m = fx.WriteMod(withNote);
            var note = Transactions.ChangePlanner.DisableNote(PackageInspector.Inspect(m.Dir));
            T.True(note is not null && note.Length <= 330, "short note: " + note?.Length);
            T.Has(new[] { note! }, "stays in the save", "keeps the concrete statement");
            var none = fx.WriteMod(PackageTests.Basic("n2", "N2"));
            T.True(Transactions.ChangePlanner.DisableNote(PackageInspector.Inspect(none.Dir)) is null, "no universal warning");
            withNote.Id = "n3"; withNote.Folder = "N3"; withNote.SidecarJson = "{\"id\":\"n3\",\"persistence\":{\"statement\":\"long\",\"disable_note\":\"Explicit short note.\"}}";
            T.Eq("Explicit short note.", Transactions.ChangePlanner.DisableNote(PackageInspector.Inspect(fx.WriteMod(withNote).Dir)), "explicit note wins");
            fx.Cleanup();
        });

        T.Run("drag-and-drop input: only existing, distinct, bounded paths reach the importer (the window's Drop handler uses this)", () =>
        {
            using var fx = new Fx("dp");
            var a = Path.Combine(fx.Root, "a.zip"); File.WriteAllBytes(a, new byte[] { 1 });
            var d = Path.Combine(fx.Root, "dir"); Directory.CreateDirectory(d);
            var list = DropPaths.Normalize(new[] { a, a.ToUpperInvariant(), d, Path.Combine(fx.Root, "missing.zip"), "", "   ", "bad|path<>" });
            T.Eq(2, list.Count, "duplicates (any case), missing and malformed entries dropped: " + string.Join(",", list));
            var many = Enumerable.Range(0, 50).Select(i => { var p = Path.Combine(fx.Root, $"f{i}.zip"); File.WriteAllBytes(p, new byte[] { 1 }); return p; }).ToList();
            T.Eq(20, DropPaths.Normalize(many).Count, "bounded to 20 per drop");
            fx.Cleanup();
        });

        T.Run("adding a linked folder lists older mods without installing, enabling, or changing anything", () =>
        {
            var s = Scn.Rich("ln2");
            var old = Path.Combine(s.Fx.Root, "olds"); Directory.CreateDirectory(old);
            s.Fx.WriteMod(PackageTests.Basic("old-a", "Old A"), old);
            var b = PackageTests.Basic("old-b", "Old B"); b.ManifestFileName = "mod.ini.disabled-for-Honest-Gauges";
            s.Fx.WriteMod(b, old);
            var before = Fs.Snapshot(s.Fx.Game, false);
            s.Svc.AddLinkedFolder(old);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(s.Fx.Game, false)), "game folder unchanged");
            var oa = s.Svc.Entries.First(e => e.Id == "old-a");
            T.True(oa.InLibrary && !oa.IsInstalled && !s.Svc.IsWanted(oa), "listed as available, not selected");
            T.Eq(0, s.Svc.PendingCount, "no pending changes");
            s.Cleanup();
        });
    }
}
