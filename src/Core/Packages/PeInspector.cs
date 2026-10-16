namespace AllStarsModManager;

/// <summary>What one named export resolves to, as a name lookup (GetProcAddress) would find it.</summary>
public enum ExportKind
{
    /// <summary>A code address inside the image.</summary>
    Code,
    /// <summary>A forwarder string (the export points into another module); not followed here, so it cannot be confirmed.</summary>
    Forwarder,
    /// <summary>The entry exists but its address is 0 (an unused slot): a lookup by name returns nothing.</summary>
    NoAddress,
    /// <summary>The name's ordinal is outside the function table, or the address is outside every section.</summary>
    Invalid
}

/// <summary>Facts read from a Windows executable file's headers. Nothing is loaded or run.</summary>
public sealed class PeSummary
{
    public bool IsPe;
    /// <summary>Why the file is not a usable PE file (when <see cref="IsPe"/> is false) or why the export table could not be read.</summary>
    public string? Problem;
    public ushort Machine;
    public bool IsPe32Plus;
    public bool IsDll;
    /// <summary>True when the export name table was read completely, so a missing name really is missing.</summary>
    public bool ExportsRead;
    public List<string> ExportNames = new();
    /// <summary>For every exported name: what its export-table entry resolves to (see <see cref="ExportKind"/>). Only filled when <see cref="ExportsRead"/> is true.</summary>
    public Dictionary<string, ExportKind> ExportKinds = new(StringComparer.Ordinal);
    /// <summary>
    /// True when the name table is in strictly ascending (byte) order with no repeated name, as the PE format requires. Windows finds a name by binary search, so in an
    /// unsorted table the lookup can miss a name that is listed; the result of a lookup is then not defined and must not be reported as confirmed.
    /// </summary>
    public bool ExportNamesSorted = true;

    public const ushort MachineI386 = 0x014C;
    public bool IsX86 => IsPe && !IsPe32Plus && Machine == MachineI386;

    public static string MachineName(ushort m) => m switch
    {
        0x014C => "x86", 0x8664 => "x64", 0x01C4 => "ARM", 0xAA64 => "ARM64", 0x0200 => "Itanium", _ => $"0x{m:X4}"
    };
}

/// <summary>
/// Bounds-checked reader for the few PE header fields that decide whether the game's loader can use a runtime DLL (processor type, DLL flag,
/// exported names). It reads bytes only: the file is never mapped, loaded or executed, and every offset in it is treated as untrusted.
/// </summary>
public static class PeInspector
{
    const int MaxSections = 96;
    const int MaxExportNames = 100_000;
    const int MaxExportFunctions = 262_144;
    const int MaxNameLength = 512;

