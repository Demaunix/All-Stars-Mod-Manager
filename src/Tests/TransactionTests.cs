using System.Text;
using AllStarsModManager.Transactions;

namespace AllStarsModManager.Tests;

public static partial class TransactionTests
{
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    static ApplyResult ApplyWant(Scn s, params string[] ids)
    {
        s.Want(ids);
        var plan = s.Prepare();
        return s.Svc.Apply(plan);
    }

    static System.Text.Json.Nodes.JsonObject FirstOp(System.Text.Json.Nodes.JsonObject journal, string kind) =>
        journal["ops"]!.AsArray().Select(o => o!.AsObject()).First(o => o["kind"]!.GetValue<string>() == kind);

    static string ManagerDir(Scn s) => TransactionStore.ManagerDir(s.Fx.Game);
    static bool JournalPending(Scn s) => File.Exists(TransactionStore.CurrentPath(s.Fx.Game));
    static string[] StageLeftovers(Scn s) => Directory.Exists(TransactionStore.StageRoot(s.Fx.Game)) ? Directory.GetDirectories(TransactionStore.StageRoot(s.Fx.Game)) : Array.Empty<string>();

    /// <summary>The rich scenario, switched to: beta on, gamma on, delta installed and on, alpha off.</summary>
    static ApplyResult Standard(Scn s) => ApplyWant(s, "beta", "gamma", "delta");

