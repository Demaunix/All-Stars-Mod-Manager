using System.Text;
using System.Text.RegularExpressions;

namespace AllStarsModManager;

/// <summary>
/// Reads one mod folder without executing anything: manifest fields (through the loader's own INI call),
/// the plan (through a port of the loader's parser and checks), the optional description file, and cheap file facts.
/// The expensive hashing of large payloads is done by <see cref="PackageVerifier"/>.
/// </summary>
public static class PackageInspector
{
    const int MaxPlanBytes = 16 * 1024 * 1024;     // same limit as the loader's text_file()
    static readonly Regex DisabledName = new(@"^mod\.ini\.disabled", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PackageInfo Inspect(string dir, string? manifestChoice = null, string? packageRoot = null)
    {
        var pkg = new PackageInfo { Dir = LoaderPath.Full(dir), PackageRoot = packageRoot };
        if (!Directory.Exists(pkg.Dir)) { pkg.Issues.Add(new(Severity.Blocker, "folder-missing", "This folder no longer exists.")); return pkg; }
        if (LoaderPath.IsReparse(pkg.Dir))
        {
            pkg.Issues.Add(new(Severity.Blocker, "link-in-folder", "This folder is a link (junction or symbolic link). The game's loader refuses links inside the mods folder and would start with no mods at all."));
            return pkg;
        }

        FindManifest(pkg, manifestChoice);
        pkg.Sidecar = SidecarReader.TryRead(pkg.Dir, pkg.Issues);
        pkg.IsResearchCard = pkg.Sidecar?.Installable == false;
        pkg.ConfigFiles = ListConfigFiles(pkg.Dir);

        if (pkg.Manifest is null || pkg.Manifest.State == ManifestState.NoManifest)
        {
            pkg.Issues.Add(pkg.IsResearchCard
                ? new(Severity.Info, "research-card", "This is a research note, not an installable mod.")
                : new(Severity.Info, "no-manifest", "No mod.ini is present, so the game's loader ignores this folder."));
            return pkg;
        }
        if (pkg.Manifest.State == ManifestState.DisabledAmbiguous && manifestChoice is null)
            pkg.Issues.Add(new(Severity.Warning, "manifest-ambiguous", "This folder has several switched-off manifests. Choose which one to use before enabling.",
                string.Join(", ", pkg.Manifest.Alternates)));

        ReadManifestFields(pkg);
        if (pkg.Manifest.Id is not null) CheckPlanAndRuntime(pkg);
        CrossCheckSidecar(pkg);
        return pkg;
    }

    // ---- manifest discovery -------------------------------------------------------------

    static void FindManifest(PackageInfo pkg, string? choice)
    {
        string? active = null;
        var disabled = new List<string>();
        var other = new List<string>();
        foreach (var f in Directory.EnumerateFiles(pkg.Dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Equals("mod.ini", StringComparison.OrdinalIgnoreCase)) active = f;
            else if (f.StartsWith("mod.ini.", StringComparison.OrdinalIgnoreCase))
            {
                if (DisabledName.IsMatch(f)) disabled.Add(f); else other.Add(f);
            }
        }
        var m = new ManifestInfo { OtherManifestLike = other };
        if (active is not null)
        {
            m.State = ManifestState.Active;
            m.FileName = active;
            m.Alternates = disabled;
        }
        else if (disabled.Count == 1)
        {
            m.State = ManifestState.Disabled;
            m.FileName = disabled[0];
        }
        else if (disabled.Count > 1)
        {
            m.State = ManifestState.DisabledAmbiguous;
            m.Alternates = disabled;
            m.FileName = choice is not null && disabled.Contains(choice, StringComparer.OrdinalIgnoreCase)
                ? disabled.First(d => d.Equals(choice, StringComparison.OrdinalIgnoreCase))
                : disabled[0];
            m.ChoiceMade = choice is not null && disabled.Contains(choice, StringComparer.OrdinalIgnoreCase);
            if (choice is not null && !disabled.Contains(choice, StringComparer.OrdinalIgnoreCase))
                pkg.Issues.Add(new(Severity.Blocker, "manifest-choice-invalid", $"The chosen manifest \"{PackageInfo.Clean(choice, 80)}\" is not in this folder."));
        }
        else
        {
            m.State = ManifestState.NoManifest;
            pkg.Manifest = m;
            return;
        }
        m.FullPath = Path.Combine(pkg.Dir, m.FileName);
        if (LoaderPath.IsReparse(m.FullPath))
            pkg.Issues.Add(new(Severity.Blocker, "link-in-folder", $"{m.FileName} is a link; the loader refuses it."));
        pkg.Manifest = m;
    }

    static void ReadManifestFields(PackageInfo pkg)
    {
        var m = pkg.Manifest!;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(m.FullPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            pkg.Issues.Add(new(Severity.Blocker, "manifest-unreadable", $"{m.FileName} cannot be read.", e.Message));
            return;
        }
        m.FileSha = Sha.OfBytes(bytes);
        m.Encoding = LoaderIni.DescribeEncoding(bytes.Length > 4096 ? bytes[..4096] : bytes);
        if (LoaderIni.HasAmbiguousNonAscii(bytes))
            pkg.Issues.Add(new(Severity.Warning, "manifest-encoding", "The manifest contains non-ASCII characters without a byte-order mark; the game's loader reads it with the system code page, so names may appear garbled."));

        try
        {
            var sections = IniDocument.Load(bytes).Lines.Where(l => l.Key is null && l.Section is not null).Select(l => l.Section!).ToList();
            if (sections.Any(s => s.Equals("Main", StringComparison.OrdinalIgnoreCase)) && !sections.Any(s => s.Equals("mod", StringComparison.OrdinalIgnoreCase)))
                pkg.Issues.Add(new(Severity.Blocker, "manifest-foreign", "This mod.ini has a [Main] section instead of [mod]. It looks like a mod for a different mod loader or game, not an All-Stars Racing loader mod."));
        }
        catch (InvalidDataException) { }
        string Get(string key, bool required, out bool ok)
        {
            var f = LoaderIni.ReadModField(m.FullPath, key);
            ok = f.Ok;
            if (!f.Ok && required)
                pkg.Issues.Add(new(Severity.Blocker, "manifest-field", $"The manifest field \"{key}\" is missing, empty or too long. The loader requires it.", Subject: key));
            return f.Ok ? f.Value : "";
        }

        var schema = Get("schema", true, out var okSchema);
        m.Schema = okSchema ? schema : null;
        if (okSchema && schema != "1")
            pkg.Issues.Add(new(Severity.Blocker, "manifest-schema", $"This manifest uses format version {PackageInfo.Clean(schema, 20)}; the installed loader only understands version 1."));
        var id = Get("id", true, out var okId); if (okId) m.Id = id;
        var plan = Get("plan", true, out var okPlan); if (okPlan) m.PlanRel = plan;
        var planSha = Get("plan_sha256", true, out var okPs); if (okPs) m.PlanSha = planSha;
        var rt = Get("runtime", true, out var okRt); if (okRt) m.RuntimeRel = rt;
        var rtSha = Get("runtime_sha256", true, out var okRs); if (okRs) m.RuntimeSha = rtSha;
        var name = Get("name", false, out var okName); if (okName) m.Name = PackageInfo.Clean(name);
        var ver = Get("version", false, out var okVer); if (okVer) m.Version = PackageInfo.Clean(ver, 80);

        foreach (var (label, value) in new[] { ("plan_sha256", m.PlanSha), ("runtime_sha256", m.RuntimeSha) })
        {
            if (value is null) continue;
            if (Sha.IsLoaderHash(value)) continue;
            pkg.Issues.Add(Sha.LooksLikeHashIgnoringCase(value)
                ? new(Severity.Blocker, "manifest-hash-case", $"{label} must be written in lowercase; the loader compares it exactly and would refuse this mod.", Subject: label)
                : new(Severity.Blocker, "manifest-hash-format", $"{label} is not a 64-digit hexadecimal hash.", Subject: label));
        }
        if (m.Id is not null && m.Id.Any(c => c is < ' ' or > '~'))
            pkg.Issues.Add(new(Severity.Warning, "id-nonascii", "The mod ID contains unusual characters. It still works if the loader reads the file the same way, but sharing it may be unreliable."));
    }

    // ---- plan and runtime ---------------------------------------------------------------

    static void CheckPlanAndRuntime(PackageInfo pkg)
    {
        var m = pkg.Manifest!;
        if (m.PlanRel is not null) CheckPlan(pkg, m);
        if (m.RuntimeRel is not null) CheckRuntime(pkg, m);
    }

    static void CheckPlan(PackageInfo pkg, ManifestInfo m)
    {
        string path;
        try
        {
            path = LoaderPath.Child(pkg.Dir, m.PlanRel!);
            LoaderPath.NoReparse(pkg.Dir, path);
        }
        catch (PathRuleException e) { pkg.Issues.Add(new(Severity.Blocker, "plan-path", "The plan file path is not allowed: " + e.Message)); return; }
        pkg.PlanFullPath = path;
        if (!File.Exists(path)) { pkg.Issues.Add(new(Severity.Blocker, "plan-missing", $"The plan file \"{PackageInfo.Clean(m.PlanRel, 80)}\" is missing.")); return; }

        byte[] bytes;
        try
        {
            var len = new FileInfo(path).Length;
            if (len > MaxPlanBytes) { pkg.Issues.Add(new(Severity.Blocker, "plan-large", "The plan file is larger than the loader accepts (16 MB).")); return; }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            pkg.Issues.Add(new(Severity.Blocker, "plan-unreadable", "The plan file cannot be read.", e.Message)); return;
        }
        var actual = Sha.OfBytes(bytes);
        bool hashOk = m.PlanSha is not null && actual == m.PlanSha;
        if (m.PlanSha is not null && Sha.IsLoaderHash(m.PlanSha) && !hashOk)
            pkg.Issues.Add(new(Severity.Blocker, "plan-hash-mismatch", "The plan file does not match the hash recorded in the manifest, so the loader would refuse it. The mod may be damaged or edited.", $"expected {m.PlanSha}, found {actual}"));

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            pkg.Issues.Add(new(Severity.Blocker, "plan-bom", "The plan file starts with a UTF-8 byte-order mark, which the loader's plan reader cannot parse."));

        JVal root;
        try { root = StrictJson.Parse(bytes); }
        catch (PlanFormatException e)
        {
            if (!pkg.Issues.Any(i => i.Code == "plan-bom"))
                pkg.Issues.Add(new(Severity.Blocker, "plan-invalid", "The plan file is not valid for the loader.", e.Message));
            return;
        }
        pkg.Plan = ParsePlan(root, pkg, bytes.Length);
    }

    static PlanInfo? ParsePlan(JVal root, PackageInfo pkg, int bytes)
    {
        var plan = new PlanInfo { Bytes = bytes };
        void Block(string code, string msg, string? detail = null) => pkg.Issues.Add(new(Severity.Blocker, code, msg, detail));
        try
        {
            plan.Schema = root.At("schema").U32();
            if (plan.Schema != 1) { Block("plan-schema", $"The plan uses format version {plan.Schema}; the loader only understands version 1."); return plan; }
            if (root.Has("mod_id") && root.At("mod_id").Kind == JKind.String)
            {
                plan.ModId = root.At("mod_id").S;
                if (pkg.Manifest?.Id is { } id && plan.ModId != id)
                    Block("id-mismatch", $"The plan says it belongs to \"{PackageInfo.Clean(plan.ModId, 60)}\" but the manifest ID is \"{PackageInfo.Clean(id, 60)}\".");
            }
            var groups = root.At("archive_groups").List();
            var seen = new HashSet<string>();
            int gi = 0;
            foreach (var g in groups)
            {
                gi++;
                var pg = new PlanGroup();
                plan.Groups.Add(pg);
                try { ParseGroup(g, pg, pkg, seen, gi); }
                catch (PlanFormatException e) { pg.Valid = false; Block("plan-group", $"Plan entry {gi}{(pg.PathAsWritten.Length > 0 ? " (" + PackageInfo.Clean(pg.PathAsWritten, 80) + ")" : "")}: {e.Message}"); }
                catch (PathRuleException e) { pg.Valid = false; Block("plan-path", $"Plan entry {gi}: path not allowed — {e.Message}"); }
            }
        }
        catch (PlanFormatException e) { Block("plan-invalid", "The plan is missing required information.", e.Message); }
        return plan;
    }

    static void ParseGroup(JVal g, PlanGroup pg, PackageInfo pkg, HashSet<string> seen, int gi)
    {
        pg.PathAsWritten = g.At("path").Str();
        var rel = LoaderPath.Relative(pg.PathAsWritten);
        pg.LogicalRel = rel;
        pg.LogicalKey = LoaderPath.Fold(rel);
        if (!seen.Add(pg.LogicalKey)) throw new PlanFormatException("the same target file is listed twice in this plan");
        pg.Format = g.At("format").Str();
        var folded = pg.LogicalKey;
        string dir = pkg.Dir;

        PlanPayload Payload(JVal holder, string role, bool requireSize)
        {
            var relPayload = holder.At("payload").Str();
            var full = LoaderPath.Child(dir, relPayload);
            LoaderPath.NoReparse(dir, full);
            var sha = holder.At("payload_sha256").Str();
            if (!Sha.IsLoaderHash(sha)) throw new PlanFormatException($"payload hash for \"{PackageInfo.Clean(relPayload, 60)}\" must be 64 lowercase hexadecimal digits");
            // Only archive members have their size enforced by the loader (stored_size); other payloads are checked by hash alone.
            long? size = requireSize ? holder.At("stored_size").U32() : null;
            var p = new PlanPayload { Rel = relPayload, FullPath = full, Sha = sha, ExpectedSize = size, Role = role };
            pg.Payloads.Add(p);
            return p;
        }
        string HashField(JVal holder, string field)
        {
            var s = holder.At(field).Str();
            if (!Sha.IsLoaderHash(s)) throw new PlanFormatException($"\"{field}\" must be 64 lowercase hexadecimal digits");
            return s;
        }

        if (pg.Format == "rtsc_merge")
        {
            if (!(folded.StartsWith("shadercache\\", StringComparison.Ordinal) && folded.Length > 5 && folded.EndsWith(".rtsc", StringComparison.Ordinal)))
                throw new PlanFormatException("shader cache entries must be ShaderCache/*.rtsc files");
            Payload(g, "shader cache seed", false);
            pg.RtscKeySize = g.At("key_size").U32();
            return;
        }
        if (!(folded.StartsWith("resource\\", StringComparison.Ordinal) || folded.StartsWith("scripts\\", StringComparison.Ordinal)))
            throw new PlanFormatException("only files under Resource and Scripts can be replaced");
        switch (pg.Format)
        {
            case "xpac":
                pg.BaseSha = HashField(g, "base_sha256");
                var seenKeys = new HashSet<uint>();
                foreach (var o in g.At("operations").List())
                {
                    var op = new PlanOp { Key = ReadKey(o.At("key")) };
                    if (!seenKeys.Add(op.Key)) throw new PlanFormatException($"archive member {op.Key:X8} is changed twice in one entry");
                    op.Kind = o.At("kind").Str();
                    if (op.Kind == "replace") op.BaseStoredSha = HashField(o, "base_stored_sha256");
                    else if (op.Kind != "add") throw new PlanFormatException($"unknown operation \"{PackageInfo.Clean(op.Kind, 20)}\"");
                    if (o.Has("name") && o.At("name").Kind == JKind.String) op.Name = PackageInfo.Clean(o.At("name").S, 120);
                    Payload(o, $"member {op.Key:X8}", true);
                    if (o.Has("entry_flags") && o.At("entry_flags").U32() != 0) throw new PlanFormatException("archive member flags are not supported");
                    pg.Ops.Add(op);
                    pg.Keys.Add(op.Key);
                }
                pg.ExpectedComposedSha = HashField(g, "expected_composed_sha256");
                pg.ExpectedComposedSize = g.At("expected_composed_size").U32();
                pg.ExpectedMemberCount = g.At("expected_member_count").U32();
                break;
            case "file":
                pg.BaseSha = HashField(g, "base_sha256");
                Payload(g, "replacement file", false);
                break;
            case "copy":
                Payload(g, "copied file", false);
                break;
            default:
                throw new PlanFormatException($"unsupported entry type \"{PackageInfo.Clean(pg.Format, 30)}\"");
        }
    }

    static uint ReadKey(JVal j)
    {
        if (j.Kind == JKind.Number) return j.U32();
        var s = j.Str();
        if (s.Length != 8) throw new PlanFormatException("archive keys must be numbers or 8 hexadecimal digits");
        uint k = 0;
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) throw new PlanFormatException("archive keys must be numbers or 8 hexadecimal digits");
            k = (k << 4) | Convert.ToUInt32(c.ToString(), 16);
        }
        return k;
    }

    static void CheckRuntime(PackageInfo pkg, ManifestInfo m)
    {
        string path;
        try
        {
            path = LoaderPath.Child(pkg.Dir, m.RuntimeRel!);
            LoaderPath.NoReparse(pkg.Dir, path);
        }
        catch (PathRuleException e) { pkg.Issues.Add(new(Severity.Blocker, "runtime-path", "The runtime file path is not allowed: " + e.Message)); return; }
        pkg.RuntimeFullPath = path;
        if (!File.Exists(path)) { pkg.Issues.Add(new(Severity.Blocker, "runtime-missing", $"The runtime file \"{PackageInfo.Clean(m.RuntimeRel, 80)}\" is missing.")); return; }
        pkg.RuntimeBytes = new FileInfo(path).Length;
        // The runtime is only read as bytes for hashing; it is never loaded or executed by the manager.
        string actual;
        try { actual = Sha.OfFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            pkg.Issues.Add(new(Severity.Blocker, "runtime-unreadable", "The runtime file cannot be read, so it cannot be checked.", e.Message));
            return;
        }
        if (m.RuntimeSha is not null && Sha.IsLoaderHash(m.RuntimeSha) && actual != m.RuntimeSha)
            pkg.Issues.Add(new(Severity.Blocker, "runtime-hash-mismatch", "The runtime file does not match the hash recorded in the manifest, so the loader would refuse it. It may be damaged, replaced or from another build.", $"expected {m.RuntimeSha}, found {actual}"));
        CheckRuntimeImage(pkg, path);
    }

    /// <summary>Names the loader looks up in every runtime with GetProcAddress (host.cpp, discover()/SsasrModsHostInitialize).</summary>
    public static readonly string[] RequiredRuntimeExports = { "SsasrModInitialize", "SsasrModRollback" };

    /// <summary>
    /// Reads the runtime's PE headers (never loads it) and reports what would make the loader's runtime-loading step fail:
    /// not a DLL, not 32-bit x86 (the game and host are x86), or a missing SsasrModInitialize / SsasrModRollback export.
    /// </summary>
    static void CheckRuntimeImage(PackageInfo pkg, string path)
    {
        var pe = PeInspector.Inspect(path);
        pkg.Runtime = pe;
        if (!pe.IsPe)
        {
            pkg.Issues.Add(new(Severity.Blocker, "runtime-not-pe", "The runtime file is not a valid Windows DLL, so the loader could not load it and would start the game with no mods.", pe.Problem));
            return;
        }
        if (!pe.IsX86)
            pkg.Issues.Add(new(Severity.Blocker, "runtime-wrong-machine", $"The runtime is built for {PeSummary.MachineName(pe.Machine)}{(pe.IsPe32Plus ? " (64-bit)" : "")}, but the game and its loader are 32-bit x86, so the loader could not load it."));
        if (!pe.IsDll)
            pkg.Issues.Add(new(Severity.Blocker, "runtime-not-dll", "The runtime file is a program, not a DLL, so the loader could not load it."));
        if (!pe.ExportsRead)
            pkg.Issues.Add(new(Severity.Warning, "runtime-exports-unreadable", "The list of functions the runtime exports could not be read, so it cannot be confirmed that it has the entry points the loader needs.", pe.Problem));
        else
        {
            // A name that is listed is not enough: the loader looks the function up by name (GetProcAddress), which goes through the ordinal and function tables.
            var missing = RequiredRuntimeExports.Where(n => !pe.ExportNames.Contains(n, StringComparer.Ordinal)).ToList();
            var noCode = RequiredRuntimeExports.Where(n => pe.ExportKinds.TryGetValue(n, out var k) && k is ExportKind.NoAddress or ExportKind.Invalid).ToList();
            var forwarded = RequiredRuntimeExports.Where(n => pe.ExportKinds.TryGetValue(n, out var k) && k == ExportKind.Forwarder).ToList();
            if (!pe.ExportNamesSorted && RequiredRuntimeExports.Any(n => pe.ExportNames.Contains(n, StringComparer.Ordinal)))
                pkg.Issues.Add(new(Severity.Warning, "runtime-exports-unreadable", "The runtime's export name table is not in sorted order (or repeats a name). Windows finds names by binary search, so it cannot be confirmed that the loader will find the entry points."));
            if (noCode.Count > 0)
                pkg.Issues.Add(new(Severity.Blocker, "runtime-exports-missing", $"The runtime lists {string.Join(" and ", noCode)} in its export table, but the entry has no valid code address, so the loader's lookup would fail and it would start the game with no mods."));
            if (forwarded.Count > 0)
                pkg.Issues.Add(new(Severity.Warning, "runtime-exports-unreadable", $"{string.Join(" and ", forwarded)} {(forwarded.Count == 1 ? "is a forwarded export" : "are forwarded exports")} (pointing into another module); that is not followed here, so it cannot be confirmed that the loader will find the entry point."));
            if (missing.Count > 0)
                pkg.Issues.Add(new(Severity.Blocker, "runtime-exports-missing", $"The runtime does not export {string.Join(" and ", missing)}, which the loader requires, so it would refuse the mod and start the game with no mods."));
        }
    }

    static void CrossCheckSidecar(PackageInfo pkg)
    {
        var sc = pkg.Sidecar;
        if (sc is null || pkg.Manifest is null) return;
        foreach (var p in sc.Problems)
        {
            // A problem in a field that can change a compatibility answer is never ignored: the file's statements cannot be relied on.
            var code = !p.Safety ? "sidecar-field" : !p.Blocking ? "sidecar-footprint" : p.Field == SidecarReader.SchemaField ? "sidecar-schema" : p.Field == "must_understand" ? "sidecar-feature" : "sidecar-unevaluable";
            pkg.Issues.Add(new(p.Safety && p.Blocking ? Severity.Blocker : Severity.Warning, code, PackageInfo.Clean(p.Message, 300), Subject: p.Field));
        }
        if (sc.Interpretable && sc.SchemaText is null && sc.HasSafetyDeclarations)
            pkg.Issues.Add(new(Severity.Info, "sidecar-legacy-schema", $"The description file does not state its format ({SidecarReader.SchemaField}); it was read as allstars-mod/1."));
        if (sc.Id is { Length: > 0 } sid && pkg.Manifest.Id is { } mid && sid != mid)
            pkg.Issues.Add(new(sc.HasSafetyDeclarations || !sc.Interpretable ? Severity.Blocker : Severity.Warning, "sidecar-id",
                $"The description file is for \"{PackageInfo.Clean(sid, 60)}\", not for this mod (\"{PackageInfo.Clean(mid, 60)}\"). {(sc.HasSafetyDeclarations ? "It contains compatibility statements that cannot be applied to this mod; correct the ID or remove the file." : "Its extra information was ignored.")}", Subject: "sidecar"));
        if (sc.Version is { Length: > 0 } sv && pkg.Manifest.Version is { Length: > 0 } mv && sv != mv)
            pkg.Issues.Add(new(Severity.Warning, "sidecar-version", $"The description file says version {PackageInfo.Clean(sv, 40)} but the manifest says {PackageInfo.Clean(mv, 40)}."));
    }
    // ---- helpers ------------------------------------------------------------------------

    public static List<string> ListConfigFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.ini", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith("mod.ini", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The same-ID relation the validator uses: manifest ID when present, never the folder name.</summary>
    public static string IdOrFolder(PackageInfo p) => p.Id ?? p.FolderName;
}
