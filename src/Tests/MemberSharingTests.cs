using System.Text;

namespace AllStarsModManager.Tests;

/// <summary>
/// ChaoGarage 1.2.0: two mods may change the same archive member when an operation says how ("variants", "optional"). The cases follow the loader's
/// merge_virtual_xpac() (virtual_xpac.h): a fitting variant of either side first (two different ones: refused); then the later mod's optional change gives
/// way, then the earlier one's; otherwise refused. Mods are folded in folder-name order; a variant-made combination combines further only through a variant.
/// </summary>
public static class MemberSharingTests
{
    static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    static LoaderEvidence V12 => LoaderEvidence.Assume(LoaderProfiles.Find("chaogarage-1.2.0")!);
    static LoaderEvidence V11 => LoaderEvidence.Assume(LoaderProfiles.Find("chaogarage-1.1.0")!);
    static LoaderEvidence Unrecognized => new() { Knowledge = LoaderKnowledge.Unrecognized, HostSha = new string('1', 64), ProxySha = new string('2', 64) };

    /// <summary>One mod changing <paramref name="key"/> of Resource/Base.xpac.</summary>
    static ModSpec Mod(string folder, uint key, byte[] data, bool? optional = null, params (byte[] Over, byte[] Data)[] variants)
    {
        var g = GroupSpec.Xpac("Resource/Base.xpac", (key, "replace", data));
        g.Ops[0].Optional = optional;
        g.Ops[0].Variants.AddRange(variants);
        return new ModSpec { Id = folder.ToLowerInvariant(), Name = folder, Folder = folder, Version = "1.0.0", Groups = { g } };
    }

    static Candidate C(Fx fx, ModSpec s)
    {
        var pkg = PackageInspector.Inspect(fx.WriteMod(s).Dir);
        T.NoBlockers(pkg.Issues, s.Folder + " reads cleanly");
        return new Candidate { Key = s.Folder, Pkg = pkg, FolderName = s.Folder, Installed = true };
    }

    static ConflictReport Run(LoaderEvidence ev, params Candidate[] set) => ConflictAnalyzer.Analyze(set, ev);
    static List<Conflict> Members(ConflictReport r) => r.Conflicts.Where(c => c.Kind == ConflictKind.SharedArchiveMember).ToList();
    static string Combined(ConflictReport r) => r.Notes.SingleOrDefault(n => n.Code == "shared-member-combined")?.Text ?? "";

    public static void Register()
    {
        T.Area("member-sharing");

        T.Run("sharing: the later mod's variant made for the earlier mod's exact content is used; no conflict, and a note says how", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var r = Run(V12, C(fx, Mod("Alpha", k, B("A"))), C(fx, Mod("Beta", k, B("B"), null, (B("A"), B("B over A")))));
            T.Eq(0, Members(r).Count, "no member conflict");
            T.Has(new[] { Combined(r) }, "Beta 1.0.0's version made for Alpha 1.0.0's change is used", "note names the variant");
            fx.Cleanup();
        });