    public static void Register()
    {
        T.Area("transactions");

        T.Run("enabling a switched-off mod renames only its manifest; every other byte in the game folder is unchanged", () =>
        {
            var s = Scn.Rich();
            var before = s.Snap();
            var origManifest = File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Beta", "mod.ini.disabled-for-reasons"));
            var r = ApplyWant(s, "alpha", "beta", "gamma");
            T.True(r.Success, "apply: " + r.Message + string.Join(";", r.Details));
            var after = s.Snap();
            var diff = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).OrderBy(k => k).ToList();
            T.True(diff.SequenceEqual(new[] { @"F mods\Beta\mod.ini", @"F mods\Beta\mod.ini.disabled-for-reasons" }), "unexpected changes: " + string.Join(" | ", diff));
            T.True(File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Beta", "mod.ini")).SequenceEqual(origManifest), "manifest bytes preserved");
            T.True(s.ActiveIds().SetEquals(new[] { "alpha", "beta", "gamma" }), "active set");
            T.False(JournalPending(s), "journal archived");
            T.Eq(0, StageLeftovers(s).Length, "stage cleaned");
            s.Cleanup();
        });

        T.Run("disabling renames mod.ini to a reason-suffixed name and re-enabling restores the exact bytes; an existing disabled file is never overwritten", () =>
        {
            var s = Scn.Rich();
            var orig = File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini"));
            File.WriteAllText(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini.disabled-by-manager"), "somebody else's older file");
            s.Reload();
            var r = ApplyWant(s, "gamma");
            T.True(r.Success, r.Message);
            T.False(File.Exists(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini")), "switched off");
            T.Eq("somebody else's older file", File.ReadAllText(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini.disabled-by-manager")), "pre-existing file untouched");
            T.True(File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini.disabled-by-manager-2")).SequenceEqual(orig), "manifest kept under a free name");
            s.Reload();
            T.Eq(ManifestState.DisabledAmbiguous, s.Installed("Alpha").Installed!.Manifest!.State, "two candidates now: the user must choose");
            s.Svc.Want[s.Installed("Alpha").Key] = true;
            var plan = s.Prepare();
            T.False(plan.CanApply, "enabling is blocked until a manifest is chosen");
            T.Has(plan.Blockers, "choose which one", "explained");
            s.Svc.ChooseManifest(s.Installed("Alpha"), "mod.ini.disabled-by-manager-2");
            var plan2 = s.Prepare();
            T.True(plan2.CanApply, "after choosing: " + string.Join(";", plan2.Blockers));
            var r2 = s.Svc.Apply(plan2);
            T.True(r2.Success, r2.Message);
            T.True(File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini")).SequenceEqual(orig), "exact original bytes restored");
            T.Eq("somebody else's older file", File.ReadAllText(Path.Combine(s.Fx.Mods, "Alpha", "mod.ini.disabled-by-manager")), "still untouched");
            s.Cleanup();
        });

        T.Run("only the chosen mods are enabled; other switched-off packages are not touched", () =>
        {
            var s = Scn.Rich();
            var extra = PackageTests.Basic("zeta", "Zeta"); extra.Groups.Clear(); extra.ManifestFileName = "mod.ini.disabled-for-Honest-Gauges";
            s.Fx.WriteMod(extra);
            s.Reload();
            var r = ApplyWant(s, "alpha", "gamma", "beta");
            T.True(r.Success, r.Message);
            T.True(File.Exists(Path.Combine(s.Fx.Mods, "Zeta", "mod.ini.disabled-for-Honest-Gauges")), "zeta still switched off under its own name");
            T.False(File.Exists(Path.Combine(s.Fx.Mods, "Zeta", "mod.ini")), "not enabled");
            s.Cleanup();
        });

        T.Run("installing from the library copies the package, adds only missing required files, and records ownership", () =>
        {
            var s = Scn.Rich();
            var baseResource = Fs.Snapshot(Path.Combine(s.Fx.Game, "Resource"));
            var r = Standard(s);
            T.True(r.Success, r.Message + " " + string.Join(";", r.Details));
            T.True(Directory.Exists(Path.Combine(s.Fx.Mods, "Delta")), "installed folder");
            T.True(File.Exists(Path.Combine(s.Fx.Game, "Resource", "Movie", "D1.usm")) && File.Exists(Path.Combine(s.Fx.Game, "Resource", "Movie", "D2.usm")), "loose files placed");
            T.True(s.ActiveIds().SetEquals(new[] { "beta", "gamma", "delta" }), "active");
            var reg = AssetRegistry.Load(s.Fx.Game);
            T.Eq(2, reg.Assets.Count, "registry entries");
            T.True(reg.Assets.Values.All(a => a.CreatedByManager && a.Owners.SequenceEqual(new[] { "Delta" })), "created by the manager, owned by Delta");
            // base archives and loader files untouched
            var afterResource = Fs.Snapshot(Path.Combine(s.Fx.Game, "Resource"));
            var changed = afterResource.Keys.Where(k => !baseResource.ContainsKey(k)).ToList();
            T.True(changed.All(k => k.Contains("Movie")), "only Resource\\Movie additions: " + string.Join(",", changed));
            foreach (var f in new[] { "ChaoGarage.dll", "dinput8.dll", @"Resource\Base.xpac", @"Resource\Racers.xpac", @"Resource\Select.xpac", "ssr_save.bin", "settings.ini" })
                T.True(s.Snap().ContainsKey("F " + f), f + " present");
            s.Cleanup();
        });

        T.Run("an identical file already in the game folder is adopted and never owned or removed; a different file blocks the install", () =>
        {
            var s = Scn.Rich();
            Directory.CreateDirectory(Path.Combine(s.Fx.Game, "Resource", "Movie"));
            File.WriteAllBytes(Path.Combine(s.Fx.Game, "Resource", "Movie", "D1.usm"), Bytes("movie one data"));
            s.Reload();
            var r = Standard(s);
            T.True(r.Success, r.Message + " " + r.Technical);
            var reg = AssetRegistry.Load(s.Fx.Game);
            T.False(reg.Assets[("Resource/Movie/D1.usm")].CreatedByManager, "adopted: not owned by the manager");
            T.True(reg.Assets["Resource/Movie/D2.usm"].CreatedByManager, "other file created");
            // uninstall Delta: only the created file is taken back
            s.Reload();
            var up = s.Svc.PrepareUninstall(s.Installed("Delta"));
            T.True(up.CanApply, string.Join(";", up.Blockers));
            var ur = s.Svc.Apply(up);
            T.True(ur.Success, ur.Message);
            T.True(File.Exists(Path.Combine(s.Fx.Game, "Resource", "Movie", "D1.usm")), "adopted file stays");
            T.False(File.Exists(Path.Combine(s.Fx.Game, "Resource", "Movie", "D2.usm")), "created file taken back");
            T.True(Directory.GetFiles(TransactionStore.BackupRoot(s.Fx.Game), "D2.usm", SearchOption.AllDirectories).Length == 1, "and kept in the backup folder");
            s.Cleanup();

            var s2 = Scn.Rich();
            Directory.CreateDirectory(Path.Combine(s2.Fx.Game, "Resource", "Movie"));
            File.WriteAllBytes(Path.Combine(s2.Fx.Game, "Resource", "Movie", "D1.usm"), Bytes("a different movie"));
            s2.Reload();
            var before = s2.Snap();
            s2.Want("delta");
            var plan = s2.Prepare();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "never overwrites", "explained");
            var res = s2.Svc.Apply(plan);
            T.True(res.Declined, "declined");
            T.Eq("", Fs.Diff(before, s2.Snap()), "nothing changed");
            T.True(File.ReadAllBytes(Path.Combine(s2.Fx.Game, "Resource", "Movie", "D1.usm")).SequenceEqual(Bytes("a different movie")), "foreign file intact");
            s2.Cleanup();
        });

        T.Run("a package that is missing its required loose files is reported incomplete and cannot be enabled", () =>
        {
            var s = Scn.Rich();
            var lib = s.E("delta").Library!;
            File.Delete(Path.Combine(lib.PackageRoot, "Resource", "Movie", "D2.usm"));
            s.Reload();
            s.Want("delta");
            var plan = s.Svc.Evaluate();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "Incomplete package", "says incomplete");
            s.Cleanup();
        });

        T.Run("a profile validates the whole set first: unresolved entries block before any change", () =>
        {
            var s = Scn.Rich();
            s.Want("alpha", "gamma");
            s.Svc.SaveProfile("Daily");
            s.Svc.Profiles.Save(new Profile { Name = "Ghost", Mods = { new ProfileEntry { Id = "alpha", Version = "1.0.0", PlanSha256 = s.E("alpha").Package.Manifest!.PlanSha, RuntimeSha256 = s.E("alpha").Package.Manifest!.RuntimeSha }, new ProfileEntry { Id = "not-installed", Name = "Ghost Mod", Version = "9.9" } } });
            var before = s.Snap();
            var res = s.Svc.UseProfile("Ghost");
            T.Eq(1, res.Missing.Count, "one missing");
            var plan = s.Prepare();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "Ghost Mod", "names the missing mod");
            T.True(s.Svc.Apply(plan).Declined, "declined");
            T.Eq("", Fs.Diff(before, s.Snap()), "unchanged");
            s.Svc.DismissProfileGaps();
            var ok = s.Svc.Apply(s.Prepare());
            T.True(ok.Success, ok.Message);
            T.True(s.ActiveIds().SetEquals(new[] { "alpha" }), "after dismissing the gap only the resolvable mod is applied");
            s.Cleanup();
        });

        T.Run("profiles round-trip by content identity, not by label or folder name", () =>
        {
            var s = Scn.Rich();
            s.Want("alpha", "gamma"); s.Svc.SaveProfile("A+G");
            ApplyWant(s, "beta");
            T.True(s.ActiveIds().SetEquals(new[] { "beta" }), "switched");
            var folderRename = Path.Combine(s.Fx.Mods, "Alpha-renamed");
            Directory.Move(Path.Combine(s.Fx.Mods, "Alpha"), folderRename);   // a folder rename by the user
            s.Reload();
            var res = s.Svc.UseProfile("A+G");
            T.Eq(0, res.Missing.Count + res.Approximate.Count, "found by content");
            var r = s.Svc.Apply(s.Prepare());
            T.True(r.Success, r.Message);
            T.True(s.ActiveIds().SetEquals(new[] { "alpha", "gamma" }), "profile applied");
            T.False(ProfileStore.ValidateName("") is null, "empty name rejected");
            s.Cleanup();
        });

        T.Run("conflicting selections are refused with an explanation and the game folder is untouched", () =>
        {
            var s = Scn.Rich();
            var clash = PackageTests.Basic("clash", "Clash"); clash.Groups.Clear();
            clash.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (s.Fx.BaseKey("Base", 0), "replace", Bytes("identical to nothing"))));
            clash.ManifestFileName = "mod.ini.disabled-x";
            s.Fx.WriteMod(clash);
            s.Reload();
            var before = s.Snap();
            s.Want("alpha", "clash");
            var plan = s.Prepare();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "same data", "explains the shared member");
            var r = s.Svc.Apply(plan);
            T.True(r.Declined, "declined");
            T.Eq("", Fs.Diff(before, s.Snap()), "nothing changed");
            T.False(Directory.Exists(ManagerDir(s)), "no manager folder was even created");
            // non-archive collisions
            var f1 = PackageTests.Basic("filer", "Filer"); f1.Groups.Clear(); f1.Groups.Add(GroupSpec.File("Scripts/Other.sbn", Bytes("mine"))); f1.ManifestFileName = "mod.ini.disabled-x";
            s.Fx.WriteMod(f1);
            s.Reload();
            s.Want("gamma", "filer");
            var p2 = s.Prepare();
            T.False(p2.CanApply, "two whole-file replacements");
            T.Has(p2.Blockers, "Other.sbn", "names the file");
            s.Cleanup();
        });

        T.Run("a damaged mod that is already active blocks any change until it is switched off", () =>
        {
            var s = Scn.Rich();
            File.WriteAllBytes(Path.Combine(s.Fx.Mods, "Gamma", "runtime", "Rt.dll"), Bytes("swapped runtime of other length"));
            s.Reload();
            s.Want("alpha", "beta", "gamma");
            var plan = s.Prepare();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "Gamma", "names the damaged mod");
            T.Has(plan.Blockers, "runtime", "says why");
            // switching it off is allowed (it fixes the set)
            s.Want("alpha", "beta");
            var fix = s.Prepare();
            T.True(fix.CanApply, "switching the damaged mod off resolves it: " + string.Join(";", fix.Blockers));
            T.True(s.Svc.Apply(fix).Success, "applied");
            s.Cleanup();
        });

        T.Run("unchanged active mods are re-verified at apply time (a same-length swap after the last scan is caught)", () =>
        {
            var s = Scn.Rich();
            var p = Path.Combine(s.Fx.Mods, "Alpha", "runtime", "Rt.dll");
            var swapped = File.ReadAllBytes(p); swapped[3] ^= 0xFF;
            File.WriteAllBytes(p, swapped);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
            s.Want("alpha", "beta", "gamma");
            var plan = s.Prepare();
            T.False(plan.CanApply, "blocked");
            T.Has(plan.Blockers, "Alpha", "names alpha");
            s.Cleanup();
        });

        T.Run("the game folder changing after the preview makes apply refuse (external-change check)", () =>
        {
            var s = Scn.Rich();
            s.Want("beta", "gamma", "alpha");
            var plan = s.Prepare();
            T.True(plan.CanApply, "ready");
            File.AppendAllText(Path.Combine(s.Fx.Mods, "Gamma", "mod.ini"), "\r\n; edited elsewhere\r\n");
            var before = s.Snap();
            var r = s.Svc.Apply(plan);
            T.True(r.Declined, "declined: " + r.Message);
            T.Has(new[] { r.Message }, "changed", "says so");
            T.Eq("", Fs.Diff(before, s.Snap()), "nothing changed");
            s.Cleanup();
        });

        T.Run("a new folder or a new loose file appearing after the preview is also detected", () =>
        {
            var s = Scn.Rich();
            s.Want("delta", "gamma");
            var plan = s.Prepare();
            T.True(plan.CanApply, "ready");
            Directory.CreateDirectory(Path.Combine(s.Fx.Game, "Resource", "Movie"));
            File.WriteAllBytes(Path.Combine(s.Fx.Game, "Resource", "Movie", "D1.usm"), Bytes("appeared meanwhile"));
            var before = s.Snap();
            var r = s.Svc.Apply(plan);
            T.True(r.Declined, "declined: " + r.Message);
            T.Eq("", Fs.Diff(before, s.Snap()), "nothing changed");
            s.Cleanup();
        });

        T.Run("game running / status unknown: every mutation is declined before any file is touched", () =>
        {
            foreach (var state in new[] { GameRunState.Running, GameRunState.Unknown })
            {
                var s = Scn.Rich();
                s.Want("beta", "gamma");
                var plan = s.Prepare();
                s.Holder.State = state;
                var before = s.Snap(false);
                var r = s.Svc.Apply(plan);
                T.True(r.Declined && !r.Success, $"{state}: declined");
                T.Has(new[] { r.Message }, state == GameRunState.Running ? "running" : "cannot tell", $"{state}: message");
                T.Eq("", Fs.Diff(before, s.Snap(false)), $"{state}: no files touched (not even the manager folder)");
                var rr = s.Svc.Recover();
                T.True(rr.Declined, $"{state}: recovery declined too");
                var up = s.Svc.PrepareUninstall(s.Installed("Gamma"));
                T.True(s.Svc.Apply(up).Declined, $"{state}: uninstall declined");
                var cfg = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", Bytes("[Alpha]\r\nSpeed=9\r\n"));
                T.True(s.Svc.Apply(cfg).Declined, $"{state}: settings save declined");
                s.Holder.State = GameRunState.NotRunning;
                s.Want("beta", "gamma");
                var okRes = s.Svc.Apply(s.Prepare());
                T.True(okRes.Success, $"{state}: works once the game is closed (positive control): {okRes.Message}");
                s.Cleanup();
            }
        });

        T.Run("a second manager holding the lock makes apply decline", () =>
        {
            var s = Scn.Rich();
            s.Want("beta", "gamma");
            var plan = s.Prepare();
            Directory.CreateDirectory(ManagerDir(s));
            using (var other = new FileStream(TransactionStore.LockPath(s.Fx.Game), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var before = s.Snap();
                var r = s.Svc.Apply(plan);
                T.True(r.Declined, "declined");
                T.Has(new[] { r.Message }, "Another copy", "explains");
                T.Eq("", Fs.Diff(before, s.Snap()), "unchanged");
            }
            s.Cleanup();
        });

        T.Run("uninstall moves the folder to the backup area intact and Undo brings it back byte for byte", () =>
        {
            var s = Scn.Rich();
            var before = s.Snap();
            var up = s.Svc.PrepareUninstall(s.Installed("Alpha"));
            T.True(up.CanApply, string.Join(";", up.Blockers));
            var r = s.Svc.Apply(up);
            T.True(r.Success, r.Message);
            T.False(Directory.Exists(Path.Combine(s.Fx.Mods, "Alpha")), "removed from mods");
            var kept = Directory.GetDirectories(TransactionStore.BackupRoot(s.Fx.Game), "Alpha", SearchOption.AllDirectories);
            T.Eq(1, kept.Length, "kept in backups");
            T.True(File.Exists(Path.Combine(kept[0], "notes", "keepme.txt")), "unknown files preserved");
            var u = s.Svc.UndoLast();
            T.True(u.Success, u.Message);
            T.Eq("", Fs.Diff(before, s.Snap()), "baseline restored exactly");
            s.Cleanup();
        });

        T.Run("Undo refuses once files changed after the change (no blind overwrite)", () =>
        {
            var s = Scn.Rich();
            var r = ApplyWant(s, "alpha", "beta", "gamma");
            T.True(r.Success, r.Message);
            File.WriteAllText(Path.Combine(s.Fx.Mods, "Beta", "mod.ini.disabled-extra"), "x");
            File.Move(Path.Combine(s.Fx.Mods, "Beta", "mod.ini"), Path.Combine(s.Fx.Mods, "Beta", "mod.ini.moved-by-user"));
            var before = s.Snap();
            var u = s.Svc.UndoLast();
            T.True(u.Declined, "declined: " + u.Message);
            T.Eq("", Fs.Diff(before, s.Snap()), "nothing touched");
            s.Cleanup();
        });

        T.Run("version switch: choosing a second version of the same ID turns the first off in one transaction", () =>
        {
            var s = Scn.Rich();
            var v2 = PackageTests.Basic("gamma", "Gamma v2", "2.0.0"); v2.Groups.Clear(); v2.Groups.Add(GroupSpec.File("Scripts/Other.sbn", Bytes("gamma two")));
            v2.Folder = "Gamma v2"; v2.ManifestFileName = "mod.ini.disabled-v2";
            s.Fx.WriteMod(v2);
            s.Reload();
            var g1 = s.Svc.Entries.First(e => e.Id == "gamma" && e.Package.Version == "1.0.0");
            var g2 = s.Svc.Entries.First(e => e.Id == "gamma" && e.Package.Version == "2.0.0");
            s.Svc.SetWanted(g2, true);
            T.False(s.Svc.IsWanted(g1), "version one deselected automatically");
            var plan = s.Prepare();
            T.True(plan.CanApply, string.Join(";", plan.Blockers));
            T.True(s.Svc.Apply(plan).Success, "applied");
            T.True(File.Exists(Path.Combine(s.Fx.Mods, "Gamma v2", "mod.ini")) && !File.Exists(Path.Combine(s.Fx.Mods, "Gamma", "mod.ini")), "swapped");
            // both active at once is a loader error and is reported even if forced
            s.Reload();
            s.Svc.Want[g1.Key] = true; s.Svc.Want[g2.Key] = true;
            var forced = s.Svc.Evaluate();
            T.False(forced.CanApply, "duplicate id blocked");
            T.Has(forced.Blockers, "Two copies", "explained");
            s.Cleanup();
        });

        T.Run("a link in the mods folder, or on the way to a required file, is refused", () =>
        {
            var s = Scn.Rich();
            var outside = Path.Combine(s.Fx.Root, "outside"); Directory.CreateDirectory(outside);
            Fs.MakeJunction(Path.Combine(s.Fx.Mods, "Trap"), outside);
            s.Reload();
            s.Want("beta", "gamma");
            var plan = s.Svc.Evaluate();
            T.False(plan.CanApply, "link in mods blocks");
            T.Has(plan.Blockers, "link", "explained");
            s.Cleanup();

            var s2 = Scn.Rich();
            var outside2 = Path.Combine(s2.Fx.Root, "outside2"); Directory.CreateDirectory(outside2);
            Fs.MakeJunction(Path.Combine(s2.Fx.Game, "Resource", "Movie"), outside2);
            s2.Reload();
            s2.Want("delta");
            var p2 = s2.Prepare();
            T.False(p2.CanApply, "required file path through a junction");
            T.Has(p2.Blockers, "link", "explained");
            T.Eq(0, Directory.GetFiles(outside2).Length, "nothing written through the junction");
            s2.Cleanup();
        });

        T.Run("a junction swapped in after the preview is caught at the step (no write escapes the game folder)", () =>
        {
            var s = Scn.Rich();
            s.Want("delta");
            var plan = s.Prepare();
            T.True(plan.CanApply, "ready");
            var outside = Path.Combine(s.Fx.Root, "escape"); Directory.CreateDirectory(outside);
            s.Hooks.Step = p =>
            {
                if (p == "journal-written")
                {
                    var movie = Path.Combine(s.Fx.Game, "Resource", "Movie");
                    if (Directory.Exists(movie)) Directory.Delete(movie, true);
                    Directory.CreateDirectory(Path.Combine(s.Fx.Game, "Resource"));
                    Fs.MakeJunction(movie, outside);
                }
            };
            var before = s.Snap();
            var r = s.Svc.Apply(plan);
            s.Hooks.Step = null;
            T.False(r.Success, "must not succeed: " + r.Message);
            T.Eq(0, Directory.GetFileSystemEntries(outside).Length, "nothing escaped through the junction");
            T.True(r.RolledBack, "rolled back cleanly: " + r.Message + r.Technical);
            T.False(JournalPending(s), "no unfinished journal");
            Directory.Delete(Path.Combine(s.Fx.Game, "Resource", "Movie"));   // removes only the junction itself
            T.Eq("", Fs.Diff(before, s.Snap()), "baseline once the planted junction is removed");
            s.Cleanup();
        });

        T.Run("settings save writes one file, keeps a backup, preserves comments/encoding, and refuses when the file changed meanwhile", () =>
        {
            var s = Scn.Rich();
            var cfgPath = Path.Combine(s.Fx.Mods, "Alpha", "config.ini");
            var orig = File.ReadAllBytes(cfgPath);
            var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
            cf.Rows.First(r => r.Key == "Speed").Value = "9";
            var bytes = ConfigService.Render(cf);
            T.Eq("; alpha settings\r\n[Alpha]\r\nEnabled=1\r\nSpeed=9\r\n", Encoding.UTF8.GetString(bytes), "only the edited value changed");
            var before = s.Snap();
            var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", bytes);
            T.True(plan.CanApply, string.Join(";", plan.Blockers));
            File.AppendAllText(cfgPath, "; added by someone else\r\n");
            var declined = s.Svc.Apply(plan);
            T.True(declined.Declined, "external edit between preview and save: " + declined.Message);
            File.WriteAllBytes(cfgPath, orig);
            s.Reload();
            var plan2 = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", bytes);
            var ok = s.Svc.Apply(plan2);
            T.True(ok.Success, ok.Message);
            T.True(File.ReadAllBytes(cfgPath).SequenceEqual(bytes), "file has the new bytes");
            var backups = Directory.GetFiles(TransactionStore.BackupRoot(s.Fx.Game), "*config.ini.before", SearchOption.AllDirectories);
            T.Eq(1, backups.Length, "one backup");
            T.True(File.ReadAllBytes(backups[0]).SequenceEqual(orig), "backup holds the original bytes");
            var u = s.Svc.UndoLast();
            T.True(u.Success, u.Message);
            T.True(File.ReadAllBytes(cfgPath).SequenceEqual(orig), "undo restores the original");
            T.Throws<InvalidOperationException>(() => ConfigService.Load(s.Installed("Alpha").Installed!, "mod.ini"), "manifest is not a settings file");
            T.Throws<InvalidOperationException>(() => ConfigService.Load(s.Installed("Alpha").Installed!, @"..\Beta\config.ini"), "paths are not accepted");
            s.Cleanup();
        });

        T.Run("a mod's own Enabled=0 is shown separately and is never changed by switching the mod on or off", () =>
        {
            var s = Scn.Rich();
            var spec = PackageTests.Basic("quick", "Quick"); spec.Groups.Clear(); spec.ManifestFileName = "mod.ini.disabled-x";
            spec.ConfigFiles["config.ini"] = "[QuickStart]\r\nEnabled=0\r\n";
            s.Fx.WriteMod(spec);
            s.Reload();
            T.Has(ConfigService.RuntimeSwitchedOff(s.E("quick").Package), "config.ini [QuickStart]", "reported");
            var before = File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Quick", "config.ini"));
            var r = ApplyWant(s, "quick");
            T.True(r.Success, r.Message);
            T.True(File.ReadAllBytes(Path.Combine(s.Fx.Mods, "Quick", "config.ini")).SequenceEqual(before), "config untouched");
            T.True(s.ActiveIds().Contains("quick"), "manifest is on while the mod's own switch stays off");
            s.Cleanup();
        });

        T.Run("scan, evaluate and prepare never write anything, and never call the launcher", () =>
        {
            var s = Scn.Rich();
            var rec = (RecordingLauncher)s.Svc.Env.Launcher;
            var before = Fs.Snapshot(s.Fx.Game, false);
            s.Reload();
            s.Want("beta", "gamma", "delta");
            s.Svc.Evaluate(); s.Prepare(); s.Svc.GetLaunchPlan();
            T.Eq("", Fs.Diff(before, Fs.Snapshot(s.Fx.Game, false)), "no writes");
            T.Eq(0, rec.Requests.Count, "launcher untouched");
            s.Cleanup();
        });

        // ------------------------------------------------------------------------------
        T.Area("transaction-faults");

        T.Run("failure or crash at EVERY step leaves the exact baseline; a retry then succeeds", () =>
        {
            // dry run to list the points
            var points = new List<string>();
            SortedDictionary<string, string> appliedFinal;
            {
                var s = Scn.Rich("fi");
                s.Hooks.Step = p => points.Add(p);
                var r = Standard(s);
                T.True(r.Success, "dry run: " + r.Message + " " + string.Join(";", r.Details));
                appliedFinal = s.Snap();
                s.Cleanup();
            }
            T.True(points.Count >= 20, $"expected a rich set of points, got {points.Count}: {string.Join(",", points)}");
            int checkedFail = 0, checkedCrash = 0, journalLive = 0;
            for (int k = 0; k < points.Count; k++)
            {
                foreach (var crash in new[] { false, true })
                {
                    var s = Scn.Rich("fi");
                    var baseline = s.Snap();
                    int n = 0; var firedAt = "";
                    s.Hooks.Step = p => { if (n++ == k) { firedAt = p; throw crash ? new SimulatedCrash(p) : new InjectedFailure(p); } };
                    ApplyResult? r = null; bool crashed = false;
                    s.Want("beta", "gamma", "delta");
                    var plan = s.Prepare();
                    try { r = s.Svc.Apply(plan); } catch (SimulatedCrash) { crashed = true; }
                    T.Eq(points[k], firedAt, $"hook #{k} fired where the dry run said");
                    s.Hooks.Step = null;
                    bool afterCommit = firedAt == "commit-saved";
                    if (crash)
                    {
                        T.True(crashed, $"#{k} {firedAt}: crash propagated");
                        if (JournalPending(s)) journalLive++;
                        // a fresh manager sees the unfinished change and restores it
                        s.Svc.Refresh(false);
                        if (JournalPending(s)) T.True(s.Svc.Game!.PendingJournal && s.Svc.Evaluate().Blockers.Any(b => b.Contains("did not finish")), $"#{k} {firedAt}: pending change is flagged");
                        var rec = s.Svc.Recover();
                        T.True(rec.Success || rec.Declined == false, $"#{k} {firedAt}: recover: {rec.Message}");
                        T.False(rec.NeedsAttention, $"#{k} {firedAt}: recovery complete");
                        checkedCrash++;
                    }
                    else
                    {
                        T.False(crashed, $"#{k} {firedAt}: no crash expected");
                        T.False(r!.Success, $"#{k} {firedAt}: must not report success");
                        T.False(r.NeedsAttention, $"#{k} {firedAt}: clean rollback expected: {r.Message}");
                        checkedFail++;
                    }
                    var now = s.Snap();
                    if (crash && afterCommit) T.Eq("", Fs.Diff(appliedFinal, now), $"#{k} {firedAt}: committed change survives crash recovery");
                    else T.Eq("", Fs.Diff(baseline, now), $"#{k} {firedAt}{(crash ? " (crash+recover)" : "")}: baseline bytes differ");
                    T.False(JournalPending(s), $"#{k} {firedAt}: no unfinished journal remains");
                    T.Eq(0, StageLeftovers(s).Length, $"#{k} {firedAt}: no staged copies remain");
                    // retry works (not needed after a committed crash)
                    if (!(crash && afterCommit))
                    {
                        s.Reload();
                        var r2 = ApplyWant(s, "beta", "gamma", "delta");
                        T.True(r2.Success, $"#{k} {firedAt}: retry: {r2.Message}");
                        T.Eq("", Fs.Diff(appliedFinal, s.Snap()), $"#{k} {firedAt}: retry result equals the clean apply");
                    }
                    s.Cleanup();
                }
            }
            Console.WriteLine($"        ({points.Count} points x2: {checkedFail} failures rolled back, {checkedCrash} crashes recovered, {journalLive} with a live journal)");
        });

        T.Run("a second fault DURING rollback leaves a recoverable journal; recovery then restores the exact baseline", () =>
        {
            // find the forward points in a middle-of-transaction failure and the undo points it triggers
            var undoPoints = new List<string>();
            int forwardTarget;
            {
                var probe = Scn.Rich("fd");
                var all = new List<string>();
                probe.Hooks.Step = p => all.Add(p);
                Standard(probe);
                forwardTarget = all.FindIndex(p => p == "post-check");   // fail at the end so every step has to be undone
                probe.Cleanup();
                var s = Scn.Rich("fd");
                int n = 0;
                s.Hooks.Step = p => { if (p.StartsWith("undo:")) undoPoints.Add(p); if (n++ == forwardTarget) throw new InjectedFailure(p); };
                var r = Standard(s);
                T.True(r.RolledBack, "rolled back: " + r.Message);
                s.Cleanup();
            }
            T.True(undoPoints.Count >= 12, "rollback exposes many points: " + undoPoints.Count);
            int attention = 0;
            for (int u = 0; u < undoPoints.Count; u++)
            {
                var s = Scn.Rich("fd");
                var baseline = s.Snap();
                int n = 0, undoSeen = 0;
                s.Hooks.Step = p =>
                {
                    if (n++ == forwardTarget) throw new InjectedFailure(p);
                    if (p.StartsWith("undo:") && undoSeen++ == u) throw new InjectedFailure(p);
                };
                var r = Standard(s);
                s.Hooks.Step = null;
                T.True(r.NeedsAttention, $"undo #{u} {undoPoints[u]}: double fault must leave the change marked unfinished: {r.Message}");
                attention++;
                T.True(JournalPending(s), $"undo #{u}: journal kept");
                // new apply attempts are refused until recovery
                s.Want("beta");
                var blocked = s.Svc.Evaluate();
                T.False(blocked.CanApply, $"undo #{u}: blocked while unfinished");
                var rec = s.Svc.Recover();
                T.True(rec.RolledBack, $"undo #{u}: recovered: {rec.Message}");
                T.Eq("", Fs.Diff(baseline, s.Snap()), $"undo #{u} {undoPoints[u]}: baseline after recovery");
                T.False(JournalPending(s), $"undo #{u}: journal archived");
                s.Cleanup();
            }
            Console.WriteLine($"        ({attention} double-fault points recovered)");
        });

        T.Run("cancelling at every step rolls back to the exact baseline; cancelling before staging changes nothing", () =>
        {
            var points = new List<string>();
            {
                var s = Scn.Rich("cx");
                s.Hooks.Step = p => points.Add(p);
                Standard(s);
                s.Cleanup();
            }
            int cancelledAtOps = 0;
            for (int k = 0; k < points.Count; k++)
            {
                var s = Scn.Rich("cx");
                var baseline = s.Snap();
                using var cts = new CancellationTokenSource();
                int n = 0;
                s.Hooks.Step = p => { if (n++ == k) cts.Cancel(); };
                s.Want("beta", "gamma", "delta");
                var plan = s.Prepare();
                var r = s.Svc.Apply(plan, cts.Token);
                s.Hooks.Step = null;
                // the token is observed at the next cancellation check; a cancel that arrives after the last check simply completes
                if (r.Success) { T.True(k >= points.Count - 5, $"#{k} {points[k]}: late cancel may complete"); s.Cleanup(); continue; }
                T.True(r.Cancelled, $"#{k} {points[k]}: reported as cancelled: {r.Message}");
                T.Eq("", Fs.Diff(baseline, s.Snap()), $"#{k} {points[k]}: baseline");
                T.False(JournalPending(s), $"#{k}: no journal");
                T.Eq(0, StageLeftovers(s).Length, $"#{k}: stage cleaned");
                cancelledAtOps++;
                s.Cleanup();
            }
            T.True(cancelledAtOps >= points.Count - 6, "most points observed the cancel: " + cancelledAtOps);
        });

        T.Run("after a crash, an edit made by the user to a file the change touched is never overwritten by recovery", () =>
        {
            var s = Scn.Rich("ex");
            var baseline = s.Snap();
            s.Want("alpha", "beta", "gamma");
            var plan = s.Prepare();
            s.Hooks.Step = p => { if (p == "op:1:after") throw new SimulatedCrash(p); };
            try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
            s.Hooks.Step = null;
            T.True(JournalPending(s), "journal remains");
            // the user (or another tool) now puts the moved content back on the old name too, so BOTH names hold it: undoing would have to overwrite one
            var betaDir = Path.Combine(s.Fx.Mods, "Beta");
            File.Copy(Path.Combine(betaDir, "mod.ini"), Path.Combine(betaDir, "mod.ini.disabled-for-reasons"));
            var keep1 = File.ReadAllBytes(Path.Combine(betaDir, "mod.ini"));
            var rec = s.Svc.Recover();
            T.True(rec.NeedsAttention, "recovery stops and says so: " + rec.Message);
            T.True(File.ReadAllBytes(Path.Combine(betaDir, "mod.ini")).SequenceEqual(keep1) && File.Exists(Path.Combine(betaDir, "mod.ini.disabled-for-reasons")), "both files untouched");
            T.True(JournalPending(s), "journal kept for a later retry");
            // if the user resolves the situation (removes the extra copy), recovery completes and restores the baseline
            File.Delete(Path.Combine(betaDir, "mod.ini.disabled-for-reasons"));
            var rec2 = s.Svc.Recover();
            T.True(rec2.RolledBack, "completes after the conflict is resolved: " + rec2.Message);
            T.Eq("", Fs.Diff(baseline, s.Snap()), "exact baseline");
            // a DIFFERENT file at the target is simply someone else's: the rename never happened there, nothing is touched
            var s3 = Scn.Rich("ex");
            s3.Want("alpha", "beta", "gamma");
            var plan3 = s3.Prepare();
            s3.Hooks.Step = p => { if (p == "op:1:after") throw new SimulatedCrash(p); };
            try { s3.Svc.Apply(plan3); } catch (SimulatedCrash) { }
            s3.Hooks.Step = null;
            var b3 = Path.Combine(s3.Fx.Mods, "Beta");
            File.Move(Path.Combine(b3, "mod.ini"), Path.Combine(b3, "mod.ini.userchoice"));
            File.WriteAllText(Path.Combine(b3, "mod.ini.disabled-for-reasons"), "user wrote this");
            File.WriteAllText(Path.Combine(b3, "mod.ini"), "user also wrote this");
            var rec4 = s3.Svc.Recover();
            T.True(rec4.RolledBack, "nothing of ours to undo: " + rec4.Message);
            T.Eq("user wrote this", File.ReadAllText(Path.Combine(b3, "mod.ini.disabled-for-reasons")), "user's file untouched");
            T.Eq("user also wrote this", File.ReadAllText(Path.Combine(b3, "mod.ini")), "second user file untouched");
            T.True(File.Exists(Path.Combine(b3, "mod.ini.userchoice")), "user's rename untouched");
            s3.Cleanup();            // and when only one side was edited, recovery has nothing to overwrite
            var s2 = Scn.Rich("ex");
            s2.Want("alpha", "beta", "gamma");
            var plan2 = s2.Prepare();
            s2.Hooks.Step = p => { if (p == "op:1:after") throw new SimulatedCrash(p); };
            try { s2.Svc.Apply(plan2); } catch (SimulatedCrash) { }
            s2.Hooks.Step = null;
            File.WriteAllText(Path.Combine(s2.Fx.Mods, "Beta", "mod.ini.disabled-for-reasons"), "user content");
            File.Delete(Path.Combine(s2.Fx.Mods, "Beta", "mod.ini"));
            var rec3 = s2.Svc.Recover();
            T.True(rec3.RolledBack, "nothing to undo: " + rec3.Message);
            T.Eq("user content", File.ReadAllText(Path.Combine(s2.Fx.Mods, "Beta", "mod.ini.disabled-for-reasons")), "user content kept");
            s2.Cleanup();
            s.Cleanup();
        });

        T.Run("leftover staging from a crash before the journal is cleaned on the next run and costs nothing", () =>
        {
            var s = Scn.Rich("or");
            var baseline = s.Snap();
            s.Want("delta", "gamma");
            var plan = s.Prepare();
            s.Hooks.Step = p => { if (p == "stage-done") throw new SimulatedCrash(p); };
            try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
            s.Hooks.Step = null;
            T.False(JournalPending(s), "no journal yet");
            T.True(StageLeftovers(s).Length == 1, "stage left behind");
            T.Eq("", Fs.Diff(baseline, s.Snap()), "game files unchanged");
            s.Reload();
            var r = ApplyWant(s, "delta", "gamma");
            T.True(r.Success, r.Message);
            T.Eq(0, StageLeftovers(s).Length, "cleaned");
            s.Cleanup();
        });

        T.Run("settings save and uninstall: a failure or crash at every step also leaves the exact baseline", () =>
        {
            var cfgBytes = Bytes("; alpha settings\r\n[Alpha]\r\nEnabled=1\r\nSpeed=9\r\n");
            foreach (var kind in new[] { "config", "uninstall" })
            {
                PlanResult Prep(Scn s) => kind == "config" ? s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", cfgBytes) : s.Svc.PrepareUninstall(s.Installed("Alpha"));
                var points = new List<string>();
                SortedDictionary<string, string> applied;
                {
                    var s = Scn.Rich("fm");
                    s.Hooks.Step = p => points.Add(p);
                    var r = s.Svc.Apply(Prep(s));
                    T.True(r.Success, kind + " dry run: " + r.Message);
                    applied = s.Snap();
                    s.Cleanup();
                }
                if (kind == "config") T.True(points.Any(p => p.EndsWith(":mid")), "the replace step exposes its mid point (backup done, file not yet replaced)");
                for (int k = 0; k < points.Count; k++)
                    foreach (var crash in new[] { false, true })
                    {
                        var s = Scn.Rich("fm");
                        var baseline = s.Snap();
                        var plan = Prep(s);
                        int n = 0; string fired = "";
                        s.Hooks.Step = p => { if (n++ == k) { fired = p; throw crash ? new SimulatedCrash(p) : new InjectedFailure(p); } };
                        bool crashed = false; ApplyResult? r = null;
                        try { r = s.Svc.Apply(plan); } catch (SimulatedCrash) { crashed = true; }
                        s.Hooks.Step = null;
                        if (crash) { T.True(crashed, $"{kind} #{k} {fired}: crash"); s.Svc.Refresh(false); var rec = s.Svc.Recover(); T.False(rec.NeedsAttention, $"{kind} #{k} {fired}: recovered: {rec.Message}"); }
                        else T.False(r!.Success || r.NeedsAttention, $"{kind} #{k} {fired}: rolled back: {r.Message}");
                        var expected = crash && fired == "commit-saved" ? applied : baseline;
                        T.Eq("", Fs.Diff(expected, s.Snap()), $"{kind} #{k} {fired}{(crash ? " crash" : "")}: bytes");
                        T.False(JournalPending(s), $"{kind} #{k}: no journal");
                        s.Cleanup();
                    }
            }
        });

        // ---- regressions for the independent review (round 1) ---------------------------------------------------------
        T.Run("review #1: a file written by someone else at the moment of a settings replace is never overwritten (their version stays, the step is undone cleanly, no stray temp files)", () =>
        {
            foreach (var race in new[] { true, false })
            {
                var s = Scn.Rich("rr");
                var cfgPath = Path.Combine(s.Fx.Mods, "Alpha", "config.ini");
                var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
                cf.Rows.First(r => r.Key == "Speed").Value = "9";
                var bytes = ConfigService.Render(cf);
                var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", bytes, Sha.OfBytes(cf.OriginalBytes));
                T.True(plan.CanApply, "ready: " + string.Join(";", plan.Blockers));
                var external = Bytes("; written by another program at the worst possible moment\r\n[Alpha]\r\nSpeed=1\r\n");
                if (race) s.Hooks.Step = p => { if (p == "op:1:mid") File.WriteAllBytes(cfgPath, external); };
                var r = s.Svc.Apply(plan);
                s.Hooks.Step = null;
                var strays = Directory.GetFiles(Path.Combine(s.Fx.Mods, "Alpha"), "*", SearchOption.TopDirectoryOnly).Where(f => f.Contains("displaced") || f.Contains("restore-")).ToList();
                T.Eq(0, strays.Count, "no temporary files left next to the settings file: " + string.Join(",", strays));
                T.False(JournalPending(s), "journal filed");
                if (race)
                {
                    T.False(r.Success, "must not report success: " + r.Message);
                    T.True(r.RolledBack, "rolled back: " + r.Message + r.Technical);
                    T.True(File.ReadAllBytes(cfgPath).SequenceEqual(external), "the other program's version is intact");
                }
                else
                {
                    T.True(r.Success, "control: without the race the same save works: " + r.Message);
                    T.True(File.ReadAllBytes(cfgPath).SequenceEqual(bytes), "control: new bytes written");
                }
                s.Cleanup();
            }
        });

        // ---- regressions for the independent review (round 2) ---------------------------------------------------------
        T.Run("review r2 #1: two outside writes around a settings replace (before and after the swap) are all kept — the newest ends in the manager's backup folder, never at a path that gets deleted, even when moving it fails and through a later recovery (apply and undo)", () =>
        {
            foreach (var undo in new[] { false, true })
                foreach (var moveFails in new[] { false, true })
                {
                    var label = (undo ? "undo" : "apply") + (moveFails ? " with a failing preservation move" : "");
                    var s = Scn.Rich("r2");
                    var cfgPath = Path.Combine(s.Fx.Mods, "Alpha", "config.ini");
                    var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
                    cf.Rows.First(r => r.Key == "Speed").Value = "9";
                    var bytes = ConfigService.Render(cf);
                    var first = Bytes("; outside write one\r\n[Alpha]\r\nSpeed=2\r\n");
                    var second = Bytes("; outside write two (newest)\r\n[Alpha]\r\nSpeed=3\r\n");
                    var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", bytes, Sha.OfBytes(cf.OriginalBytes));
                    T.True(plan.CanApply, "ready: " + string.Join(";", plan.Blockers));
                    string pre = undo ? "undo-" : "";
                    Action<string> hook = p =>
                    {
                        if (p == (undo ? "undo:1:replace" : "op:1:mid")) File.WriteAllBytes(cfgPath, first);
                        else if (p == pre + "replace:swapped") File.WriteAllBytes(cfgPath, second);
                        else if (moveFails && p == pre + "replace:preserve") throw new InjectedFailure(p);
                    };
                    ApplyResult r;
                    if (!undo) { s.Hooks.Step = hook; r = s.Svc.Apply(plan); }
                    else { T.True(s.Svc.Apply(plan).Success, "setup: the save itself works"); s.Hooks.Step = hook; r = s.Svc.UndoLast(); }
                    s.Hooks.Step = null;
                    T.False(r.Success, label + " must not report success: " + r.Message);
                    T.True(File.ReadAllBytes(cfgPath).SequenceEqual(first), label + ": the first outside version is what the file holds");
                    string[] Holding() => Directory.GetFiles(Path.Combine(s.Fx.Mods, ".allstars-manager"), "*", SearchOption.AllDirectories).Where(f => File.ReadAllBytes(f).SequenceEqual(second)).ToArray();
                    var kept = Holding();
                    T.Eq(1, kept.Length, label + ": the newest outside version is kept in the manager's folder: " + r.Message);
                    T.True(Path.GetFileName(kept[0]).Contains(moveFails ? ".swapback-" : ".external-"), label + ": kept under " + Path.GetFileName(kept[0]));
                    var strays = Directory.GetFiles(Path.Combine(s.Fx.Mods, "Alpha"), "*", SearchOption.TopDirectoryOnly).Where(f => f.Contains("displaced") || f.Contains("restore-")).ToList();
                    T.Eq(0, strays.Count, label + ": no temporary files left next to the settings file: " + string.Join(",", strays));
                    // a later recovery / cleanup must not consume it either
                    s.Svc.Refresh(false);
                    if (JournalPending(s)) s.Svc.Recover();
                    T.Eq(1, Holding().Length, label + ": still kept after recovery");
                    T.True(File.ReadAllBytes(cfgPath).SequenceEqual(first), label + ": and the file is still the first outside version after recovery");
                    s.Cleanup();
                }
        });

        T.Run("review r3 #2: undo and recovery move only the displaced file this step recorded; a look-alike file belonging to someone else stays where it is", () =>
        {
            foreach (var crashFirst in new[] { false, true })
            {
                var s = Scn.Rich("r3");
                var dir = Path.Combine(s.Fx.Mods, "Alpha");
                var cfgPath = Path.Combine(dir, "config.ini");
                var lookalike = Path.Combine(dir, "config.ini.displaced-abcdef");
                File.WriteAllText(lookalike, "someone else's file");
                var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
                cf.Rows.First(r => r.Key == "Speed").Value = "9";
                var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", ConfigService.Render(cf), Sha.OfBytes(cf.OriginalBytes));
                var orig = File.ReadAllBytes(cfgPath);
                if (crashFirst)
                {
                    s.Hooks.Step = p => { if (p == "replace:swapped") throw new SimulatedCrash(p); };
                    try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
                    s.Hooks.Step = null;
                    s.Svc.Refresh(false);
                    var rec = s.Svc.Recover();
                    T.False(rec.NeedsAttention, "recovered: " + rec.Message);
                    T.True(File.ReadAllBytes(cfgPath).SequenceEqual(orig), "original settings restored");
                    var parked = Directory.GetFiles(Path.Combine(s.Fx.Mods, ".allstars-manager"), "*.leftover", SearchOption.AllDirectories);
                    T.Eq(1, parked.Length, "the step's own parked version was kept, not deleted");
                }
                else
                {
                    T.True(s.Svc.Apply(plan).Success, "setup: the save works");
                    var u = s.Svc.UndoLast();
                    T.True(u.Success, "undo works: " + u.Message);
                }
                T.True(File.Exists(lookalike) && File.ReadAllText(lookalike) == "someone else's file", (crashFirst ? "after crash recovery" : "after undo") + ": the look-alike file was not touched");
                s.Cleanup();
            }
        });

        T.Run("review r4 #1: recovery refuses to look at or collect a parked file when the mod folder has become a junction to somewhere else; the outside folder is not touched, and recovery works once the link is gone", () =>
        {
            var s = Scn.Rich("r4");
            var modDir = Path.Combine(s.Fx.Mods, "Alpha");
            var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
            cf.Rows.First(r => r.Key == "Speed").Value = "9";
            var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", ConfigService.Render(cf), Sha.OfBytes(cf.OriginalBytes));
            s.Hooks.Step = p => { if (p == "op:1:before") throw new SimulatedCrash(p); };
            try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
            s.Hooks.Step = null;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(TransactionStore.CurrentPath(s.Fx.Game)))!.AsObject();
            var parkName = Path.GetFileName(FirstOp(node, "ReplaceFile")["displacedPath"]!.GetValue<string>());
            var origBytes = File.ReadAllBytes(Path.Combine(modDir, "config.ini"));
            var realCopy = modDir + "-real";
            Directory.Move(modDir, realCopy);
            var outside = Path.Combine(s.Fx.Root, "outside-r4"); Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "config.ini"), origBytes);
            File.WriteAllText(Path.Combine(outside, parkName), "outside file that must never be moved");
            Fs.MakeJunction(modDir, outside);
            var before = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains("outside-r4")).ToDictionary(k => k.Key, k => k.Value);
            var rec = s.Svc.Recover();
            T.False(rec.Success, "must not report a clean recovery through a link: " + rec.Message);
            var after = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains("outside-r4")).ToDictionary(k => k.Key, k => k.Value);
            T.True(before.Count == after.Count && before.All(kv => after.TryGetValue(kv.Key, out var v) && v == kv.Value), "nothing outside the game folder was changed");
            Directory.Delete(modDir);   // removes only the link
            Directory.Move(realCopy, modDir);
            var ok = s.Svc.Recover();
            T.True(ok.RolledBack || ok.Success, "recovery completes once the link is gone: " + ok.Message);
            s.Cleanup();
        });

        T.Run("review r3 #2: a journal naming a parking file elsewhere than beside its own target is refused before anything is written", () =>
        {
            var s = Scn.Rich("r3g");
            var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
            cf.Rows.First(r => r.Key == "Speed").Value = "9";
            var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", ConfigService.Render(cf), Sha.OfBytes(cf.OriginalBytes));
            s.Hooks.Step = p => { if (p == "op:1:mid") throw new SimulatedCrash(p); };
            try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
            s.Hooks.Step = null;
            var cur = TransactionStore.CurrentPath(s.Fx.Game);
            var healthy = File.ReadAllText(cur);
            T.True(healthy.Contains("displacedPath", StringComparison.OrdinalIgnoreCase), "the journal records the parking name before the step starts");
            foreach (var (name, bad) in new[] { ("elsewhere in the game folder", Path.Combine(s.Fx.Game, "settings.ini")), ("beside the target but not generated-looking", Path.Combine(s.Fx.Mods, "Alpha", "config.ini.displaced-zz")), ("in another mod folder", Path.Combine(s.Fx.Mods, "Beta", "config.ini.displaced-abcdef")) })
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(healthy)!.AsObject();
                FirstOp(node, "ReplaceFile")["displacedPath"] = bad;
                var tampered = node.ToJsonString();
                File.WriteAllText(cur, tampered);
                var before = Fs.Snapshot(s.Fx.Game, false);
                var rec = s.Svc.Recover();
                T.True(rec.Declined && !rec.Success, name + ": declined: " + rec.Message);
                T.Eq("", Fs.Diff(before, Fs.Snapshot(s.Fx.Game, false)), name + ": nothing changed");
            }
            File.WriteAllText(cur, healthy);
            var ok = s.Svc.Recover();
            T.True(ok.RolledBack || ok.Success, "the healthy record still recovers: " + ok.Message);
            s.Cleanup();
        });

        T.Run("review #2: Undo and Recover take the lock before reading anything, so another manager's unfinished journal can never be overwritten", () =>
        {
            var s = Scn.Rich("lk");
            T.True(Standard(s).Success, "setup (creates history)");
            var cur = TransactionStore.CurrentPath(s.Fx.Game);
            File.WriteAllText(cur, "another manager's unfinished journal - must survive");
            var kept = File.ReadAllBytes(cur);
            using (var other = new FileStream(TransactionStore.LockPath(s.Fx.Game), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var u = s.Svc.UndoLast();
                T.True(u.Declined, "undo declined while another manager holds the lock: " + u.Message);
                T.Has(new[] { u.Message }, "Another copy", "says why");
                var rec = s.Svc.Recover();
                T.True(rec.Declined, "recover declined too: " + rec.Message);
                T.True(File.ReadAllBytes(cur).SequenceEqual(kept), "their journal is untouched");
            }
            // lock free but an unfinished journal exists: undo refuses for that reason and still does not touch it
            var u2 = s.Svc.UndoLast();
            T.True(u2.Declined, "declined: " + u2.Message);
            T.True(File.ReadAllBytes(cur).SequenceEqual(kept), "journal still untouched");
            s.Cleanup();
        });

        T.Run("review #3: filing a record never writes through a link in the manager's own folders (history junction), and the record is filed once the link is gone", () =>
        {
            var s = Scn.Rich("hj");
            s.Want("beta", "gamma", "delta");
            var plan = s.Prepare();
            s.Hooks.Step = p => { if (p == "commit-saved") throw new SimulatedCrash(p); };
            try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
            s.Hooks.Step = null;
            var outside = Path.Combine(s.Fx.Root, "history-target"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "must never change");
            var hist = TransactionStore.HistoryDir(s.Fx.Game);
            Directory.CreateDirectory(Path.GetDirectoryName(hist)!);
            Fs.MakeJunction(hist, outside);
            var before = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains("history-target")).ToDictionary(k => k.Key, k => k.Value);
            var rec = s.Svc.Recover();
            T.False(rec.Success, "must not file through the link: " + rec.Message);
            var after = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains("history-target")).ToDictionary(k => k.Key, k => k.Value);
            T.True(before.Count == after.Count && before.All(kv => after.TryGetValue(kv.Key, out var v) && v == kv.Value), "nothing was written to the link's target");
            T.True(JournalPending(s), "the record is kept");
            T.True(s.Svc.UndoLast().Declined, "undo does not read a history reached through a link");
            Directory.Delete(hist);   // removes only the junction
            var ok = s.Svc.Recover();
            T.True(ok.Success, "filed once the link is gone: " + ok.Message);
            T.False(JournalPending(s), "journal archived");
            T.True(Directory.GetFiles(hist, "journal.json", SearchOption.AllDirectories).Length == 1, "the record is in the real history folder");
            s.Cleanup();
        });

        T.Run("review #4: saving settings from a stale editor snapshot is refused (the other program's edit would be lost) and succeeds after reopening", () =>
        {
            var s = Scn.Rich("sc");
            var cfgPath = Path.Combine(s.Fx.Mods, "Alpha", "config.ini");
            var cf = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
            cf.Rows.First(r => r.Key == "Speed").Value = "9";
            var bytes = ConfigService.Render(cf);
            // another program edits a DIFFERENT key after the editor opened the file
            File.WriteAllText(cfgPath, "; alpha settings\r\n[Alpha]\r\nEnabled=1\r\nSpeed=5\r\nNewKey=added elsewhere\r\n");
            var changed = File.ReadAllBytes(cfgPath);
            var plan = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", bytes, Sha.OfBytes(cf.OriginalBytes));
            T.False(plan.CanApply, "refused");
            T.Has(plan.Blockers, "changed by another program", "explained");
            T.True(s.Svc.Apply(plan).Declined, "apply declined");
            T.True(File.ReadAllBytes(cfgPath).SequenceEqual(changed), "the other program's edit is intact");
            // reopening picks the new content up; the same user change then saves on top of it
            s.Reload();
            var cf2 = ConfigService.Load(s.Installed("Alpha").Installed!, "config.ini");
            cf2.Rows.First(r => r.Key == "Speed").Value = "9";
            var p2 = s.Svc.PrepareConfigSave(s.Installed("Alpha"), "config.ini", ConfigService.Render(cf2), Sha.OfBytes(cf2.OriginalBytes));
            T.True(p2.CanApply, "after reopening: " + string.Join(";", p2.Blockers));
            T.True(s.Svc.Apply(p2).Success, "saved");
            T.Has(new[] { File.ReadAllText(cfgPath) }, "NewKey=added elsewhere", "the other program's key is still there");
            T.Has(new[] { File.ReadAllText(cfgPath) }, "Speed=9", "and so is the user's change");
            s.Cleanup();
        });

        T.Run("review #5: installing a package whose manifest is stored under a switched-off name lands switched on in one step; Undo then works, and still refuses after a real edit", () =>
        {
            foreach (var edit in new[] { false, true })
            {
                var s = Scn.Rich("dm");
                var src = Path.Combine(s.Fx.Root, "eps-src");
                var spec = PackageTests.Basic("eps", "Eps"); spec.Groups.Clear(); spec.Groups.Add(GroupSpec.File("Scripts/Other.sbn", Bytes("eps")));
                spec.ManifestFileName = "mod.ini.disabled-for-testing"; spec.Folder = "Eps";
                s.Fx.WriteMod(spec, src);
                var pv = s.Svc.Importer.Inspect(Path.Combine(src, "Eps"), s.Svc.Library);
                s.Svc.Importer.AddToLibrary(pv);
                s.Svc.Refresh(false); s.Svc.VerifyAll();
                var libManifest = Path.Combine(s.Svc.Library.First(l => l.Package.Id == "eps").DirPath, "mod.ini.disabled-for-testing");
                var libBytes = File.ReadAllBytes(libManifest);
                var r = ApplyWant(s, "alpha", "eps");
                T.True(r.Success, "install + switch on: " + r.Message + r.Technical);
                var inst = Path.Combine(s.Fx.Mods, "Eps");
                T.True(File.Exists(Path.Combine(inst, "mod.ini")) && !File.Exists(Path.Combine(inst, "mod.ini.disabled-for-testing")), "the installed copy has its manifest in the switched-on form");
                T.True(File.ReadAllBytes(Path.Combine(inst, "mod.ini")).SequenceEqual(libBytes), "same bytes as the original manifest");
                T.True(File.Exists(libManifest), "the library original keeps its own name");
                T.False(s.Svc.Evaluate().Plan.Ops.Any(o => o.Kind == OpKind.RenameFile), "no separate rename step exists for installs");
                if (edit) File.AppendAllText(Path.Combine(inst, "notes.txt"), "user note");
                var u = s.Svc.UndoLast();
                if (edit) { T.True(u.Declined, "refused after a real edit: " + u.Message); T.True(File.Exists(Path.Combine(inst, "notes.txt")), "edit preserved"); }
                else { T.True(u.Success, "undo of an unchanged install: " + u.Message); T.False(Directory.Exists(inst), "folder taken out"); }
                s.Cleanup();
            }
        });

        T.Run("review #6: two mods needing the same absent, identical extra file apply together (one copy, both owners recorded) and the file is only taken back with its last owner", () =>
        {
            var s = Scn.Rich("sh");
            foreach (var id in new[] { "share-a", "share-b" })
            {
                var pkg = Path.Combine(s.Fx.Root, "in-" + id);
                var spec = PackageTests.Basic(id, id.ToUpperInvariant()); spec.Groups.Clear();
                Scn.BuildLooseModPackage(s.Fx, pkg, spec, ("Resource/Movie/SHARED.usm", Bytes("one shared movie file")));
                s.Svc.Importer.AddToLibrary(s.Svc.Importer.Inspect(pkg, s.Svc.Library));
                s.Svc.Refresh(false);
            }
            s.Svc.VerifyAll();
            var r = ApplyWant(s, "alpha", "gamma", "share-a", "share-b");
            T.True(r.Success, "apply: " + r.Message + r.Technical);
            var movie = Path.Combine(s.Fx.Game, "Resource", "Movie", "SHARED.usm");
            T.True(File.ReadAllBytes(movie).SequenceEqual(Bytes("one shared movie file")), "one copy in place");
            var reg = AssetRegistry.Load(s.Fx.Game).Assets["Resource/Movie/SHARED.usm"];
            T.True(reg.CreatedByManager && reg.Owners.OrderBy(o => o).SequenceEqual(new[] { "SHARE-A", "SHARE-B" }), "both owners recorded: " + string.Join(",", reg.Owners));
            s.Reload();
            T.True(s.Svc.Apply(s.Svc.PrepareUninstall(s.Installed("SHARE-A"))).Success, "remove A");
            T.True(File.Exists(movie), "still there for B");
            s.Reload();
            T.True(s.Svc.Apply(s.Svc.PrepareUninstall(s.Installed("SHARE-B"))).Success, "remove B");
            T.False(File.Exists(movie), "taken back with its last owner");
            T.Eq(1, Directory.GetFiles(TransactionStore.BackupRoot(s.Fx.Game), "SHARED.usm", SearchOption.AllDirectories).Length, "kept in the backup folder");
            s.Cleanup();
        });

        T.Run("Undo after a finished change refuses (and preserves everything) when installed content was added to, edited same-size, or removed", () =>
        {
            string FirstPayload(Scn s) => Directory.GetFiles(Path.Combine(s.Fx.Mods, "Delta", "payloads"))[0];
            var variants = new (string Name, Action<Scn> Tamper)[]
            {
                ("a file added inside the installed folder", s => File.WriteAllText(Path.Combine(s.Fx.Mods, "Delta", "my-notes.txt"), "user's notes")),
                ("a same-size edit of an installed payload", s => { var p = FirstPayload(s); var b = File.ReadAllBytes(p); b[b.Length / 2] ^= 0x5A; File.WriteAllBytes(p, b); }),
                ("a file removed from the installed folder", s => File.Delete(FirstPayload(s))),
                ("a same-size edit of a switched-on manifest", s => { var p = Path.Combine(s.Fx.Mods, "Beta", "mod.ini"); var b = File.ReadAllBytes(p); b[^3] ^= 0x01; File.WriteAllBytes(p, b); }),
            };
            foreach (var (name, tamper) in variants)
            {
                var s = Scn.Rich("ud");
                T.True(Standard(s).Success, "setup");
                tamper(s);
                var changed = s.Snap(false);
                var u = s.Svc.UndoLast();
                T.True(u.Declined && !u.Success, $"{name}: undo must be declined: {u.Message}");
                T.Has(new[] { u.Message }, "changed since", $"{name}: explains");
                T.Eq("", Fs.Diff(changed, s.Snap(false)), $"{name}: every byte (including the user's edit) preserved and nothing moved");
                T.True(Directory.Exists(Path.Combine(s.Fx.Mods, "Delta")), $"{name}: installed folder still in place");
                s.Cleanup();
            }
            var ok = Scn.Rich("ud");
            T.True(Standard(ok).Success, "setup");
            var before = ok.Snap();
            T.True(ok.Svc.UndoLast().Success, "control: unchanged content can be undone");
            T.False(Directory.Exists(Path.Combine(ok.Fx.Mods, "Delta")), "control: installed folder taken out");
            T.True(Directory.GetDirectories(TransactionStore.BackupRoot(ok.Fx.Game), "*Delta*", SearchOption.AllDirectories).Length == 1, "control: kept in the backup area, not deleted");
            ok.Cleanup();
        });

        T.Run("an interrupted rollback followed by a user edit: recovery refuses to consume the edited folder, and completes once the edit is resolved", () =>
        {
            var s = Scn.Rich("ir");
            var baseline = s.Snap();
            s.Want("beta", "gamma", "delta");
            var plan = s.Prepare();
            int moveSeq = plan.Plan.Ops.First(o => o.Kind == OpKind.MoveDir).Seq;
            s.Hooks.Step = p => { if (p == "post-check") throw new InjectedFailure(p); if (p == $"undo:{moveSeq}:before") throw new InjectedFailure(p); };
            var r = s.Svc.Apply(plan);
            s.Hooks.Step = null;
            T.True(r.NeedsAttention, "rollback was interrupted at the install step: " + r.Message);
            T.True(Directory.Exists(Path.Combine(s.Fx.Mods, "Delta")), "the installed folder is still there");
            File.WriteAllText(Path.Combine(s.Fx.Mods, "Delta", "added-by-user.txt"), "precious");
            var rec = s.Svc.Recover();
            T.True(rec.NeedsAttention, "recovery stops: " + rec.Message);
            T.Eq("precious", File.ReadAllText(Path.Combine(s.Fx.Mods, "Delta", "added-by-user.txt")), "the user's file is intact");
            T.True(JournalPending(s), "journal kept");
            File.Delete(Path.Combine(s.Fx.Mods, "Delta", "added-by-user.txt"));
            var rec2 = s.Svc.Recover();
            T.True(rec2.RolledBack, "completes after the edit is removed: " + rec2.Message);
            T.Eq("", Fs.Diff(baseline, s.Snap()), "exact baseline");
            s.Cleanup();
        });

        T.Run("a tampered or corrupt journal causes no write outside the manager's own transaction folders; the healthy journal still recovers", () =>
        {
            var outsideName = "outside-sentinel";
            var variants = new (string Name, Action<System.Text.Json.Nodes.JsonObject, string> Tamper)[]
            {
                ("absolute change id", (j, root) => j["txn"] = @"C:\Windows\Temp\evil"),
                ("change id with ..", (j, root) => j["txn"] = @"..\..\x"),
                ("change id with a separator", (j, root) => j["txn"] = j["txn"] + "/.."),
                ("empty change id", (j, root) => j["txn"] = ""),
                ("staging folder elsewhere", (j, root) => j["stageDir"] = Path.Combine(root, "Resource")),
                ("backup folder elsewhere", (j, root) => j["backupDir"] = root),
                ("another game folder", (j, root) => j["game"] = Path.Combine(root, "..", outsideName)),
                ("unknown version", (j, root) => j["version"] = 2),
                ("no steps", (j, root) => j["ops"] = new System.Text.Json.Nodes.JsonArray()),
                ("step targeting a base archive", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["path"] = Path.Combine(root, "Resource", "Base.xpac"); }),
                ("step targeting a game script", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["path"] = Path.Combine(root, "Scripts", "FrontEndScripts.sbn"); }),
                ("step targeting a path outside the game", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["path"] = Path.Combine(root, "..", outsideName, "x.usm"); }),
                ("undo location outside the backup area", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["undoPath"] = Path.Combine(root, "..", outsideName, "undo.usm"); }),
                ("undo location inside Resource", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["undoPath"] = Path.Combine(root, "Resource", "Movie", "undo.usm"); }),
                ("rename of a mod's plan", (j, root) => { var op = FirstOp(j, "RenameFile"); op["from"] = Path.Combine(root, "mods", "Gamma", "plan.json"); op["path"] = Path.Combine(root, "mods", "Gamma", "plan.json.moved"); }),
                ("folder move into the game root", (j, root) => { var op = FirstOp(j, "MoveDir"); op["path"] = Path.Combine(root, "Resource"); }),
                ("parking name on a step that never replaces a file", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["displacedPath"] = Path.Combine(root, "mods", "Alpha", "config.ini.displaced-abcdef"); }),
                ("replace of a game file", (j, root) => { var op = FirstOp(j, "MoveNewFile"); op["kind"] = "ReplaceFile"; op["path"] = Path.Combine(root, "settings.ini"); op["backupPath"] = Path.Combine(root, "settings.ini.bak"); }),
            };
            foreach (var (name, tamper) in variants)
            {
                var s = Scn.Rich("tj");
                var outside = Path.Combine(s.Fx.Root, outsideName); Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "must never change");
                s.Want("beta", "gamma", "delta");
                var plan = s.Prepare();
                s.Hooks.Step = p => { if (p == "op:3:after") throw new SimulatedCrash(p); };
                try { s.Svc.Apply(plan); } catch (SimulatedCrash) { }
                s.Hooks.Step = null;
                var cur = TransactionStore.CurrentPath(s.Fx.Game);
                var healthy = File.ReadAllText(cur);
                var node = System.Text.Json.Nodes.JsonNode.Parse(healthy)!.AsObject();
                tamper(node, s.Fx.Game);
                var tampered = node.ToJsonString();
                File.WriteAllText(cur, tampered);
                var gameBefore = Fs.Snapshot(s.Fx.Game, false);
                var outsideBefore = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains(outsideName)).ToDictionary(k => k.Key, k => k.Value);
                var rec = s.Svc.Recover();
                T.True(rec.Declined && !rec.Success, $"{name}: declined: {rec.Message}");
                T.Has(new[] { rec.Message }, "does not look like one this manager wrote", $"{name}: explains");
                T.Eq("", Fs.Diff(gameBefore, Fs.Snapshot(s.Fx.Game, false)), $"{name}: not a single byte changed in the game folder (including the manager's own folder)");
                var outsideAfter = Fs.Snapshot(s.Fx.Root).Where(kv => kv.Key.Contains(outsideName)).ToDictionary(k => k.Key, k => k.Value);
                T.True(outsideBefore.Count == outsideAfter.Count && outsideBefore.All(kv => outsideAfter.TryGetValue(kv.Key, out var v) && v == kv.Value), $"{name}: nothing outside the game folder changed");
                T.Eq(tampered, File.ReadAllText(cur), $"{name}: the record itself is left as found");
                // the healthy record (restored) recovers normally
                File.WriteAllText(cur, healthy);
                var baseline = Scn.Rich("tjb");
                var ok = s.Svc.Recover();
                T.True(ok.RolledBack || ok.Success, $"{name}: healthy record recovers: {ok.Message}");
                T.False(JournalPending(s), $"{name}: journal archived");
                T.Eq(0, s.ActiveIds().Except(new[] { "alpha", "gamma" }).Count(), $"{name}: back to the original active set");
                baseline.Cleanup();
                s.Cleanup();
            }
        });

        T.Run("a tampered history record cannot steer Undo outside the manager's scope", () =>
        {
            var s = Scn.Rich("th");
            T.True(Standard(s).Success, "setup");
            var hist = Directory.GetFiles(TransactionStore.HistoryDir(s.Fx.Game), "journal.json", SearchOption.AllDirectories).Single();
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hist))!.AsObject();
            FirstOp(node, "MoveNewFile")["undoPath"] = Path.Combine(s.Fx.Game, "Resource", "Base.xpac");
            File.WriteAllText(hist, node.ToJsonString());
            var before = s.Snap(false);
            var u = s.Svc.UndoLast();
            T.True(u.Declined, "declined: " + u.Message);
            T.Eq("", Fs.Diff(before, s.Snap(false)), "no change");
            // a record filed under a different folder name than its own id is ignored
            var dir = Path.GetDirectoryName(hist)!;
            Directory.Move(dir, Path.Combine(Path.GetDirectoryName(dir)!, "20200101T000000Z-aaaaaa"));
            T.True(s.Svc.UndoLast().Declined, "mismatched record ignored");
            s.Cleanup();
        });

        T.Run("a tampered list of added files cannot make an uninstall touch anything outside Resource/", () =>
        {
            var s = Scn.Rich("rg");
            T.True(Standard(s).Success, "setup");
            var victim = Path.Combine(s.Fx.Mods, "Gamma", "plan.json");
            var victimBytes = File.ReadAllBytes(victim);
            var regPath = TransactionStore.RegistryPath(s.Fx.Game);
            var reg = AssetRegistry.Load(s.Fx.Game);
            reg.Assets["mods/Gamma/plan.json"] = new AssetRegistry.Entry { Sha = Sha.OfBytes(victimBytes), CreatedByManager = true, Owners = { "Delta" } };
            reg.Assets["Scripts/FrontEndScripts.sbn"] = new AssetRegistry.Entry { Sha = Sha.OfFile(Path.Combine(s.Fx.Game, "Scripts", "FrontEndScripts.sbn")), CreatedByManager = true, Owners = { "Delta" } };
            File.WriteAllBytes(regPath, reg.ToBytes());
            var before = s.Snap();
            s.Reload();
            var up = s.Svc.PrepareUninstall(s.Installed("Delta"));
            var r = s.Svc.Apply(up);
            T.True(r.Success, r.Message + r.Technical);
            T.True(File.ReadAllBytes(victim).SequenceEqual(victimBytes), "another mod's plan untouched");
            T.True(File.Exists(Path.Combine(s.Fx.Game, "Scripts", "FrontEndScripts.sbn")), "game script untouched");
            T.True(up.Plan.Notes.Any(n => n.Contains("ignored")), "the odd entries are reported");
            s.Cleanup();
        });

        T.Run("a file someone else creates after the journal is written is never overwritten (the step refuses and everything rolls back)", () =>
        {
            var s = Scn.Rich("rc");
            s.Want("delta");
            var plan = s.Prepare();
            T.True(plan.CanApply, "ready: " + string.Join(";", plan.Blockers));
            var movie = Path.Combine(s.Fx.Game, "Resource", "Movie");
            s.Hooks.Step = p =>
            {
                if (p == "journal-written")
                {
                    Directory.CreateDirectory(movie);
                    File.WriteAllBytes(Path.Combine(movie, "D1.usm"), Bytes("somebody else's movie, created in the race window"));
                }
            };
            var r = s.Svc.Apply(plan);
            s.Hooks.Step = null;
            T.False(r.Success, "must not succeed: " + r.Message);
            T.True(r.RolledBack, "rolled back: " + r.Message + r.Technical);
            T.True(File.ReadAllBytes(Path.Combine(movie, "D1.usm")).SequenceEqual(Bytes("somebody else's movie, created in the race window")), "the foreign file is intact");
            T.False(Directory.Exists(Path.Combine(s.Fx.Mods, "Delta")), "the install was undone");
            T.False(JournalPending(s), "no unfinished journal");
            s.Cleanup();
        });

