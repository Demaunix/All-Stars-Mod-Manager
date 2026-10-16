using System.Text;

namespace AllStarsModManager.Tests;

public static class PackageTests
{
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    public static ModSpec Basic(string id = "mod-a", string folder = "Mod A", string version = "1.0.0") => new()
    {
        Id = id, Name = folder, Folder = folder, Version = version,
        Groups = { GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("patched " + id)) }
    };

    static (PackageInfo Pkg, VerifyResult Vr) Check(Fx fx, BuiltMod m, bool deep = true)
    {
        var pkg = PackageInspector.Inspect(m.Dir);
        var vr = PackageVerifier.Verify(pkg, new VerifyOptions { Cache = new HashCache(), GameRoot = fx.Game, Fresh = true, Deep = deep });
        PackageVerifier.Apply(pkg, vr);
        return (pkg, vr);
    }

    static void Expect(string name, Func<Fx, ModSpec> make, string code, bool inVerify = false, Action<Fx, BuiltMod>? afterWrite = null)
    {
        T.Run($"tamper: {name} -> {code}", () =>
        {
            using var fx = new Fx("pk");
            var spec = make(fx);
            var m = fx.WriteMod(spec);
            afterWrite?.Invoke(fx, m);
            var (pkg, vr) = Check(fx, m);
            var all = pkg.AllIssues.ToList();
            T.HasCode(all, code, name);
            T.True(pkg.HasBlockers, "a blocker must be raised");
            T.False(pkg.Integrity == Integrity.Verified && pkg.Fit == GameFit.Matches && !pkg.HasBlockers, "must not look healthy");
            fx.Cleanup();
        });
    }

    public static void Register()
    {
        T.Area("packages");

        T.Run("a healthy package inspects and verifies with no blockers (positive control)", () =>
        {
            using var fx = new Fx("pk");
            var spec = Basic();
            spec.PlanModId = "mod-a";
            spec.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("new member")), (0x77777777u, "add", Bytes("added"))));
            spec.Groups.Add(GroupSpec.Rtsc("ShaderCache/a~PS_46.rtsc", Bytes("seed")));
            spec.Groups.Add(GroupSpec.Copy("Resource/Copied.bin", Bytes("copy me")));
            spec.ConfigFiles["config.ini"] = "[Mod]\r\nEnabled=1\r\n";
            var m = fx.WriteMod(spec);
            var (pkg, vr) = Check(fx, m);
            T.NoBlockers(pkg.AllIssues, "healthy");
            T.Eq(Integrity.Verified, pkg.Integrity, "integrity");
            T.Eq(GameFit.Matches, pkg.Fit, "fit");
            T.Eq("mod-a", pkg.Id, "id");
            T.Eq(4, pkg.Plan!.Groups.Count, "groups");
            T.Eq(2, pkg.Plan.Groups.First(g => g.Format == "xpac").Keys.Count, "keys");
            T.True(pkg.ConfigFiles.SequenceEqual(new[] { "config.ini" }), "config files");
            T.Eq(ManifestState.Active, pkg.Manifest!.State, "active");
            fx.Cleanup();
        });

        Expect("plan edited after packaging", f => { var s = Basic(); s.WrongPlanHash = true; return s; }, "plan-hash-mismatch");
        Expect("runtime replaced", f => { var s = Basic(); s.WrongRuntimeHash = true; return s; }, "runtime-hash-mismatch");
        Expect("uppercase pin hashes", f => { var s = Basic(); s.UppercasePinHash = true; return s; }, "manifest-hash-case");
        Expect("schema 2 manifest", f => { var s = Basic(); s.Schema = "2"; return s; }, "manifest-schema");
        Expect("plan starts with a UTF-8 BOM", f => { var s = Basic(); s.PlanOverrideText = "﻿{\"schema\":1,\"archive_groups\":[]}"; return s; }, "plan-bom");
        Expect("plan is not valid JSON for the loader", f => { var s = Basic(); s.PlanOverrideText = "{\"schema\":1,\"archive_groups\":[],}"; return s; }, "plan-invalid");
        Expect("plan schema 2", f => { var s = Basic(); s.PlanOverrideText = "{\"schema\":2,\"archive_groups\":[]}"; return s; }, "plan-schema");
        Expect("plan lacks archive_groups", f => { var s = Basic(); s.PlanOverrideText = "{\"schema\":1}"; return s; }, "plan-invalid");
        Expect("plan mod_id disagrees with manifest id", f => { var s = Basic(); s.PlanModId = "someone-else"; return s; }, "id-mismatch");
        Expect("payload bytes changed", f => { var s = Basic(); s.Groups[0].Payload = Bytes("x"); return s; }, "payload-hash", afterWrite: (f, m) => { var p = Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0]; File.WriteAllBytes(p, Enumerable.Repeat((byte)'Z', (int)new FileInfo(p).Length).ToArray()); });
        Expect("whole-file payload grew (the loader checks that hash)", f => Basic(), "payload-hash", afterWrite: (f, m) => File.AppendAllText(Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0], "more"));
        Expect("archive member payload size changed", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (f.BaseKey("Base", 0), "replace", Bytes("abc"))); return s; }, "payload-size", afterWrite: (f, m) => File.AppendAllText(Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0], "more"));
        Expect("payload deleted", f => Basic(), "payload-missing", afterWrite: (f, m) => File.Delete(Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0]));
        Expect("runtime deleted", f => Basic(), "runtime-missing", afterWrite: (f, m) => File.Delete(Path.Combine(m.Dir, "runtime", "Rt.dll")));
        Expect("plan deleted", f => Basic(), "plan-missing", afterWrite: (f, m) => File.Delete(Path.Combine(m.Dir, "plan.json")));
        Expect("manifest lacks id", f => { var s = Basic(); s.ManifestText = "[mod]\r\nschema=1\r\nplan=plan.json\r\nplan_sha256=" + new string('a', 64) + "\r\nruntime=runtime/Rt.dll\r\nruntime_sha256=" + new string('b', 64) + "\r\n"; return s; }, "manifest-field");
        Expect("manifest is empty of required fields", f => { var s = Basic(); s.ManifestText = "[mod]\r\n"; return s; }, "manifest-field");
        Expect("path escapes the mod folder", f => { var s = Basic(); s.Groups[0].Path = "Resource/../../evil.bin"; return s; }, "plan-path");
        Expect("target outside Resource/Scripts", f => { var s = Basic(); s.Groups[0].Path = "Config.exe"; return s; }, "plan-group");
        Expect("shader cache path outside ShaderCache", f => { var s = Basic(); s.Groups[0] = GroupSpec.Rtsc("Resource/x.rtsc", Bytes("s")); return s; }, "plan-group");
        Expect("unsupported group format", f => { var s = Basic(); s.Groups[0].Format = "delta"; return s; }, "plan-group");
        Expect("the same target listed twice (case differs)", f => { var s = Basic(); s.Groups.Add(GroupSpec.File("scripts/frontendscripts.SBN", Bytes("again"))); return s; }, "plan-group");
        Expect("the same archive member changed twice", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (f.BaseKey("Base", 0), "replace", Bytes("a")), (f.BaseKey("Base", 0), "replace", Bytes("b"))); return s; }, "plan-group");
        Expect("hash fields written in uppercase", f => { var s = Basic(); s.Groups[0].UppercaseHashes = true; return s; }, "plan-group");
        Expect("file replacement made for a different original", f => { var s = Basic(); s.Groups[0].WrongBase = true; return s; }, "base-mismatch");
        Expect("archive replacement made for a different original", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (f.BaseKey("Base", 0), "replace", Bytes("a"))); s.Groups[0].WrongBase = true; return s; }, "base-mismatch");
        Expect("replaced member does not exist in the game file", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (0xDEAD0001u, "replace", Bytes("a"))); return s; }, "member-missing");
        Expect("added member already exists in the game file", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (f.BaseKey("Base", 1), "add", Bytes("a"))); return s; }, "member-exists");
        Expect("the archive promise (composed hash) is wrong", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (f.BaseKey("Base", 0), "replace", Bytes("a"))); s.Groups[0].WrongComposed = true; return s; }, "composed-mismatch");
        Expect("the mod changes a file the game does not have", f => { var s = Basic(); s.Groups[0] = GroupSpec.Xpac("Resource/Missing.xpac", (1u, "add", Bytes("a"))); return s; }, "base-missing");
        Expect("a foreign (other-loader) mod.ini", f => { var s = Basic(); s.ManifestText = "[Main]\r\nID=SomeSA2Mod\r\nName=Other\r\nDLLFile=x.dll\r\n"; return s; }, "manifest-foreign");

        T.Run("deep check passes on a correct archive and distinguishes a wrong one (negative control)", () =>
        {
            using var fx = new Fx("pk");
            var good = Basic("good", "Good"); good.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("r")), (0x5555u, "add", Bytes("a")));
            var m1 = fx.WriteMod(good);
            var (p1, _) = Check(fx, m1);
            T.NoBlockers(p1.AllIssues, "good deep");
            var bad = Basic("bad", "Bad"); bad.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("r"))); bad.Groups[0].WrongComposed = true;
            var m2 = fx.WriteMod(bad);
            var (p2, _) = Check(fx, m2);
            T.HasCode(p2.AllIssues, "composed-mismatch", "bad deep");
            // without the deep rebuild the bad promise is not noticed: proves the deep step is what catches it
            var shallow = PackageInspector.Inspect(m2.Dir);
            var vr = PackageVerifier.Verify(shallow, new VerifyOptions { Cache = new HashCache(), GameRoot = fx.Game, Deep = false });
            T.False(vr.Issues.Any(i => i.Code == "composed-mismatch"), "shallow check cannot see it");
            fx.Cleanup();
        });

        T.Run("independent in-memory rebuild agrees with the streaming composer for every combination of add/replace", () =>
        {
            using var fx = new Fx("pk");
            var rng = new Random(1234);
            for (int round = 0; round < 25; round++)
            {
                var ops = new List<(uint, string, byte[])>();
                foreach (var k in fx.BaseArchives["Base"].Keys.Where(_ => rng.Next(3) == 0)) ops.Add((k, "replace", new byte[rng.Next(0, 5000)]));
                for (int a = 0; a < rng.Next(0, 4); a++) ops.Add(((uint)(0x90000 + a + round * 10), "add", new byte[rng.Next(0, 4200)]));
                if (ops.Count == 0) ops.Add((0x90000u, "add", new byte[10]));
                var spec = Basic("rb" + round, "RB" + round);
                spec.Groups[0] = GroupSpec.Xpac("Resource/Base.xpac", ops.ToArray());
                var m = fx.WriteMod(spec);
                var (pkg, _) = Check(fx, m);
                T.NoBlockers(pkg.AllIssues, $"round {round}");
            }
            fx.Cleanup();
        });

        T.Run("a link (junction) inside the package is refused, not followed", () =>
        {
            using var fx = new Fx("pk");
            var outside = Path.Combine(fx.Root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "evil.bin"), Bytes("outside bytes"));
            var spec = Basic();
            var m = fx.WriteMod(spec);
            Directory.Delete(Path.Combine(m.Dir, "payloads"), true);
            Fs.MakeJunction(Path.Combine(m.Dir, "payloads"), outside);
            var (pkg, _) = Check(fx, m);
            T.True(pkg.AllIssues.Any(i => i.Severity == Severity.Blocker && i.Code is "plan-path" or "plan-group"), "payload through a junction must raise a blocker; got " + string.Join(",", pkg.AllIssues.Select(i => i.Code)));
            fx.Cleanup();
        });

        T.Run("a mod folder that is itself a junction is reported and not inspected", () =>
        {
            using var fx = new Fx("pk");
            var real = Path.Combine(fx.Root, "real-mod");
            var spec = Basic(); spec.Folder = "real-mod";
            fx.WriteMod(spec, fx.Root);
            var link = Path.Combine(fx.Mods, "Linked");
            Fs.MakeJunction(link, real);
            var g = GameFolderScanner.Scan(fx.Game);
            T.True(g.Folders.Single().IsLink, "flagged as link");
            T.HasCode(g.Issues, "folder-link", "game issue");
            T.True(g.Folders.Single().Package is null, "not inspected");
            fx.Cleanup();
        });

        T.Run("artifact listings: a listed file that is here must match; one that lives elsewhere (package root) or is absent is not a problem; unsafe paths are flagged", () =>
        {
            using var fx = new Fx("pk");
            var spec = Basic("art", "Art"); spec.ExtraFiles["README.txt"] = "readme text";
            var m = fx.WriteMod(spec);
            string Json(params (string path, string sha)[] items) => "{\"id\":\"art\",\"artifacts\":[" + string.Join(",", items.Select(i => $"{{\"path\":\"{i.path}\",\"sha256\":\"{i.sha}\",\"bytes\":1}}")) + "]}";
            var good = Sha.OfBytes(Encoding.ASCII.GetBytes("readme text"));
            File.WriteAllText(Path.Combine(m.Dir, SidecarReader.FileName), Json(("README.txt", good), ("Resource/Movie/not-here.usm", new string('a', 64))));
            var pkg = PackageInspector.Inspect(m.Dir);
            var vr = PackageVerifier.Verify(pkg, new VerifyOptions { Cache = new HashCache(), Fresh = true });
            T.False(vr.Issues.Any(i => i.Code.StartsWith("artifact")), "present+matching and absent entries raise nothing: " + string.Join("|", vr.Issues.Select(i => i.Message)));
            File.WriteAllText(Path.Combine(m.Dir, SidecarReader.FileName), Json(("README.txt", new string('b', 64))));
            var bad = PackageVerifier.Verify(PackageInspector.Inspect(m.Dir), new VerifyOptions { Cache = new HashCache(), Fresh = true });
            T.HasCode(bad.Issues, "artifact-hash", "a present file that differs");
            File.WriteAllText(Path.Combine(m.Dir, SidecarReader.FileName), Json(("../outside.txt", good)));
            var unsafePath = PackageVerifier.Verify(PackageInspector.Inspect(m.Dir), new VerifyOptions { Cache = new HashCache(), Fresh = true });
            T.HasCode(unsafePath.Issues, "artifact-path", "an unsafe path");
            // a file at the package root (outside the mod folder) is found there
            var root = Path.Combine(fx.Root, "pkgroot"); Directory.CreateDirectory(Path.Combine(root, "mods"));
            var inner = fx.WriteMod(Basic("art2", "Art2"), Path.Combine(root, "mods"));
            File.WriteAllText(Path.Combine(root, "TOP.txt"), "top file");
            File.WriteAllText(Path.Combine(inner.Dir, SidecarReader.FileName), Json(("TOP.txt", new string('c', 64))).Replace("\"art\"", "\"art2\""));
            var rooted = PackageInspector.Inspect(inner.Dir, packageRoot: root);
            T.HasCode(PackageVerifier.Verify(rooted, new VerifyOptions { Cache = new HashCache(), Fresh = true }).Issues, "artifact-hash", "package-root file is checked too");
            fx.Cleanup();
        });

        T.Run("sidecar metadata is parsed, sanitised and cross-checked", () =>
        {
            using var fx = new Fx("pk");
            var spec = Basic("meta-mod", "Meta Mod", "2.1.0");
            spec.SidecarJson = "{\"manifest_schema\":\"allstars-mod/1\",\"id\":\"meta-mod\",\"name\":\"Fancy\\u0007Name\",\"version\":\"2.1.0\",\"summary\":\"Does things\",\"status\":{\"stage\":\"CANDIDATE_OFFLINE_VERIFIED\"},\"requires\":[{\"id\":\"other\",\"versions\":\">=1.0\"}],\"conflicts\":[{\"with\":\"banjo-kazooie <= 0.1.0-alpha.6\",\"effect\":\"bad\"},{\"with\":\"any mod writing 0x545434\"}],\"capabilities_exclusive\":[\"native:x\"],\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"0x545434\",\"length\":8}]},\"persistence\":{\"statement\":\"Saved things stay. Second sentence here.\"}}";
            var m = fx.WriteMod(spec);
            var pkg = PackageInspector.Inspect(m.Dir);
            var sc = pkg.Sidecar!;
            T.False(sc.Name!.Contains('\a'), "control characters stripped");
            T.Eq("CANDIDATE_OFFLINE_VERIFIED", sc.Stage, "raw stage kept");
            T.Eq("Offline tested · game test pending", StageText.Describe(sc.Stage).Short, "friendly stage");
            T.Eq(1, sc.Requires.Count, "requires");
            T.Eq(1, sc.ConflictsWith.Count, "machine-readable conflict parsed");
            T.Eq("banjo-kazooie", sc.ConflictsWith[0].Id, "conflict id");
            T.Eq("<=0.1.0-alpha.6", sc.ConflictsWith[0].Versions, "conflict version");
            T.Eq(1, sc.FreeTextConflicts.Count, "free-text conflict kept as a note");
            T.Eq(1u, (uint)sc.NativePatches.Count, "native patch");
            T.Eq(0x545434u, sc.NativePatches[0].Va, "patch address");
            fx.Cleanup();
        });

        T.Run("a sidecar for another mod is flagged and a malformed one never breaks inspection", () =>
        {
            using var fx = new Fx("pk");
            var spec = Basic("real-id", "R"); spec.SidecarJson = "{\"id\":\"imposter\",\"name\":\"X\"}";
            var m = fx.WriteMod(spec);
            var pkg = PackageInspector.Inspect(m.Dir);
            T.HasCode(pkg.Issues, "sidecar-id", "imposter");
            var spec2 = Basic("real-2", "R2"); spec2.SidecarJson = "{ not json";
            var m2 = fx.WriteMod(spec2);
            var pkg2 = PackageInspector.Inspect(m2.Dir);
            T.HasCode(pkg2.Issues, "sidecar-unreadable", "malformed");
            // Format 1: a damaged description may hold a requirement or conflict, so it fails closed (Unknown, blocking in the manager) instead of reading as "no rules".
            T.True(pkg2.HasBlockers && IssueInfo.IsUnknown(pkg2.Issues.First(i => i.Code == "sidecar-unreadable")), "damaged description file is an unconfirmed (not definite) blocker");
            File.Delete(Path.Combine(m2.Dir, "allstars-mod.json"));
            T.False(PackageInspector.Inspect(m2.Dir).HasBlockers, "removing the damaged description leaves a plain, usable loader mod");
            fx.Cleanup();
        });

        T.Run("disabled manifest states: unique, ambiguous, none, and look-alikes are not adopted", () =>
        {
            using var fx = new Fx("pk");
            var one = Basic("one", "One"); one.ManifestFileName = "mod.ini.disabled-for-X";
            var m1 = fx.WriteMod(one);
            var p1 = PackageInspector.Inspect(m1.Dir);
            T.Eq(ManifestState.Disabled, p1.Manifest!.State, "single disabled");
            var two = Basic("two", "Two"); two.ManifestFileName = "mod.ini.disabled-a";
            var m2 = fx.WriteMod(two);
            File.Copy(m2.ManifestPath, Path.Combine(m2.Dir, "mod.ini.disabled-b"));
            var p2 = PackageInspector.Inspect(m2.Dir);
            T.Eq(ManifestState.DisabledAmbiguous, p2.Manifest!.State, "ambiguous");
            T.False(p2.Manifest.ChoiceMade, "no choice yet");
            var p2c = PackageInspector.Inspect(m2.Dir, "mod.ini.disabled-b");
            T.True(p2c.Manifest!.ChoiceMade && p2c.Manifest.FileName == "mod.ini.disabled-b", "choice honoured");
            var three = Basic("three", "Three"); three.ManifestFileName = "mod.ini.bak";
            var m3 = fx.WriteMod(three);
            var p3 = PackageInspector.Inspect(m3.Dir);
            T.Eq(ManifestState.NoManifest, p3.Manifest!.State, "mod.ini.bak is not a disabled manifest");
            T.Has(p3.Manifest.OtherManifestLike, "mod.ini.bak", "listed but ignored");
            var four = Basic("four", "Four");
            var m4 = fx.WriteMod(four);
            File.Copy(m4.ManifestPath, Path.Combine(m4.Dir, "mod.ini.disabled-old"));
            var p4 = PackageInspector.Inspect(m4.Dir);
            T.Eq(ManifestState.Active, p4.Manifest!.State, "mod.ini wins");
            T.Has(p4.Manifest.Alternates, "mod.ini.disabled-old", "alternate remembered");
            fx.Cleanup();
        });

        T.Run("unknown files, comments and encodings in mod folders are untouched by inspection and verification", () =>
        {
            using var fx = new Fx("pk");
            var spec = Basic("keep", "Keep"); spec.ConfigFiles["config.ini"] = "; note\r\n[A]\r\nx=1\r\n"; spec.ExtraFiles["notes/readme.txt"] = "hello"; spec.ExtraFiles["weird.bin"] = "zz";
            var m = fx.WriteMod(spec);
            var before = Fs.Snapshot(fx.Root);
            Check(fx, m);
            GameFolderScanner.Scan(fx.Game);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(fx.Root)), "scan/verify wrote nothing");
            fx.Cleanup();
        });
    }
}
