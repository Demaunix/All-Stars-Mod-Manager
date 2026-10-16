using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AllStarsModManager;

/// <summary>
/// Reads the optional allstars-mod.json description (format "allstars-mod/1"). It is untrusted, declarative data: nothing in it is ever
/// executed, opened or used as a destination outside the rules enforced by the callers.
///
/// Fields that can change a compatibility answer (requirements, conflicts, exclusive claims, native patch ranges, required extra files,
/// loader requirements, load order, installability) are read strictly: an entry that cannot be read as written is recorded as a
/// <see cref="SidecarProblem"/> with Safety = true and is never treated as "no rule". Display-only fields are read leniently.
/// </summary>
public static class SidecarReader
{
    public const string FileName = "allstars-mod.json";
    public const string SchemaField = "manifest_schema";
    public const int SupportedMajor = 1;
    const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>Optional-feature names a file may list in "must_understand". A name not in this list makes the file's declarations unreadable (fail closed).</summary>
    public static readonly string[] SupportedFeatures = { "loader-requirements/1", "load-order/1", "target-build/1" };

    static readonly Regex SchemaRx = new(@"\Aallstars-mod/(?<major>[0-9]{1,6})(?:\.(?<minor>[0-9]{1,6}))?\z", RegexOptions.CultureInvariant);
    static readonly Regex CapabilityRx = new(@"\A[a-z0-9][a-z0-9._:\-]{0,63}\z", RegexOptions.CultureInvariant);

