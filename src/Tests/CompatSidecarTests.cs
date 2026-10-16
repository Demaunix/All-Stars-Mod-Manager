using System.Text;
using System.Text.Json;

namespace AllStarsModManager.Tests;

/// <summary>Reading allstars-mod.json strictly, and reading runtime DLL headers without loading them.</summary>
public static class CompatSidecarTests
{
    static Sidecar Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SidecarReader.Parse(doc.RootElement, "test.json");
    }

    static bool Safety(Sidecar s, string field) => s.Problems.Any(p => p.Safety && p.Field.StartsWith(field, StringComparison.Ordinal));

    public static void Register()
    {
        T.Area("compat-sidecar");

        T.Run("sidecar format: a legacy file without manifest_schema reads as allstars-mod/1; versioned files must name a supported major", () =>
        {
            var legacy = Parse("{\"id\":\"a\",\"requires\":[{\"id\":\"b\"}]}");
            T.True(legacy.Interpretable && legacy.SchemaText is null && legacy.Requires.Count == 1 && legacy.Problems.Count == 0, "legacy file is read as schema 1");
            var v1 = Parse("{\"manifest_schema\":\"allstars-mod/1\",\"requires\":[{\"id\":\"b\"}]}");
            T.True(v1.Interpretable && v1.SchemaMajor == 1 && v1.SchemaMinor == 0 && v1.Requires.Count == 1, "allstars-mod/1");
            var v14 = Parse("{\"manifest_schema\":\"allstars-mod/1.4\",\"requires\":[{\"id\":\"b\"}],\"brand_new_field\":{\"x\":[1,2]}}");
            T.True(v14.Interpretable && v14.SchemaMinor == 4 && v14.Requires.Count == 1 && v14.Problems.Count == 0, "a later minor of major 1 is read and unknown fields are ignored (forward compatible)");
            var v2 = Parse("{\"manifest_schema\":\"allstars-mod/2\",\"id\":\"a\",\"name\":\"Shown\",\"requires\":[{\"id\":\"b\"}],\"conflicts_with\":[\"c\"]}");
            T.True(!v2.Interpretable && Safety(v2, SidecarReader.SchemaField), "future major is not interpretable and says why");
            T.True(v2.Requires.Count == 0 && v2.ConflictsWith.Count == 0, "nothing from a file of a newer format is applied partially");
            T.Eq("Shown", v2.Name, "display text is still available");
            foreach (var bad in new[] { "\"allstars-mod/\"", "\"allstars-mod/x\"", "\"Allstars-mod/1\"", "\"allstars-mod/1.\"", "\"allstars-mod/-1\"", "\"other/1\"", "1", "true", "[]", "{}", "\"\"", "\"allstars-mod/1 \\n\"" })
            {
                var s = Parse("{\"manifest_schema\":" + bad + ",\"requires\":[{\"id\":\"b\"}]}");
                T.True(!s.Interpretable && Safety(s, SidecarReader.SchemaField) && s.Requires.Count == 0, $"manifest_schema {bad} is refused");
            }
            var nullSchema = Parse("{\"manifest_schema\":null,\"requires\":[\"a\"]}");
            T.True(!nullSchema.Interpretable && Safety(nullSchema, SidecarReader.SchemaField) && nullSchema.Requires.Count == 0, "an explicit null schema is unreadable, not \"not stated\"");
        });

        T.Run("sidecar format: must_understand names features the reader must know; an unknown one makes the declarations unreadable", () =>
        {
            var ok = Parse("{\"must_understand\":[\"loader-requirements/1\",\"load-order/1\"],\"requires\":[\"b\"]}");
            T.True(ok.Interpretable && ok.Requires.Count == 1, "known features");
            var no = Parse("{\"must_understand\":[\"load-order/1\",\"dependency-solver/9\"],\"requires\":[\"b\"]}");
            T.True(!no.Interpretable && Safety(no, "must_understand") && no.Requires.Count == 0, "unknown feature fails closed and nothing is applied");
            T.True(!Parse("{\"must_understand\":[1]}").Interpretable, "not a name");
            T.True(!Parse("{\"must_understand\":\"nope/1\"}").Interpretable, "single unknown name");
        });

        T.Run("sidecar relations: lists, one-object shorthand (used by existing packages) and plain ids are all read; nothing readable is dropped", () =>
        {
            var s = Parse("{\"requires\":{\"id\":\"pack\",\"versions\":\"=0.1.0-alpha.2\"},\"conflicts_with\":\"standalone\",\"composes_with\":[\"x\",{\"id\":\"y\",\"versions\":\">=1\",\"evidence\":\"tested\"}]}");
            T.Eq(1, s.Requires.Count, "object shorthand for requires (the 1.1.0 reader silently dropped it)");
            T.Eq("=0.1.0-alpha.2", s.Requires[0].Versions, "range kept");
            T.Eq("standalone", s.ConflictsWith.Single().Id, "string shorthand");
            T.Eq(2, s.ComposesWith.Count, "composes_with forms");
            T.Eq(0, s.Problems.Count, "no problems");
            T.Eq("*", Parse("{\"requires\":[{\"id\":\"a\"}]}").Requires[0].Versions, "no range means any version");
            T.Eq(">=1.0.0", Parse("{\"requires\":[{\"id\":\"a\",\"version\":\">=1.0.0\"}]}").Requires[0].Versions, "'version' is accepted for 'versions'");
        });

        T.Run("explicit JSON null is never read as an absent constraint: every safety field and member given as null leaves a recorded problem", () =>
        {
            foreach (var json in new[]
            {
                "{\"requires\":null}", "{\"conflicts_with\":null}", "{\"conflicts\":null}", "{\"installable\":null}", "{\"loose_assets\":null}", "{\"capabilities_exclusive\":null}", "{\"load_order\":null}",
                "{\"loader\":null}", "{\"loader\":{\"host_abi\":null}}", "{\"loader\":{\"capabilities\":null}}", "{\"loader\":{\"host_sha256\":null}}", "{\"must_understand\":null}",
                "{\"package_completeness\":null}", "{\"package_completeness\":{\"mod_folder_alone_is_complete\":null}}", "{\"requires\":[{\"id\":\"a\",\"versions\":null}]}",
                "{\"load_order\":{\"after\":null}}", "{\"touches\":null}", "{\"touches\":{\"native_patches\":null}}",
                "{\"loose_assets\":[{\"target\":\"Resource/a.usm\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":null}]}"
            })
            {
                var s = Parse(json);
                T.True(s.Problems.Count > 0 || s.Requires.Any(r => r.Invalid is not null) || !s.Interpretable, $"{json}: null must leave a trace");
            }
            T.Eq(0, Parse("{\"target\":null,\"summary\":null,\"homepage\":null,\"status\":null,\"config_schema\":null}").Problems.Count, "display-only fields given as null stay harmless");
        });

        T.Run("sidecar relations: an entry that cannot be read is a safety problem or an invalid relation, never 'no rule'", () =>
        {
            T.True(Safety(Parse("{\"requires\":5}"), "requires"), "requires: number");
            T.True(Safety(Parse("{\"requires\":true}"), "requires"), "requires: boolean");
            T.True(Safety(Parse("{\"requires\":[{\"versions\":\">=1\"}]}"), "requires"), "requires: no id");
            T.True(Safety(Parse("{\"requires\":[7]}"), "requires"), "requires: bare number");
            T.True(Safety(Parse("{\"conflicts_with\":[{\"id\":\"\"}]}"), "conflicts_with"), "conflicts_with: empty id");
            T.True(Safety(Parse("{\"conflicts\":[{\"effect\":\"x\"}]}"), "conflicts"), "conflicts: no 'with'");
            var numeric = Parse("{\"requires\":[{\"id\":\"a\",\"versions\":2}]}");
            T.True(numeric.Requires.Single().Invalid is not null, "numeric range is an invalid relation: " + numeric.Requires.Single().Invalid);
            var garbage = Parse("{\"conflicts_with\":[{\"id\":\"a\",\"versions\":\">=1.0 maybe\"}]}");
            T.True(garbage.ConflictsWith.Single().Invalid is not null, "garbled range is an invalid relation");
            T.True(Parse("{\"requires\":[{\"id\":\"a\",\"versions\":\"UNKNOWN\"}]}").Requires.Single().Invalid is null, "UNKNOWN is valid text (it evaluates to unknown)");
            T.Eq(0, Parse("{\"composes_with\":[{\"id\":\"a\",\"versions\":\"junk junk\"}]}").ComposesWith.Count, "an unreadable composes_with adds no reassurance");
            var free = Parse("{\"conflicts\":[{\"with\":\"any mod that writes 0x545434\",\"effect\":\"refuses to start\"},{\"with\":\"other-mod < 2.0.0\"}]}");
            T.Eq(1, free.FreeTextConflicts.Count, "free text kept as an unchecked note");
            T.Eq("<2.0.0", free.ConflictsWith.Single().Versions, "'<id> <op> <version>' is machine readable");
        });

        T.Run("sidecar safety fields: required files, native patches, exclusive claims, installable and completeness fail closed when malformed", () =>
        {
            T.True(Safety(Parse("{\"loose_assets\":[{\"target\":\"Resource/Movie/a.usm\"}]}"), "loose_assets"), "loose asset without a hash is not skipped");
            T.True(Safety(Parse("{\"loose_assets\":[{\"target\":\"Resource/Movie/a.usm\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":-4}]}"), "loose_assets"), "negative size");
            T.True(Safety(Parse("{\"loose_assets\":[{\"target\":\"Resource/Movie/a.usm\",\"sha256\":\"" + new string('a', 64) + "\",\"bytes\":\"4\"}]}"), "loose_assets"), "size as text");
            T.True(Safety(Parse("{\"loose_assets\":\"Resource/a.usm\"}"), "loose_assets"), "not a list");
            T.True(Safety(Parse("{\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"zz\",\"length\":8}]}}"), "touches") && Parse("{\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"zz\",\"length\":8}]}}").Problems.All(p => !p.Blocking), "bad address (a footprint problem: unconfirmed, not blocking)");
            T.True(Safety(Parse("{\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"0x545434\",\"length\":0}]}}"), "touches"), "zero length");
            T.True(Safety(Parse("{\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"0x545434\",\"length\":\"8\"}]}}"), "touches"), "length as text");
            T.True(Safety(Parse("{\"touches\":[]}"), "touches"), "touches not an object");
            T.Eq(1, Parse("{\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"0x545434\",\"length\":8}]}}").NativePatches.Count, "valid patch");
            T.Eq(1, Parse("{\"touches\":{\"native_patches\":{\"id\":\"p\",\"va\":5456948,\"length\":8}}}").NativePatches.Count, "numeric address and one-object shorthand");
            T.True(Safety(Parse("{\"capabilities_exclusive\":[\"native:x\",5]}"), "capabilities_exclusive"), "non-text capability");
            T.True(Safety(Parse("{\"installable\":\"false\"}"), "installable"), "installable as text must not read as true");
            T.True(Safety(Parse("{\"package_completeness\":{\"mod_folder_alone_is_complete\":\"no\"}}"), "package_completeness"), "completeness as text");
            T.Eq(false, Parse("{\"installable\":false}").Installable, "installable false is read");
            T.Eq(false, Parse("{\"package_completeness\":{\"mod_folder_alone_is_complete\":false}}").ModFolderAloneIsComplete, "completeness false is read");
        });

        T.Run("sidecar format 1 additions: loader requirements, target build and load order are read strictly", () =>
        {
            var h = new string('b', 64);
            var s = Parse("{\"loader\":{\"host_abi\":1,\"mod_ini_schema\":1,\"plan_schema\":1,\"capabilities\":[\"xpac-member-merge\"],\"host_sha256\":[\"" + h + "\"],\"proxy_sha256\":\"" + h.ToUpperInvariant() + "\"},\"target\":{\"executable_sha256\":\"" + h + "\"},\"load_order\":{\"after\":[\"a\",{\"id\":\"b\"}],\"before\":\"c\"}}");
            T.True(s.Loader.Declared && s.Loader.HostAbi == 1 && s.Loader.ModIniSchema == 1 && s.Loader.PlanSchema == 1, "numbers");
            T.Eq("xpac-member-merge", s.Loader.Capabilities.Single(), "capability");
            T.Eq(h, s.Loader.HostSha256.Single(), "host hash");
            T.Eq(h, s.Loader.ProxySha256.Single(), "proxy hash is normalised to lowercase");
            T.Eq(h, s.TargetExecutableSha256.Single(), "target executable");
            T.Eq("a,b", string.Join(",", s.LoadAfter.Select(x => x.Id)), "after");
            T.Eq("c", s.LoadBefore.Single().Id, "before (string shorthand)");
            T.Eq(0, s.Problems.Count, "no problems");
            T.True(Safety(Parse("{\"loader\":5}"), "loader"), "loader must be an object");
            T.True(Safety(Parse("{\"loader\":{\"host_abi\":\"1\"}}"), "loader"), "host_abi as text");
            T.True(Safety(Parse("{\"loader\":{\"host_abi\":-1}}"), "loader"), "negative abi");
            T.True(Safety(Parse("{\"loader\":{\"capabilities\":[\"Has Spaces\"]}}"), "loader"), "capability name syntax");
            T.True(Safety(Parse("{\"loader\":{\"host_sha256\":[\"abc\"]}}"), "loader"), "short hash");
            T.True(Safety(Parse("{\"loader\":{\"proxy_sha256\":7}}"), "loader"), "hash as number");
            T.True(Parse("{\"loader\":{}}").Loader.Declared, "an empty loader object still counts as declared");
            T.True(Safety(Parse("{\"load_order\":[\"a\"]}"), "load_order"), "load_order must be an object");
            T.True(Safety(Parse("{\"load_order\":{\"after\":[3]}}"), "load_order"), "order id must be text");
            var tgt = Parse("{\"target\":{\"executable_sha256\":\"nothash\"}}");
            T.True(tgt.TargetExecutableSha256.Count == 0 && tgt.Problems.Any(p => !p.Safety && p.Field.StartsWith("target")), "an unreadable target hash is a warning-level problem, not a safety one");
        });

        T.Run("sidecar file syntax is the loader's own JSON subset: anything outside it is unreadable (and never partly read), with advice that keeps manual installs possible", () =>
        {
            using var fx = new Fx("sc");
            var spec = PackageTests.Basic("syn", "Syn");
            var m = fx.WriteMod(spec);
            var file = Path.Combine(m.Dir, "allstars-mod.json");
            void Expect(string what, byte[] content, bool readable)
            {
                File.WriteAllBytes(file, content);
                var pkg = PackageInspector.Inspect(m.Dir);
                if (readable) T.True(pkg.Sidecar is not null && !pkg.Issues.Any(i => i.Code == "sidecar-unreadable"), what + ": should be readable");
                else
                {
                    var iss = pkg.Issues.FirstOrDefault(i => i.Code == "sidecar-unreadable");
                    T.True(pkg.Sidecar is null && iss is not null && iss.Severity == Severity.Blocker && IssueInfo.IsUnknown(iss), what + ": must be refused as unconfirmed, not ignored");
                    T.False(iss!.Message.Contains("remove the file", StringComparison.OrdinalIgnoreCase) || iss.Message.Contains("delete", StringComparison.OrdinalIgnoreCase), "advice must not tell people to delete safety metadata: " + iss.Message);
                    T.True(iss.Message.Contains("copied into the game folder by hand"), "keeps the manual route visible: " + iss.Message);
                }
            }
            byte[] U(string s) => Encoding.UTF8.GetBytes(s);
            Expect("plain object", U("{\"requires\":[\"a\"]}"), true);
            Expect("tabs, CR and LF between tokens", U("{\r\n\t\"requires\" : [ \"a\" ]\r\n}\r\n"), true);
            Expect("raw UTF-8 text", U("{\"name\":\"Caf\u00e9 \ud83d\ude00\"}"), true);
            Expect("unsigned whole numbers", U("{\"loader\":{\"host_abi\":1},\"loose_assets\":[]}"), true);
            Expect("duplicate property names", U("{\"requires\":[\"a\"],\"requires\":[]}"), false);
            Expect("comment", U("{\"id\":\"syn\", /* note */ \"requires\":[\"a\"]}"), false);
            Expect("line comment", U("{\"id\":\"syn\", // note\n \"requires\":[\"a\"]}"), false);
            Expect("trailing comma in a list", U("{\"requires\":[\"a\",]}"), false);
            Expect("trailing comma in an object", U("{\"requires\":[\"a\"],}"), false);
            Expect("UTF-8 byte-order mark", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(U("{\"requires\":[\"a\"]}")).ToArray(), false);
            Expect("negative number", U("{\"loader\":{\"host_abi\":-1}}"), false);
            Expect("decimal number in a display field", U("{\"config_schema\":[{\"key\":\"k\",\"max\":1.5}]}"), false);
            Expect("exponent", U("{\"x\":1e3}"), false);
            Expect("leading zero", U("{\"loader\":{\"host_abi\":01}}"), false);
            Expect("\\u surrogate pair escape", U("{\"name\":\"\\ud83d\\ude00\"}"), false);
            Expect("nesting of 33 lists (the loader's limit: a top-level value is depth 0 and depth 32 is the last allowed)", U(new string('[', 33) + new string(']', 33)), true);
            Expect("nesting of 34 lists", U(new string('[', 34) + new string(']', 34)), false);
            Expect("single quotes", U("{'requires':['a']}"), false);
            Expect("trailing text", U("{\"requires\":[\"a\"]} x"), false);
            Expect("empty file", Array.Empty<byte>(), false);
            Expect("5 MB file", new byte[5 * 1024 * 1024], false);
            Expect("not an object", U("[1,2,3]"), true);      // valid syntax; the content check below refuses it
            var pkg2 = PackageInspector.Inspect(m.Dir);
            T.True(pkg2.Sidecar is { Interpretable: false } && pkg2.Issues.Any(i => i.Code == "sidecar-unevaluable" && i.Severity == Severity.Blocker), "root must be an object");
            fx.Cleanup();
        });

        T.Run("sidecar footprint declarations that cannot be read (placeholder native patch ranges as in real packages) are unconfirmed warnings, not blockers", () =>
        {
            using var fx = new Fx("sc");
            var spec = PackageTests.Basic("foot", "Foot");
            spec.SidecarJson = "{\"manifest_schema\":\"allstars-mod/1\",\"touches\":{\"native_patches\":[{\"id\":\"runtime\",\"va\":\"see the other package\",\"length\":0,\"exclusive\":false,\"after\":\"free text\"}]},\"capabilities_exclusive\":[7]}";
            var pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
            var foot = pkg.Issues.Where(i => i.Code == "sidecar-footprint").ToList();
            T.Eq(2, foot.Count, "two footprint findings");
            T.True(foot.All(i => i.Severity == Severity.Warning && IssueInfo.IsUnknown(i)), "warnings that say 'unconfirmed'");
            T.False(pkg.HasBlockers, "the mod can still be switched on in the manager");
            var r = CompatCheckerTests.Cli(Path.Combine(fx.Mods, "Foot"), "--loader", "ssasr-host-1", "--json");
            T.Eq(1, r.Exit, "but the checker cannot call it 'declared compatible': " + r.Out);
            fx.Cleanup();
        });

        T.Run("sidecar numbers: sizes and addresses are whole numbers up to 4294967295; decimal limits for the settings editor are written as text", () =>
        {
            var h = new string('a', 64);
            T.Eq(1, Parse("{\"loose_assets\":[{\"target\":\"Resource/a.usm\",\"sha256\":\"" + h + "\",\"bytes\":4294967295}]}").LooseAssets.Count, "uint32 maximum");
            T.True(Safety(Parse("{\"loose_assets\":[{\"target\":\"Resource/a.usm\",\"sha256\":\"" + h + "\",\"bytes\":4294967296}]}"), "loose_assets"), "one above the maximum");
            var cfg = Parse("{\"config_schema\":[{\"key\":\"Speed\",\"type\":\"float\",\"min\":\"-1.5\",\"max\":\"2.25\"},{\"key\":\"Count\",\"type\":\"int\",\"min\":0,\"max\":10},{\"key\":\"Bad\",\"min\":\"low\"}]}");
            T.True(cfg.ConfigSchema[0].Min == -1.5 && cfg.ConfigSchema[0].Max == 2.25 && cfg.ConfigSchema[1].Max == 10 && cfg.ConfigSchema[2].Min is null, "limits");
            T.True(cfg.Problems.Count == 0, "display-only limits never raise problems");
        });
        T.Run("sidecar inspection: problems become blocking, unconfirmed findings with stable codes; display-only mistakes stay harmless", () =>
        {
            using var fx = new Fx("sc");
            var spec = PackageTests.Basic("probs", "Probs");
            spec.SidecarJson = "{\"manifest_schema\":\"allstars-mod/1\",\"id\":\"probs\",\"requires\":5,\"config_schema\":\"junk\",\"artifacts\":7,\"human_gameplay_requirements\":\"one string\",\"summary\":12,\"status\":\"text\"}";
            var pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
            var issue = pkg.Issues.Single(i => i.Code == "sidecar-unevaluable");
            T.True(issue.Severity == Severity.Blocker && IssueInfo.IsUnknown(issue) && !IssueInfo.LoaderEnforced(issue), "unconfirmed, blocking, tools-only");
            T.Eq(1, pkg.Issues.Count(i => i.Severity >= Severity.Warning), "only the safety field raised anything; display-only fields did not");
            T.Eq("one string", pkg.Sidecar!.HumanGameplayRequirements.Single(), "string form of a display list is kept");
            spec = PackageTests.Basic("future", "Future");
            spec.SidecarJson = "{\"manifest_schema\":\"allstars-mod/3\",\"id\":\"future\"}";
            pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
            T.True(pkg.Issues.Any(i => i.Code == "sidecar-schema" && i.Severity == Severity.Blocker && IssueInfo.IsUnknown(i)), "future schema");
            spec = PackageTests.Basic("imposter-host", "Imposter");
            spec.SidecarJson = "{\"id\":\"someone-else\",\"requires\":[\"x\"]}";
            pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
            T.True(pkg.Issues.Any(i => i.Code == "sidecar-id" && i.Severity == Severity.Blocker), "a description for another mod that carries rules is a blocker (its rules could not be applied)");
            spec = PackageTests.Basic("plain-imposter", "PlainImposter");
            spec.SidecarJson = "{\"id\":\"someone-else\",\"name\":\"Other\"}";
            pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
            T.True(pkg.Issues.Any(i => i.Code == "sidecar-id" && i.Severity == Severity.Warning), "a purely descriptive description for another mod stays a warning");
            fx.Cleanup();
        });

        T.Run("sidecar fuzz: 3000 mutations of a rich description never throw, and a mutation never turns into 'no rule' without leaving a trace", () =>
        {
            const string seedJson = "{\"manifest_schema\":\"allstars-mod/1\",\"id\":\"fz\",\"requires\":[{\"id\":\"a\",\"versions\":\">=1.0.0 <2.0.0\"}],\"conflicts_with\":[{\"id\":\"b\"}],\"capabilities_exclusive\":[\"native:x\"],\"touches\":{\"native_patches\":[{\"id\":\"p\",\"va\":\"0x545434\",\"length\":8}]},\"loose_assets\":[{\"target\":\"Resource/Movie/a.usm\",\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"bytes\":4}],\"loader\":{\"host_abi\":1,\"capabilities\":[\"xpac-member-merge\"]},\"load_order\":{\"after\":[\"c\"]},\"installable\":true}";
            var rng = new Random(20261016);
            var chars = "{}[]\",:0123456789abcdefxX.-+*<>=!\\ \n\t\u0000é1".ToCharArray();
            int parsed = 0, traced = 0, silent = 0;
            for (int i = 0; i < 3000; i++)
            {
                var sb = new StringBuilder(seedJson);
                for (int k = rng.Next(1, 5); k > 0; k--)
                {
                    int pos = rng.Next(sb.Length);
                    switch (rng.Next(3))
                    {
                        case 0: sb[pos] = chars[rng.Next(chars.Length)]; break;
                        case 1: sb.Remove(pos, Math.Min(rng.Next(1, 12), sb.Length - pos)); break;
                        default: sb.Insert(pos, chars[rng.Next(chars.Length)]); break;
                    }
                }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(sb.ToString(), new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false }); } catch (JsonException) { continue; }
                using (doc)
                {
                    Sidecar s = SidecarReader.Parse(doc.RootElement, "fuzz.json");   // must not throw
                    parsed++;
                    // rules that survive, or a recorded problem/invalid relation/uninterpretable flag, must account for every safety field the mutation touched
                    var root = doc.RootElement;
                    bool trace = !s.Interpretable || s.Problems.Count > 0 || s.Requires.Any(r => r.Invalid is not null) || s.ConflictsWith.Any(r => r.Invalid is not null);
                    if (trace) traced++;
                    // invariant: a safety field whose JSON type cannot be read must leave a trace
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var key in new[] { "requires", "conflicts_with", "capabilities_exclusive", "loose_assets", "conflicts", "touches", "loader", "load_order", "installable" })
                        {
                            if (!root.TryGetProperty(key, out var v)) continue;
                            bool readable = (key is "touches" or "loader" or "load_order" ? v.ValueKind == JsonValueKind.Object
                                    : key == "installable" ? v.ValueKind is JsonValueKind.True or JsonValueKind.False
                                    : key is "loose_assets" or "conflicts" ? v.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                                    : v.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.String);
                            if (!readable && !trace) silent++;      // (an explicit null is NOT readable: it must leave a trace as well)
                        }
                    }
                }
            }            T.True(parsed > 500, $"enough mutations stayed valid JSON ({parsed})");
            T.True(traced > 100, $"many mutations produced a recorded problem ({traced})");
            T.Eq(0, silent, "no safety field of an unreadable type was ignored without a trace");
        });

        T.Area("compat-pe");

        T.Run("PE inspection: a synthetic runtime reads as an x86 DLL with the entry points the loader looks up", () =>
        {
            var pe = Inspect(SyntheticPe.Dll());
            T.True(pe.IsPe && pe.IsX86 && pe.IsDll && pe.ExportsRead && !pe.IsPe32Plus, "headers: " + pe.Problem);
            T.True(pe.ExportNames.OrderBy(x => x).SequenceEqual(new[] { "SsasrModInitialize", "SsasrModRollback" }), "exports: " + string.Join(",", pe.ExportNames));
            T.True(pe.ExportKinds.Count == 2 && pe.ExportKinds.Values.All(k => k == ExportKind.Code), "both names resolve to code addresses");
            T.True(pe.ExportNamesSorted && !Inspect(SyntheticPe.Dll("u", reverseNameTable: true)).ExportNamesSorted, "name-table order is detected (a reversed table keeps its name-to-function mapping but is not sorted)");
            T.True(Inspect(SyntheticPe.Dll("u", reverseNameTable: true)).ExportKinds.Values.All(k => k == ExportKind.Code), "the mapping itself is intact in the reversed table: only the order is wrong");
            var zero = Inspect(SyntheticPe.Dll("z", exportAddress: "zero"));
            T.True(zero.ExportsRead && zero.ExportNames.Count == 2 && zero.ExportKinds.Values.All(k => k == ExportKind.NoAddress), "zero addresses are seen as such");
            T.True(Inspect(SyntheticPe.Dll("b", exportAddress: "badordinal")).ExportKinds.Values.All(k => k == ExportKind.Invalid), "ordinal past the function table");
            T.True(Inspect(SyntheticPe.Dll("o", exportAddress: "outside")).ExportKinds.Values.All(k => k == ExportKind.Invalid), "address outside every section");
            T.True(Inspect(SyntheticPe.Dll("f", exportAddress: "forwarder")).ExportKinds.Values.All(k => k == ExportKind.Forwarder), "address inside the export directory is a forwarder");
            T.True(SyntheticPe.Dll("a").SequenceEqual(SyntheticPe.Dll("a")) && !SyntheticPe.Dll("a").SequenceEqual(SyntheticPe.Dll("b")), "the salt varies the bytes deterministically");
            var many = Inspect(SyntheticPe.Dll("m", new[] { "SsasrModRollback", "Zed", "alpha", "SsasrModInitialize", "Beta" }));
            T.Eq(5, many.ExportNames.Count, "five exports");
        });

        T.Run("PE inspection: wrong processor, program instead of DLL, missing exports and non-PE files become definite loader-level blockers", () =>
        {
            Issues(SyntheticPe.Dll("x", machine: 0x8664, pe32Plus: true)).Has("runtime-wrong-machine");
            Issues(SyntheticPe.Dll("x", machine: 0x8664)).Has("runtime-wrong-machine");
            Issues(SyntheticPe.Dll("x", machine: 0xAA64, pe32Plus: true)).Has("runtime-wrong-machine");
            Issues(SyntheticPe.Dll("x", dllFlag: false)).Has("runtime-not-dll");
            Issues(SyntheticPe.Dll("x", exports: new[] { "SsasrModInitialize" })).Has("runtime-exports-missing");
            Issues(SyntheticPe.Dll("x", exports: new[] { "ssasrmodinitialize", "ssasrmodrollback" })).Has("runtime-exports-missing");   // GetProcAddress is case-sensitive
            Issues(SyntheticPe.Dll("x", exports: new[] { "_SsasrModInitialize@8", "SsasrModRollback" })).Has("runtime-exports-missing");   // decorated names do not match
            Issues(Encoding.ASCII.GetBytes("MZ-not-a-real-dll-test-bytes-1")).Has("runtime-not-pe");
            Issues(Array.Empty<byte>()).Has("runtime-not-pe");
            Issues(Encoding.ASCII.GetBytes("#!/bin/sh\necho hi\n")).Has("runtime-not-pe");
            Issues(SyntheticPe.Dll("ok")).HasNone();
            // a listed name must also resolve (name -> ordinal -> function address): the reviewer's case, names visible but every address zeroed, is a definite failure
            Issues(SyntheticPe.Dll("x", exportAddress: "zero")).Has("runtime-exports-missing");
            Issues(SyntheticPe.Dll("x", exportAddress: "badordinal")).Has("runtime-exports-missing");
            Issues(SyntheticPe.Dll("x", exportAddress: "outside")).Has("runtime-exports-missing");
            // the reviewer's counterexample: names starting with bytes 0x80, 0x81, 0x82 ahead of the two required names. After lossy ASCII decoding ("?a", "?b", "?c") the table looked ascending;
            // in real byte order it is not, and a binary search can miss the required names.
            var raw = new[] { "\u0080a", "\u0081b", "\u0082c", "SsasrModInitialize", "SsasrModRollback" };
            T.False(Inspect(SyntheticPe.Dll("r", exports: raw, sortNames: false)).ExportNamesSorted, "raw-byte order is judged, not decoded text");
            Issues(SyntheticPe.Dll("r", exports: raw, sortNames: false)).Has("runtime-exports-unreadable");
            T.False(Inspect(SyntheticPe.Dll("r2", exports: new[] { "SsasrModInitialize", "SsasrModRollback", "\u00e9x" })).ExportNamesSorted, "any name with a byte above 0x7F is not confirmed, even in a sorted table");
            T.True(Inspect(SyntheticPe.Dll("r3", exports: new[] { "SsasrModRollback", "SsasrModInitialize" }, sortNames: false)).ExportNamesSorted == false, "two required names in the wrong order");
            var unsorted = Issues(SyntheticPe.Dll("x", reverseNameTable: true));
            unsorted.Has("runtime-exports-unreadable"); unsorted.HasNoBlocker();      // an unsorted name table cannot be confirmed (binary-search lookup), but is not a definite failure either
            var fwd = Issues(SyntheticPe.Dll("x", exportAddress: "forwarder"));
            fwd.Has("runtime-exports-unreadable"); fwd.HasNoBlocker();
            T.True(PackageInspector.RequiredRuntimeExports.SequenceEqual(new[] { "SsasrModInitialize", "SsasrModRollback" }), "the two names the loader looks up");
        });

        T.Run("PE inspection: every truncation and 4000 random corruptions of a valid image are handled without exceptions or runaway reads", () =>
        {
            var good = SyntheticPe.Dll("fuzz");
            // the image has padding after its last meaningful byte; a prefix is only complete once it contains the last export name
            int needed = System.Text.Encoding.ASCII.GetString(good).IndexOf("SsasrModRollback\0", StringComparison.Ordinal) + "SsasrModRollback\0".Length;
            T.True(needed > 0x200, "found the end of the export strings");
            for (int len = 0; len < good.Length; len += len < 900 ? 1 : 37)
            {
                var s = Inspect(good.AsSpan(0, len).ToArray());
                if (len < needed) T.False(s.IsPe && s.ExportsRead && s.ExportNames.Count == 2, $"a {len}-byte prefix must not look complete");
                else T.True(s.IsPe && s.ExportsRead && s.ExportNames.Count == 2, $"a {len}-byte prefix holds everything that is read");
            }
            var rng = new Random(77);
            int notRead = 0;
            for (int i = 0; i < 4000; i++)
            {
                var b = (byte[])good.Clone();
                for (int k = rng.Next(1, 6); k > 0; k--) b[rng.Next(b.Length < 0x400 ? b.Length : 0x400)] = (byte)rng.Next(256);
                var s = Inspect(b);       // must not throw
                if (!s.IsPe || !s.ExportsRead) notRead++;
                T.True(s.ExportNames.Count <= 100_000 && s.ExportNames.All(n => n.Length < 512), "bounded output");
            }
            T.True(notRead > 50, $"corruptions were detected ({notRead})");
            // an implausible export count must not allocate or loop
            var bad = (byte[])good.Clone();
            var dirOff = 0x200 + 8;           // two stubs of 4 bytes, export directory follows
            BitConverter.GetBytes(0xFFFFFFF0u).CopyTo(bad, dirOff + 24);
            var sBad = Inspect(bad);
            T.True(sBad.IsPe && !sBad.ExportsRead, "huge NumberOfNames: " + sBad.Problem);
            Issues(bad).Has("runtime-exports-unreadable");
        });

        T.Run("PE inspection: reading a runtime never loads it (the module list of this process is unchanged) and the unreadable-exports case is unconfirmed, not definite", () =>
        {
            using var fx = new Fx("pe");
            var spec = PackageTests.Basic("peload", "PeLoad");
            var m = fx.WriteMod(spec);
            var before = System.Diagnostics.Process.GetCurrentProcess().Modules.Count;
            var pkg = PackageInspector.Inspect(m.Dir);
            var runtime = Path.Combine(m.Dir, "runtime", "Rt.dll");
            T.False(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Any(x => string.Equals(x.FileName, runtime, StringComparison.OrdinalIgnoreCase)), "the runtime is not a loaded module");
            T.True(System.Diagnostics.Process.GetCurrentProcess().Modules.Count - before <= 2, "no new native module appeared beyond runtime housekeeping");
            T.True(pkg.Runtime is { IsX86: true, ExportsRead: true }, "facts were stored");
            var warn = new Issue(Severity.Warning, "runtime-exports-unreadable", "x");
            T.True(IssueInfo.IsUnknown(warn) && !IssueInfo.LoaderEnforced(warn), "classification of the soft case");
            T.True(IssueInfo.LoaderEnforced(new Issue(Severity.Blocker, "runtime-exports-missing", "x")), "classification of the hard case");
            fx.Cleanup();
        });
    }

    static bool SeedFieldsAllPresent(JsonElement root) =>
        root.TryGetProperty("requires", out _) && root.TryGetProperty("conflicts_with", out _) && root.TryGetProperty("loose_assets", out _)
        && root.TryGetProperty("touches", out _) && root.TryGetProperty("loader", out _) && root.TryGetProperty("capabilities_exclusive", out _) && root.TryGetProperty("load_order", out _);

    static PeSummary Inspect(byte[] bytes) { using var ms = new MemoryStream(bytes); return PeInspector.Inspect(ms); }

    sealed class IssueSet(List<Issue> issues)
    {
        public void Has(string code) => T.HasCode(issues, code, "runtime image");
        public void HasNone() => T.Eq(0, issues.Count(i => i.Code.StartsWith("runtime-")), "no runtime issues");
        public void HasNoBlocker() => T.False(issues.Any(i => i.Severity == Severity.Blocker), "an unconfirmable forwarder is a warning, not a definite failure");
    }

    static IssueSet Issues(byte[] runtime)
    {
        using var fx = new Fx("pe");
        var spec = PackageTests.Basic("rt", "Rt"); spec.Runtime = runtime;
        var pkg = PackageInspector.Inspect(fx.WriteMod(spec).Dir);
        var list = pkg.Issues.ToList();
        fx.Cleanup();
        return new IssueSet(list);
    }
}
