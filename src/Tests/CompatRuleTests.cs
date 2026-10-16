using System.Text;

namespace AllStarsModManager.Tests;

/// <summary>Cross-package rules added or hardened for format 1: requirements, conflicts, load order, loader requirements, target build.</summary>
public static class CompatRuleTests
{
    public const string HostA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ProxyA = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string ExeA = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public const string ExeB = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    public static LoaderProfile Profile(params string[] caps) =>
        new("test-host", "synthetic loader for tests", HostA, ProxyA, 1, 1, 1, caps.Length > 0 ? caps : new[] { "host-abi-1", "xpac-member-merge", "plan-format-xpac" }, "synthetic");

    public static LoaderEvidence Verified(LoaderProfile? p = null, string? exe = ExeA) =>
        new() { Knowledge = LoaderKnowledge.Verified, Profile = p ?? Profile(), HostSha = HostA, ProxySha = ProxyA, GameInspected = exe is not null, ExeShaProvider = exe is null ? null : () => exe };

    static PackageInfo Mk(Fx fx, string id, string folder, string version, string? sidecar = null, Action<ModSpec>? tweak = null)
    {
        var spec = new ModSpec { Id = id, Name = folder, Folder = folder, Version = version, SidecarJson = sidecar };
        tweak?.Invoke(spec);
        return PackageInspector.Inspect(fx.WriteMod(spec).Dir);
    }

    static Candidate C(PackageInfo p, string folder, string? key = null, bool installed = true) => new() { Key = key ?? folder, Pkg = p, FolderName = folder, Installed = installed };

    static List<Conflict> Run(LoaderEvidence? ev, params Candidate[] set) => ConflictAnalyzer.Analyze(set, ev).Conflicts;

    static Conflict Only(IEnumerable<Conflict> cs, string code) => cs.Single(c => c.Code == code);