    public static Sidecar? TryRead(string modDir, List<Issue> issues)
    {
        var path = Path.Combine(modDir, FileName);
        // Only a confirmed absence makes a mod without description (a legacy mod). Anything else under this name - a folder, a link, a name
        // that cannot be inspected - may hold a requirement and is unreadable, never "no rules" (the loader refuses such a mod as well).
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { issues.Add(Unreadable("it could not be inspected (" + PackageInfo.Clean(e.Message, 120) + ")")); return null; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add(Unreadable("it is a link; only a real file inside the mod folder is read")); return null; }
        if ((attributes & FileAttributes.Directory) != 0) { issues.Add(Unreadable("it is a folder, not a file")); return null; }
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > MaxBytes) { issues.Add(Unreadable("it is larger than 4 MB")); return null; }
            // Duplicate property names are refused: two readers could otherwise disagree about which "requires" counts.
            var bytes = File.ReadAllBytes(path);
            // .NET cannot open a name without following a link, so look again after reading: a link put in place meanwhile is not read as the description.
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) { issues.Add(Unreadable("it changed into a link or folder while it was read")); return null; }
            // The syntax is the loader's own JSON subset (the same reader that parses plan.json): no byte-order mark, comments, trailing commas, duplicate names,
            // negative or fractional numbers, \u surrogate escapes or nesting deeper than 32. A file outside it is unreadable, never "partly read".
            try { StrictJson.Parse(bytes); }
            catch (PlanFormatException e) { issues.Add(Unreadable("it is not plain JSON in the subset the game loader's own reader accepts (" + PackageInfo.Clean(e.Message, 100) + ")")); return null; }
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });            return Parse(doc.RootElement, path);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            issues.Add(Unreadable(PackageInfo.Clean(e.Message, 160)));
            return null;
        }
    }

    // A description file that is present but cannot be read may hold a requirement or conflict; saying nothing would look like "no rules".
    static Issue Unreadable(string why) => new(Severity.Blocker, "sidecar-unreadable",
        "The package's description file (allstars-mod.json) cannot be read as the format requires, so its compatibility statements cannot be checked and are not assumed to be absent. Ask the mod's author for a corrected file. The mod can still be copied into the game folder by hand, but ChaoGarage 1.1.0 and later refuse it at game start as well; loaders before 1.1.0 do not read this file.", why);

    // ---- small helpers over untrusted JSON -------------------------------------------------------

    static string? S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static JsonElement? Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    /// <summary>
    /// True when the property is present. An explicit JSON null counts as present: for the strict (safety) readers null is a value of the wrong type and becomes a
    /// problem, never "not stated". Display-only readers do not use this helper (they skip anything unreadable).
    /// </summary>
    static bool Has(JsonElement e, string name, out JsonElement v)
    {
        v = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v);
    }

    /// <summary>Lenient: arrays only, anything else yields nothing (display-only fields).</summary>
    static IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>Strict list: an array, or one object/string standing for a one-element array (accepted shorthand, used by existing packages). Anything else is a problem.</summary>
    static List<JsonElement> List(JsonElement parent, string field, Sidecar sc, bool allowString = false, bool blocking = true)
    {
        var list = new List<JsonElement>();
        if (!Has(parent, field, out var v)) return list;
        switch (v.ValueKind)
        {
            case JsonValueKind.Array: list.AddRange(v.EnumerateArray()); break;
            case JsonValueKind.Object: list.Add(v); break;
            case JsonValueKind.String when allowString: list.Add(v); break;
            default: sc.Problems.Add(new(field, $"\"{field}\" must be a list, but it is {Describe(v.ValueKind)}.", true, blocking)); break;
        }
        return list;
    }

    static string Describe(JsonValueKind k) => k switch
    {
        JsonValueKind.String => "text", JsonValueKind.Number => "a number", JsonValueKind.True or JsonValueKind.False => "true/false",
        JsonValueKind.Object => "an object", JsonValueKind.Array => "a list", _ => "empty"
    };

    static uint? ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static string Cap(string? s, int max = 600) => PackageInfo.Clean(s, max);

    static bool GoodId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Trim().Length <= 200 && !id.Any(char.IsControl);

    // ---- parse ------------------------------------------------------------------------------------

    public static Sidecar Parse(JsonElement root, string path)
    {
        var sc = new Sidecar { FilePath = path };
        if (root.ValueKind != JsonValueKind.Object)
        {
            sc.Interpretable = false;
            sc.Problems.Add(new("(file)", "The description file must contain one JSON object.", true));
            return sc;
        }

        // Display fields first: they are shown even when the declarations cannot be acted on.
        sc.Id = S(root, "id") ?? S(root, "package_id");
        sc.Name = Cap(S(root, "name"));
        sc.Version = Cap(S(root, "version"), 80);
        sc.Summary = Cap(S(root, "summary"), 1200);
        sc.Kind = Cap(S(root, "kind"), 80);
        sc.Redistribution = Cap(S(root, "redistribution"), 400);
        if (S(root, "homepage") is { } hp && hp.StartsWith("https://", StringComparison.Ordinal) && hp.Length <= 200 && !hp.Any(ch => ch <= ' ' || ch > '~')) sc.Homepage = hp;
        if (Obj(root, "status") is { } st) sc.Stage = Cap(S(st, "stage"), 120);
        if (Obj(root, "persistence") is { } pe)
        {
            sc.PersistenceStatement = Cap(S(pe, "statement"), 2000);
            sc.DisableNote = Cap(S(pe, "disable_note"), 400);
        }

        // Schema: absent = legacy file (read as schema 1). Present: must be allstars-mod/<major>[.<minor>].
        if (Has(root, SchemaField, out var schema))
        {
            var text = schema.ValueKind == JsonValueKind.String ? schema.GetString() : null;      // matched exactly: no trimming, no case folding
            var m = text is null ? null : SchemaRx.Match(text);
            if (m is null || !m.Success)
            {
                sc.Interpretable = false;
                sc.Problems.Add(new(SchemaField, $"\"{SchemaField}\" is not a recognised format name (expected allstars-mod/1).", true));
            }
            else
            {
                sc.SchemaText = text;
                sc.SchemaMajor = int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture);
                sc.SchemaMinor = m.Groups["minor"].Success ? int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
                if (sc.SchemaMajor != SupportedMajor)
                {
                    sc.Interpretable = false;
                    sc.Problems.Add(new(SchemaField, $"This description uses format {text}; this version of the tools understands allstars-mod/{SupportedMajor}.x only, so its compatibility statements cannot be checked. A newer manager or checker may read it.", true));
                }
            }
        }

        // must_understand: features this file relies on. An unknown name means a rule exists that this reader cannot apply.
        foreach (var f in List(root, "must_understand", sc, allowString: true))
        {
            var name = f.ValueKind == JsonValueKind.String ? f.GetString()!.Trim() : null;
            if (name is null) { sc.Interpretable = false; sc.Problems.Add(new("must_understand", "\"must_understand\" must list feature names as text.", true)); continue; }
            sc.MustUnderstand.Add(Cap(name, 80));
            if (!SupportedFeatures.Contains(name, StringComparer.Ordinal))
            {
                sc.Interpretable = false;
                sc.Problems.Add(new("must_understand", $"The description requires the feature \"{Cap(name, 60)}\", which this version of the tools does not know, so its compatibility statements cannot be checked.", true));
            }
        }

        if (!sc.Interpretable) return sc;     // declarations are not read at all: nothing partial is ever applied

        if (Has(root, "installable", out var inst))
        {
            if (inst.ValueKind is JsonValueKind.True or JsonValueKind.False) sc.Installable = inst.GetBoolean();
            else sc.Problems.Add(new("installable", "\"installable\" must be true or false.", true));
        }
        if (Has(root, "package_completeness", out var pcv) && pcv.ValueKind != JsonValueKind.Object)
            sc.Problems.Add(new("package_completeness", "\"package_completeness\" must be an object.", true));
        if (Obj(root, "package_completeness") is { } pc)
        {
            if (Has(pc, "mod_folder_alone_is_complete", out var b))
            {
                if (b.ValueKind is JsonValueKind.True or JsonValueKind.False) sc.ModFolderAloneIsComplete = b.GetBoolean();
                else sc.Problems.Add(new("package_completeness.mod_folder_alone_is_complete", "\"mod_folder_alone_is_complete\" must be true or false.", true));
            }
            var outside = S(pc, "required_outside_mod_folder");
            sc.CompletenessNote = Cap(outside ?? (pc.TryGetProperty("required_outside_mod_folder", out var ro) && ro.ValueKind == JsonValueKind.Array ? string.Join(", ", ro.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : null), 800);
        }

        ReadLooseAssets(root, sc);
        foreach (var r in List(root, "requires", sc, allowString: true)) AddRelation(sc.Requires, r, "requires", sc, needed: true);
        foreach (var r in List(root, "conflicts_with", sc, allowString: true)) AddRelation(sc.ConflictsWith, r, "conflicts_with", sc, needed: true);
        foreach (var r in List(root, "composes_with", sc, allowString: true)) AddRelation(sc.ComposesWith, r, "composes_with", sc, needed: false);
        ReadFreeTextConflicts(root, sc);
        foreach (var c in List(root, "capabilities_exclusive", sc, allowString: true, blocking: false))
        {
            if (c.ValueKind == JsonValueKind.String && GoodId(c.GetString())) sc.CapabilitiesExclusive.Add(c.GetString()!.Trim());
            else sc.Problems.Add(new("capabilities_exclusive", "An exclusive capability must be non-empty text, so clashes over it cannot be checked.", true, Blocking: false));
        }
        if (Has(root, "touches", out var touches))
        {
            if (touches.ValueKind != JsonValueKind.Object) sc.Problems.Add(new("touches", "\"touches\" must be an object, so the mod's code changes cannot be compared with other mods'.", true, Blocking: false));
            else ReadNativePatches(touches, sc);
        }
        ReadLoader(root, sc);
        ReadTarget(root, sc);
        ReadLoadOrder(root, sc);

        // Display-only sections (malformed entries are skipped: they never change a compatibility answer).
        foreach (var e in Arr(root, "config_schema"))
        {
            var entry = new ConfigSchemaEntry
            {
                File = S(e, "file") ?? "config.ini",
                Section = S(e, "section") ?? "",
                Key = S(e, "key") ?? "",
                Label = Cap(S(e, "label"), 120),
                Description = Cap(S(e, "description"), 400),
                Type = (S(e, "type") ?? "text").ToLowerInvariant()
            };
            if (entry.Key.Length == 0) continue;
            entry.Choices = Arr(e, "choices").Where(c => c.ValueKind == JsonValueKind.String).Select(c => Cap(c.GetString(), 80)).ToArray();
            // The file syntax allows only unsigned whole numbers, so decimals and negatives are written as text ("-1.5").
            entry.Min = Limit(e, "min"); entry.Max = Limit(e, "max");
            sc.ConfigSchema.Add(entry);
        }
        foreach (var a in Arr(root, "artifacts"))
        {
            var p = S(a, "path"); var h = S(a, "sha256");
            if (p is null || h is null) continue;
            sc.Artifacts.Add(new ArtifactRef { Path = p, Sha = h, Bytes = a.TryGetProperty("bytes", out var bb) && bb.ValueKind == JsonValueKind.Number && bb.TryGetInt64(out var bv) ? bv : -1 });
        }
        foreach (var h in Arr(root, "human_gameplay_requirements")) if (h.ValueKind == JsonValueKind.String) sc.HumanGameplayRequirements.Add(Cap(h.GetString(), 500));
        if (root.TryGetProperty("human_gameplay_requirements", out var hs) && hs.ValueKind == JsonValueKind.String) sc.HumanGameplayRequirements.Add(Cap(hs.GetString(), 500));
        return sc;
    }

    static double? Limit(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    static void ReadLooseAssets(JsonElement root, Sidecar sc)
    {
        foreach (var a in List(root, "loose_assets", sc))
        {
            var target = S(a, "target");
            var sha = S(a, "sha256");
            if (a.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(target) || sha is null)
            {
                // Skipping it would report a package that lacks a required file as complete.
                sc.Problems.Add(new("loose_assets", "A required extra file entry needs a text \"target\" and \"sha256\".", true));
                continue;
            }
            long bytes = -1;
            if (Has(a, "bytes", out var bb))
            {
                if (bb.ValueKind == JsonValueKind.Number && bb.TryGetUInt32(out var bv)) bytes = bv;
                else { sc.Problems.Add(new("loose_assets", $"The size of required file \"{Cap(target, 60)}\" is not a whole number.", true)); continue; }
            }
            sc.LooseAssets.Add(new LooseAsset
            {
                Target = target.Trim(),
                Sha = sha.Trim(),
                Bytes = bytes,
                SourceInPackage = S(a, "source_in_package") ?? target.Trim(),
                Role = Cap(S(a, "role"), 160)
            });
        }
    }

    static void ReadFreeTextConflicts(JsonElement root, Sidecar sc)
    {
        foreach (var c in List(root, "conflicts", sc))
        {
            // Older descriptions use free text in "with". Only an exact "<id>" or "<id> <op> <version>" is machine readable.
            var with = S(c, "with");
            if (with is null) { sc.Problems.Add(new("conflicts", "A conflict entry needs a text \"with\".", true)); continue; }
            var m = Regex.Match(with.Trim(), @"\A([a-z0-9][a-z0-9\-]*)(?:\s*(<=|>=|<|>|==|=)\s*([0-9A-Za-z.\-+]+))?\z", RegexOptions.CultureInvariant);
            if (m.Success)
                sc.ConflictsWith.Add(new Relation { Id = m.Groups[1].Value, Versions = m.Groups[2].Success ? m.Groups[2].Value + m.Groups[3].Value : "*", Note = Cap(S(c, "effect"), 400) });
            else
                sc.FreeTextConflicts.Add(Cap(with + (S(c, "effect") is { } eff ? " — " + eff : ""), 500));
        }
    }

    static void ReadNativePatches(JsonElement touches, Sidecar sc)
    {
        foreach (var p in List(touches, "native_patches", sc, blocking: false))
        {
            uint? va = null; uint len = 0;
            if (p.ValueKind == JsonValueKind.Object)
            {
                if (Has(p, "va", out var v))
                    va = v.ValueKind == JsonValueKind.String ? ParseHex(v.GetString()) : v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var vn) ? vn : null;
                if (Has(p, "length", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetUInt32(out var lv)) len = lv;
            }
            if (va is null || len == 0) { sc.Problems.Add(new("touches.native_patches", "A native patch entry has no usable address range (it needs a hexadecimal \"va\" such as \"0x545434\" and a whole-number \"length\" above 0), so overlaps between this mod's code changes and other mods' cannot be checked.", true, Blocking: false)); continue; }
            sc.NativePatches.Add(new NativePatch { Id = Cap(S(p, "id"), 80), Va = va.Value, Length = len });
        }
    }

    static void ReadLoader(JsonElement root, Sidecar sc)
    {
        if (!Has(root, "loader", out var l)) return;
        if (l.ValueKind != JsonValueKind.Object) { sc.Problems.Add(new("loader", "\"loader\" must be an object.", true)); return; }
        var req = sc.Loader;
        req.Declared = true;
        uint? Num(string name)
        {
            if (!Has(l, name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n)) return n;
            sc.Problems.Add(new("loader." + name, $"\"{name}\" must be a whole number.", true));
            return null;
        }
        req.HostAbi = Num("host_abi");
        req.ModIniSchema = Num("mod_ini_schema");
        req.PlanSchema = Num("plan_schema");
        foreach (var c in List(l, "capabilities", sc, allowString: true))
        {
            var s = c.ValueKind == JsonValueKind.String ? c.GetString()!.Trim() : null;
            if (s is not null && CapabilityRx.IsMatch(s)) req.Capabilities.Add(s);
            else sc.Problems.Add(new("loader.capabilities", "A loader capability must be a short lowercase name such as xpac-member-merge.", true));
        }
        ReadHashes(l, "host_sha256", req.HostSha256, sc, safety: true, "loader.host_sha256");
        ReadHashes(l, "proxy_sha256", req.ProxySha256, sc, safety: true, "loader.proxy_sha256");
    }

    static void ReadTarget(JsonElement root, Sidecar sc)
    {
        if (!Has(root, "target", out var t)) return;
        if (t.ValueKind != JsonValueKind.Object) return;     // descriptive only
        ReadHashes(t, "executable_sha256", sc.TargetExecutableSha256, sc, safety: false, "target.executable_sha256");
    }

    static void ReadHashes(JsonElement parent, string field, List<string> into, Sidecar sc, bool safety, string label)
    {
        if (!Has(parent, field, out var v)) return;
        var items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : new List<JsonElement> { v };
        foreach (var it in items)
        {
            var s = it.ValueKind == JsonValueKind.String ? it.GetString()!.Trim().ToLowerInvariant() : null;
            if (s is not null && Sha.IsLoaderHash(s)) { if (!into.Contains(s)) into.Add(s); }
            else sc.Problems.Add(new(label, $"\"{field}\" must hold SHA-256 hashes (64 hexadecimal digits).", safety));
        }
    }

    static void ReadLoadOrder(JsonElement root, Sidecar sc)
    {
        if (!Has(root, "load_order", out var lo)) return;
        if (lo.ValueKind != JsonValueKind.Object) { sc.Problems.Add(new("load_order", "\"load_order\" must be an object with \"after\" and/or \"before\".", true)); return; }
        void Read(string field, List<OrderRef> into)
        {
            foreach (var e in List(lo, field, sc, allowString: true))
            {
                var id = e.ValueKind == JsonValueKind.String ? e.GetString() : S(e, "id");
                if (GoodId(id)) into.Add(new OrderRef { Id = id!.Trim() });
                else sc.Problems.Add(new("load_order." + field, "Each load-order entry must be a mod ID (text) or an object with an \"id\".", true));
            }
        }
        Read("after", sc.LoadAfter);
        Read("before", sc.LoadBefore);
    }

    static void AddRelation(List<Relation> into, JsonElement e, string field, Sidecar sc, bool needed)
    {
        if (e.ValueKind == JsonValueKind.String && GoodId(e.GetString()))     // shorthand: "other-mod" means any version
        {
            into.Add(new Relation { Id = e.GetString()!.Trim(), Versions = "*" });
            return;
        }
        var id = S(e, "id");
        if (e.ValueKind != JsonValueKind.Object || !GoodId(id))
        {
            if (needed) sc.Problems.Add(new(field, $"An entry in \"{field}\" needs a text \"id\".", true));
            return;
        }
        string? invalid = null;
        string versions = "*";
        foreach (var key in new[] { "versions", "version" })
            if (Has(e, key, out var vv))
            {
                if (vv.ValueKind == JsonValueKind.String) { versions = vv.GetString()!.Trim(); invalid = null; break; }
                invalid = $"\"{key}\" for \"{Cap(id, 60)}\" must be text such as \">=1.0.0 <2.0.0\".";
            }
        if (invalid is null)
        {
            var err = VersionSpec.RangeError(versions);
            if (err is not null) invalid = $"The version range \"{Cap(versions, 60)}\" for \"{Cap(id, 60)}\" is not valid ({err}).";
        }
        if (invalid is not null && !needed) { return; }      // composes_with only ever adds reassurance; an unreadable one adds none
        into.Add(new Relation
        {
            Id = id!.Trim(),
            Versions = versions,
            Note = Cap(S(e, "reason") ?? S(e, "note") ?? S(e, "limits"), 500),
            Evidence = Cap(S(e, "evidence"), 400),
            Invalid = invalid
        });
    }
}

/// <summary>User-facing wording for descriptive status strings recorded in metadata. The raw value stays available.</summary>
public static class StageText
{
    public static (string Short, string Long, bool GameTested) Describe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "", false);
        return raw.Trim().ToUpperInvariant() switch
        {
            "CANDIDATE_OFFLINE_VERIFIED" => ("Offline tested · game test pending", "Checked offline by its author; it has not been confirmed in the game yet.", false),
            "RESEARCH_INPUTS_AVAILABLE_NOT_IMPLEMENTED" or "INPUT_CANDIDATES_FOUND_NOT_IMPLEMENTED" => ("Research note", "Research only. Nothing here can be installed yet.", false),
            "RELEASE" or "RELEASED" or "TESTED_IN_GAME" => ("Tested in game", "The author reports in-game testing.", true),
            _ => ("Status: " + PackageInfo.Clean(raw, 60), "Status text from the mod's own description.", false)
        };
    }
}