    public static PeSummary Inspect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Inspect(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PeSummary { Problem = "The file could not be read: " + e.Message };
        }
    }

    sealed class Bad(string why) : Exception(why);

    public static PeSummary Inspect(Stream s)
    {
        var r = new PeSummary();
        try { Read(s, r); }
        catch (Bad b)
        {
            if (!r.IsPe) r.Problem = b.Message;
            else { r.ExportsRead = false; r.Problem = b.Message; }
        }
        return r;
    }

    static byte[] At(Stream s, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > s.Length) throw new Bad("The file ends inside its headers.");
        var buf = new byte[count];
        s.Seek(offset, SeekOrigin.Begin);
        int got = 0;
        while (got < count) { int n = s.Read(buf, got, count - got); if (n <= 0) throw new Bad("The file ended unexpectedly."); got += n; }
        return buf;
    }

    static ushort U16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    static uint U32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    sealed record Section(uint Va, uint VirtualSize, uint RawSize, uint RawPtr);

    static void Read(Stream s, PeSummary r)
    {
        if (s.Length < 0x40) throw new Bad("The file is too small to be a Windows program file.");
        var dos = At(s, 0, 0x40);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new Bad("The file does not start with the Windows program signature (MZ).");
        long pe = U32(dos, 0x3C);
        if (pe < 0x40 || pe > 0x1000000) throw new Bad("The Windows program header offset is not valid.");
        var head = At(s, pe, 24);
        if (head[0] != 'P' || head[1] != 'E' || head[2] != 0 || head[3] != 0) throw new Bad("The Windows program header (PE) is missing.");
        r.Machine = U16(head, 4);
        int sections = U16(head, 6);
        int optSize = U16(head, 20);
        ushort characteristics = U16(head, 22);
        r.IsDll = (characteristics & 0x2000) != 0;
        if (optSize < 96 || sections > MaxSections) throw new Bad("The Windows program header is not valid.");
        var opt = At(s, pe + 24, optSize);
        ushort magic = U16(opt, 0);
        if (magic != 0x10B && magic != 0x20B) throw new Bad("The optional header type is not recognised.");
        r.IsPe32Plus = magic == 0x20B;
        r.IsPe = true;

        int dirBase = r.IsPe32Plus ? 112 : 96;
        int rvaCountOffset = dirBase - 4;
        if (optSize < dirBase + 8) { r.ExportsRead = true; return; }     // no data directories at all: nothing is exported
        uint dirCount = U32(opt, rvaCountOffset);
        if (dirCount == 0) { r.ExportsRead = true; return; }
        uint exportRva = U32(opt, dirBase), exportSize = U32(opt, dirBase + 4);
        if (exportRva == 0 || exportSize == 0) { r.ExportsRead = true; return; }

        var table = At(s, pe + 24 + optSize, sections * 40);
        var secs = new List<Section>();
        for (int i = 0; i < sections; i++)
        {
            int o = i * 40;
            secs.Add(new Section(U32(table, o + 12), U32(table, o + 8), U32(table, o + 16), U32(table, o + 20)));
        }
        long Map(uint rva)
        {
            foreach (var sec in secs)
            {
                uint span = Math.Max(sec.VirtualSize, sec.RawSize);
                if (rva >= sec.Va && rva - sec.Va < span)
                {
                    long off = (long)sec.RawPtr + (rva - sec.Va);
                    if (rva - sec.Va >= sec.RawSize) throw new Bad("The export table lies outside the file's stored data.");
                    return off;
                }
            }
            throw new Bad("The export table points outside every section.");
        }

        var dir = At(s, Map(exportRva), 40);
        uint funcCount = U32(dir, 20), nameCount = U32(dir, 24), funcsRva = U32(dir, 28), namesRva = U32(dir, 32), ordsRva = U32(dir, 36);
        if (nameCount > MaxExportNames) throw new Bad("The export table lists an implausible number of names.");
        if (funcCount > MaxExportFunctions) throw new Bad("The export table lists an implausible number of functions.");
        if (nameCount == 0) { r.ExportsRead = true; return; }
        var pointers = At(s, Map(namesRva), checked((int)nameCount * 4));
        var ordinals = At(s, Map(ordsRva), checked((int)nameCount * 2));
        var functions = funcCount == 0 ? Array.Empty<byte>() : At(s, Map(funcsRva), checked((int)funcCount * 4));
        bool InSection(uint rva) { foreach (var sec in secs) if (rva >= sec.Va && rva - sec.Va < Math.Max(sec.VirtualSize, sec.RawSize)) return true; return false; }
        var kinds = new Dictionary<string, ExportKind>(StringComparer.Ordinal);
        byte[]? previousName = null;
        for (int i = 0; i < nameCount; i++)
        {
            long off = Map(U32(pointers, i * 4));
            int max = (int)Math.Min(MaxNameLength, s.Length - off);
            if (max <= 0) throw new Bad("An exported name lies outside the file.");
            var raw = At(s, off, max);
            int len = Array.IndexOf(raw, (byte)0);
            if (len < 0) throw new Bad("An exported name is not terminated.");
            // The order is judged on the ORIGINAL bytes, never on decoded text (decoding would turn every byte above 0x7F into '?'). A name with a byte above 0x7F is not
            // confirmed at all: how the system compares such bytes is not something this reader can vouch for.
            var nameBytes = raw.AsSpan(0, len);
            if (nameBytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0) r.ExportNamesSorted = false;
            else if (previousName is not null && previousName.AsSpan().SequenceCompareTo(nameBytes) >= 0) r.ExportNamesSorted = false;
            previousName = nameBytes.ToArray();
            var name = System.Text.Encoding.ASCII.GetString(raw, 0, len);
            r.ExportNames.Add(name);
            // the name table gives an index into the ordinal table, whose value indexes the function table (a lookup by name goes through all three)
            uint ord = U16(ordinals, i * 2);
            ExportKind kind;
            if (ord >= funcCount) kind = ExportKind.Invalid;
            else
            {
                uint target = U32(functions, (int)ord * 4);
                kind = target == 0 ? ExportKind.NoAddress
                    : target >= exportRva && target - exportRva < exportSize ? ExportKind.Forwarder
                    : InSection(target) ? ExportKind.Code : ExportKind.Invalid;
            }
            if (kinds.TryGetValue(name, out var earlier) && earlier != ExportKind.Code) kind = earlier;     // a repeated name keeps the worse entry: which one a lookup finds is not defined
            kinds[name] = kind;
        }
        r.ExportKinds = kinds;
        r.ExportsRead = true;
    }
}
