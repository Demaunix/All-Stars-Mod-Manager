namespace AllStarsModManager;

public sealed class ConfigRow
{
    public string Section = "";
    public string Key = "";
    public string Value = "";
    public string OriginalValue = "";
    public ConfigSchemaEntry? Schema;       // only when the mod itself declares one
    public bool Changed => Value != OriginalValue;
}

public sealed class ConfigFile
{
    public string FileName = "";
    public string FullPath = "";
    public IniDocument Document = null!;
    public byte[] OriginalBytes = Array.Empty<byte>();
    public List<ConfigRow> Rows = new();
    public string EncodingText => Document.Encoding switch
    {
        IniDocument.Enc.Utf8Bom => "UTF-8 with byte-order mark",
        IniDocument.Enc.Utf16LeBom => "UTF-16",
        IniDocument.Enc.Utf16BeBom => "UTF-16 (big-endian)",
        IniDocument.Enc.LegacyAnsi => "8-bit text",
        _ => "plain text"
    };
}

/// <summary>
/// Reads and edits the mod's own settings files. No setting meanings are assumed: values are plain text unless the
/// mod itself describes a setting in its allstars-mod.json. A mod's own "Enabled=0" is shown, never changed implicitly.
/// </summary>
public static class ConfigService
{
    const long MaxConfigBytes = 2 * 1024 * 1024;

    public static ConfigFile Load(PackageInfo pkg, string fileName)
    {
        if (!PackageInspector.ListConfigFiles(pkg.Dir).Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"\"{fileName}\" is not a settings file of this mod.");
        var path = Path.Combine(pkg.Dir, fileName);
        if (LoaderPath.IsReparse(path)) throw new PathRuleException("The settings file is a link and will not be edited.");
        var fi = new FileInfo(path);
        if (fi.Length > MaxConfigBytes) throw new InvalidDataException("The settings file is unusually large; open it in a text editor instead.");
        var bytes = File.ReadAllBytes(path);
        var doc = IniDocument.Load(bytes);
        var cf = new ConfigFile { FileName = fi.Name, FullPath = path, Document = doc, OriginalBytes = bytes };
        foreach (var e in doc.Entries())
        {
            var schema = pkg.Sidecar?.ConfigSchema.FirstOrDefault(s =>
                string.Equals(s.File, fi.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Section, e.Section, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Key, e.Key, StringComparison.OrdinalIgnoreCase));
            cf.Rows.Add(new ConfigRow { Section = e.Section, Key = e.Key, Value = e.Value, OriginalValue = e.Value, Schema = schema });
        }
        return cf;
    }

    /// <summary>The bytes the file would have after applying the edited rows. Untouched lines keep their exact bytes.</summary>
    public static byte[] Render(ConfigFile cf)
    {
        var doc = IniDocument.Load(cf.OriginalBytes);
        foreach (var r in cf.Rows.Where(r => r.Changed))
        {
            ValidateValue(r);
            doc.Set(r.Section, r.Key, r.Value);
        }
        return doc.ToBytes();
    }

    public static void ValidateValue(ConfigRow r)
    {
        if (r.Value.Contains('\r') || r.Value.Contains('\n')) throw new InvalidDataException($"{r.Key}: a value cannot contain line breaks.");
        var s = r.Schema;
        if (s is null) return;
        switch (s.Type)
        {
            case "bool":
                if (r.Value is not ("0" or "1" or "true" or "false")) throw new InvalidDataException($"{r.Key}: use 0/1 or true/false.");
                break;
            case "int":
                if (!long.TryParse(r.Value, out var iv)) throw new InvalidDataException($"{r.Key}: enter a whole number.");
                if (s.Min is { } mn && iv < mn || s.Max is { } mx && iv > mx) throw new InvalidDataException($"{r.Key}: out of range.");
                break;
            case "float":
                if (!double.TryParse(r.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv)) throw new InvalidDataException($"{r.Key}: enter a number.");
                if (s.Min is { } fmn && fv < fmn || s.Max is { } fmx && fv > fmx) throw new InvalidDataException($"{r.Key}: out of range.");
                break;
            case "enum":
                if (s.Choices.Length > 0 && !s.Choices.Contains(r.Value)) throw new InvalidDataException($"{r.Key}: choose one of {string.Join(", ", s.Choices)}.");
                break;
        }
    }

    /// <summary>Validates a whole-file text edit: must decode in the file's own encoding. Returns the bytes to store.</summary>
    public static byte[] RenderRawText(ConfigFile cf, string newText)
    {
        var probe = IniDocument.Load(cf.OriginalBytes);
        var rendered = new List<byte>();
        var enc = probe.Encoding;
        System.Text.Encoding e = enc switch
        {
            IniDocument.Enc.Utf16LeBom => new System.Text.UnicodeEncoding(false, false, true),
            IniDocument.Enc.Utf16BeBom => new System.Text.UnicodeEncoding(true, false, true),
            IniDocument.Enc.LegacyAnsi => System.Text.Encoding.Latin1,
            _ => new System.Text.UTF8Encoding(false, true)
        };
        if ((enc == IniDocument.Enc.Utf8OrAscii || enc == IniDocument.Enc.LegacyAnsi) && newText.Any(c => c > 126 || (c < 32 && c is not ('\r' or '\n' or '\t'))))
            throw new InvalidDataException("This file has no byte-order mark, so special characters cannot be stored safely.");
        try { if (enc == IniDocument.Enc.Utf8Bom) rendered.AddRange(new byte[] { 0xEF, 0xBB, 0xBF }); else if (enc == IniDocument.Enc.Utf16LeBom) rendered.AddRange(new byte[] { 0xFF, 0xFE }); else if (enc == IniDocument.Enc.Utf16BeBom) rendered.AddRange(new byte[] { 0xFE, 0xFF }); rendered.AddRange(e.GetBytes(newText)); }
        catch (System.Text.EncoderFallbackException) { throw new InvalidDataException("The text contains characters this file's encoding cannot store."); }
        return rendered.ToArray();
    }

    public static string RawText(ConfigFile cf)
    {
        var doc = IniDocument.Load(cf.OriginalBytes);
        return string.Concat(doc.Lines.Select(l => l.Text + l.Terminator));
    }

    /// <summary>Files in this mod where the mod's own "Enabled" switch is 0. This is separate from the mod being switched on in the manager.</summary>
    public static List<string> RuntimeSwitchedOff(PackageInfo pkg)
    {
        var res = new List<string>();
        foreach (var f in pkg.ConfigFiles)
        {
            try
            {
                var path = Path.Combine(pkg.Dir, f);
                if (LoaderPath.IsReparse(path) || new FileInfo(path).Length > MaxConfigBytes) continue;
                var doc = IniDocument.Load(File.ReadAllBytes(path));
                foreach (var e in doc.Entries())
                    if (e.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && e.Value.Trim() == "0")
                    { res.Add(string.IsNullOrEmpty(e.Section) ? f : $"{f} [{e.Section}]"); }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return res;
    }
}
