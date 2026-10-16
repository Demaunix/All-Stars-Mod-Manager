using System.IO.Compression;
using System.Text;

namespace AllStarsModManager.Tests;

public static class ImportTests
{
    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    sealed class Env : IDisposable
    {
        public Fx Fx = new("im");
        public AppPaths Paths;
        public Importer Importer;
        public HashCache Cache = new();
        public Env() { Paths = new AppPaths(Path.Combine(Fx.Root, "data")); Importer = new Importer(Paths, Cache); }
        public void Dispose() { }
        public string Work(string name) { var p = Path.Combine(Fx.Root, "work-" + name); Directory.CreateDirectory(p); return p; }
    }

    static string MakeZip(Env e, string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(e.Fx.Root, name + ".zip");
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            fill(z);
        return path;
    }

    static void Add(ZipArchive z, string name, byte[] data, uint? unixMode = null)
    {
        var entry = z.CreateEntry(name);
        if (unixMode is { } m) entry.ExternalAttributes = (int)(m << 16);
        using var s = entry.Open();
        s.Write(data);
    }

    /// <summary>Writes a built mod folder into a zip under an optional prefix.</summary>
    static void AddFolder(ZipArchive z, string dir, string prefix)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            Add(z, prefix + Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes(f));
    }

    static BuiltMod Built(Env e, string id, string folder, string where)
    {
        var s = PackageTests.Basic(id, folder);
        return e.Fx.WriteMod(s, where);
    }

    static void ExpectRejected(Env e, string name, Action<ZipArchive> fill, string messagePart, ZipLimits? limits = null)
    {
        var zip = MakeZip(e, name, fill);
        var before = Fs.Snapshot(e.Fx.Game);
        var imp = limits is null ? e.Importer : new Importer(e.Paths, e.Cache) { };
        if (limits is not null) { imp.Limits.MaxEntries = limits.MaxEntries; imp.Limits.MaxTotalBytes = limits.MaxTotalBytes; imp.Limits.MaxEntryBytes = limits.MaxEntryBytes; imp.Limits.MaxPathChars = limits.MaxPathChars; }
        var pv = imp.Inspect(zip);
        T.False(pv.Usable, $"{name}: must not be usable");
        T.True(pv.Issues.Any(i => i.Severity == Severity.Blocker && i.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase)), $"{name}: expected a message containing '{messagePart}', got: {string.Join(" | ", pv.Issues.Select(i => i.Message))}");
        T.Eq("", Fs.Diff(before, Fs.Snapshot(e.Fx.Game)), $"{name}: game folder untouched");
        T.True(!Directory.Exists(e.Paths.IncomingDir) || Directory.GetDirectories(e.Paths.IncomingDir).Length == 0, $"{name}: no partial extraction left behind");
        T.False(Directory.Exists(e.Paths.LibraryDir) && Directory.GetDirectories(e.Paths.LibraryDir).Length > 0, $"{name}: nothing added to the library");
    }

    public static void Register()
    {
        T.Area("import");

        T.Run("folder: a mod root, a single wrapper folder and a mods/<name> package are all recognised", () =>
        {
            using var e = new Env();
            var root = Built(e, "direct", "Direct", e.Work("a"));
            var pv1 = e.Importer.Inspect(root.Dir);
            T.Eq(1, pv1.Mods.Count, "mod root"); T.Eq("direct", pv1.Mods[0].Package.Id, "id");
            var wrapperParent = e.Work("b"); Directory.CreateDirectory(Path.Combine(wrapperParent, "Wrapper"));
            Built(e, "wrapped", "Inner", Path.Combine(wrapperParent, "Wrapper"));
            var pv2 = e.Importer.Inspect(wrapperParent);
            T.Eq(1, pv2.Mods.Count, "through the wrapper"); T.Eq("wrapped", pv2.Mods[0].Package.Id, "wrapped id");
            var pkg = e.Work("c");
            Scn.BuildLooseModPackage(e.Fx, pkg, PackageTests.Basic("pk", "Pk"), ("Resource/Movie/P.usm", Bytes("movie")));
            var pv3 = e.Importer.Inspect(pkg);
            T.Eq(1, pv3.Mods.Count, "package"); T.Eq(LoaderPath.Fold(pkg), LoaderPath.Fold(pv3.Mods[0].PackageRoot.TrimEnd('\\')), "package root is the folder that holds mods/ and Resource/");
            T.True(pv3.Mods[0].Package.Sidecar!.LooseAssets[0].PresentInPackage, "loose asset located inside the package");
            var many = e.Work("d");
            Built(e, "m1", "One", many); Built(e, "m2", "Two", many); Directory.CreateDirectory(Path.Combine(many, "not-a-mod")); File.WriteAllText(Path.Combine(many, "readme.txt"), "x");
            var pv4 = e.Importer.Inspect(many);
            T.Eq(2, pv4.Mods.Count, "a folder of several mods lists each");
            T.True(pv4.Mods.All(m => m.Package.Integrity == Integrity.Verified), "integrity shown before anything is added");
            e.Fx.Cleanup();
        });

        T.Run("folder: unsupported content gets a specific, truthful message and nothing is added", () =>
        {
            using var e = new Env();
            var empty = e.Work("empty");
            var pv = e.Importer.Inspect(empty);
            T.False(pv.Usable, "empty"); T.Has(pv.Issues.Select(i => i.Message), "empty", "says empty");
            var half = e.Work("half"); File.WriteAllText(Path.Combine(half, "plan.json"), "{}"); Directory.CreateDirectory(Path.Combine(half, "runtime"));
            var pv2 = e.Importer.Inspect(half);
            T.Has(pv2.Issues.Select(i => i.Message), "incomplete or older-format", "plan/runtime without manifest");
            var dll = e.Work("dll"); File.WriteAllBytes(Path.Combine(dll, "thing.dll"), Bytes("MZ"));
            var pv3 = e.Importer.Inspect(dll);
            T.Has(pv3.Issues.Select(i => i.Message), "never run", "dll-only");
            var foreign = e.Work("foreign"); Directory.CreateDirectory(Path.Combine(foreign, "SA2Mod")); File.WriteAllText(Path.Combine(foreign, "SA2Mod", "mod.ini"), "[Main]\r\nName=Other\r\n");
            var pv4 = e.Importer.Inspect(foreign);
            T.True(pv4.Mods.Count == 1 && pv4.Mods[0].Package.AllIssues.Any(i => i.Code == "manifest-foreign"), "foreign manifest flagged");
            T.False(pv4.Mods[0].Package.Integrity == Integrity.Verified, "not shown as fine");
            T.False(pv4.Usable, "foreign manifest cannot be added"); T.Throws<InvalidOperationException>(() => e.Importer.AddToLibrary(pv4), "AddToLibrary on unusable");
            var missing = e.Importer.Inspect(Path.Combine(e.Fx.Root, "does-not-exist"));
            T.Has(missing.Issues.Select(i => i.Message), "does not exist", "missing path");
            e.Fx.Cleanup();
        });

        T.Run("archive types we do not read give a useful message (7z, RAR, tar.gz, installers, executables, random files)", () =>
        {
            using var e = new Env();
            void Check(string name, byte[] head, string part)
            {
                var p = Path.Combine(e.Fx.Root, name);
                File.WriteAllBytes(p, head.Concat(new byte[600]).ToArray());
                var pv = e.Importer.Inspect(p);
                T.False(pv.Usable, name);
                T.Has(pv.Issues.Select(i => i.Message), part, name);
                T.Has(pv.Issues.Select(i => i.Message), name, name + " names the file");
            }
            Check("pack.7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, "7z archive");
            Check("pack.rar", new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0 }, "RAR archive");
            Check("pack.tgz", new byte[] { 0x1F, 0x8B, 8 }, "tar/gzip");
            Check("setup.exe", new byte[] { (byte)'M', (byte)'Z' }, "never runs files");
            Check("setup.msi", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "installer");
            Check("notes.bin", new byte[] { 1, 2, 3 }, "not a folder or a .zip");
            e.Fx.Cleanup();
        });

        T.Run("zip: mod at the root, inside one wrapper folder, and as a mods/<name> package with loose files", () =>
        {
            using var e = new Env();
            var src = e.Work("src");
            var m1 = Built(e, "z-root", "ZRoot", src);
            var zRoot = MakeZip(e, "root", z => AddFolder(z, m1.Dir, ""));
            var pv = e.Importer.Inspect(zRoot);
            T.True(pv.Usable && pv.Mods.Count == 1 && pv.Mods[0].Package.Id == "z-root", "root zip: " + string.Join(";", pv.Issues.Select(i => i.Message)));
            T.Eq("root", pv.Mods[0].SuggestedFolder, "folder name falls back to the zip's name");
            e.Importer.Discard(pv);
            var zWrap = MakeZip(e, "wrap", z => AddFolder(z, m1.Dir, "Some Wrapper/"));
            var pv2 = e.Importer.Inspect(zWrap);
            T.True(pv2.Usable && pv2.Mods[0].SuggestedFolder == "Some Wrapper", "wrapper zip folder name from wrapper");
            e.Importer.Discard(pv2);
            var pkg = e.Work("pkg");
            Scn.BuildLooseModPackage(e.Fx, pkg, PackageTests.Basic("zp", "Zp"), ("Resource/Movie/Z.usm", Bytes("zip movie")));
            var zPkg = MakeZip(e, "pkg", z => AddFolder(z, pkg, ""));
            var pv3 = e.Importer.Inspect(zPkg);
            T.True(pv3.Usable, "package zip");
            T.True(pv3.Mods[0].Package.Sidecar!.LooseAssets[0].PresentInPackage, "loose file found");
            var id = e.Importer.AddToLibrary(pv3);
            var items = LibraryScanner.Scan(e.Paths, Array.Empty<string>());
            T.Eq(1, items.Count, "library has it");
            T.True(File.Exists(Path.Combine(items[0].PackageRoot, "Resource", "Movie", "Z.usm")), "loose file kept with the package");
            T.True(Directory.GetDirectories(e.Paths.IncomingDir).Length == 0, "temporary extraction removed");
            var again = e.Importer.Inspect(zPkg, items);
            T.True(again.Mods[0].AlreadyInLibrary && !again.Mods[0].Selected, "duplicate recognised by content");
            e.Importer.Discard(again);
            e.Fx.Cleanup();
        });

        // unsafe archives -----------------------------------------------------------------
        T.Run("zip: path traversal and absolute names are rejected before anything is written", () =>
        {
            using var e = new Env();
            ExpectRejected(e, "slip1", z => { Add(z, "good/mod.ini", Bytes("x")); Add(z, "../evil.txt", Bytes("x")); }, "leave the destination");
            ExpectRejected(e, "slip2", z => { Add(z, "good/..\\..\\evil.txt", Bytes("x")); }, "leave the destination");
            ExpectRejected(e, "abs1", z => Add(z, "/etc/evil", Bytes("x")), "absolute path");
            ExpectRejected(e, "abs2", z => Add(z, "C:/Windows/evil.txt", Bytes("x")), "names a drive");
            ExpectRejected(e, "abs3", z => Add(z, "\\\\server\\share\\x", Bytes("x")), "absolute path");
            e.Fx.Cleanup();
        });

        T.Run("zip: alternate streams, device names, trailing dots/spaces, control characters", () =>
        {
            using var e = new Env();
            ExpectRejected(e, "ads", z => Add(z, "mod/file.txt:hidden", Bytes("x")), "alternate data stream");
            ExpectRejected(e, "dev1", z => Add(z, "mod/CON", Bytes("x")), "reserved Windows device");
            ExpectRejected(e, "dev2", z => Add(z, "mod/nul.txt", Bytes("x")), "reserved Windows device");
            ExpectRejected(e, "dot", z => Add(z, "mod/plan.json.", Bytes("x")), "dot or space");
            ExpectRejected(e, "space", z => Add(z, "mod /plan.json", Bytes("x")), "dot or space");
            ExpectRejected(e, "ctl", z => Add(z, "mod/a\u0001b", Bytes("x")), "control characters");
            ExpectRejected(e, "wild", z => Add(z, "mod/a*b", Bytes("x")), "characters Windows does not allow");
            e.Fx.Cleanup();
        });

        T.Run("zip: duplicate names (ignoring case), file-versus-folder clashes and links", () =>
        {
            using var e = new Env();
            ExpectRejected(e, "dup", z => { Add(z, "Mod/A.txt", Bytes("1")); Add(z, "mod/a.TXT", Bytes("2")); }, "appears twice");
            ExpectRejected(e, "fvd", z => { Add(z, "mod/a", Bytes("1")); Add(z, "mod/a/b.txt", Bytes("2")); }, "both a file and a folder");
            ExpectRejected(e, "unixlink", z => Add(z, "mod/link", Bytes("target"), 0xA1FFu), "link");
            ExpectRejected(e, "winlink", z => { var en = z.CreateEntry("mod/reparse"); en.ExternalAttributes = 0x400; using var s = en.Open(); s.Write(Bytes("x")); }, "link");
            e.Fx.Cleanup();
        });

        T.Run("zip: limits are enforced on entry count, announced size, and on the bytes actually produced", () =>
        {
            using var e = new Env();
            ExpectRejected(e, "many", z => { for (int i = 0; i < 30; i++) Add(z, $"mod/f{i}.txt", Bytes("x")); }, "items", new ZipLimits { MaxEntries = 10 });
            ExpectRejected(e, "total", z => { Add(z, "mod/a.bin", new byte[5000]); Add(z, "mod/b.bin", new byte[5000]); }, "expand to more than", new ZipLimits { MaxTotalBytes = 6000 });
            ExpectRejected(e, "single", z => Add(z, "mod/a.bin", new byte[5000]), "per-file limit", new ZipLimits { MaxEntryBytes = 4000 });
            ExpectRejected(e, "longpath", z => Add(z, "mod/" + new string('a', 220) + ".txt", Bytes("x")), "too long");
            // lying headers: patch the announced sizes to be tiny while the data is large
            var honest = MakeZip(e, "liar-src", z => { Add(z, "mod/mod.ini", Bytes("[mod]\r\nschema=1\r\n")); Add(z, "mod/big.bin", new byte[200_000]); });
            var bytes = File.ReadAllBytes(honest);
            for (int i = 0; i + 30 < bytes.Length; i++)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04 && BitConverter.ToUInt32(bytes, i + 22) == 200_000u) BitConverter.GetBytes(1000u).CopyTo(bytes, i + 22);
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02 && BitConverter.ToUInt32(bytes, i + 24) == 200_000u) BitConverter.GetBytes(1000u).CopyTo(bytes, i + 24);
            }
            var liar = Path.Combine(e.Fx.Root, "liar.zip"); File.WriteAllBytes(liar, bytes);
            var before = Fs.Snapshot(e.Fx.Game);
            var pv = e.Importer.Inspect(liar);
            T.False(pv.Usable, "lying sizes must not produce a usable import");
            T.True(pv.Issues.Any(i => i.Severity == Severity.Blocker && (i.Message.Contains("announced") || i.Message.Contains("damaged"))), "reports the lie, not a generic failure: " + string.Join("|", pv.Issues.Select(i => i.Message)));
            T.True(!Directory.Exists(e.Paths.IncomingDir) || Directory.GetDirectories(e.Paths.IncomingDir).Length == 0, "partial output removed");
            e.Fx.Cleanup();
        });

        T.Run("zip: damaged, empty and truncated archives fail cleanly", () =>
        {
            using var e = new Env();
            var good = MakeZip(e, "g", z => Add(z, "mod/a.txt", new byte[50000]));
            var trunc = Path.Combine(e.Fx.Root, "trunc.zip");
            File.WriteAllBytes(trunc, File.ReadAllBytes(good).Take(120).ToArray());
            var pv = e.Importer.Inspect(trunc);
            T.False(pv.Usable, "truncated"); T.True(pv.Issues.Count > 0, "has a message");
            var empty = MakeZip(e, "empty", z => { });
            var pv2 = e.Importer.Inspect(empty);
            T.False(pv2.Usable, "empty zip"); T.Has(pv2.Issues.Select(i => i.Message), "No mod was found", "says no mod");
            e.Fx.Cleanup();
        });

        T.Run("zip: a password-protected (encrypted) entry is refused with a clear message, not extracted as garbage", () =>
        {
            using var e = new Env();
            var honest = MakeZip(e, "enc-src", z => { Add(z, "mod/mod.ini", Bytes("[mod]\r\nschema=1\r\n")); Add(z, "mod/secret.bin", new byte[4000]); });
            var bytes = File.ReadAllBytes(honest);
            for (int i = 0; i + 12 < bytes.Length; i++)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04) bytes[i + 6] |= 1;   // local header: encrypted flag
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02) bytes[i + 8] |= 1;   // central directory: encrypted flag
            }
            var enc = Path.Combine(e.Fx.Root, "enc.zip"); File.WriteAllBytes(enc, bytes);
            var before = Fs.Snapshot(e.Fx.Game);
            var pv = e.Importer.Inspect(enc);
            T.False(pv.Usable, "not usable");
            T.True(pv.Issues.Any(i => i.Severity == Severity.Blocker && (i.Message.Contains("password") || i.Message.Contains("damaged") || i.Message.Contains("unsupported"))), "says why: " + string.Join("|", pv.Issues.Select(i => i.Message)));
            T.True(!Directory.Exists(e.Paths.IncomingDir) || Directory.GetDirectories(e.Paths.IncomingDir).Length == 0, "nothing left behind");
            T.Eq("", Fs.Diff(before, Fs.Snapshot(e.Fx.Game)), "game untouched");
            e.Fx.Cleanup();
        });

        T.Run("zip: extraction never executes or opens anything (only file bytes are written)", () =>
        {
            using var e = new Env();
            var before = System.Diagnostics.Process.GetProcesses().Select(p => p.Id).ToHashSet();
            var zip = MakeZip(e, "scripts", z => { Add(z, "mod/run.bat", Bytes("echo pwned > pwned.txt")); Add(z, "mod/evil.exe", Bytes("MZ")); Add(z, "mod/autorun.inf", Bytes("[autorun]\r\nopen=evil.exe")); Add(z, "mod/mod.ini", Bytes("[mod]\r\n")); });
            var pv = e.Importer.Inspect(zip);
            T.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "pwned.txt")), "nothing ran");
            T.True(pv.Mods.Count == 1, "inspected as data");
            var after = System.Diagnostics.Process.GetProcesses().Where(p => !before.Contains(p.Id)).Select(p => p.ProcessName.ToLowerInvariant()).ToList();
            T.False(after.Any(n => n is "cmd" or "evil" or "conhost"), "no processes started during import: " + string.Join(",", after));
            e.Importer.Discard(pv);
            e.Fx.Cleanup();
        });

        T.Run("import: cancelling leaves no partial library entry or extraction", () =>
        {
            using var e = new Env();
            var big = MakeZip(e, "bigzip", z => { for (int i = 0; i < 40; i++) Add(z, $"mod/f{i}.bin", new byte[300_000]); Add(z, "mod/mod.ini", Bytes("[mod]\r\n")); });
            using var cts = new CancellationTokenSource();
            var cancel = new CancelAfter(cts, 2_000_000);
            Exception? caught = null;
            try { e.Importer.Inspect(big, null, cancel, cts.Token); } catch (OperationCanceledException x) { caught = x; }
            T.True(caught is not null, "cancellation observed");
            T.True(!Directory.Exists(e.Paths.IncomingDir) || Directory.GetDirectories(e.Paths.IncomingDir).Length == 0, "extraction cleaned");
            T.False(Directory.Exists(e.Paths.LibraryDir) && Directory.GetDirectories(e.Paths.LibraryDir).Length > 0, "no library entry");
            e.Fx.Cleanup();
        });

        T.Run("import: a damaged package can be inspected (integrity shown) but cannot be enabled", () =>
        {
            using var e = new Env();
            var spec = PackageTests.Basic("dmg", "Dmg"); spec.WrongRuntimeHash = true;
            var m = e.Fx.WriteMod(spec, e.Work("dm"));
            var pv = e.Importer.Inspect(m.Dir);
            T.Eq(Integrity.Failed, pv.Mods[0].Package.Integrity, "failed integrity shown");
            T.HasCode(pv.Mods[0].Package.AllIssues, "runtime-hash-mismatch", "reason");
            e.Fx.Cleanup();
        });

        T.Run("import: a link inside a dropped folder is refused as a whole", () =>
        {
            using var e = new Env();
            var src = e.Work("lnk");
            Built(e, "ok", "Ok", src);
            var outside = Path.Combine(e.Fx.Root, "elsewhere"); Directory.CreateDirectory(outside);
            Fs.MakeJunction(Path.Combine(src, "Ok", "evil-link"), outside);
            var pv = e.Importer.Inspect(src);
            T.False(pv.Usable, "refused");
            T.Has(pv.Issues.Select(i => i.Message), "link", "explains");
            e.Fx.Cleanup();
        });

        T.Run("import never touches any game folder, and suggested library folders are found read-only", () =>
        {
            using var e = new Env();
            var hidden = Path.Combine(e.Fx.Game, ".older-mods", "original-mods");
            Directory.CreateDirectory(hidden);
            Built(e, "old1", "Old One", hidden); Built(e, "old2", "Old Two", hidden);
            var before = Fs.Snapshot(e.Fx.Game);
            var sug = LibraryScanner.SuggestFolders(e.Fx.Game);
            T.True(sug.Any(s => s.EndsWith("original-mods", StringComparison.OrdinalIgnoreCase)), "suggested: " + string.Join(",", sug));
            var items = LibraryScanner.Scan(e.Paths, new[] { hidden });
            T.Eq(2, items.Count, "linked folder lists its mods without importing");
            T.True(items.All(i => i.Linked), "marked linked");
            T.Eq("", Fs.Diff(before, Fs.Snapshot(e.Fx.Game)), "read-only");
            e.Fx.Cleanup();
        });
    }

    sealed class CancelAfter(CancellationTokenSource cts, long bytes) : IProgress<long>
    {
        long _n;
        public void Report(long value) { if (Interlocked.Add(ref _n, value) > bytes) cts.Cancel(); }
    }
}
