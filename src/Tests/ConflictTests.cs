using System.Text;

namespace AllStarsModManager.Tests;

public static class ConflictTests
{
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    public static List<Candidate> Cands(params BuiltMod[] mods) => mods.Select((m, i) =>
    {
        var pkg = PackageInspector.Inspect(m.Dir);
        return new Candidate { Key = "k" + i, Pkg = pkg, FolderName = pkg.FolderName, Installed = true };
    }).ToList();

    static ModSpec Spec(string id, params GroupSpec[] groups) => new() { Id = id, Name = id, Folder = id, Groups = groups.ToList(), Version = "1.0.0" };

    static ConflictReport Run(Fx fx, params ModSpec[] specs) => ConflictAnalyzer.Analyze(Cands(specs.Select(s => fx.WriteMod(s)).ToArray()));

    static void Kinds(ConflictReport r, string what, params ConflictKind[] expected)
    {
        var got = r.Conflicts.Select(c => c.Kind).OrderBy(k => k).ToArray();
        var want = expected.OrderBy(k => k).ToArray();
        if (!got.SequenceEqual(want)) throw new TestFailure($"{what}: expected [{string.Join(",", want)}] got [{string.Join(",", got)}]");
    }

    public static void Register()
    {
        T.Area("conflicts");

        T.Run("archive members: different members of the same archive combine (positive control)", () =>
        {
            using var fx = new Fx("cf");
            var r = Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("A")))), Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 1), "replace", Bytes("B")))));
            Kinds(r, "disjoint members");
            fx.Cleanup();
        });

        T.Run("archive members: the same member conflicts, even with byte-identical replacement data", () =>
        {
            using var fx = new Fx("cf");
            var k = fx.BaseKey("Base", 2);
            var r = Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("SAME")))), Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("SAME")))));
            Kinds(r, "identical bytes", ConflictKind.SharedArchiveMember);
            T.True(r.Conflicts[0].LoaderEnforced && r.HasBlockers, "enforced by the loader");
            T.Has(new[] { r.Conflicts[0].Explanation }, "even when the replacement data is identical", "explanation says why");
            fx.Cleanup();
        });

        T.Run("archive members: numeric and hexadecimal keys (either case) are the same key", () =>
        {
            using var fx = new Fx("cf");
            var k = fx.BaseKey("Base", 3);
            foreach (var (sa, sb) in new[] { (KeyStyle.Number, KeyStyle.Hex), (KeyStyle.Hex, KeyStyle.HexLower), (KeyStyle.Number, KeyStyle.HexLower) })
            {
                Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
                var a = Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("A")))); a.Groups[0].Ops[0].Style = sa;
                var b = Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("B")))); b.Groups[0].Ops[0].Style = sb;
                Kinds(Run(fx, a, b), $"{sa}/{sb}", ConflictKind.SharedArchiveMember);
            }
            fx.Cleanup();
        });

        T.Run("archive members: an add of the same new key by two mods conflicts", () =>
        {
            using var fx = new Fx("cf");
            Kinds(Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (0x70000u, "add", Bytes("A")))), Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (0x70000u, "add", Bytes("B"))))), "same added key", ConflictKind.SharedArchiveMember);
            fx.Cleanup();
        });

        T.Run("archives written for different originals cannot be combined", () =>
        {
            using var fx = new Fx("cf");
            var b = Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 1), "replace", Bytes("B")))); b.Groups[0].BaseOverrideHash = Sha.OfBytes(Bytes("other original"));
            Kinds(Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("A")))), b), "different bases", ConflictKind.DifferentBaseArchive);
            fx.Cleanup();
        });

        T.Run("targets are compared ignoring capitals and slash style", () =>
        {
            using var fx = new Fx("cf");
            var k = fx.BaseKey("Base", 4);
            var b = Spec("b", GroupSpec.Xpac("resource\\BASE.XPAC", (k, "replace", Bytes("B"))));
            Kinds(Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("A")))), b), "same member different spelling", ConflictKind.SharedArchiveMember);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var c = Spec("c", GroupSpec.Xpac("resource\\BASE.XPAC", (fx.BaseKey("Base", 5), "replace", Bytes("C"))));
            Kinds(Run(fx, Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (k, "replace", Bytes("A")))), c), "disjoint members different spelling");
            fx.Cleanup();
        });

        T.Run("a whole-file replacement conflicts with any other change to the same file", () =>
        {
            using var fx = new Fx("cf");
            Kinds(Run(fx, Spec("a", GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("A"))), Spec("b", GroupSpec.File("scripts/frontendscripts.sbn", Bytes("B")))), "file vs file", ConflictKind.SharedWholeFile);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Spec("a", GroupSpec.File("Resource/Base.xpac", Bytes("A"))), Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("B"))))), "file vs archive", ConflictKind.FileVersusArchive);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Spec("a", GroupSpec.Copy("Resource/Copied.bin", Bytes("A"))), Spec("b", GroupSpec.Copy("Resource/Copied.bin", Bytes("A")))), "copy vs copy (identical)", ConflictKind.SharedWholeFile);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Spec("a", GroupSpec.Copy("Resource/Base.xpac", Bytes("A"))), Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("B"))))), "copy vs archive", ConflictKind.SharedWholeFile);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Spec("a", GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("A"))), Spec("b", GroupSpec.File("Scripts/Other.sbn", Bytes("B")))), "different files (control)");
            fx.Cleanup();
        });

        T.Run("shader cache targets are exclusive, even with identical contents; different targets are fine", () =>
        {
            using var fx = new Fx("cf");
            Kinds(Run(fx, Spec("a", GroupSpec.Rtsc("ShaderCache/x~PS_46.rtsc", Bytes("seed"))), Spec("b", GroupSpec.Rtsc("shadercache\\X~ps_46.RTSC", Bytes("seed")))), "same cache", ConflictKind.SharedShaderCache);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Spec("a", GroupSpec.Rtsc("ShaderCache/x~PS_46.rtsc", Bytes("seed"))), Spec("b", GroupSpec.Rtsc("ShaderCache/y~PS_46.rtsc", Bytes("seed")))), "different caches (control)");
            fx.Cleanup();
        });

        T.Run("duplicate mod IDs are a loader-enforced conflict; IDs differing only by case are only noted", () =>
        {
            using var fx = new Fx("cf");
            var a = Spec("same-id"); var b = Spec("same-id"); b.Folder = "other folder";
            Kinds(Run(fx, a, b), "duplicate id", ConflictKind.DuplicateId);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var c = Spec("Case-Id"); var d = Spec("case-id"); d.Folder = "d";
            var r = Run(fx, c, d);
            Kinds(r, "case-only difference");
            T.True(r.Notes.Any(n => n.Title.Contains("letter case")), "noted");
            fx.Cleanup();
        });

        T.Run("three mods: two compose, the third conflicts with the combination, and the right pairs are named", () =>
        {
            using var fx = new Fx("cf");
            var a = Spec("a", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("A"))));
            var b = Spec("b", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 1), "replace", Bytes("B"))));
            var c = Spec("c", GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 1), "replace", Bytes("C"))), GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("C")));
            var r = Run(fx, a, b, c);
            Kinds(r, "three", ConflictKind.SharedArchiveMember);
            var parties = r.Conflicts[0].Parties;
            T.True(parties.Contains("k1") && parties.Contains("k2") && !parties.Contains("k0"), "b and c named, not a");
            fx.Cleanup();
        });

        T.Run("declared requirements: missing, version too old, satisfied", () =>
        {
            using var fx = new Fx("cf");
            var needs = Spec("needs"); needs.SidecarJson = "{\"id\":\"needs\",\"requires\":[{\"id\":\"base-mod\",\"versions\":\">=0.1.0-alpha.7\"}]}";
            var old = Spec("base-mod"); old.Version = "0.1.0-alpha.6";
            var good = Spec("base-mod"); good.Version = "0.1.0-alpha.7";
            Kinds(Run(fx, needs), "missing", ConflictKind.MissingRequirement);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, needs, old), "too old", ConflictKind.MissingRequirement);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, needs, good), "satisfied");
            fx.Cleanup();
        });

        T.Run("declared conflicts honour the version range", () =>
        {
            using var fx = new Fx("cf");
            var ryo = Spec("ryo"); ryo.SidecarJson = "{\"id\":\"ryo\",\"conflicts\":[{\"with\":\"banjo <= 0.1.0-alpha.6\",\"effect\":\"adapter mismatch\"}]}";
            var b6 = Spec("banjo"); b6.Version = "0.1.0-alpha.6";
            var b7 = Spec("banjo"); b7.Version = "0.1.0-alpha.7";
            Kinds(Run(fx, ryo, b6), "alpha.6 conflicts", ConflictKind.DeclaredConflict);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, ryo, b7), "alpha.7 does not");
            fx.Cleanup();
        });

        T.Run("exclusive capabilities, native patch overlaps and loose-file collisions", () =>
        {
            using var fx = new Fx("cf");
            ModSpec Cap(string id, string cap) { var s = Spec(id); s.SidecarJson = "{\"id\":\"" + id + "\",\"capabilities_exclusive\":[\"" + cap + "\"]}"; return s; }
            Kinds(Run(fx, Cap("a", "native:hook"), Cap("b", "native:hook")), "same capability", ConflictKind.ExclusiveCapability);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Cap("a", "native:one"), Cap("b", "native:two")), "different capabilities");
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            ModSpec Patch(string id, string va, int len) { var s = Spec(id); s.SidecarJson = "{\"id\":\"" + id + "\",\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"" + va + "\",\"length\":" + len + "}]}}"; return s; }
            Kinds(Run(fx, Patch("a", "0x545434", 8), Patch("b", "0x545438", 4)), "overlapping patches", ConflictKind.NativePatchOverlap);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Patch("a", "0x545434", 8), Patch("b", "0x54543C", 4)), "adjacent patches");
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            ModSpec Loose(string id, string sha) { var s = Spec(id); s.SidecarJson = "{\"id\":\"" + id + "\",\"loose_assets\":[{\"target\":\"Resource/Movie/X.usm\",\"sha256\":\"" + sha + "\",\"bytes\":3}]}"; return s; }
            Kinds(Run(fx, Loose("a", new string('1', 64)), Loose("b", new string('2', 64))), "different loose file", ConflictKind.LooseAssetCollision);
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            Kinds(Run(fx, Loose("a", new string('1', 64)), Loose("b", new string('1', 64))), "same loose file is shared");
            fx.Cleanup();
        });

        T.Run("notes: native interactions are never promised safe; tested pairs are shown as offline-tested only", () =>
        {
            using var fx = new Fx("cf");
            var a = Spec("alpha"); a.SidecarJson = "{\"id\":\"alpha\",\"composes_with\":[{\"id\":\"beta\",\"versions\":\">=1.0.0\",\"evidence\":\"tests/x.json\"}]}";
            var b = Spec("beta"); var c = Spec("gamma");
            var r = Run(fx, a, b, c);
            Kinds(r, "no data conflicts");
            T.True(r.Notes.Any(n => n.Title.Contains("tested together offline")), "offline-tested pair noted");
            var unknown = r.Notes.First(n => n.Title.Contains("Native code interactions are unknown"));
            T.Has(new[] { unknown.Text }, "gamma", "untested pair named");
            T.False(r.Notes.Any(n => n.Text.Contains("works together") || n.Text.Contains("safe")), "no reassurance language");
            fx.Cleanup();
        });

        T.Run("startup order: Metal Sonic first, then ordinal folder-name order; the manager exposes no ordering control", () =>
        {
            var items = new[] { "zeta", "Alpha", "alpha", "Metal", "beta" };
            var ordered = StartupOrder.Compute(items, s => s, s => s == "Metal" ? "metal-sonic" : s);
            T.True(ordered.SequenceEqual(new[] { "Metal", "Alpha", "alpha", "beta", "zeta" }), "got " + string.Join(",", ordered));
            // The service surface has no reorder/priority API: ordering is informational (reflection guard against adding a cosmetic control).
            var names = typeof(ManagerService).GetMethods().Select(m => m.Name.ToLowerInvariant());
            T.False(names.Any(n => n.Contains("priority") || n.Contains("reorder") || n.Contains("loadorder") || n.Contains("moveup") || n.Contains("movedown")), "no ordering API");
        });
    }
}
