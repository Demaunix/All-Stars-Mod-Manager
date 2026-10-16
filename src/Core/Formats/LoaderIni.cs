using System.Runtime.InteropServices;
using System.Text;

namespace AllStarsModManager;

/// <summary>
/// Reads [mod] fields the way the loader does: through GetPrivateProfileStringW with a 2048-character buffer,
/// and a value only counts when it is non-empty and shorter than 2047 characters. Using the same Win32 call
/// means encoding, quoting, comment and whitespace rules match the game's loader exactly.
/// </summary>
public static class LoaderIni
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPrivateProfileStringW")]
    static extern uint GetPrivateProfileString(string section, string key, string defaultValue, [Out] char[] result, uint size, string file);

    public readonly record struct Field(bool Ok, string Value);

    public static Field ReadModField(string absoluteIniPath, string key)
    {
        var buf = new char[2048];
        uint n = GetPrivateProfileString("mod", key, "", buf, (uint)buf.Length, absoluteIniPath);
        return new Field(n > 0 && n < 2047, new string(buf, 0, (int)Math.Min(n, 2047)));
    }

    public static string DescribeEncoding(byte[] head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return "UTF-8 with byte-order mark";
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return "UTF-16 little-endian";
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) return "UTF-16 big-endian";
        foreach (var b in head) if (b >= 0x80) return "8-bit text without a byte-order mark";
        return "ASCII";
    }

    /// <summary>True when the manifest has non-ASCII bytes but no byte-order mark: the loader then reads it with the system code page.</summary>
    public static bool HasAmbiguousNonAscii(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return false;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return false;
        foreach (var b in bytes) if (b >= 0x80) return true;
        return false;
    }
}

/// <summary>
/// A text-preserving INI document for mod configuration files. Every line keeps its original bytes and line ending;
/// only a value the user edits is re-encoded, in the file's own encoding. Loading and saving without edits yields
/// byte-identical output.
/// </summary>
public sealed class IniDocument
{
    public enum Enc { Utf8Bom, Utf16LeBom, Utf16BeBom, Utf8OrAscii, LegacyAnsi }

    public sealed class Line
    {
        public string Text = "";        // content without terminator
        public string Terminator = "";  // "\r\n", "\n", "\r" or "" (last line without one)
        public string? Section;         // set on section header lines
        public string? Key;             // set on key=value lines
        public int ValueStart;          // index in Text where the value begins
        public bool Edited;
        public byte[]? OriginalBytes;   // raw bytes for untouched lines (keeps unusual bytes intact)
    }

    public Enc Encoding { get; private set; }
    public List<Line> Lines { get; } = new();
    byte[] _bom = Array.Empty<byte>();

    static readonly Encoding Latin1 = System.Text.Encoding.Latin1;

