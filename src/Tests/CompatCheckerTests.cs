using System.Text;
using System.Text.Json;
using AllStarsModManager.Compat;

namespace AllStarsModManager.Tests;

/// <summary>The manager-free checker (CheckCli/CheckRunner) on isolated fixture folders, and its agreement with the manager's rules.</summary>
public static class CompatCheckerTests
{
    static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>A mod with its own copy target (so mods never clash on data unless a test wants them to).</summary>
    public static ModSpec Spec(string id, string folder, string version = "1.0.0", string? sidecar = null, bool native = false)
    {
        var s = new ModSpec { Id = id, Name = folder, Folder = folder, Version = version, SidecarJson = sidecar, Runtime = SyntheticPe.Dll(id) };
        s.Groups.Add(GroupSpec.Copy($"Resource/{id}.bin", B("payload of " + id)));
        return s;
    }

    /// <summary>Writes a loader profile that matches the fixture's placeholder loader files, so the fixture counts as a verified build.</summary>
    public static string ProfileFile(Fx fx, string caps = "\"host-abi-1\",\"xpac-member-merge\",\"plan-format-copy\"")
    {
        var host = Sha.OfFile(fx.GamePath("ChaoGarage.dll")); var proxy = Sha.OfFile(fx.GamePath("dinput8.dll"));
        var path = Path.Combine(fx.Root, "profiles.json");
        File.WriteAllText(path, "{\"schema\":\"allstars-loader-profiles/1\",\"profiles\":[{\"id\":\"fixture-host\",\"description\":\"fixture\",\"host_sha256\":\"" + host + "\",\"proxy_sha256\":\"" + proxy + "\",\"host_abi\":1,\"mod_ini_schema\":1,\"plan_schema\":1,\"capabilities\":[" + caps + "]}]}");
        return path;
    }

    public static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = CheckCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
    /// <summary>Text output is wrapped at 100 columns; collapse white space before looking for a phrase.</summary>
    static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    static IEnumerable<JsonElement> Findings(JsonElement r) => r.GetProperty("findings").EnumerateArray();
    static bool HasFinding(JsonElement r, string code, string? impact = null) => Findings(r).Any(f => f.GetProperty("code").GetString() == code && (impact is null || f.GetProperty("impact").GetString() == impact));

    static ManagerService Manager(Fx fx)
    {
        var paths = new AppPaths(Path.Combine(fx.Root, "data"));
        var svc = new ManagerService(paths, ManagerEnvironment.Fixture(new Scn.ProbeHolder()));
        svc.Settings.GameRoot = fx.Game;
        svc.Settings.Save(paths);
        svc.Refresh(false);
        svc.VerifyAll();
        return svc;
    }

