using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AllStarsModManager.Tests;

/// <summary>
/// Checks a release ZIP as a user would receive it: exact contents against the built folder and its BUILD-INFO.json, nothing private inside, then
/// the extracted programs are started through their real entry points (checker: help, version, healthy / unconfirmed / failing / bad-input runs
/// against a practice folder that must stay byte-identical; manager: fixture startup through UI Automation). Nothing is installed. No game and
/// no Steam process is started, and the default (no-options) startup path, which looks at Steam's library list, is not used here.
/// </summary>
public static class PackageCheck
{
    delegate void CheckFn(bool ok, string what, string? detail = null);

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // Generic things that must not be inside a published package: absolute local paths under a user profile, e-mail addresses, credentials.
    // A maintainer can add more patterns (one regular expression per line) in the file named by ALLSTARS_FORBIDDEN_PATTERNS_FILE; that file is not part of this tree.
    static readonly Regex[] PrivateText = BuildPrivateText();
    static Regex[] BuildPrivateText()
    {
        var list = new List<Regex>
        {
            new(@"[A-Za-z]:\\(Users|Documents and Settings)\\", RegexOptions.IgnoreCase), new(@"[A-Za-z]:/Users/", RegexOptions.IgnoreCase),
            new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), new(@"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_|BEGIN [A-Z ]*PRIVATE KEY|AKIA[0-9A-Z]{16}"),
        };
        if (Environment.GetEnvironmentVariable("ALLSTARS_FORBIDDEN_PATTERNS_FILE") is { Length: > 0 } f && File.Exists(f))
            foreach (var line in File.ReadAllLines(f).Where(l => l.Trim().Length > 0)) list.Add(new Regex(line.Trim(), RegexOptions.IgnoreCase));
        return list.ToArray();
    }
    static readonly string[] PrivateNames = { "Private", "oracle", ".xpac", ".sbn", ".usm", "ChaoGarage.dll", "dinput8.dll", ".pdb" };
    /// <param name="kind">"manager" or "checker"</param>
    public static int Run(string kind, string zipPath, string distDir, string resultFile)
    {
        var report = new Dictionary<string, object?>();
        var failures = new List<string>();
        void Check(bool ok, string what, string? detail = null) { report[what] = ok ? "ok" : "FAILED: " + detail; if (!ok) failures.Add(what + (detail is null ? "" : ": " + detail)); Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(ok || detail is null ? "" : " - " + detail)}"); }

        bool manager = kind == "manager";
        string top = manager ? "AllStarsModManager" : "AllStarsModCheck";
        var extractRoot = Path.Combine(Program.WorkRoot, "package-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(extractRoot);
        report["zip_name"] = Path.GetFileName(zipPath); report["zip_sha256"] = Sha(File.ReadAllBytes(zipPath));

        // ---- 1. exact contents --------------------------------------------------------------------------------
        var inZip = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var za = ZipFile.OpenRead(zipPath))
            foreach (var e in za.Entries.Where(e => !e.FullName.EndsWith("/")))
            {
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                inZip[e.FullName] = Sha(ms.ToArray());
            }
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(distDir, "*", SearchOption.AllDirectories))
            expected[top + "/" + Path.GetRelativePath(distDir, f).Replace('\\', '/')] = Sha(File.ReadAllBytes(f));
        Check(inZip.Count == expected.Count && inZip.All(kv => expected.TryGetValue(kv.Key, out var h) && h == kv.Value), "zip holds exactly the built files (names and hashes)", $"zip {inZip.Count} files, built {expected.Count} files");
        Check(inZip.Keys.All(k => k.StartsWith(top + "/")), "everything sits in one top folder");
        var must = new[] { "AllStarsModCheck.exe", "AllStarsModCheck.dll", "AllStarsModManager.Core.dll", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "CHANGELOG.md", "BUILD-INFO.json",
                           "docs/PACKAGE-FORMAT.md", "docs/AUTHOR-GUIDE.md", "docs/COMPATIBILITY-CHECKER.md", "docs/LOADER-INTEGRATION.md", "docs/PROVENANCE.md",
                           "examples/minimal-mod/mod.ini", "examples/minimal-mod-addon/mod.ini", "tools/Update-ModHashes.ps1" }.Concat(manager ? new[] { "AllStarsModManager.exe", "docs/ROLLBACK-AND-SAFETY.md", "docs/BUILDING.md", "docs/DEVELOPER-GUIDE.md", "docs/images/preview.png" } : Array.Empty<string>());
        var missing = must.Where(m => !inZip.ContainsKey(top + "/" + m)).ToList();
        Check(missing.Count == 0, "required files are present", string.Join(", ", missing));
        var badNames = inZip.Keys.Where(k => PrivateNames.Any(n => k.Contains(n, StringComparison.OrdinalIgnoreCase)) && !k.Contains("/docs/images/") ).ToList();
        Check(badNames.Count == 0, "no private, game, loader or debug files by name", string.Join(", ", badNames));
        Check(!inZip.Keys.Any(k => (k.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) && !k.Contains("/docs/images/")), "no pictures except the documented preview");
        var coreSha = inZip[top + "/AllStarsModManager.Core.dll"];
        // ---- 2. BUILD-INFO.json agrees with the files and carries a source binding ------------------------------------
        var infoText = File.ReadAllText(Path.Combine(distDir, "BUILD-INFO.json"));
        using (var info = JsonDocument.Parse(infoText))
        {
            var r = info.RootElement;
            var listed = r.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("sha256").GetString()!);
            var actual = expected.Where(kv => kv.Key != top + "/BUILD-INFO.json").ToDictionary(kv => kv.Key[(top.Length + 1)..], kv => kv.Value);
            Check(listed.Count == actual.Count && listed.All(kv => actual.TryGetValue(kv.Key, out var h) && h == kv.Value), "BUILD-INFO.json lists every shipped file with the right hash");
            Check(Sha256Hex(r.GetProperty("source_tree_sha256").GetString()), "BUILD-INFO.json names the source tree hash", r.GetProperty("source_tree_sha256").GetString());
            Check(r.GetProperty("core_library_sha256").GetString() == coreSha, "the recorded Core library hash is the shipped one", coreSha);
            Check(r.GetProperty("version").GetString() == "1.2.0", "build info version", r.GetProperty("version").GetString());
            report["source_tree_sha256"] = r.GetProperty("source_tree_sha256").GetString();
            report["dotnet_sdk"] = r.GetProperty("dotnet_sdk").GetString();
        }
        report["core_library_sha256"] = coreSha;
        // ---- 3. nothing private inside text files -------------------------------------------------------------------------------------------
        ZipFile.ExtractToDirectory(zipPath, extractRoot);
        var pkgRoot = Path.Combine(extractRoot, top);
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(pkgRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".md" or ".txt" or ".json" or ".ini" or ".ps1" or ".cmd" or ".c" or ".def" or "")) continue;
            var text = File.ReadAllText(f);
            foreach (var rx in PrivateText) if (rx.IsMatch(text)) { hits.Add(Path.GetRelativePath(pkgRoot, f) + " ~ " + rx); break; }
        }
        Check(hits.Count == 0, "no local paths, account names, job ids, e-mail addresses or credentials in any text file", string.Join("; ", hits.Take(5)));

        // every relative link in the packaged documents must resolve inside the package (a README that points at a document the ZIP does not hold is a defect)
        var brokenLinks = new List<string>();
        foreach (var md in Directory.EnumerateFiles(pkgRoot, "*.md", SearchOption.AllDirectories))
            foreach (Match lm in Regex.Matches(File.ReadAllText(md), @"\]\(([^)\s]+)\)"))
            {
                var link = lm.Groups[1].Value;
                if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase) || link.StartsWith("mailto:") || link.StartsWith("#")) continue;
                var target = link.Split('#')[0];
                if (target.Length == 0) continue;
                if (!File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(md)!, target.Replace('/', '\\')))) && !Directory.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(md)!, target.Replace('/', '\\')))))
                    brokenLinks.Add(Path.GetRelativePath(pkgRoot, md) + " -> " + link);
            }
        Check(brokenLinks.Count == 0, "every relative link in the packaged documents resolves inside the package", string.Join("; ", brokenLinks.Take(5)));

        // ---- 4. the extracted checker, through its real entry point -----------------------------------------------------------------------
        var chk = Path.Combine(pkgRoot, "AllStarsModCheck.exe");
        var ver = FileVersionInfo.GetVersionInfo(chk);
        Check((ver.ProductVersion ?? "").StartsWith("1.2.0+"), "the checker reports version 1.2.0", ver.ProductVersion);
        RunChecker(chk, pkgRoot, new CheckFn(Check), report);

        // ---- 5. the extracted manager, in fixture mode ---------------------------------------------------------------------------------------
        if (manager) StartManager(Path.Combine(pkgRoot, "AllStarsModManager.exe"), new CheckFn(Check), failures, report);

        report["extract_dir_listing_top"] = string.Join("|", Directory.EnumerateFileSystemEntries(pkgRoot).Select(Path.GetFileName));
        File.WriteAllText(resultFile, JsonSerializer.Serialize(new { utc = DateTime.UtcNow.ToString("o"), kind, passed = failures.Count == 0, failures, report }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(failures.Count == 0 ? $"{kind} package check: all passed" : $"{kind} package check: {failures.Count} failed");
        try { Fs.DeleteTree(extractRoot); } catch (IOException) { }
        return failures.Count == 0 ? 0 : 1;
    }

    static bool Sha256Hex(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    sealed record Result(int Exit, string Out, string Err);

    static Result Exec(string exe, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workDir, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch { } return new Result(-1, "", "timeout"); }
        return new Result(p.ExitCode, o.Result, e.Result);
    }

    static void RunChecker(string chk, string pkgRoot, CheckFn Check, Dictionary<string, object?> report)
    {
        var help = Exec(chk, pkgRoot, "--help");
        Check(help.Exit == 0 && help.Out.Contains("Usage:") && help.Out.Contains("never runs or loads a mod DLL"), "checker --help works and states what it does not do", $"exit {help.Exit}");
        var v = Exec(chk, pkgRoot, "--version");
        Check(v.Exit == 0 && v.Out.StartsWith("AllStarsModCheck 1.2.0"), "checker --version", v.Out);
        Check(Exec(chk, pkgRoot, "--list-loaders").Out.Contains("ssasr-host-1"), "checker --list-loaders prints the built-in table");
        Check(Exec(chk, pkgRoot, "--bogus").Exit == 64, "usage error exits 64");
        Check(Exec(chk, pkgRoot, Path.Combine(pkgRoot, "no-such-folder")).Exit == 3, "missing folder exits 3");

        // the shipped examples
        Check(Exec(chk, pkgRoot, "examples\\minimal-mod", "--loader", "ssasr-host-1").Exit == 0, "shipped minimal example is declared compatible (exit 0)");
        Check(Exec(chk, pkgRoot, "examples\\minimal-mod-addon", "--loader", "ssasr-host-1").Exit == 2, "shipped add-on alone is incompatible: it requires the minimal example (exit 2)");
        var both = Exec(chk, pkgRoot, "examples", "--loader", "ssasr-host-1", "--json");
        Check(both.Exit == 1 && both.Out.Contains("native-unknown") && !both.Out.Contains("\"blocker\""), "both examples together: insufficient information because two native runtimes are untested together (exit 1)", $"exit {both.Exit}");

        // practice game folders; every run must leave them byte-identical
        Result Safe(Fx fx, string label, params string[] args)
        {
            var before = Fs.Snapshot(fx.Root, excludeManagerDir: false);
            var r = Exec(chk, pkgRoot, args);
            var diff = Fs.Diff(before, Fs.Snapshot(fx.Root, excludeManagerDir: false));
            Check(diff.Length == 0, $"read-only: '{label}' left the practice folder byte-identical", diff);
            return r;
        }
        using var fx = new Fx("pkgchk");
        var pf = CompatCheckerTests.ProfileFile(fx);
        fx.WriteMod(CompatCheckerTests.Spec("solo", "Solo"));
        var healthy = Safe(fx, "healthy", fx.Game, "--loader-profiles", pf, "--json");
        Check(healthy.Exit == 0 && healthy.Out.Contains("\"declared-compatible\""), "practice folder, healthy: exit 0 and 'declared-compatible'", $"exit {healthy.Exit}");
        Check(Safe(fx, "healthy --quick", fx.Game, "--loader-profiles", pf, "--quick").Exit == 0, "practice folder, healthy with --quick: exit 0");
        var unrec = Safe(fx, "unrecognised loader", fx.Game, "--json");
        Check(unrec.Exit == 1 && unrec.Out.Contains("loader-unrecognized"), "practice folder with a loader that is not in the table: exit 1 and says so", $"exit {unrec.Exit}");
        fx.WriteMod(CompatCheckerTests.Spec("second", "Second"));
        var unsure = Safe(fx, "two native mods", fx.Game, "--loader-profiles", pf, "--json");
        Check(unsure.Exit == 1 && unsure.Out.Contains("\"insufficient-information\"") && unsure.Out.Contains("native-unknown"), "practice folder, two native mods nobody tested together: exit 1", $"exit {unsure.Exit}");
        fx.WriteMod(CompatCheckerTests.Spec("second", "Second Again"));
        var bad = Safe(fx, "duplicate id", fx.Game, "--loader-profiles", pf);
        Check(bad.Exit == 2 && bad.Out.Contains("RESULT: INCOMPATIBLE") && bad.Out.Replace("\r\n", " ").Contains("Two copies of"), "practice folder, duplicate ID: exit 2 with the plain-language reason", $"exit {bad.Exit}");
        Safe(fx, "deep", fx.Game, "--loader-profiles", pf, "--deep");
        Safe(fx, "mods folder", fx.Mods, "--loader", "fixture-host", "--loader-profiles", pf, "--json");
        Check(!Directory.Exists(Path.Combine(pkgRoot, "data")) && !Directory.Exists(Path.Combine(pkgRoot, "cache")), "the checker created no data or cache folder next to itself");
        fx.Cleanup();
    }
    static void StartManager(string exe, CheckFn Check, List<string> failures, Dictionary<string, object?> report)
    {
        Check(File.Exists(exe), "the extracted manager exists");
        var info = FileVersionInfo.GetVersionInfo(exe);
        report["exe_product_version"] = info.ProductVersion;
        Check((info.ProductVersion ?? "").StartsWith("1.2.0+"), "the manager reports version 1.2.0", info.ProductVersion);
        var gameBefore = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Sonic", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        var steamBefore = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("steam", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        var f = UiFixture.Build("pkgui");
        using (var app = UiApp.Start(exe, "--fixture", f.Game, "--data-dir", f.Data))
        {
            UiTests.WaitLoaded(app, 5);
            Check(app.Window.Current.Name == "All-Stars Mod Manager", "fixture startup shows the main window", app.Window.Current.Name);
            Check(app.RowNames().Any(n => n.Contains("Quick Start")) && app.RowNames().Count >= 5, "the practice folder's mods are listed", string.Join("|", app.RowNames().Take(6)));
            Check(UiApp.Find(app.Window, "PlayButton")?.Current.IsEnabled == false, "Play is off in fixture mode");
            UiApp.Click(UiApp.Need(app.Window, "TabManager"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "AboutText") is not null, 10, "about text");
            var about = UiApp.Text(app.Need("AboutText"));
            Check(about.Contains("1.2.0") && about.Contains("AllStarsModCheck.exe") && about.Contains("do not need this manager"), "the settings page says mods work without the manager and names the checker", about);
            Check(app.CloseCleanly() == 0, "the manager closes with exit code 0");
        }
        var gameAfter = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Sonic", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        var steamAfter = Process.GetProcesses().Where(p => p.ProcessName.StartsWith("steam", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        Check(gameAfter.SetEquals(gameBefore) && steamAfter.IsSubsetOf(steamBefore), "no game or Steam process was started");
        f.Scn.Cleanup();
    }
}