#if PRIVATE_TESTS
        if (File.Exists(OracleTests.OraclePath)) RegisterOracleAcceptance();
        else T.Skip("what the manager leaves behind is accepted by the loader's own discovery code", "private loader-parity oracle executable not present in this checkout");
#else
        T.Skip("what the manager leaves behind is accepted by the loader's own discovery code", "the private loader-parity oracle is not part of the public source tree");
#endif

        T.Run("the write set of a normal apply is minimal and stays inside mods/ and Resource/Movie/", () =>
        {
            var s = Scn.Rich("ws");
            var before = Fs.Snapshot(s.Fx.Game, false);
            var r = Standard(s);
            T.True(r.Success, r.Message);
            var after = Fs.Snapshot(s.Fx.Game, false);
            var touched = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).Select(k => k[2..]).ToList();
            T.True(touched.All(p => p.StartsWith(@"mods\") || p.StartsWith(@"Resource\Movie")), "outside write: " + string.Join(", ", touched.Where(p => !(p.StartsWith(@"mods\") || p.StartsWith(@"Resource\Movie")))));
            foreach (var f in new[] { @"Resource\Base.xpac", @"Resource\Racers.xpac", @"Resource\Select.xpac", "ChaoGarage.dll", "dinput8.dll", GameFolderInfo.ExeName, "settings.ini", "ssr_save.bin", @"Scripts\FrontEndScripts.sbn" })
                T.False(touched.Contains(f), f + " must not change");
            s.Cleanup();
        });
    }
}