    public static void Register()
    {
        T.Area("compat-checker");

        T.Run("checker: a healthy set on a recognised loader is 'declared compatible' (exit 0) in text and JSON, and says what it did not do", () =>
        {
            using var fx = new Fx("ck");
            fx.WriteMod(Spec("solo", "Solo"));
            var pf = ProfileFile(fx);
            var (code, text, err) = Cli(fx.Game, "--loader-profiles", pf);
            T.Eq(0, code, "exit code; output: " + text);
            T.True(text.Contains("RESULT: DECLARED COMPATIBLE") && text.Contains("not a game test"), "plain-language result");
            T.True(Flat(text).Contains("Nothing was run or tested in the game") && Flat(text).Contains("never loaded"), "limits are stated");
            T.True(text.Contains("\"fixture-host\"") && text.Contains("profile file you supplied"), "loader line");
            var (jc, json, _) = Cli(fx.Game, "--loader-profiles", pf, "--json");
            var r = Json(json);
            T.Eq(0, jc, "json exit");
            T.Eq("declared-compatible", r.GetProperty("verdict").GetString()!, "verdict");
            T.Eq("allstars-check-report/1", r.GetProperty("schema").GetString()!, "schema");
            T.Eq(0, r.GetProperty("exit_code").GetInt32(), "exit_code field");
            T.Eq("game-folder", r.GetProperty("input_kind").GetString()!, "input kind");
            T.False(r.GetProperty("executed_mod_code").GetBoolean() || r.GetProperty("modified_anything").GetBoolean(), "explicit non-claims");
            T.Eq("verified", r.GetProperty("loader").GetProperty("knowledge").GetString()!, "loader knowledge");
            T.Eq("Solo", r.GetProperty("start_order")[0].GetString()!, "order");
            T.True(r.GetProperty("not_checked").GetArrayLength() >= 3, "not-checked list");
            T.True(err.Contains("Checking file hashes") && !text.Contains("Checking file hashes"), "progress goes to stderr, not into the result");
            fx.Cleanup();
        });

        T.Run("checker: three-valued result - a definite clash is exit 2, an unconfirmed matter is exit 1, never confused", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("one", "One")); fx.WriteMod(Spec("two", "Two"));
            var unknown = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.Eq(1, unknown.Exit, "two native runtimes with no recorded pair test: insufficient information");
            var u = Json(unknown.Out);
            T.True(HasFinding(u, "native-unknown", "unknown") && !Findings(u).Any(f => f.GetProperty("impact").GetString() == "blocker"), "unknown, no blocker");
            T.Eq("insufficient-information", u.GetProperty("verdict").GetString()!, "verdict name");
            fx.WriteMod(Spec("one", "OneAgain"));      // same ID twice: the loader would refuse the whole set
            var def = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.Eq(2, def.Exit, "duplicate ID is definite");
            var d = Json(def.Out);
            var dup = Findings(d).Single(f => f.GetProperty("code").GetString() == "duplicate-id");
            T.True(dup.GetProperty("impact").GetString() == "blocker" && dup.GetProperty("enforced_by").GetString() == "loader", "enforced by the loader");
            T.True(HasFinding(d, "native-unknown", "unknown"), "the unconfirmed matter is still listed beside the definite one");
            T.Eq("incompatible", d.GetProperty("verdict").GetString()!, "verdict");
            fx.Cleanup();
        });

        T.Run("checker: recorded pair evidence turns the native-interaction question from unknown to noted (offline evidence only)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("one", "One", "1.0.0", "{\"composes_with\":[{\"id\":\"two\",\"versions\":\"1.0.0\",\"evidence\":\"offline fixture test\"}]}"));
            fx.WriteMod(Spec("two", "Two"));
            var (code, text, _) = Cli(fx.Game, "--loader-profiles", pf);
            T.Eq(0, code, text);
            T.True(Flat(text).Contains("tested together offline") && Flat(text).Contains("not the same as in-game testing"), "evidence is labelled as offline");
            fx.Cleanup();
        });

        T.Run("checker: an unrecognised loader build is never reported as known - the verdict can be at best 'insufficient information'", () =>
        {
            using var fx = new Fx("ck");
            fx.WriteMod(Spec("solo", "Solo"));
            var (code, text, _) = Cli(fx.Game, "--json");
            var r = Json(text);
            T.Eq(1, code, "exit");
            T.True(HasFinding(r, "loader-unrecognized", "unknown"), "finding");
            T.Eq("unrecognized", r.GetProperty("loader").GetProperty("knowledge").GetString()!, "knowledge");
            T.Eq(1, Findings(r).Count(f => f.GetProperty("code").GetString() is "loader-unrecognized" or "loader-missing"), "reported once, not twice");
            var (_, plain, _) = Cli(fx.Game);
            T.True(Flat(plain).Contains("never by a version label"), "explains that recognition is by hash: " + plain);
            // a version label in a profile or description cannot make it known
            File.WriteAllText(Path.Combine(fx.Game, "ChaoGarage.version.txt"), "ssasr-host-1");
            T.Eq(1, Cli(fx.Game).Exit, "a self-written label next to the loader does not matter");
            fx.Cleanup();
        });

        T.Run("checker: missing loader files and a missing mods folder are stated plainly", () =>
        {
            using var fx = new Fx("ck");
            fx.WriteMod(Spec("solo", "Solo"));
            File.Delete(fx.GamePath("dinput8.dll"));
            var r = Json(Cli(fx.Game, "--json").Out);
            T.True(HasFinding(r, "loader-missing", "unknown"), "loader-missing");
            T.Eq("absent", r.GetProperty("loader").GetProperty("knowledge").GetString()!, "knowledge");
            using var fx2 = new Fx("ck");
            Directory.Delete(fx2.Mods, true);
            var r2 = Cli(fx2.Game, "--json");
            T.True(Json(r2.Out).GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString() == "no-mods-folder"), "no mods folder");
            fx.Cleanup(); fx2.Cleanup();
        });

        T.Run("checker: damaged files are found by the default hash pass and skipped (and said to be skipped) with --quick", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var m = fx.WriteMod(Spec("solo", "Solo"));
            File.WriteAllBytes(Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0], B("tampered"));
            var full = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.Eq(2, full.Exit, "tampered payload is a definite problem");
            var f = Findings(Json(full.Out)).Single(x => x.GetProperty("code").GetString() == "payload-hash");
            T.Eq("loader", f.GetProperty("enforced_by").GetString()!, "the loader checks payload hashes too");
            var quick = Cli(fx.Game, "--loader-profiles", pf, "--quick", "--json");
            var q = Json(quick.Out);
            T.True(quick.Exit != 2 && q.GetProperty("file_hashes").GetString() == "skipped", "--quick skips hashing");
            T.True(q.GetProperty("not_checked").EnumerateArray().Any(n => n.GetString()!.Contains("File hashes were skipped")), "and says what that leaves unchecked");
            // the runtime and plan pins are still checked in --quick mode: they are small
            File.WriteAllBytes(Path.Combine(m.Dir, "plan.json"), B("{\"schema\":1,\"archive_groups\":[]} "));
            T.Eq(2, Cli(fx.Game, "--loader-profiles", pf, "--quick").Exit, "plan pin mismatch is found without hashing payloads");
            fx.Cleanup();
        });

        T.Run("checker: reads only - the whole fixture tree (including the manager's own folder) is byte-identical after every kind of run", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("one", "One")); fx.WriteMod(Spec("two", "Two"));
            fx.WriteMod(Spec("off", "Off"));
            File.Move(Path.Combine(fx.Mods, "Off", "mod.ini"), Path.Combine(fx.Mods, "Off", "mod.ini.disabled-by-manager"));
            Directory.CreateDirectory(Path.Combine(fx.Mods, "Third Party Tool")); File.WriteAllText(Path.Combine(fx.Mods, "Third Party Tool", "readme.txt"), "not ours");
            Directory.CreateDirectory(Path.Combine(fx.Mods, ".allstars-manager")); File.WriteAllText(Path.Combine(fx.Mods, ".allstars-manager", "marker.txt"), "keep");
            var before = Fs.Snapshot(fx.Root, excludeManagerDir: false);
            foreach (var args in new[] { new[] { fx.Game }, new[] { fx.Game, "--json" }, new[] { fx.Game, "--quick" }, new[] { fx.Game, "--deep" }, new[] { fx.Game, "--loader-profiles", pf, "--ascii" }, new[] { fx.Mods }, new[] { Path.Combine(fx.Mods, "One") }, new[] { fx.Game, "--add", Path.Combine(fx.Mods, "Two") } })
                Cli(args);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(fx.Root, excludeManagerDir: false)), "no file created, changed, removed or touched in content/attributes");
            T.False(Directory.Exists(Path.Combine(fx.Root, "data")), "no data folder, cache or settings were created");
            fx.Cleanup();
        });

        T.Run("checker: input and usage errors have their own exit codes and never print a stack trace", () =>
        {
            using var fx = new Fx("ck");
            var empty = Path.Combine(fx.Root, "empty"); Directory.CreateDirectory(empty);
            var file = Path.Combine(fx.Root, "afile.zip"); File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            foreach (var (args, code, needle) in new[]
            {
                (new[] { Path.Combine(fx.Root, "nope") }, 3, "does not exist"),
                (new[] { file }, 3, "Unzip the mod first"),
                (new[] { empty }, 3, "Nothing to check"),
                (new[] { "--bogus" }, 64, "Unknown option"),
                (new[] { fx.Game, "extra" }, 64, "Only one folder"),
                (new[] { fx.Game, "--add" }, 64, "needs"),
                (new[] { fx.Game, "--as", "banana" }, 64, "--as must be"),
                (new[] { fx.Game, "--quick", "--deep" }, 64, "cannot be combined"),
                (new[] { fx.Game, "--loader", "no-such-build" }, 3, "Unknown loader profile"),
                (new[] { fx.Game, "--loader-profiles", Path.Combine(fx.Root, "missing.json") }, 3, "does not exist"),
                (new[] { fx.Game, "--loader-profiles", "bad|name<>.json" }, 3, "does not exist"),
                (new[] { fx.Game, "--add", Path.Combine(fx.Root, "nope") }, 3, "does not exist"),
                (new[] { fx.Game, "--add", empty }, 3, "no mod.ini"),
            })
            {
                var r = Cli(args);
                T.Eq(code, r.Exit, $"exit code for {string.Join(" ", args.Select(a => Path.GetFileName(a)))}: {r.Err}{r.Out}");
                T.True(r.Err.Contains(needle), $"message '{needle}' in: {r.Err}");
                T.False(r.Err.Contains(" at ") && r.Err.Contains("Exception"), "no stack trace");
            }
            var bad = Path.Combine(fx.Root, "badprofiles.json"); File.WriteAllText(bad, "{\"schema\":\"allstars-loader-profiles/1\",\"profiles\":[{\"id\":\"X\"}]}");
            T.Eq(3, Cli(fx.Game, "--loader-profiles", bad).Exit, "invalid profile table");
            // usage errors never repeat what was typed after an unknown option: a drive path with the account name, a UNC path with a non-ASCII share name,
            // forward slashes, a device path, or an option that is itself a path (fourth review finding: the Unicode share name came back)
            foreach (var typedArg in new[] { "--output=D:\\Downloads\\" + Environment.UserName + "\\private-stuff", "--output=\\\\nas-box\\\u5171\u6709\\private-stuff",
                "--output=//nas-box/share/private-stuff", "--output=\\\\.\\pipe\\private-stuff", "-C:\\private-stuff", "--\u5171\u6709=private-stuff" })
            {
                var u = Cli(typedArg);
                T.True(u.Exit == 64 && u.Err.Contains("Unknown option") && !u.Err.Contains("private-stuff") && !u.Err.Contains("nas-box") && !u.Err.Contains("\u5171")
                    && !u.Err.Contains("Downloads") && !u.Err.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "usage error does not repeat " + typedArg + ": " + u.Err);
            }
            T.True(Cli("--output=x").Err.Contains("Unknown option --output."), "a plain option name is still named");
            // "C:\dir\" inside quotes reaches the program as C:\dir" (a stray trailing quote): accepted
            T.Eq(0, Cli(fx.Game + "\"", "--quick").Exit, "stray trailing quote after a backslash");
            T.Eq(0, Cli(fx.Game + "\\\"", "--quick").Exit, "backslash and quote");
            T.Eq(3, Cli(Path.Combine(fx.Root, "nope") + "\"").Exit, "a missing folder is still missing");
            var help = Cli("--help");
            T.True(help.Exit == 0 && help.Out.Contains("Exit codes") && help.Out.Contains("never runs or loads a mod DLL"), "help");
            var ver = Cli("--version");
            T.True(ver.Exit == 0 && ver.Out.StartsWith("AllStarsModCheck 1."), "version: " + ver.Out);
            var ll = Cli("--list-loaders");
            T.True(ll.Exit == 0 && ll.Out.Contains("ssasr-host-1") && ll.Out.Contains(GameFolderInfo.KnownHostSha), "list-loaders");
            fx.Cleanup();
        });

        T.Run("checker: other people's folders and files are listed as not examined and are never changed; a foreign mod.ini is a loader-level blocker", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("solo", "Solo"));
            Directory.CreateDirectory(Path.Combine(fx.Mods, "Some Tool")); File.WriteAllText(Path.Combine(fx.Mods, "Some Tool", "config.cfg"), "x=1");
            Directory.CreateDirectory(Path.Combine(fx.Mods, "Research Note")); File.WriteAllText(Path.Combine(fx.Mods, "Research Note", "allstars-mod.json"), "{\"installable\":false,\"name\":\"Idea\"}");
            File.WriteAllText(Path.Combine(fx.Mods, "loose-file.txt"), "ignored");
            var before = Fs.Snapshot(fx.Root, excludeManagerDir: false);
            var (code, json, _) = Cli(fx.Game, "--loader-profiles", pf, "--json");
            var r = Json(json);
            T.Eq(0, code, "unmanaged folders do not make a set incompatible");
            var left = r.GetProperty("packages").EnumerateArray().Where(p => p.GetProperty("state").GetString() == "unmanaged").Select(p => p.GetProperty("folder").GetString()).OrderBy(x => x).ToList();
            T.Eq("Research Note,Some Tool", string.Join(",", left), "listed, not examined");
            T.True(Flat(Cli(fx.Game, "--loader-profiles", pf).Out).Contains("does not examine or change it"), "text says so");
            // a mod.ini in the format of another loader makes the real loader reject everything
            Directory.CreateDirectory(Path.Combine(fx.Mods, "Other Loader Mod"));
            File.WriteAllText(Path.Combine(fx.Mods, "Other Loader Mod", "mod.ini"), "[Main]\r\nName=Something\r\n");
            var foreign = Json(Cli(fx.Game, "--loader-profiles", pf, "--json").Out);
            var f = Findings(foreign).Single(x => x.GetProperty("code").GetString() == "manifest-foreign");
            T.True(f.GetProperty("impact").GetString() == "blocker" && f.GetProperty("enforced_by").GetString() == "loader", "foreign manifest");
            Directory.Delete(Path.Combine(fx.Mods, "Other Loader Mod"), true);
            T.Eq("", Fs.Diff(before, Fs.Snapshot(fx.Root, excludeManagerDir: false)), "nothing else changed");
            fx.Cleanup();
        });

        T.Run("checker: a link inside the mods folder is reported as the loader's refusal and is not followed", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("solo", "Solo"));
            var outside = Path.Combine(fx.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.txt"), "do not touch");
            Fs.MakeJunction(Path.Combine(fx.Mods, "Linked"), outside);
            var r = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.Eq(2, r.Exit, "a junction makes the real loader start no mods");
            T.True(HasFinding(Json(r.Out), "folder-link", "blocker"), "reported");
            T.True(File.Exists(Path.Combine(outside, "secret.txt")), "target untouched");
            Fs.DeleteTree(Path.Combine(fx.Mods, "Linked"));
            fx.Cleanup();
        });

        T.Run("checker: --add checks a mod that is not installed yet against what is installed, without touching either", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("have", "Have", "1.0.0", "{\"composes_with\":[{\"id\":\"newmod\",\"versions\":\"*\",\"evidence\":\"x\"}]}"));
            var staging = Path.Combine(fx.Root, "downloaded"); Directory.CreateDirectory(staging);
            fx.WriteMod(Spec("newmod", "NewMod", "2.0.0", "{\"requires\":[{\"id\":\"absent-dependency\"}]}"), staging);
            var before = Fs.Snapshot(fx.Root, excludeManagerDir: false);
            var r = Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(staging, "NewMod"), "--json");
            var j = Json(r.Out);
            T.Eq(2, r.Exit, "missing dependency is found: " + r.Out);
            T.True(j.GetProperty("packages").EnumerateArray().Any(p => p.GetProperty("folder").GetString() == "NewMod" && p.GetProperty("state").GetString() == "would-be-added"), "listed as would-be-added");
            T.True(HasFinding(j, "missing-requirement", "blocker"), "the requirement");
            // adding a folder that holds several mods
            var r2 = Cli(fx.Game, "--loader-profiles", pf, "--add", staging, "--json");
            T.Eq(2, r2.Exit, "a folder of mods works the same");
            T.Eq("", Fs.Diff(before, Fs.Snapshot(fx.Root, excludeManagerDir: false)), "neither the game folder nor the downloaded mod changed");
            fx.Cleanup();
        });

        T.Run("checker: a single package or a plain mods folder works without any game folder; the loader is then 'not inspected' unless a profile is named, and a named one is labelled as assumed", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var m = fx.WriteMod(Spec("solo", "Solo"));
            var pkg = Cli(m.Dir, "--json");
            var j = Json(pkg.Out);
            T.Eq("package", j.GetProperty("input_kind").GetString()!, "kind");
            T.Eq(1, pkg.Exit, "without a loader the answer can only be 'insufficient information'");
            T.True(HasFinding(j, "loader-not-inspected", "unknown"), "says why");
            var assumed = Cli(m.Dir, "--loader", "fixture-host", "--loader-profiles", pf);
            T.Eq(0, assumed.Exit, assumed.Out);
            T.True(assumed.Out.Contains("assumed profile \"fixture-host\"") && assumed.Out.Contains("not verified against real files"), "labelled as assumed");
            var mods = Json(Cli(fx.Mods, "--loader", "fixture-host", "--loader-profiles", pf, "--json").Out);
            T.Eq("mods-folder", mods.GetProperty("input_kind").GetString()!, "mods folder kind");
            T.True(mods.GetProperty("not_checked").EnumerateArray().Any(n => n.GetString()!.Contains("No game folder was given")), "says plans were not compared with a game");
            var both = Json(Cli(fx.Game, "--loader", "fixture-host", "--loader-profiles", pf, "--json").Out);
            T.True(both.GetProperty("not_checked").EnumerateArray().Any(n => n.GetString()!.Contains("--loader was ignored")), "a real game folder wins over an assumed profile");
            T.Eq("verified", both.GetProperty("loader").GetProperty("knowledge").GetString()!, "real files used");
            fx.Cleanup();
        });

        T.Run("checker: a mod that ships its required extra files is not reported as incomplete, with or without --quick (--add)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var pkgRoot = Path.Combine(fx.Root, "dl", "pkg");
            var spec = Spec("loosy", "Loosy");
            Scn.BuildLooseModPackage(fx, pkgRoot, spec, ("Resource/Movie/L1.usm", B("movie one")), ("Resource/Movie/L2.usm", B("movie two")));
            foreach (var mode in new[] { "--quick", "--loader-profiles" })
            {
                var args = mode == "--quick" ? new[] { fx.Game, "--loader-profiles", pf, "--add", Path.Combine(pkgRoot, "mods", "Loosy"), "--quick", "--json" }
                                             : new[] { fx.Game, "--loader-profiles", pf, "--add", Path.Combine(pkgRoot, "mods", "Loosy"), "--json" };
                var r = Cli(args);
                T.False(r.Out.Contains("loose-incomplete") || r.Out.Contains("Incomplete package"), $"{mode}: complete package reported incomplete: {r.Out}");
                T.Eq(0, r.Exit, $"{mode}: exit code; {r.Out}");
            }
            File.Delete(Path.Combine(pkgRoot, "Resource", "Movie", "L2.usm"));
            var broken = Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(pkgRoot, "mods", "Loosy"), "--quick", "--json");
            T.True(broken.Exit == 2 && broken.Out.Contains("loose-"), "a required file that really is missing is still reported: " + broken.Out);
            fx.Cleanup();
        });
        T.Run("checker: packages to add that share a folder name are all examined, collisions are reported, repeated inputs count once (review finding)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var a = Path.Combine(fx.Root, "dlA"); var b = Path.Combine(fx.Root, "dlB");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            var bad = Spec("bad-one", "Mod"); bad.WrongRuntimeHash = true;
            fx.WriteMod(bad, a);
            fx.WriteMod(Spec("good-one", "Mod"), b);
            var r = Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(a, "Mod"), "--add", Path.Combine(b, "Mod"), "--json");
            var j = Json(r.Out);
            T.Eq(2, r.Exit, "a damaged first package is not hidden by a healthy second one with the same folder name: " + r.Out);
            var pinFinding = Findings(j).Single(f => f.GetProperty("code").GetString() == "runtime-hash-mismatch");
            T.Eq("Mod (to add #1)", pinFinding.GetProperty("packages")[0].GetString()!, "the finding names the right package");
            var collision = Findings(j).Single(f => f.GetProperty("code").GetString() == "folder-name-collision");
            T.True(collision.GetProperty("impact").GetString() == "blocker" && collision.GetProperty("packages").GetArrayLength() == 2, "the shared folder name is reported");
            T.Eq(2, j.GetProperty("packages").EnumerateArray().Count(p => p.GetProperty("state").GetString() == "would-be-added"), "both are listed");
            // conflicts between same-named packages are still found (the old shared key skipped equal-key pairs)
            var c1 = Path.Combine(fx.Root, "dlC"); var c2 = Path.Combine(fx.Root, "dlD"); Directory.CreateDirectory(c1); Directory.CreateDirectory(c2);
            fx.WriteMod(SharedMember(fx, "m-one", "Same"), c1); fx.WriteMod(SharedMember(fx, "m-two", "Same"), c2);
            var r2 = Json(Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(c1, "Same"), "--add", Path.Combine(c2, "Same"), "--json").Out);
            T.True(HasFinding(r2, "shared-archive-member", "blocker"), "the clash between two same-named additions is found");
            // the same folder given twice counts once; a package named like an installed folder collides with it
            var once = Json(Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(b, "Mod"), "--add", Path.Combine(b, "Mod") + "\\", "--json").Out);
            T.Eq(1, once.GetProperty("packages").EnumerateArray().Count(p => p.GetProperty("state").GetString() == "would-be-added"), "counted once");
            T.True(once.GetProperty("not_checked").EnumerateArray().Any(n => n.GetString()!.Contains("counted once")), "and says so");
            fx.WriteMod(Spec("installed-one", "Mod"));
            var r3 = Json(Cli(fx.Game, "--loader-profiles", pf, "--add", Path.Combine(b, "Mod"), "--json").Out);
            T.True(HasFinding(r3, "folder-name-collision", "blocker"), "a package that would be installed over an existing folder name collides with it");
            var dupPkg = PackageInspector.Inspect(Path.Combine(fx.Mods, "Mod"));
            T.Throws<ArgumentException>(() => ActiveSetValidator.Validate(new[] { new Candidate { Key = "k", Pkg = dupPkg, FolderName = "A" }, new Candidate { Key = "k", Pkg = dupPkg, FolderName = "B" } }, (GameFolderInfo?)null, new HashCache(), (LoaderEvidence?)null, (ActiveSetValidator.ValidationOptions?)null),
                "the shared validator itself refuses duplicate candidate keys instead of overwriting findings");            fx.Cleanup();
        });

        T.Run("checker: files held by another program cannot be read - that is a finding, not a crash, and reports and messages stay free of paths (locked-file control)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var m = fx.WriteMod(Spec("locked", "Locked"));
            void NoPaths(string label, string text) { foreach (var s in new[] { fx.Root, fx.Game, Environment.UserName }) T.False(text.Contains(s, StringComparison.OrdinalIgnoreCase), $"{label}: no installation path or account name in the output"); }
            foreach (var (what, path, code, args) in new[]
            {
                ("plan.json", Path.Combine(m.Dir, "plan.json"), "plan-unreadable", new[] { "--loader-profiles", pf }),
                ("runtime", Path.Combine(m.Dir, "runtime", "Rt.dll"), "runtime-unreadable", new[] { "--loader-profiles", pf }),
                ("payload", Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0], "payload-unreadable", new[] { "--loader-profiles", pf }),
            })
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))      // exclusive: nobody else can read it meanwhile
                {
                    var r = Cli(new[] { fx.Game }.Concat(args).Append("--json").ToArray());
                    T.Eq(2, r.Exit, $"{what} locked: a finding, not exit 3 / a crash: {r.Out} {r.Err}");
                    T.True(HasFinding(Json(r.Out), code, "blocker"), $"{what} locked: {code}");
                    NoPaths(what + " (json)", r.Out + r.Err);
                    NoPaths(what + " (text)", Cli(new[] { fx.Game }.Concat(args).ToArray()).Out);
                }
            }
            using (new FileStream(Directory.GetFiles(Path.Combine(m.Dir, "payloads"))[0], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var q = Cli(fx.Game, "--loader-profiles", pf, "--quick", "--json");
                T.False(q.Out.Contains("payload-unreadable"), "--quick does not read payloads, so a locked payload is not an issue there");
            }
            T.Eq(0, Cli(fx.Game, "--loader-profiles", pf).Exit, "once nothing is locked the same folder is declared compatible");
            fx.Cleanup();
        });

        T.Run("checker: every report field and every error channel is scrubbed - a folder named like the account, a path in a version text, a locked profile file (second review finding)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var named = Spec("named-one", Environment.UserName, "C:\\Windows\\Temp\\secret-version");
            fx.WriteMod(named);
            var r = Cli(fx.Game, "--loader-profiles", pf, "--json");
            var text = Cli(fx.Game, "--loader-profiles", pf).Out;
            T.False(r.Out.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) || text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "the account name is not in any field (folder names included): " + r.Out);
            T.True(Json(r.Out).GetProperty("start_order")[0].GetString()!.Contains("<user>"), "a folder named like the account is shown as <user>");
            T.False(r.Out.Contains("secret-version") || text.Contains("secret-version"), "a path inside a version text is not echoed");
            T.True(Json(r.Out).GetProperty("packages").EnumerateArray().Any(p => (p.TryGetProperty("version", out var v) && v.GetString()!.Contains("<path>"))), "it is replaced by <path>");
            T.True(text.Contains("<user>"), "the text renderer still finds its labels after scrubbing");
            // the same pass keeps a genuine web address intact (root finding: https://gamebanana.com/mods/123456 became http<path>)
            fx.WriteMod(Spec("linked", "Linked", "1.0.0", "{\"homepage\":\"https://gamebanana.com/mods/123456\"}"));
            var linked = Json(Cli(fx.Game, "--loader-profiles", pf, "--json").Out).GetProperty("packages").EnumerateArray().Single(p => p.GetProperty("folder").GetString() == "Linked");
            T.Eq("https://gamebanana.com/mods/123456", linked.GetProperty("homepage").GetString(), "homepage kept in the report");
            // and a double-quoted path with spaces and an apostrophe inside a report field is removed completely, in JSON and in text
            fx.WriteMod(Spec("quoted", "Quoted", "see \"E:\\Secret Folder\\Bob's Mods\\v\" now"));
            var qj = Cli(fx.Game, "--loader-profiles", pf, "--json").Out; var qt = Cli(fx.Game, "--loader-profiles", pf).Out;
            T.False(qj.Contains("Secret") || qj.Contains("Bob") || qt.Contains("Secret") || qt.Contains("Bob"), "no part of the quoted path is left: " + qj);
            T.True(Json(qj).GetProperty("packages").EnumerateArray().Any(p => p.TryGetProperty("version", out var v) && v.GetString() == "see \"<path>\" now"), "the version text keeps its words around <path>");
            fx.WriteMod(Spec("squoted", "SQuoted", "see 'E:\\Data\\Zed' s Mods\\v' now"));
            var sj = Cli(fx.Game, "--loader-profiles", pf, "--json").Out; var st = Cli(fx.Game, "--loader-profiles", pf).Out;
            T.False(sj.Contains("Zed") || sj.Contains("s Mods") || st.Contains("Zed") || st.Contains("s Mods"), "no part of a single-quoted path with an apostrophe is left: " + sj);
            // a locked --loader-profiles file: the CLI error channel is scrubbed as well
            using (new FileStream(pf, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var e = Cli(fx.Game, "--loader-profiles", pf);
                T.Eq(3, e.Exit, "an unreadable profile file is an input error");
                T.False(e.Err.Contains(fx.Root, StringComparison.OrdinalIgnoreCase) || e.Err.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "the error text carries no path or account name: " + e.Err);
            }
            fx.Cleanup();
        });

        T.Run("checker and manager: an allstars-mod.json that is a folder, a link or cannot be read is an unreadable description, never 'no description' (as the 1.1.0 loader refuses it)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var m = fx.WriteMod(Spec("disc", "Disc"));
            var side = Path.Combine(m.Dir, SidecarReader.FileName);
            (int Exit, List<string> Codes) Check()
            {
                var r = Cli(fx.Game, "--loader-profiles", pf, "--json");
                return (r.Exit, Json(r.Out).GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!).ToList());
            }
            bool Blocked() => PackageInspector.Inspect(m.Dir).Issues.Any(i => i.Code == "sidecar-unreadable" && i.Severity == Severity.Blocker);
            var healthy = Check();
            T.True(healthy.Exit == 0 && !healthy.Codes.Contains("sidecar-unreadable"), "control: no description, declared compatible");
            File.WriteAllText(side, "{\"loader\":{\"host_abi\":2}}");
            T.True(Check() is { Exit: 2 } real && real.Codes.Contains("loader-requirement"), "control: a real description with an unmet requirement is a definite problem");
            File.Delete(side);
            // a folder under the description's name (root reproduction: this was declared compatible)
            Directory.CreateDirectory(side);
            var folder = Check();
            T.True(folder.Exit != 0 && folder.Codes.Contains("sidecar-unreadable") && Blocked(), "a folder named allstars-mod.json: unreadable, blocking in the manager: " + string.Join(",", folder.Codes));
            Directory.Delete(side);
            // a junction under that name
            var elsewhere = Path.Combine(fx.Root, "elsewhere"); Directory.CreateDirectory(elsewhere);
            Fs.MakeJunction(side, elsewhere);
            var junction = Check();
            T.True(junction.Exit != 0 && junction.Codes.Contains("sidecar-unreadable") && Blocked(), "a junction named allstars-mod.json: unreadable: " + string.Join(",", junction.Codes));
            new DirectoryInfo(side).Delete();
            // a file symbolic link to a description with an unmet requirement (only where this account may create one)
            var target = Path.Combine(fx.Root, "target.json"); File.WriteAllText(target, "{\"loader\":{\"host_abi\":2}}");
            bool linked = false;
            try { File.CreateSymbolicLink(side, target); linked = true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (linked)
            {
                var link = Check();
                T.True(link.Exit != 0 && link.Codes.Contains("sidecar-unreadable") && !link.Codes.Contains("loader-requirement") && Blocked(), "a file link named allstars-mod.json: unreadable, its target is not read: " + string.Join(",", link.Codes));
                File.Delete(side);
            }
            else Console.WriteLine("        (file symbolic link case not run: this account cannot create file links)");
            // a description held open exclusively by another program
            File.WriteAllText(side, "{\"loader\":{\"host_abi\":2}}");
            using (new FileStream(side, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = Check();
                T.True(locked.Exit != 0 && locked.Codes.Contains("sidecar-unreadable") && Blocked(), "a locked description: unreadable: " + string.Join(",", locked.Codes));
            }
            fx.Cleanup();
        });

        T.Run("checker: a conflict a mod describes only in words is surfaced as unconfirmed, never silently dropped (review finding)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("worded", "Worded", "1.0.0", "{\"conflicts\":[{\"with\":\"any mod that writes 0x545434\",\"effect\":\"refuses to start\"}]}"));
            var json = Cli(fx.Game, "--loader-profiles", pf, "--json");
            var j = Json(json.Out);
            T.Eq(1, json.Exit, "not 'declared compatible': " + json.Out);
            var f = Findings(j).Single(x => x.GetProperty("code").GetString() == "free-text-conflict");
            T.True(f.GetProperty("impact").GetString() == "unknown" && f.GetProperty("detail").GetString()!.Contains("any mod that writes 0x545434") && f.GetProperty("detail").GetString()!.Contains("refuses to start"), "the declaration is shown");
            T.True(Flat(Cli(fx.Game, "--loader-profiles", pf).Out).Contains("any mod that writes 0x545434"), "also in the text output");
            // the same note is produced by the shared analyzer (the manager shows it as a warning line)
            var pkg = PackageInspector.Inspect(Path.Combine(fx.Mods, "Worded"));
            T.True(ConflictAnalyzer.Analyze(new[] { new Candidate { Key = "w", Pkg = pkg, FolderName = "Worded", Installed = true } }).Notes.Any(n => n.Code == "free-text-conflict"), "analyzer note");
            fx.Cleanup();
        });

        T.Run("checker: reports contain no installation paths or account names even when an error message does (junction in a plan path, operating-system texts) (review finding)", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            var m = fx.WriteMod(Spec("linky", "Linky"));
            var outside = Path.Combine(fx.Root, "outside-plan"); Directory.CreateDirectory(outside);
            File.Copy(Path.Combine(m.Dir, "plan.json"), Path.Combine(outside, "plan.json"));
            Fs.MakeJunction(Path.Combine(m.Dir, "lnk"), outside);
            File.WriteAllText(Path.Combine(m.Dir, "mod.ini"), File.ReadAllText(Path.Combine(m.Dir, "mod.ini")).Replace("plan=plan.json", "plan=lnk/plan.json"));
            var r = Cli(fx.Game, "--loader-profiles", pf, "--json");
            var text = Cli(fx.Game, "--loader-profiles", pf).Out;
            T.Eq(2, r.Exit, "the junction in the plan path is a definite problem: " + r.Out);
            T.True(HasFinding(Json(r.Out), "plan-path", "blocker"), "reported");
            foreach (var s in new[] { fx.Root, fx.Game, Environment.UserName })
                T.False(r.Out.Contains(s, StringComparison.OrdinalIgnoreCase) || text.Contains(s, StringComparison.OrdinalIgnoreCase), "no installation path or account name in the output");
            new DirectoryInfo(Path.Combine(m.Dir, "lnk")).Delete();
            // the scrubber itself
            var roots = new[] { @"C:\Games\Racing", @"D:\Downloads\Some Mod" };
            T.Eq("mods\\X\\plan.json is a link", CheckOutput.ScrubPaths(@"C:\Games\Racing\mods\X\plan.json is a link", roots), "path under a checked folder becomes relative");
            T.Eq("Access to the path <path> is denied.", CheckOutput.ScrubPaths(@"Access to the path 'C:\Windows\Temp\secret\file.dat' is denied.", roots).Replace("'<path>'", "<path>").Replace("'", ""), "OS error text");
            T.Eq("x <path> y", CheckOutput.ScrubPaths(@"x \\?\C:\Windows\Temp\z y", roots), "extended-length prefix");
            T.Eq("x <path> y", CheckOutput.ScrubPaths(@"x \\server\share\folder\a.txt y", roots), "UNC path");
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x C:/Windows/Temp/a.txt y", roots), "forward slashes");
            T.Eq("<folder>", CheckOutput.ScrubPaths(@"D:\Downloads\Some Mod", roots), "the folder itself");
            T.Eq("no paths here: 1.2.3 and a/b", CheckOutput.ScrubPaths("no paths here: 1.2.3 and a/b", roots), "ordinary text is untouched");
            // web addresses survive (root finding: https://… became http<path>); file: addresses are paths
            var none = Array.Empty<string>();
            foreach (var url in new[] { "https://gamebanana.com/mods/123456", "see http://example.com/a//b?c=d:/e#f here", "HTTPS://Example.com/x/y", "git+ssh://host/repo" })
                T.Eq(url, CheckOutput.ScrubPaths(url, roots, none), "web address kept: " + url);
            T.Eq("<path>", CheckOutput.ScrubPaths("file:///C:/Secret/x.txt", roots, none), "file: address with a drive");
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x file://nas-box/share/x y", roots, none), "file: address naming a server");
            // UNC and device forms with any letters, forward slashes, a bare server name, a one-letter "scheme" that is really a drive
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x \\\\nas-box\\\u5171\u6709\\private y", roots, none), "UNC path with a non-ASCII share name");
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x \\\\\u00e9t\u00e9-box\\share y", roots, none), "UNC path with a non-ASCII server name");
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x //nas-box/share/private y", roots, none), "UNC path with forward slashes");
            T.Eq("x <path> y", CheckOutput.ScrubPaths(@"x \\.\pipe\private y", roots, none), "device path");
            T.Eq("x <path> y", CheckOutput.ScrubPaths(@"x \\nas-box y", roots, none), "a bare server name");
            T.Eq("x <path> y", CheckOutput.ScrubPaths("x c://secret/x y", roots, none), "c:// is a drive, not a web address");
            // a quoted path runs to the closing quote even when it has spaces (operating-system messages quote paths)
            T.Eq("Could not find a part of the path '<path>'.", CheckOutput.ScrubPaths(@"Could not find a part of the path 'E:\Secret Folder\My Mods\x.json'.", roots, none), "quoted path with spaces");
            T.Eq("open \"<path>\" failed", CheckOutput.ScrubPaths("open \"E:\\Secret Folder\\x\" failed", roots, none), "double-quoted path with spaces");
            // an apostrophe is a valid file-name character (review finding: a double-quoted path with one leaked its later folders)
            T.Eq("open \"<path>\" failed", CheckOutput.ScrubPaths("open \"E:\\Secret Folder\\Bob's Mods\\private.json\" failed", roots, none), "double-quoted path with an apostrophe");
            T.Eq("Could not find file '<path>'.", CheckOutput.ScrubPaths(@"Could not find file 'E:\Data\O'Brien Mods\private.json'.", roots, none), "single-quoted path with an apostrophe");
            T.Eq("Could not find file '<path>'.", CheckOutput.ScrubPaths(@"Could not find file 'E:\Data\Bob' s Mods\private.json'.", roots, none), "an apostrophe followed by a space inside a single-quoted path (follow-up review finding)");
            T.Eq("'<path>' for x", CheckOutput.ScrubPaths(@"'E:\a b\x' and 'D:\c d\y' for x", roots, none), "two single-quoted paths on one line: the text between them goes too (never part of a path left)");
            // a valid quoted path longer than 2048 characters with a space near its end is still removed whole (M1c review finding), both quote kinds
            var longDir = "E:\\" + string.Concat(Enumerable.Repeat("a\\", 1025)) + "Secret Folder\\private.json";
            T.Eq("x \"<path>\" y", CheckOutput.ScrubPaths("x \"" + longDir + "\" y", roots, none), "long double-quoted path");
            T.Eq("x '<path>' y", CheckOutput.ScrubPaths("x '" + longDir + "' y", roots, none), "long single-quoted path");
            // two long single-quoted paths on a line, the second with an apostrophe and a space (M1d review finding): nothing of either is left
            var p1 = @"\\?\E:\" + string.Concat(Enumerable.Repeat("a\\", 200)) + "one";
            var p2 = @"\\?\D:\Bob' s\" + string.Concat(Enumerable.Repeat("a\\", 16300)) + "Secret Folder\\private.json";
            var two = CheckOutput.ScrubPaths("'" + p1 + "' and '" + p2 + "'", roots, none);
            T.True(two == "'<path>'" && !two.Contains("Secret"), "two long single-quoted paths: " + (two.Length > 80 ? two[..80] + "…" : two));
            // adversarially long text (a 4 MiB description is allowed) finishes quickly and never leaves a path behind
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hostile = CheckOutput.ScrubPaths(string.Concat(Enumerable.Repeat("'C:\\x ", 200_000)), roots, none);
            T.True(sw.Elapsed.TotalSeconds < 15 && !hostile.Contains(":\\"), $"long hostile text: {sw.Elapsed.TotalSeconds:0.0}s, {(hostile == CheckOutput.Withheld ? "withheld" : "scrubbed")}");
            // long ordinary text is linear work and is kept, not withheld (a long word once made the web-address rule quadratic)
            var plain = new string('a', 4 << 20) + " https://example.com/x";
            T.Eq(plain, CheckOutput.ScrubPaths(plain, roots, none), "a 4 MiB word followed by a web address is returned unchanged");
            // account names: also with a space, also inside a path or a web address, and the profile folder name when it differs
            T.Eq("<path> is here", CheckOutput.ScrubPaths(@"D:\Backups\First Last\mods\x is here", roots, new[] { "First Last" }), "account name with a space inside an unquoted path");
            T.Eq("<path>", CheckOutput.ScrubPaths(@"D:\Profiles\fl_000 two\x", roots, new[] { "flast", "fl_000 two" }), "profile folder name");
            T.Eq("https://gamebanana.com/members/<user> by <user>", CheckOutput.ScrubPaths("https://gamebanana.com/members/flast by FLast", roots, new[] { "flast" }), "account name inside a web address");
            T.Eq("Note: a // comment and/or flasting", CheckOutput.ScrubPaths("Note: a // comment and/or flasting", roots, new[] { "flast" }), "prose, double slashes and longer words are untouched");
            fx.Cleanup();
        });
        T.Run("checker: sidecar-declared loader needs and load order work on manually installed packages, same as in the manager", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("needy", "Needy", "1.0.0", "{\"loader\":{\"host_abi\":1,\"capabilities\":[\"xpac-member-merge\"]}}"));
            T.Eq(0, Cli(fx.Game, "--loader-profiles", pf).Exit, "met");
            fx.WriteMod(Spec("greedy", "Greedy", "1.0.0", "{\"loader\":{\"capabilities\":[\"time-travel\"]}}"));
            var bad = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.Eq(2, bad.Exit, "unmet capability");
            var f = Findings(Json(bad.Out)).Single(x => x.GetProperty("code").GetString() == "loader-requirement");
            T.True(f.GetProperty("detail").GetString()!.Contains("time-travel") && f.GetProperty("enforced_by").GetString() == "tools", "tools-only rule, names the capability");
            Directory.Delete(Path.Combine(fx.Mods, "Greedy"), true);
            fx.WriteMod(Spec("first", "ZZ Late", "1.0.0", "{\"load_order\":{\"after\":[\"needy\"]}}"));
            var late = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.True(late.Exit == 1 && !HasFinding(Json(late.Out), "load-order") && HasFinding(Json(late.Out), "native-unknown"), "satisfied order: no load-order finding (two runtimes keep the result at insufficient information)");
            Directory.Delete(Path.Combine(fx.Mods, "ZZ Late"), true);
            fx.WriteMod(Spec("first", "0 Early", "1.0.0", "{\"load_order\":{\"after\":[\"needy\"]}}"));
            var order = Cli(fx.Game, "--loader-profiles", pf, "--json");
            T.True(order.Exit == 2 && HasFinding(Json(order.Out), "load-order", "blocker"), "folder name that sorts too early");
            T.True(Cli(fx.Game, "--loader-profiles", pf).Out.Contains("0 Early"), "text names the folder");
            fx.Cleanup();
        });

        T.Run("checker: a set built by hand and the same set chosen in the manager get the same answer (shared rules)", () =>
        {
            foreach (var scenario in new (string Name, Func<Fx, string>[] Mods)[]
            {
                ("duplicate ids", new Func<Fx, string>[] { fx => Add(fx, Spec("dup", "DupA")), fx => Add(fx, Spec("dup", "DupB")) }),
                ("missing requirement", new Func<Fx, string>[] { fx => Add(fx, Spec("needs", "Needs", "1.0.0", "{\"requires\":[\"ghost\"]}")) }),
                ("declared conflict", new Func<Fx, string>[] { fx => Add(fx, Spec("c1", "C1", "1.0.0", "{\"conflicts_with\":[\"c2\"]}")), fx => Add(fx, Spec("c2", "C2")) }),
                ("shared archive member", new Func<Fx, string>[] { fx => Add(fx, SharedMember(fx, "s1", "S1")), fx => Add(fx, SharedMember(fx, "s2", "S2")) }),
                ("load order", new Func<Fx, string>[] { fx => Add(fx, Spec("lo1", "B-after", "1.0.0", "{\"load_order\":{\"after\":[\"lo2\"]}}")), fx => Add(fx, Spec("lo2", "C-later")) }),
                ("loader requirement", new Func<Fx, string>[] { fx => Add(fx, Spec("lr", "Lr", "1.0.0", "{\"loader\":{\"capabilities\":[\"warp-drive\"]}}")) }),
                ("unreadable rule", new Func<Fx, string>[] { fx => Add(fx, Spec("ur", "Ur", "1.0.0", "{\"requires\":[{\"id\":\"x\",\"versions\":\"1.x\"}]}")) }),
                ("future schema", new Func<Fx, string>[] { fx => Add(fx, Spec("fs", "Fs", "1.0.0", "{\"manifest_schema\":\"allstars-mod/9\"}")) }),
                ("clean", new Func<Fx, string>[] { fx => Add(fx, Spec("ok", "Ok")) }),
            })
            {
                // (a) copied by hand into a game folder, then checked
                using var manual = new Fx("eq");
                var pfm = ProfileFile(manual);
                foreach (var make in scenario.Mods) make(manual);
                var handChecked = Json(Cli(manual.Game, "--loader-profiles", pfm, "--json").Out);
                // (b) the same packages sit switched off in another game folder; the manager's intended set switches them on
                using var managed = new Fx("eq");
                var pfg = ProfileFile(managed);
                foreach (var make in scenario.Mods) make(managed);
                foreach (var d in Directory.GetDirectories(managed.Mods)) File.Move(Path.Combine(d, "mod.ini"), Path.Combine(d, "mod.ini.disabled-by-manager"));
                Directory.CreateDirectory(Path.Combine(managed.Root, "data"));
                File.Copy(pfg, Path.Combine(managed.Root, "data", "loader-profiles.json"), true);
                var svc2 = new ManagerService(new AppPaths(Path.Combine(managed.Root, "data")), ManagerEnvironment.Fixture(new Scn.ProbeHolder()));
                svc2.SelectGame(managed.Game, remember: false); svc2.VerifyAll();
                foreach (var e in svc2.Entries) svc2.Want[e.Key] = true;
                var val = svc2.Evaluate().Validation;
                string Key(string code, IEnumerable<string> who) => code + ":" + string.Join("+", who.OrderBy(x => x, StringComparer.Ordinal));
                var checkerBlocking = Findings(handChecked).Where(f => f.GetProperty("impact").GetString() is "blocker" or "unknown" && f.GetProperty("code").GetString() is not ("native-unknown" or "startup-order" or "tested-together"))
                    .Where(f => f.GetProperty("packages").GetArrayLength() > 0 && f.GetProperty("code").GetString() != "loader-not-inspected")
                    .Select(f => Key(f.GetProperty("code").GetString()!, f.GetProperty("packages").EnumerateArray().Select(p => p.GetString()!))).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var managerBlocking = val.Conflicts.Conflicts.Where(c => c.Severity == Severity.Blocker || c.Certainty == Certainty.Unknown)
                    .Select(c => Key(c.Code, c.Parties.Select(k => svc2.Entries.First(e => e.Key == k).InstalledFolder!)))
                    .Concat(val.ByCandidate.SelectMany(kv => kv.Value.Where(i => i.Severity == Severity.Blocker && i.Code is not ("not-verified" or "not-fit-checked"))
                        .Select(i => Key(i.Code, new[] { svc2.Entries.First(e => e.Key == kv.Key).InstalledFolder! }))))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                // the checker folds per-package issues and cross-package conflicts into one list; so does this comparison
                T.Eq(string.Join("|", managerBlocking), string.Join("|", checkerBlocking), $"scenario '{scenario.Name}': checker and manager agree");
                T.Eq(scenario.Name == "clean", handChecked.GetProperty("exit_code").GetInt32() == 0, $"scenario '{scenario.Name}': only the clean set is declared compatible");
                manual.Cleanup(); managed.Cleanup();
            }
        });

        T.Run("checker output: deterministic, free of absolute paths and account names, and plain ASCII on request", () =>
        {
            using var fx = new Fx("ck");
            var pf = ProfileFile(fx);
            fx.WriteMod(Spec("one", "One")); fx.WriteMod(Spec("two", "Twoé"));
            var a = Cli(fx.Game, "--loader-profiles", pf, "--json").Out;
            var b = Cli(fx.Game, "--loader-profiles", pf, "--json").Out;
            T.Eq(a, b, "two runs give identical JSON");
            var text = Cli(fx.Game, "--loader-profiles", pf).Out;
            foreach (var s in new[] { fx.Root, fx.Game, Environment.UserName, Environment.MachineName })
                T.False(a.Contains(s, StringComparison.OrdinalIgnoreCase) || text.Contains(s, StringComparison.OrdinalIgnoreCase), $"output must not contain '{(s == fx.Root || s == fx.Game ? "<fixture path>" : "<account or machine name>")}'");
            var ascii = Cli(fx.Game, "--loader-profiles", pf, "--ascii").Out;
            T.True(ascii.All(c => c < 128), "--ascii output is 7-bit");
            T.True(text.Contains("->") || text.Contains("→"), "start order arrows present in text");
            fx.Cleanup();
        });

        T.Run("checker at scale: 150 mod folders finish quickly in --quick mode", () =>
        {
            using var fx = new Fx("ck");
            for (int i = 0; i < 150; i++) fx.WriteMod(Spec($"bulk-{i:000}", $"Bulk {i:000}"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = Cli(fx.Game, "--quick", "--json");
            T.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:0.0}s");
            T.Eq(150, Json(r.Out).GetProperty("start_order").GetArrayLength(), "all listed in order");
            fx.Cleanup();
        });

        T.Area("compat-manager");

        T.Run("manager: a package that declares loader needs cannot be switched on under an unrecognised loader, a legacy package can (1.1.0 behaviour kept), and a user profile file lifts the block", () =>
        {
            using var fx = new Fx("mg");
            var declared = Spec("needy", "Needy", "1.0.0", "{\"loader\":{\"capabilities\":[\"xpac-member-merge\"]}}");
            var legacy = Spec("plain", "Plain", "1.0.0", "{\"id\":\"plain\",\"summary\":\"legacy description without rules\"}");
            foreach (var s in new[] { declared, legacy }) { s.ManifestFileName = "mod.ini.disabled-by-manager"; fx.WriteMod(s); }
            var svc = Manager(fx);
            var needy = svc.Entries.First(e => e.Id == "needy"); var plain = svc.Entries.First(e => e.Id == "plain");
            svc.SetWanted(plain, true);
            var okPlan = svc.Evaluate();
            T.True(okPlan.CanApply, "legacy package under an unrecognised loader: " + string.Join(" | ", okPlan.Blockers));
            svc.SetWanted(plain, false); svc.SetWanted(needy, true);
            var blocked = svc.Evaluate();
            T.False(blocked.CanApply, "declared loader needs cannot be confirmed -> fail closed");
            T.True(blocked.Blockers.Any(b => b.Contains("loader requirements cannot be confirmed")), "reason: " + string.Join(" | ", blocked.Blockers));
            var pf = ProfileFile(fx);
            File.Copy(pf, Path.Combine(fx.Root, "data", "loader-profiles.json"), true);
            var svc2 = new ManagerService(new AppPaths(Path.Combine(fx.Root, "data")), ManagerEnvironment.Fixture(new Scn.ProbeHolder()));
            svc2.Refresh(false); svc2.VerifyAll();
            T.Eq(1, svc2.ExtraProfiles.Count, "user profile loaded");
            svc2.SetWanted(svc2.Entries.First(e => e.Id == "needy"), true);
            var lifted = svc2.Evaluate();
            T.True(lifted.CanApply, "the user's own profile describes this loader build: " + string.Join(" | ", lifted.Blockers));
            File.WriteAllText(Path.Combine(fx.Root, "data", "loader-profiles.json"), "{ nope");
            var svc3 = new ManagerService(new AppPaths(Path.Combine(fx.Root, "data")), ManagerEnvironment.Fixture(new Scn.ProbeHolder()));
            T.True(svc3.ExtraProfiles.Count == 0 && svc3.ProfileFileProblems.Count > 0, "a broken profile file is reported and trusted for nothing");
            fx.Cleanup();
        });

        T.Run("manager: other people's folders and manually installed mods survive an apply byte for byte", () =>
        {
            using var fx = new Fx("mg");
            fx.WriteMod(Spec("manual", "Manual Install"));
            var tool = Path.Combine(fx.Mods, "Third Party Tool"); Directory.CreateDirectory(tool); File.WriteAllText(Path.Combine(tool, "tool.cfg"), "theirs");
            var off = Spec("later", "Later"); off.ManifestFileName = "mod.ini.disabled-by-manager"; fx.WriteMod(off);
            var svc = Manager(fx);
            T.Eq("Third Party Tool", string.Join(",", svc.UnmanagedFolders), "the manager lists other folders as not managed (and not as mods)");
            T.False(svc.Entries.Any(e => e.InstalledFolder == "Third Party Tool"), "and does not offer them as mods");
            var before = Fs.Snapshot(fx.Game);
            svc.SetWanted(svc.Entries.First(e => e.Id == "later"), true);
            var plan = svc.Prepare();
            T.True(plan.CanApply, string.Join(" | ", plan.Blockers));
            var res = svc.Apply(plan);
            T.True(res.Success, res.Message);
            var diff = Fs.Diff(before, Fs.Snapshot(fx.Game));
            T.True(diff.Contains("mod.ini") && !diff.Contains("Third Party Tool") && !diff.Contains("Manual Install"), "only the switched mod's manifest changed: " + diff);
            T.Eq("theirs", File.ReadAllText(Path.Combine(tool, "tool.cfg")), "third-party file");
            T.True(File.Exists(Path.Combine(fx.Mods, "Manual Install", "mod.ini")), "manual install untouched");
            fx.Cleanup();
        });
    }

    static string Add(Fx fx, ModSpec spec) { fx.WriteMod(spec); return spec.Folder; }

    static ModSpec SharedMember(Fx fx, string id, string folder)
    {
        var s = Spec(id, folder); s.Groups.Clear();
        s.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", B("member from " + id))));
        return s;
    }
}