    public static void Register()
    {
        T.Area("compat-rules");

        T.Run("requirements: a one-object 'requires' (as existing packages write it) is enforced; missing, wrong-version and right-version cases", () =>
        {
            using var fx = new Fx("cr");
            var needs = Mk(fx, "layer", "Layer", "1.0.0", "{\"requires\":{\"id\":\"base\",\"versions\":\">=2.0.0 <3.0.0\"}}");
            var baseOk = Mk(fx, "base", "Base", "2.5.0");
            var baseOld = Mk(fx, "base", "Base", "1.9.9");
            var baseNew = Mk(fx, "base", "Base", "3.0.0-rc.1");      // pre-release of 3.0.0 is still below 3.0.0
            T.Eq(0, Run(null, C(needs, "Layer"), C(baseOk, "Base")).Count(c => c.Kind == ConflictKind.MissingRequirement), "satisfied");
            var absent = Run(null, C(needs, "Layer")).Single(c => c.Kind == ConflictKind.MissingRequirement);
            T.True(absent.Severity == Severity.Blocker && absent.Certainty == Certainty.Definite && !absent.LoaderEnforced, "absent: definite, declared-only blocker");
            var wrong = Run(null, C(needs, "Layer"), C(baseOld, "Base")).Single(c => c.Kind == ConflictKind.MissingRequirement);
            T.True(wrong.Certainty == Certainty.Definite && wrong.Explanation.Contains("1.9.9"), "too old: definite");
            T.Eq(0, Run(null, C(needs, "Layer"), C(baseNew, "Base")).Count(c => c.Kind == ConflictKind.MissingRequirement), "3.0.0-rc.1 satisfies <3.0.0");
            fx.Cleanup();
        });

        T.Run("requirements: an unstated or UNKNOWN version, or a range outside the grammar, is never reported as satisfied", () =>
        {
            using var fx = new Fx("cr");
            var noVersion = Mk(fx, "base", "Base", "1.0.0", null, s => s.Version = null);
            var needsRange = Mk(fx, "a1", "A1", "1.0.0", "{\"requires\":[{\"id\":\"base\",\"versions\":\">=1.0.0\"}]}");
            var unk = Run(null, C(needsRange, "A1"), C(noVersion, "Base")).Single(c => c.Code == "requirement-version-unknown");
            T.True(unk.Certainty == Certainty.Unknown && unk.Severity == Severity.Warning, "other mod has no version: unknown, warning");
            var needsUnknown = Mk(fx, "a2", "A2", "1.0.0", "{\"requires\":[{\"id\":\"base\",\"versions\":\"UNKNOWN\"}]}");
            var baseV = Mk(fx, "base", "BaseV", "1.0.0");
            T.True(Run(null, C(needsUnknown, "A2"), C(baseV, "BaseV")).Any(c => c.Code == "requirement-version-unknown"), "author-declared UNKNOWN");
            var bad = Mk(fx, "a3", "A3", "1.0.0", "{\"requires\":[{\"id\":\"base\",\"versions\":\">=1.0 maybe\"}]}");
            var r = Run(null, C(bad, "A3"), C(baseV, "BaseV")).Single(c => c.Kind == ConflictKind.UnreadableRule);
            T.True(r.Certainty == Certainty.Unknown && r.Severity == Severity.Blocker, "invalid range fails closed (blocker, unconfirmed)");
            T.True(Run(null, C(bad, "A3")).Any(c => c.Kind == ConflictKind.UnreadableRule), "reported even when the named mod is absent");
            fx.Cleanup();
        });

        T.Run("declared conflicts: in-range is definite, out-of-range is silent, an unreadable range cannot rule the clash out", () =>
        {
            using var fx = new Fx("cr");
            var a = Mk(fx, "a", "A", "1.0.0", "{\"conflicts_with\":{\"id\":\"b\",\"versions\":\"<2.0.0\",\"reason\":\"old b breaks\"}}");
            var bOld = Mk(fx, "b", "B", "1.5.0");
            var bNew = Mk(fx, "b", "B", "2.0.0");
            var hit = Run(null, C(a, "A"), C(bOld, "B")).Single(c => c.Kind == ConflictKind.DeclaredConflict);
            T.True(hit.Certainty == Certainty.Definite && hit.Explanation.Contains("old b breaks"), "in range");
            T.Eq(0, Run(null, C(a, "A"), C(bNew, "B")).Count(c => c.Kind == ConflictKind.DeclaredConflict), "out of range");
            T.Eq(0, Run(null, C(a, "A")).Count, "other mod absent");
            var vague = Mk(fx, "v", "V", "1.0.0", "{\"conflicts_with\":[{\"id\":\"b\",\"versions\":\"1.x\"}]}");
            T.True(Run(null, C(vague, "V"), C(bNew, "B")).Any(c => c.Kind == ConflictKind.UnreadableRule && c.Certainty == Certainty.Unknown), "unreadable range");
            var noVer = Mk(fx, "b", "BNoVer", "1.0.0", null, s => s.Version = null);
            var vague2 = Mk(fx, "w", "W", "1.0.0", "{\"conflicts_with\":[{\"id\":\"b\",\"versions\":\"<2.0.0\"}]}");
            var uncertain = Run(null, C(vague2, "W"), C(noVer, "BNoVer")).Single(c => c.Kind == ConflictKind.DeclaredConflict);
            T.True(uncertain.Certainty == Certainty.Unknown && uncertain.Severity == Severity.Blocker, "other mod has no version: fail closed");
            fx.Cleanup();
        });

        T.Run("a description in a newer format applies none of its rules, and says so", () =>
        {
            using var fx = new Fx("cr");
            var future = Mk(fx, "f", "F", "1.0.0", "{\"manifest_schema\":\"allstars-mod/2\",\"requires\":[\"nobody\"],\"conflicts_with\":[\"g\"]}");
            var g = Mk(fx, "g", "G", "1.0.0");
            T.Eq(0, Run(null, C(future, "F"), C(g, "G")).Count(c => c.Kind is ConflictKind.MissingRequirement or ConflictKind.DeclaredConflict), "nothing partially applied");
            T.True(future.Issues.Any(i => i.Code == "sidecar-schema" && i.Severity == Severity.Blocker), "but the package itself is flagged");
            fx.Cleanup();
        });

        T.Run("load order: constraints are checked against the loader's real order (folder names compared character by character, Metal Sonic first)", () =>
        {
            using var fx = new Fx("cr");
            var roster = Mk(fx, "roster", "All-Stars Roster Pack", "1.0.0");
            var hangar = Mk(fx, "hangar", "Egg Hangar", "1.0.0", "{\"load_order\":{\"after\":[\"roster\"]}}");
            // "All-Stars Roster Pack" < "Egg Hangar": satisfied
            T.Eq(0, Run(null, C(roster, "All-Stars Roster Pack"), C(hangar, "Egg Hangar")).Count(c => c.Kind == ConflictKind.LoadOrder), "after: satisfied");
            // the manual-install mistake: a folder named so that it sorts first
            var early = Run(null, C(roster, "All-Stars Roster Pack"), C(hangar, "0 Egg Hangar")).Single(c => c.Kind == ConflictKind.LoadOrder);
            T.True(early.Severity == Severity.Blocker && early.Certainty == Certainty.Definite && !early.LoaderEnforced && early.Code == "load-order", "after: violated by folder name");
            T.True(early.Explanation.Contains("0 Egg Hangar") && early.Explanation.Contains("All-Stars Roster Pack"), "names the folders: " + early.Explanation);
            // ordinal, not culture or case-insensitive: capital 'Z' sorts before lowercase 'a'
            var lower = Mk(fx, "low", "alpha", "1.0.0", "{\"load_order\":{\"after\":[\"upper\"]}}");
            var upper = Mk(fx, "upper", "Zulu", "1.0.0");
            T.Eq(0, Run(null, C(lower, "alpha"), C(upper, "Zulu")).Count(c => c.Kind == ConflictKind.LoadOrder), "'alpha' sorts after 'Zulu' (capital letters come first), so 'alpha after Zulu' holds");
            var lower2 = Mk(fx, "low2", "alpha2", "1.0.0", "{\"load_order\":{\"before\":[\"upper\"]}}");
            T.Eq(1, Run(null, C(lower2, "alpha2"), C(upper, "Zulu")).Count(c => c.Kind == ConflictKind.LoadOrder), "'alpha2 before Zulu' is violated; a case-insensitive comparison would wrongly accept it");
            fx.Cleanup();
        });

        T.Run("load order: before/after, absent partners, Metal Sonic's fixed first place, contradictions and planned folder names", () =>
        {
            using var fx = new Fx("cr");
            var a = Mk(fx, "a", "A-first", "1.0.0", "{\"load_order\":{\"before\":[\"b\"]}}");
            var b = Mk(fx, "b", "B-second", "1.0.0");
            T.Eq(0, Run(null, C(a, "A-first"), C(b, "B-second")).Count(c => c.Kind == ConflictKind.LoadOrder), "before: satisfied");
            T.Eq(1, Run(null, C(a, "C-late"), C(b, "B-second")).Count(c => c.Kind == ConflictKind.LoadOrder), "before: violated when the folder name sorts later");
            T.Eq(0, Run(null, C(a, "Z")).Count(c => c.Kind == ConflictKind.LoadOrder), "a partner that is not part of the set imposes nothing");
            var metal = Mk(fx, "metal-sonic", "Zzz Metal", "1.0.0");
            var afterMetal = Mk(fx, "m1", "AAA", "1.0.0", "{\"load_order\":{\"after\":[\"metal-sonic\"]}}");
            var beforeMetal = Mk(fx, "m2", "BBB", "1.0.0", "{\"load_order\":{\"before\":[\"metal-sonic\"]}}");
            T.Eq(0, Run(null, C(metal, "Zzz Metal"), C(afterMetal, "AAA")).Count(c => c.Kind == ConflictKind.LoadOrder), "metal-sonic always starts first, whatever its folder is called");
            var impossible = Run(null, C(metal, "Zzz Metal"), C(beforeMetal, "BBB")).Single(c => c.Kind == ConflictKind.LoadOrder);
            T.True(impossible.Explanation.Contains("always starts"), "nothing can start before metal-sonic: " + impossible.Explanation);
            var p = Mk(fx, "p", "P", "1.0.0", "{\"load_order\":{\"after\":[\"q\"]}}");
            var q = Mk(fx, "q", "Q", "1.0.0", "{\"load_order\":{\"after\":[\"p\"]}}");
            var cs = Run(null, C(p, "P"), C(q, "Q")).Where(c => c.Kind == ConflictKind.LoadOrder).ToList();
            T.True(cs.Count >= 1 && cs.All(c => c.Explanation.Contains("opposite orders")), "contradictory descriptions are named as such");
            // a library mod is judged by the folder it would be installed into
            T.Eq(1, Run(null, C(b, "B-second"), C(a, "ZZ-planned", "lib:a", installed: false)).Count(c => c.Kind == ConflictKind.LoadOrder), "planned folder name is used");
            fx.Cleanup();
        });

        T.Run("loader requirements: met, unmet and unconfirmed are three different answers", () =>
        {
            using var fx = new Fx("cr");
            string Req(string inner) => "{\"manifest_schema\":\"allstars-mod/1\",\"loader\":{" + inner + "}}";
            var ok = Mk(fx, "ok", "Ok", "1.0.0", Req("\"host_abi\":1,\"capabilities\":[\"xpac-member-merge\"],\"host_sha256\":[\"" + HostA + "\"]"));
            T.Eq(0, Run(Verified(), C(ok, "Ok")).Count, "everything met by a verified profile");
            var capMissing = Mk(fx, "cm", "CapMissing", "1.0.0", Req("\"capabilities\":[\"xpac-member-merge\",\"hot-reload\"]"));
            var cm = Run(Verified(), C(capMissing, "CapMissing")).Single(c => c.Kind == ConflictKind.LoaderRequirement);
            T.True(cm.Certainty == Certainty.Definite && cm.Severity == Severity.Blocker && cm.Explanation.Contains("hot-reload") && !cm.Explanation.Contains("xpac-member-merge,"), "missing capability: definite, names only the missing one: " + cm.Explanation);
            var abi = Mk(fx, "abi", "Abi", "1.0.0", Req("\"host_abi\":2"));
            T.True(Run(Verified(), C(abi, "Abi")).Single().Explanation.Contains("version 2"), "abi mismatch");
            var pin = Mk(fx, "pin", "Pin", "1.0.0", Req("\"host_sha256\":\"" + new string('e', 64) + "\""));
            T.Eq(Certainty.Definite, Run(Verified(), C(pin, "Pin")).Single().Certainty, "build pin mismatch is definite when the host hash is known");
            var unrec = new LoaderEvidence { Knowledge = LoaderKnowledge.Unrecognized, HostSha = new string('9', 64), ProxySha = ProxyA, GameInspected = true };
            var u1 = Run(unrec, C(capMissing, "CapMissing")).Single();
            T.True(u1.Certainty == Certainty.Unknown && u1.Severity == Severity.Blocker && u1.Code == "loader-requirement-unconfirmed", "unrecognised build: unconfirmed and blocking (fail closed)");
            var u2 = Run(unrec, C(pin, "Pin")).Single();
            T.Eq(Certainty.Definite, u2.Certainty, "a hash pin can be compared even for an unrecognised build");
            var absent = new LoaderEvidence { Knowledge = LoaderKnowledge.Absent, GameInspected = true };
            var a1 = Run(absent, C(capMissing, "CapMissing")).Single();
            T.True(a1.Certainty == Certainty.Unknown && a1.Severity == Severity.Warning, "no loader files: unconfirmed, warning only (the usual 'loader missing' warning already applies)");
            var none = Run(null, C(capMissing, "CapMissing")).Single();
            T.True(none.Certainty == Certainty.Unknown && none.Severity == Severity.Warning, "nothing inspected: unconfirmed");
            var assumed = new LoaderEvidence { Knowledge = LoaderKnowledge.Assumed, Profile = Profile(), HostSha = HostA, ProxySha = ProxyA };
            T.Eq(0, Run(assumed, C(ok, "Ok")).Count, "assumed profile answers capability questions");
            T.True(Run(assumed, C(capMissing, "CapMissing")).Single().Explanation.Contains("assumed"), "and the wording says it was assumed");
            var noReq = Mk(fx, "legacy", "Legacy", "1.0.0", "{\"id\":\"legacy\",\"name\":\"x\"}");
            T.Eq(0, Run(unrec, C(noReq, "Legacy")).Count, "a package that declares nothing is not affected by an unrecognised loader (1.1.0 behaviour kept)");
            fx.Cleanup();
        });

        T.Run("target build: matching executable is silent; different or uncomparable is a non-blocking unconfirmed finding", () =>
        {
            using var fx = new Fx("cr");
            var m = Mk(fx, "t", "T", "1.0.0", "{\"target\":{\"game\":\"x\",\"executable_sha256\":\"" + ExeA + "\"}}");
            T.Eq(0, Run(Verified(exe: ExeA), C(m, "T")).Count, "match");
            var diff = Run(Verified(exe: ExeB), C(m, "T")).Single();
            T.True(diff.Code == "target-build" && diff.Certainty == Certainty.Unknown && diff.Severity == Severity.Warning, "different build");
            var none = Run(Verified(exe: null), C(m, "T")).Single();
            T.True(none.Title.Contains("not compared"), "no executable inspected");
            var multi = Mk(fx, "t2", "T2", "1.0.0", "{\"target\":{\"executable_sha256\":[\"" + ExeB + "\",\"" + ExeA + "\"]}}");
            T.Eq(0, Run(Verified(exe: ExeA), C(multi, "T2")).Count, "any listed build matches");
            fx.Cleanup();
        });

        T.Run("native interactions: two runtimes are unknown unless the descriptions record the pair; the unknown note is always coded", () =>
        {
            using var fx = new Fx("cr");
            var a = Mk(fx, "na", "NA", "1.0.0", null, s => s.Runtime = SyntheticPe.Dll("na"));
            var b = Mk(fx, "nb", "NB", "1.0.0", null, s => s.Runtime = SyntheticPe.Dll("nb"));
            var rep = ConflictAnalyzer.Analyze(new[] { C(a, "NA"), C(b, "NB") }, Verified());
            T.True(rep.Notes.Any(n => n.Code == "native-unknown"), "no recorded test");
            var a2 = Mk(fx, "na", "NA2", "1.0.0", "{\"composes_with\":[{\"id\":\"nb\",\"versions\":\"1.0.0\",\"evidence\":\"offline fixture\"}]}");
            var rep2 = ConflictAnalyzer.Analyze(new[] { C(a2, "NA2"), C(b, "NB") }, Verified());
            T.False(rep2.Notes.Any(n => n.Code == "native-unknown"), "pair recorded");
            T.True(rep2.Notes.Any(n => n.Code == "tested-together"), "and shown as offline evidence only");
            var a3 = Mk(fx, "na", "NA3", "1.0.0", "{\"composes_with\":[{\"id\":\"nb\",\"versions\":\">=9.0.0\"}]}");
            T.True(ConflictAnalyzer.Analyze(new[] { C(a3, "NA3"), C(b, "NB") }, Verified()).Notes.Any(n => n.Code == "native-unknown"), "evidence for another version does not count");
            fx.Cleanup();
        });

        T.Run("loader-enforced rules stay loader-enforced: duplicate IDs and shared archive members are never downgraded", () =>
        {
            using var fx = new Fx("cr");
            Action<ModSpec> same = s => s.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Encoding.ASCII.GetBytes("x"))));
            var d1 = Mk(fx, "dup", "Dup1", "1.0.0"); var d2 = Mk(fx, "dup", "Dup2", "1.0.0");
            var dup = Run(Verified(), C(d1, "Dup1"), C(d2, "Dup2")).Single(c => c.Kind == ConflictKind.DuplicateId);
            T.True(dup.LoaderEnforced && dup.Code == "duplicate-id" && dup.Certainty == Certainty.Definite, "duplicate id");
            var s1 = Mk(fx, "s1", "S1", "1.0.0", null, same); var s2 = Mk(fx, "s2", "S2", "1.0.0", null, same);
            var shared = Run(Verified(), C(s1, "S1"), C(s2, "S2")).Single(c => c.Kind == ConflictKind.SharedArchiveMember);
            T.True(shared.LoaderEnforced && shared.Code == "shared-archive-member", "shared member");
            fx.Cleanup();
        });

        T.Run("loader profiles: a build is recognised only when BOTH hashes equal a profile's pair; the table is validated; the built-in profile matches the constants the manager always used", () =>
        {
            var host = new string('1', 64); var proxy = new string('2', 64);
            var user = new LoaderProfile("mine", "d", host, proxy, 1, 1, 1, new[] { "host-abi-1" }, "x", UserSupplied: true);
            T.Eq("mine", LoaderProfiles.Match(host, proxy, new[] { user })?.Id, "both hashes match");
            T.True(LoaderProfiles.Match(host, new string('3', 64), new[] { user }) is null, "host only");
            T.True(LoaderProfiles.Match(new string('3', 64), proxy, new[] { user }) is null, "proxy only");
            T.True(LoaderProfiles.Match(host, null, new[] { user }) is null && LoaderProfiles.Match(null, proxy, new[] { user }) is null && LoaderProfiles.Match(null, null) is null, "a missing file never matches");
            T.True(LoaderProfiles.Match(GameFolderInfo.KnownHostSha, GameFolderInfo.KnownProxySha) is { UserSupplied: false } b && b.Id == "ssasr-host-1", "built-in pair matches the constants");
            T.True(LoaderProfiles.Match(GameFolderInfo.KnownHostSha, new string('9', 64)) is null && LoaderProfiles.Match(new string('9', 64), GameFolderInfo.KnownProxySha) is null, "built-in: one hash is not enough");
            var v10 = LoaderProfiles.Find("ssasr-host-1")!;
            T.True(v10.Capabilities.Contains("xpac-member-merge") && v10.Capabilities.Contains("bootstrap-abi-1") && v10.HostAbi == 1, "built-in capabilities (1.0)");
            T.True(LoaderProfiles.BuiltIn.All(p => p.Evidence.Contains("not proof of behaviour in the game")), "every built-in evidence text states its limit");
            // ChaoGarage 1.1.0: its capability list is the loader's own table, as listed in the shared vector file; it no longer provides bootstrap-abi-1
            var v11 = LoaderProfiles.Find("chaogarage-1.1.0")!;
            var vectorCaps = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Program.ModuleRoot, "src", "Tests", "vectors", "loader-requirements.json"))).RootElement
                .GetProperty("self").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();
            T.True(v11.HostAbi == 1 && v11.ModIniSchema == 1 && v11.PlanSchema == 1 && v11.Capabilities.SequenceEqual(vectorCaps) && !v11.Capabilities.Contains("bootstrap-abi-1"), "built-in 1.1.0 profile matches the loader's capability table");
            T.True(LoaderProfiles.Match(v11.HostSha256, v11.ProxySha256)?.Id == "chaogarage-1.1.0" && LoaderProfiles.Match(v11.HostSha256, v10.ProxySha256) is null && LoaderProfiles.Match(v10.HostSha256, v11.ProxySha256) is null, "a mixed 1.0/1.1 pair is not recognised");
            string Tbl(string profiles, string schema = "allstars-loader-profiles/1") => "{\"schema\":\"" + schema + "\",\"profiles\":[" + profiles + "]}";
            string One(string id = "p1", string h = null!, string pr = null!, string abi = "1", string caps = "[\"a\"]") => "{\"id\":\"" + id + "\",\"host_sha256\":\"" + (h ?? host) + "\",\"proxy_sha256\":\"" + (pr ?? proxy) + "\",\"host_abi\":" + abi + ",\"capabilities\":" + caps + "}";
            List<string> Problems(string json, out List<LoaderProfile> list) { var pr = new List<string>(); list = LoaderProfiles.Parse(Encoding.UTF8.GetBytes(json), true, pr); return pr; }
            T.True(Problems(Tbl(One()), out var ok).Count == 0 && ok.Count == 1 && ok[0].UserSupplied, "valid table, entries marked user-supplied");
            foreach (var (what, json) in new[]
            {
                ("wrong schema", Tbl(One(), "other/1")), ("upper-case hash", Tbl(One(h: host.Replace('1', 'A')))), ("short hash", Tbl(One(h: "abc"))), ("zero abi", Tbl(One(abi: "0"))),
                ("bad id", Tbl(One(id: "Bad Id"))), ("caps not a list", Tbl(One(caps: "\"a\""))), ("not json", "{ nope"), ("no profiles list", "{\"schema\":\"allstars-loader-profiles/1\"}"),
                ("duplicate id", Tbl(One() + "," + One(h: new string('4', 64)))), ("duplicate pair", Tbl(One() + "," + One(id: "p2"))), ("duplicate member names", "{\"schema\":\"allstars-loader-profiles/1\",\"schema\":\"x\",\"profiles\":[]}")
            })
            {
                var probs = Problems(json, out var l);
                T.True(probs.Count > 0 && (what is "duplicate id" or "duplicate pair" ? l.Count == 1 : l.Count == 0), $"{what}: refused ({string.Join("; ", probs)})");
            }
            // a scan of a game folder whose host matches a profile but whose proxy does not is NOT recognised
            using var fx = new Fx("lp");
            var hostSha = Sha.OfFile(fx.GamePath("ChaoGarage.dll")); var proxySha = Sha.OfFile(fx.GamePath("dinput8.dll"));
            var halfMatch = new LoaderProfile("half", "d", hostSha, new string('7', 64), 1, 1, 1, new[] { "x" }, "x", true);
            var scan = GameFolderScanner.Scan(fx.Game, null, new[] { halfMatch });
            T.True(!scan.LoaderRecognized && scan.LoaderProfile is null && scan.Issues.Any(i => i.Code == "loader-unrecognized"), "host-only match does not count");
            var fullMatch = new LoaderProfile("full", "d", hostSha, proxySha, 1, 1, 1, new[] { "x" }, "x", true);
            var scan2 = GameFolderScanner.Scan(fx.Game, null, new[] { fullMatch });
            T.True(scan2.LoaderRecognized && scan2.LoaderProfile?.UserSupplied == true && !scan2.Issues.Any(i => i.Code == "loader-unrecognized"), "full match through a user profile");
            fx.Cleanup();
        });

        T.Run("user-supplied loader profiles are labelled as such in the checker's text and JSON and in the explanation of a failed requirement", () =>
        {
            using var fx = new Fx("lp");
            fx.WriteMod(CompatCheckerTests.Spec("needy", "Needy", "1.0.0", "{\"loader\":{\"capabilities\":[\"time-travel\"]}}"));
            var pf = CompatCheckerTests.ProfileFile(fx);
            var text = CompatCheckerTests.Cli(fx.Game, "--loader-profiles", pf).Out;
            T.True(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Contains("described by a profile file you supplied") && text.Contains("your statement, not evidence"), "text: " + text);
            var json = System.Text.Json.JsonDocument.Parse(CompatCheckerTests.Cli(fx.Game, "--loader-profiles", pf, "--json").Out).RootElement;
            T.Eq("user-supplied", json.GetProperty("loader").GetProperty("profile_source").GetString()!, "json profile_source");
            T.True(json.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("detail").GetString()!.Contains("a profile file you supplied")), "the failed requirement names the source of the loader description");
            var builtin = System.Text.Json.JsonDocument.Parse(CompatCheckerTests.Cli(Path.Combine(fx.Mods, "Needy"), "--loader", "ssasr-host-1", "--json").Out).RootElement;
            T.Eq("built-in", builtin.GetProperty("loader").GetProperty("profile_source").GetString()!, "built-in profile source");
            fx.Cleanup();
        });
    }
}