    public static IniDocument Load(byte[] bytes)
    {
        var d = new IniDocument();
        int start = 0;
        System.Text.Encoding enc;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { d.Encoding = Enc.Utf8Bom; d._bom = bytes[..3]; start = 3; enc = new UTF8Encoding(false, true); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { d.Encoding = Enc.Utf16LeBom; d._bom = bytes[..2]; start = 2; enc = new UnicodeEncoding(false, false, true); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { d.Encoding = Enc.Utf16BeBom; d._bom = bytes[..2]; start = 2; enc = new UnicodeEncoding(true, false, true); }
        else
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes); d.Encoding = Enc.Utf8OrAscii; enc = new UTF8Encoding(false, true); }
            catch (DecoderFallbackException) { d.Encoding = Enc.LegacyAnsi; enc = Latin1; }
        }
        var body = bytes.AsSpan(start);
        string text;
        try { text = enc.GetString(body); }
        catch (DecoderFallbackException) { throw new InvalidDataException("This file is not valid text in its declared encoding."); }
        // Split keeping terminators; track raw bytes per line for byte-exact round trips.
        int i = 0;
        string? section = null;
        while (i < text.Length)
        {
            int j = i;
            while (j < text.Length && text[j] != '\r' && text[j] != '\n') j++;
            string content = text.Substring(i, j - i);
            string term = "";
            if (j < text.Length)
            {
                if (text[j] == '\r' && j + 1 < text.Length && text[j + 1] == '\n') { term = "\r\n"; j += 2; }
                else { term = text[j].ToString(); j += 1; }
            }
            var line = new Line { Text = content, Terminator = term };
            line.OriginalBytes = enc.GetBytes(content + term);
            Classify(line, ref section);
            d.Lines.Add(line);
            i = j;
        }
        return d;
    }

    static void Classify(Line line, ref string? section)
    {
        var t = line.Text.TrimStart();
        if (t.Length == 0 || t[0] == ';' || t[0] == '#') return;
        if (t[0] == '[')
        {
            int e = t.IndexOf(']');
            if (e > 0) { section = t.Substring(1, e - 1).Trim(); line.Section = section; }
            return;
        }
        int eq = line.Text.IndexOf('=');
        if (eq <= 0) return;
        var key = line.Text.Substring(0, eq).Trim();
        if (key.Length == 0) return;
        line.Key = key;
        int vs = eq + 1;
        while (vs < line.Text.Length && (line.Text[vs] == ' ' || line.Text[vs] == '\t')) vs++;
        line.ValueStart = vs;
        line.Section = section;
    }

    public IEnumerable<(string Section, string Key, string Value, int Index)> Entries()
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (l.Key is null) continue;
            yield return (l.Section ?? "", l.Key, l.Text.Substring(l.ValueStart).TrimEnd(), i);
        }
    }

    public string? Get(string section, string key)
    {
        foreach (var e in Entries())
            if (string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                return e.Value;
        return null;
    }

    /// <summary>Replace the value on an existing line, or append the key to its section (creating the section if needed).</summary>
    public void Set(string section, string key, string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("A value cannot contain line breaks.");
        // Without a byte-order mark the game's INI reader uses the system code page, so only plain ASCII is safe to write.
        if ((Encoding == Enc.LegacyAnsi || Encoding == Enc.Utf8OrAscii) && value.Any(c => c > 126 || (c < 32 && c != '\t')))
            throw new InvalidDataException("This file has no byte-order mark, so values with special characters cannot be stored safely.");
        for (int i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (l.Key is not null && string.Equals(l.Section ?? "", section, StringComparison.OrdinalIgnoreCase) && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                l.Text = l.Text.Substring(0, l.ValueStart) + value;
                l.Edited = true;
                return;
            }
        }
        string nl = DominantTerminator();
        int sectionEnd = -1;   // index of the last key line in the section, or of its header when it has no keys
        for (int i = 0; i < Lines.Count; i++)
        {
            if (!IsHeader(Lines[i]) || !string.Equals(Lines[i].Section, section, StringComparison.OrdinalIgnoreCase)) continue;
            sectionEnd = i;
            for (int k = i + 1; k < Lines.Count && !IsHeader(Lines[k]); k++)
                if (Lines[k].Key is not null) sectionEnd = k;
            break;
        }
        var added = new Line { Text = key + "=" + value, Terminator = nl, Edited = true, Key = key, Section = section, ValueStart = key.Length + 1 };
        if (sectionEnd >= 0)
        {
            EnsureTerminated(sectionEnd, nl);
            Lines.Insert(sectionEnd + 1, added);
        }
        else
        {
            if (Lines.Count > 0) EnsureTerminated(Lines.Count - 1, nl);
            Lines.Add(new Line { Text = "[" + section + "]", Terminator = nl, Edited = true, Section = section });
            Lines.Add(added);
        }
    }

    static bool IsHeader(Line l) => l.Key is null && l.Section is not null;

    void EnsureTerminated(int index, string nl)
    {
        var l = Lines[index];
        if (l.Terminator.Length == 0) { l.Terminator = nl; l.Edited = true; l.OriginalBytes = null; }
    }

    string DominantTerminator()
    {
        int crlf = 0, lf = 0, cr = 0;
        foreach (var l in Lines) { if (l.Terminator == "\r\n") crlf++; else if (l.Terminator == "\n") lf++; else if (l.Terminator == "\r") cr++; }
        if (crlf == 0 && lf == 0 && cr == 0) return "\r\n";
        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    public byte[] ToBytes()
    {
        System.Text.Encoding enc = Encoding switch
        {
            Enc.Utf8Bom or Enc.Utf8OrAscii => new UTF8Encoding(false, true),
            Enc.Utf16LeBom => new UnicodeEncoding(false, false, true),
            Enc.Utf16BeBom => new UnicodeEncoding(true, false, true),
            _ => Latin1
        };
        using var ms = new MemoryStream();
        ms.Write(_bom);
        foreach (var l in Lines)
        {
            if (!l.Edited && l.OriginalBytes is not null) ms.Write(l.OriginalBytes);
            else ms.Write(enc.GetBytes(l.Text + l.Terminator));
        }
        return ms.ToArray();
    }
}