        T.Run("sharing: the earlier mod's variant made for the later mod's content is used too", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var r = Run(V12, C(fx, Mod("Alpha", k, B("A"), null, (B("B"), B("A over B")))), C(fx, Mod("Beta", k, B("B"))));
            T.Eq(0, Members(r).Count, "no member conflict");
            T.Has(new[] { Combined(r) }, "Alpha 1.0.0's version made for Beta 1.0.0's change is used", "note");
            fx.Cleanup();
        });

        T.Run("sharing: a variant made for other content (the other mod updated) does not fit; without optional the pair is refused", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var r = Run(V12, C(fx, Mod("Alpha", k, B("A version 2"))), C(fx, Mod("Beta", k, B("B"), null, (B("A version 1"), B("B over A1")))));
            var m = Members(r).Single();
            T.True(m.Certainty == Certainty.Definite && m.LoaderEnforced && m.Severity == Severity.Blocker && m.Code == "shared-archive-member", "definite, loader-enforced");
            T.Has(new[] { m.Explanation }, "neither says how to combine", "explanation");
            T.True(m.Parties.SequenceEqual(new[] { "Alpha", "Beta" }), "parties");
            fx.Cleanup();
        });

        T.Run("sharing: two mods that each bring a different version made for the other's content are refused, not guessed between", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var r = Run(V12, C(fx, Mod("Alpha", k, B("A"), true, (B("B"), B("A over B")))), C(fx, Mod("Beta", k, B("B"), true, (B("A"), B("B over A")))));
            var m = Members(r).Single();
            T.Eq("shared-archive-member-two-versions", m.Code, "code");
            T.True(m.LoaderEnforced && m.Certainty == Certainty.Definite, "definite even though both are optional (the loader throws before looking at optional)");
            // the same version from both sides is not a choice: it is used
            var same = Run(V12, C(fx, Mod("Gamma", k, B("G"), null, (B("D"), B("both")))), C(fx, Mod("Delta", k, B("D"), null, (B("G"), B("both")))));
            T.Eq(0, Members(same).Count, "identical versions combine");
            fx.Cleanup();
        });

        T.Run("sharing: an optional change gives way: the later mod's first, else the earlier one's; a fitting variant comes before optional", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var later = Run(V12, C(fx, Mod("Alpha", k, B("A"))), C(fx, Mod("Beta", k, B("B"), true)));
            T.Eq(0, Members(later).Count, "later optional: no conflict");
            T.Has(new[] { Combined(later) }, "Beta 1.0.0's optional change gives way; Alpha 1.0.0's is used", "later gives way");
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var earlier = Run(V12, C(fx, Mod("Alpha", k, B("A"), true)), C(fx, Mod("Beta", k, B("B"))));
            T.Has(new[] { Combined(earlier) }, "Alpha 1.0.0's optional change gives way; Beta 1.0.0's is used", "earlier gives way");
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var both = Run(V12, C(fx, Mod("Alpha", k, B("A"), true)), C(fx, Mod("Beta", k, B("B"), true)));
            T.Has(new[] { Combined(both) }, "Beta 1.0.0's optional change gives way; Alpha 1.0.0's is used", "both optional: the later one gives way");
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var variantFirst = Run(V12, C(fx, Mod("Alpha", k, B("A"), true)), C(fx, Mod("Beta", k, B("B"), true, (B("A"), B("B over A")))));
            T.Has(new[] { Combined(variantFirst) }, "version made for", "variant before optional");
            T.False(Combined(variantFirst).Contains("gives way"), "nothing gave way");
            fx.Cleanup();
        });

        T.Run("sharing: folder-name order decides which is 'later' (not the order given, and metal-sonic is not moved first here)", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var z = Mod("Zed", k, B("Z"), true); z.Id = "metal-sonic";
            var a = Mod("Abe", k, B("A"), true);
            var r = Run(V12, C(fx, z), C(fx, a));
            T.Has(new[] { Combined(r) }, "Zed 1.0.0's optional change gives way; Abe 1.0.0's is used", "Zed sorts later, so it gives way");
            fx.Cleanup();
        });

        T.Run("sharing: three mods: a variant-made combination combines further only through a variant made for the combined content", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var a = C(fx, Mod("Alpha", k, B("A")));
            var b = C(fx, Mod("Beta", k, B("B"), null, (B("A"), B("B over A"))));
            var stale = C(fx, Mod("Gamma", k, B("G"), true, (B("A"), B("G over A"))));     // made for Alpha alone: does not fit the combination
            var r = Run(V12, a, b, stale);
            T.Has(new[] { Combined(r) }, "Gamma 1.0.0's optional change gives way", "an optional third mod gives way to the combination");
            var strict = C(fx, Mod("Gamma2", k, B("G"), null, (B("A"), B("G over A"))));
            var m = Members(Run(V12, a, b, strict)).Single();
            T.True(m.Parties.SequenceEqual(new[] { "Alpha", "Beta", "Gamma2" }) && m.Explanation.Contains("Alpha 1.0.0 + Beta 1.0.0 and Gamma2 1.0.0"), "refused against the combination: " + m.Explanation);
            var fits = C(fx, Mod("Gamma3", k, B("G"), null, (B("B over A"), B("G over A+B"))));
            T.Eq(0, Members(Run(V12, a, b, fits)).Count, "a variant made for the combined content fits");
            fx.Cleanup();
        });

        T.Run("sharing: when an optional change gave way, the remaining change keeps its own variants (and a member nobody else changes is untouched)", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0); var k2 = fx.BaseKey("Base", 1);
            var aSpec = Mod("Alpha", k, B("A"), null, (B("C"), B("A over C")));
            aSpec.Groups[0].Ops.Add(new OpSpec { Key = k2, Payload = B("A2") });
            var r = Run(V12, C(fx, aSpec), C(fx, Mod("Beta", k, B("B"), true)), C(fx, Mod("Gamma", k, B("C"))));
            T.Eq(0, Members(r).Count, "Beta gives way, then Alpha's variant made for Gamma fits");
            T.Has(new[] { Combined(r) }, "Alpha 1.0.0's version made for Gamma 1.0.0's change is used", "remaining change kept its variants");
            fx.Cleanup();
        });

        T.Run("sharing: ChaoGarage 1.1.0 ignores variants and optional, so the pair is refused as before", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var r = Run(V11, C(fx, Mod("Alpha", k, B("A"), true)), C(fx, Mod("Beta", k, B("B"), true, (B("A"), B("B over A")))));
            var m = Members(r).Single();
            T.True(m.Certainty == Certainty.Definite && m.LoaderEnforced && m.Code == "shared-archive-member", "definite");
            T.Has(new[] { m.Explanation }, "even when the replacement data is identical", "the earlier explanation");
            T.Eq("", Combined(r), "no combination note");
            fx.Cleanup();
        });

        T.Run("sharing: with the loader not confirmed, a pair only 1.2.0 combines is 'could not be confirmed' (blocker when the build is unrecognised), and a pair every loader refuses stays definite", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0); var k2 = fx.BaseKey("Base", 1);
            var a = C(fx, Mod("Alpha", k, B("A")));
            var b = C(fx, Mod("Beta", k, B("B"), null, (B("A"), B("B over A"))));
            var unrec = Members(Run(Unrecognized, a, b)).Single();
            T.True(unrec.Certainty == Certainty.Unknown && !unrec.LoaderEnforced && unrec.Severity == Severity.Blocker && unrec.Code == "shared-archive-member-unconfirmed", "unrecognised: unknown blocker");
            T.Has(new[] { unrec.Title }, "only with ChaoGarage 1.2.0 or newer", "title");
            var none = Members(Run(LoaderEvidence.NotInspected, a, b)).Single();
            T.True(none.Certainty == Certainty.Unknown && none.Severity == Severity.Warning, "not inspected: unknown warning");
            var cSpec = Mod("Gamma", k2, B("C")); var dSpec = Mod("Delta", k2, B("D"));
            var refused = Members(Run(LoaderEvidence.NotInspected, C(fx, cSpec), C(fx, dSpec))).Single();
            T.True(refused.Certainty == Certainty.Definite && refused.LoaderEnforced && refused.Code == "shared-archive-member", "refused by every loader: definite");
            T.True(LoaderVerdict.Evaluate(new[] { a, b }).Accept && !LoaderVerdict.Evaluate(new[] { a, b }, V11).Accept, "loader verdict: accepted unless judged against 1.1.0");
            fx.Cleanup();
        });

        T.Run("sharing: plan fields are read as the 1.2.0 loader reads them (wrong types refused, never read as absent); variant payloads are checked like any payload", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            var sha = new string('a', 64);
            foreach (var (what, extra) in new[]
            {
                ("optional as text", ",\"optional\":\"yes\""), ("optional as number", ",\"optional\":1"), ("variants not a list", ",\"variants\":{}"),
                ("variant not an object", ",\"variants\":[1]"), ("variant without payload", ",\"variants\":[{\"over_payload_sha256\":\"" + sha + "\"}]"),
                ("over hash in capitals", ",\"variants\":[{\"over_payload_sha256\":\"" + sha.ToUpperInvariant() + "\",\"payload\":\"x.bin\",\"payload_sha256\":\"" + sha + "\",\"stored_size\":1}]"),
                ("over_mod not text", ",\"variants\":[{\"over_mod\":5,\"over_payload_sha256\":\"" + sha + "\",\"payload\":\"x.bin\",\"payload_sha256\":\"" + sha + "\",\"stored_size\":1}]"),
                ("variant without stored_size", ",\"variants\":[{\"over_payload_sha256\":\"" + sha + "\",\"payload\":\"x.bin\",\"payload_sha256\":\"" + sha + "\"}]")
            })
            {
                Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
                var s = Mod("Alpha", k, B("A")); s.Groups[0].Ops[0].RawExtra = extra;
                var pkg = PackageInspector.Inspect(fx.WriteMod(s).Dir);
                T.True(pkg.Issues.Any(i => i.Code == "plan-group" && i.Severity == Severity.Blocker), what + ": refused");
            }
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var dup = Mod("Alpha", k, B("A"), null, (B("X"), B("one")), (B("X"), B("two")));
            T.True(PackageInspector.Inspect(fx.WriteMod(dup).Dir).Issues.Any(i => i.Code == "plan-group" && i.Message.Contains("same content")), "two variants for the same content: refused");

            // a valid variant does not change the composed archive the plan promises; a damaged variant payload is found even though it may never be used
            Fs.DeleteTree(fx.Mods); Directory.CreateDirectory(fx.Mods);
            var ok = fx.WriteMod(Mod("Alpha", k, B("A"), true, (B("X"), B("A over X"))));
            var pkgOk = PackageInspector.Inspect(ok.Dir);
            var vr = PackageVerifier.Verify(pkgOk, new VerifyOptions { Cache = new HashCache(), GameRoot = fx.Game, Fresh = true, Deep = true });
            T.NoBlockers(vr.Issues, "plan with a variant verifies, deep");
            var op = pkgOk.Plan!.Groups[0].Ops[0];
            T.True(op.Optional && op.Variants.Count == 1 && op.Variants[0].OverSha == Sha.OfBytes(B("X")) && op.Variants[0].OverMod == "other", "fields read");
            File.WriteAllBytes(op.Variants[0].Payload.FullPath, B("A over Y"));
            var bad = PackageVerifier.Verify(PackageInspector.Inspect(ok.Dir), new VerifyOptions { Cache = new HashCache(), Fresh = true });
            T.HasCode(bad.Issues, "payload-hash", "damaged variant payload");
            File.Delete(op.Variants[0].Payload.FullPath);
            T.HasCode(PackageVerifier.Verify(PackageInspector.Inspect(ok.Dir), new VerifyOptions { Cache = new HashCache(), Fresh = true }).Issues, "payload-missing", "missing variant payload");
            fx.Cleanup();
        });

        T.Run("checker: a game folder whose loader has the 1.2.0 capabilities declares a variant-combined pair compatible; with the 1.1.0 capabilities it is a definite problem", () =>
        {
            using var fx = new Fx("ms");
            var k = fx.BaseKey("Base", 0);
            fx.WriteMod(Mod("Alpha", k, B("A")));
            fx.WriteMod(Mod("Beta", k, B("B"), null, (B("A"), B("B over A"))));
            var caps11 = string.Join(",", LoaderProfiles.Find("chaogarage-1.1.0")!.Capabilities.Select(c => "\"" + c + "\""));
            var caps12 = string.Join(",", LoaderProfiles.Find("chaogarage-1.2.0")!.Capabilities.Select(c => "\"" + c + "\""));
            var r12 = CompatCheckerTests.Cli(fx.Game, "--loader-profiles", CompatCheckerTests.ProfileFile(fx, caps12), "--json");
            var j12 = System.Text.Json.JsonDocument.Parse(r12.Out).RootElement;
            T.False(j12.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString()!.StartsWith("shared-archive-member")), "1.2.0: no member finding");
            T.True(r12.Exit is 0 or 1, "1.2.0: not a definite problem (exit " + r12.Exit + ")");
            var r11 = CompatCheckerTests.Cli(fx.Game, "--loader-profiles", CompatCheckerTests.ProfileFile(fx, caps11), "--json");
            T.Eq(2, r11.Exit, "1.1.0: definite problem");
            T.True(System.Text.Json.JsonDocument.Parse(r11.Out).RootElement.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString() == "shared-archive-member"), "1.1.0: member finding");
            fx.Cleanup();
        });
    }
}
