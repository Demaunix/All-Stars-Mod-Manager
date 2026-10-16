using System.Text;

namespace AllStarsModManager.Tests;

/// <summary>
/// Builds tiny synthetic Windows DLL images for tests: one code section holding a no-op function per export and a correct export table.
/// They contain no game or third-party bytes. The images are valid enough for header inspection; tests never load them.
/// </summary>
public static class SyntheticPe
{
    public static readonly string[] LoaderExports = { "SsasrModInitialize", "SsasrModRollback" };

    /// <param name="salt">Varies the file bytes (and so the hash) between mods.</param>
    /// <param name="exportAddress">how each export-table entry resolves: code (normal), zero (address 0), forwarder (points into the export directory), outside (beyond every section), badordinal (the name table points past the function table)</param>
    public static byte[] Dll(string salt = "default", string[]? exports = null, ushort machine = 0x014C, bool pe32Plus = false, bool dllFlag = true, string exportAddress = "code", bool reverseNameTable = false, bool sortNames = true)
    {
        exports ??= LoaderExports;
        var names = sortNames ? exports.OrderBy(n => n, StringComparer.Ordinal).ToArray() : exports.ToArray();
        const uint sectionVa = 0x1000, raw = 0x200;
        // section content: code, then export directory, tables, strings, salt
        var sec = new MemoryStream();
        var w = new BinaryWriter(sec);
        foreach (var _ in names) { w.Write((byte)0x33); w.Write((byte)0xC0); w.Write((byte)0xC3); w.Write((byte)0xCC); }
        uint exportDirOff = (uint)sec.Length;
        uint tablesOff = exportDirOff + 40;
        uint funcsOff = tablesOff, namesOff = funcsOff + 4u * (uint)names.Length, ordsOff = namesOff + 4u * (uint)names.Length;
        uint dllNameOff = ordsOff + 2u * (uint)names.Length;
        uint stringsOff = dllNameOff + 16;
        var strings = new List<byte>();
        var nameRvas = new List<uint>();
        foreach (var n in names) { nameRvas.Add(sectionVa + stringsOff + (uint)strings.Count); strings.AddRange(Encoding.Latin1.GetBytes(n)); strings.Add(0); }
        w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(sectionVa + dllNameOff); w.Write(1u);
        w.Write((uint)names.Length); w.Write((uint)names.Length);
        w.Write(sectionVa + funcsOff); w.Write(sectionVa + namesOff); w.Write(sectionVa + ordsOff);
        for (int i = 0; i < names.Length; i++)
            w.Write(exportAddress switch { "zero" => 0u, "forwarder" => sectionVa + dllNameOff, "outside" => 0x7FFF0000u, _ => sectionVa + (uint)(i * 4) });
        // (reverseNameTable: the pointer array and the ordinal array are written back to front, so every name still maps to its own function but the table is not sorted)
        foreach (var rva in reverseNameTable ? Enumerable.Reverse(nameRvas) : nameRvas) w.Write(rva);
        for (int i = 0; i < names.Length; i++) { int k = reverseNameTable ? names.Length - 1 - i : i; w.Write((ushort)(exportAddress == "badordinal" ? k + 50 : k)); }
        w.Write(Encoding.ASCII.GetBytes("synthetic.dll\0\0\0"));
        w.Write(strings.ToArray());
        w.Write(Encoding.ASCII.GetBytes(salt));
        uint size = (uint)((sec.Length + raw - 1) / raw * raw);
        while (sec.Length < size) sec.WriteByte(0);

        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        var dos = new byte[0x40];
        dos[0] = (byte)'M'; dos[1] = (byte)'Z'; BitConverter.GetBytes(0x40u).CopyTo(dos, 0x3C);
        f.Write(dos);
        f.Write(Encoding.ASCII.GetBytes("PE\0\0"));
        int optSize = pe32Plus ? 240 : 224;
        f.Write(machine); f.Write((ushort)1); f.Write(0u); f.Write(0u); f.Write(0u); f.Write((ushort)optSize);
        f.Write((ushort)((dllFlag ? 0x2000 : 0) | 0x0002 | (pe32Plus ? 0x0020 : 0x0100)));
        long optStart = file.Position;
        f.Write((ushort)(pe32Plus ? 0x20B : 0x10B)); f.Write((byte)14); f.Write((byte)0);
        f.Write(size); f.Write(0u); f.Write(0u); f.Write(0u); f.Write(sectionVa);
        if (pe32Plus) f.Write(0x180000000UL); else { f.Write(sectionVa); f.Write(0x10000000u); }
        f.Write(0x1000u); f.Write(0x200u);
        f.Write((ushort)6); f.Write((ushort)0); f.Write((ushort)0); f.Write((ushort)0); f.Write((ushort)6); f.Write((ushort)0);
        f.Write(0u); f.Write(sectionVa + size); f.Write(0x200u); f.Write(0u);
        f.Write((ushort)2); f.Write((ushort)0x0140);
        if (pe32Plus) { f.Write(0x100000UL); f.Write(0x1000UL); f.Write(0x100000UL); f.Write(0x1000UL); }
        else { f.Write(0x100000u); f.Write(0x1000u); f.Write(0x100000u); f.Write(0x1000u); }
        f.Write(0u); f.Write(16u);
        for (int i = 0; i < 16; i++)
        {
            if (i == 0) { f.Write(sectionVa + exportDirOff); f.Write(40u + 4u * (uint)names.Length * 2 + 2u * (uint)names.Length + 16 + (uint)strings.Count); }
            else { f.Write(0u); f.Write(0u); }
        }
        if (file.Position - optStart != optSize) throw new InvalidOperationException("synthetic PE optional header size is wrong");
        f.Write(Encoding.ASCII.GetBytes(".text\0\0\0"));
        f.Write(size); f.Write(sectionVa); f.Write(size); f.Write(raw);
        f.Write(0u); f.Write(0u); f.Write((ushort)0); f.Write((ushort)0); f.Write(0x60000020u);
        while (file.Position < raw) file.WriteByte(0);
        f.Write(sec.ToArray());
        return file.ToArray();
    }
}
