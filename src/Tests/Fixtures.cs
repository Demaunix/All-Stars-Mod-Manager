using System.Security.Cryptography;
using System.Text;

namespace AllStarsModManager.Tests;

public static class TestXpac
{
    static uint Up(uint n) => (n + 2047) & ~2047u;

    public static byte[] Build(SortedDictionary<uint, byte[]> members)
    {
        uint count = (uint)members.Count;
        uint dirEnd = Up(24 + 20 * count);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(0u); w.Write(dirEnd); w.Write(count); w.Write(0u); w.Write(0u);
        uint cursor = dirEnd;
        foreach (var (key, data) in members)
        {
            w.Write(key); w.Write(cursor); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write(0u);
            cursor = Up(cursor + (uint)data.Length);
        }
        while (ms.Length < dirEnd) w.Write((byte)0);
        foreach (var (_, data) in members)
        {
            w.Write(data);
            var pad = Up((uint)ms.Length) - (uint)ms.Length;
            for (uint i = 0; i < pad; i++) w.Write((byte)0);
        }
        return ms.ToArray();
    }
}

public enum KeyStyle { Hex, HexLower, Number }

public sealed class OpSpec
{
    public uint Key;
    public string Kind = "replace";
    public byte[] Payload = Array.Empty<byte>();
    public KeyStyle Style = KeyStyle.Hex;
    public bool CorruptPayloadHash;
    public bool NullBaseHash;
}

public sealed class GroupSpec
{
    public string Format = "xpac";
    public string Path = "Resource/Base.xpac";
    public List<OpSpec> Ops = new();
    public byte[] Payload = Array.Empty<byte>();     // file/copy/rtsc
    public string PayloadRel = "";
    public bool WrongBase;
    public bool WrongComposed;
    public string? BaseOverrideHash;
    public uint KeySize = 32;
    public bool UppercaseHashes;

    public static GroupSpec Xpac(string path, params (uint key, string kind, byte[] data)[] ops) =>
        new() { Format = "xpac", Path = path, Ops = ops.Select(o => new OpSpec { Key = o.key, Kind = o.kind, Payload = o.data }).ToList() };
    public static GroupSpec File(string path, byte[] data) => new() { Format = "file", Path = path, Payload = data };
    public static GroupSpec Rtsc(string path, byte[] data) => new() { Format = "rtsc_merge", Path = path, Payload = data };
    public static GroupSpec Copy(string path, byte[] data) => new() { Format = "copy", Path = path, Payload = data };
}

public sealed class ModSpec
{
    public string Id = "test-mod";
    public string? Name = "Test Mod";
    public string? Version = "1.0.0";
    public string Folder = "Test Mod";
    public List<GroupSpec> Groups = new();
    public string? PlanModId;                      // written when non-null
    public byte[] Runtime = SyntheticPe.Dll("default");
    public Dictionary<string, string> ConfigFiles = new();
    public string? SidecarJson;
    public string ManifestFileName = "mod.ini";
    public bool WrongPlanHash, WrongRuntimeHash, UppercasePinHash;
    public string Schema = "1";
    public string? PlanOverrideText;               // replaces plan.json text completely (hash still computed from it unless WrongPlanHash)
    public Dictionary<string, string> ExtraFiles = new();
    public string IniExtra = "";
    public string? ManifestText;                   // full manifest override
}

public sealed class BuiltMod
{
    public string Dir = "";
    public string PlanSha = "", RuntimeSha = "";
    public ModSpec Spec = null!;
    public string ManifestPath => Path.Combine(Dir, Spec.ManifestFileName);
}

/// <summary>A disposable game folder + builder for mod folders. Everything lives under the module's work/ tree.</summary>
public sealed class Fx : IDisposable
{
    public string Root { get; }
    public string Game { get; }
    public string Mods => Path.Combine(Game, "mods");
    public SortedDictionary<uint, byte[]> BaseMembers = new();
    public Dictionary<string, SortedDictionary<uint, byte[]>> BaseArchives = new(StringComparer.OrdinalIgnoreCase);
    public byte[] FrontEnd = Encoding.ASCII.GetBytes("original front end script bytes");
    public byte[] Other = Encoding.ASCII.GetBytes("another original script");
    static int _counter;

    public Fx(string? tag = null)
    {
        var work = Path.Combine(Program.WorkRoot, "fixtures");
        Root = Path.Combine(work, $"{tag ?? "fx"}-{Interlocked.Increment(ref _counter):000}-{Guid.NewGuid():N}"[..Math.Min(48, (tag?.Length ?? 2) + 14)]);
        Game = Path.Combine(Root, "game");
        Directory.CreateDirectory(Path.Combine(Game, "Resource"));
        Directory.CreateDirectory(Path.Combine(Game, "Scripts"));
        Directory.CreateDirectory(Path.Combine(Game, "ShaderCache"));
        Directory.CreateDirectory(Mods);
        File.WriteAllBytes(Path.Combine(Game, GameFolderInfo.ExeName), Encoding.ASCII.GetBytes("not a real game executable - fixture"));
        File.WriteAllBytes(Path.Combine(Game, "ChaoGarage.dll"), Encoding.ASCII.GetBytes("fixture host placeholder"));
        File.WriteAllBytes(Path.Combine(Game, "dinput8.dll"), Encoding.ASCII.GetBytes("fixture proxy placeholder"));
        File.WriteAllBytes(Path.Combine(Game, "Scripts", "FrontEndScripts.sbn"), FrontEnd);
        File.WriteAllBytes(Path.Combine(Game, "Scripts", "Other.sbn"), Other);
        File.WriteAllText(Path.Combine(Game, "settings.ini"), "[Display]\r\nWidth=1280\r\n");
        File.WriteAllBytes(Path.Combine(Game, "ssr_save.bin"), Enumerable.Range(0, 600).Select(i => (byte)(i * 7)).ToArray());
        foreach (var name in new[] { "Base", "Racers", "Select" })
        {
            var members = new SortedDictionary<uint, byte[]>();
            for (uint k = 1; k <= 8; k++) members[0x1000u * (uint)(name.Length) + k] = Encoding.ASCII.GetBytes($"{name} original member {k} " + new string('x', (int)(k * 37)));
            BaseArchives[name] = members;
            File.WriteAllBytes(Path.Combine(Game, "Resource", name + ".xpac"), TestXpac.Build(members));
        }
    }

    public string BaseKeyOf(string archive, int index) => BaseArchives[archive].Keys.ElementAt(index).ToString("X8");
    public uint BaseKey(string archive, int index) => BaseArchives[archive].Keys.ElementAt(index);
    public string GamePath(string rel) => Path.Combine(Game, rel.Replace('/', '\\'));

    static string Hex(string sha, bool upper) => upper ? sha.ToUpperInvariant() : sha;

    public BuiltMod WriteMod(ModSpec spec, string? underMods = null)
    {
        var dir = Path.Combine(underMods ?? Mods, spec.Folder);
        Directory.CreateDirectory(dir);
        int payloadNo = 0;
        var plan = new StringBuilder();
        string Payload(byte[] bytes, string hint)
        {
            var rel = $"payloads/{hint}-{++payloadNo}.bin";
            var full = Path.Combine(dir, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return rel;
        }
        var groups = new List<string>();
        foreach (var g in spec.Groups)
        {
            var sb = new StringBuilder();
            string P(string s) => System.Text.Json.JsonSerializer.Serialize(s);
            sb.Append("{\"path\":").Append(P(g.Path)).Append(",\"format\":").Append(P(g.Format));
            string H(string sha) => Hex(sha, g.UppercaseHashes);
            if (g.Format == "xpac")
            {
                var archiveName = System.IO.Path.GetFileNameWithoutExtension(g.Path);
                var baseMembers = BaseArchives.TryGetValue(archiveName, out var bm) ? bm : new SortedDictionary<uint, byte[]>();
                var baseFile = GamePath(g.Path);
                var baseSha = g.BaseOverrideHash ?? (File.Exists(baseFile) ? Sha.OfFile(baseFile) : new string('0', 64));
                if (g.WrongBase) baseSha = Sha.OfBytes(Encoding.ASCII.GetBytes("different base"));
                sb.Append(",\"base_sha256\":\"").Append(H(baseSha)).Append("\",\"operations\":[");
                var composed = new SortedDictionary<uint, byte[]>(baseMembers);
                bool first = true;
                foreach (var o in g.Ops)
                {
                    if (!first) sb.Append(','); first = false;
                    var payRel = Payload(o.Payload, $"m{o.Key:X8}");
                    var key = o.Style switch { KeyStyle.Number => o.Key.ToString(), KeyStyle.HexLower => "\"" + o.Key.ToString("x8") + "\"", _ => "\"" + o.Key.ToString("X8") + "\"" };
                    sb.Append("{\"key\":").Append(key).Append(",\"kind\":").Append(P(o.Kind));
                    if (o.Kind == "replace" && baseMembers.TryGetValue(o.Key, out var orig)) sb.Append(",\"base_stored_sha256\":\"").Append(H(Sha.OfBytes(orig))).Append('"');
                    else if (o.Kind == "replace") sb.Append(",\"base_stored_sha256\":\"").Append(new string('a', 64)).Append('"');
                    else sb.Append(",\"base_stored_sha256\":null");
                    var ph = Sha.OfBytes(o.Payload);
                    if (o.CorruptPayloadHash) ph = Sha.OfBytes(Encoding.ASCII.GetBytes("corrupt"));
                    sb.Append(",\"payload\":").Append(P(payRel)).Append(",\"payload_sha256\":\"").Append(H(ph)).Append("\",\"stored_size\":").Append(o.Payload.Length).Append(",\"entry_flags\":0}");
                    composed[o.Key] = o.Payload;
                }
                sb.Append(']');
                var bytes = TestXpac.Build(composed);
                var csha = g.WrongComposed ? Sha.OfBytes(Encoding.ASCII.GetBytes("x")) : Sha.OfBytes(bytes);
                sb.Append(",\"expected_composed_sha256\":\"").Append(H(csha)).Append("\",\"expected_composed_size\":").Append(bytes.Length).Append(",\"expected_member_count\":").Append(composed.Count);
            }
            else if (g.Format == "file")
            {
                var baseFile = GamePath(g.Path);
                var baseSha = File.Exists(baseFile) ? Sha.OfFile(baseFile) : new string('0', 64);
                if (g.WrongBase) baseSha = Sha.OfBytes(Encoding.ASCII.GetBytes("different base"));
                var rel = Payload(g.Payload, "file");
                sb.Append(",\"base_sha256\":\"").Append(H(baseSha)).Append("\",\"payload\":").Append(P(rel)).Append(",\"payload_sha256\":\"").Append(H(Sha.OfBytes(g.Payload))).Append("\",\"stored_size\":").Append(g.Payload.Length);
            }
            else
            {
                var rel = Payload(g.Payload, g.Format);
                sb.Append(",\"payload\":").Append(P(rel)).Append(",\"payload_sha256\":\"").Append(H(Sha.OfBytes(g.Payload))).Append("\",\"stored_size\":").Append(g.Payload.Length);
                if (g.Format == "rtsc_merge") sb.Append(",\"key_size\":").Append(g.KeySize);
            }
            sb.Append('}');
            groups.Add(sb.ToString());
        }
        plan.Append("{\"schema\":1");
        if (spec.PlanModId is not null) plan.Append(",\"mod_id\":\"").Append(spec.PlanModId).Append('"');
        plan.Append(",\"archive_groups\":[").Append(string.Join(",", groups)).Append("]}");
        var planText = spec.PlanOverrideText ?? plan.ToString();
        var planBytes = new UTF8Encoding(false).GetBytes(planText);
        if (spec.PlanOverrideText is not null && planText.StartsWith('﻿')) planBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(planText.TrimStart('﻿'))).ToArray();
        File.WriteAllBytes(Path.Combine(dir, "plan.json"), planBytes);
        Directory.CreateDirectory(Path.Combine(dir, "runtime"));
        File.WriteAllBytes(Path.Combine(dir, "runtime", "Rt.dll"), spec.Runtime);
        var planSha = Sha.OfBytes(planBytes);
        var rtSha = Sha.OfBytes(spec.Runtime);
        var pinPlan = spec.WrongPlanHash ? Sha.OfBytes(Encoding.ASCII.GetBytes("wrong plan")) : planSha;
        var pinRt = spec.WrongRuntimeHash ? Sha.OfBytes(Encoding.ASCII.GetBytes("wrong runtime")) : rtSha;
        if (spec.UppercasePinHash) { pinPlan = pinPlan.ToUpperInvariant(); pinRt = pinRt.ToUpperInvariant(); }
        var ini = spec.ManifestText ?? new StringBuilder()
            .Append("[mod]\r\nschema=").Append(spec.Schema).Append("\r\nid=").Append(spec.Id).Append("\r\n")
            .Append(spec.Name is null ? "" : "name=" + spec.Name + "\r\n")
            .Append(spec.Version is null ? "" : "version=" + spec.Version + "\r\n")
            .Append("plan=plan.json\r\nplan_sha256=").Append(pinPlan).Append("\r\nruntime=runtime/Rt.dll\r\nruntime_sha256=").Append(pinRt).Append("\r\n")
            .Append(spec.IniExtra).ToString();
        File.WriteAllText(Path.Combine(dir, spec.ManifestFileName), ini, new UTF8Encoding(false));
        foreach (var (name, text) in spec.ConfigFiles) File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
        foreach (var (name, text) in spec.ExtraFiles)
        {
            var p = Path.Combine(dir, name.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text, new UTF8Encoding(false));
        }
        if (spec.SidecarJson is not null) File.WriteAllText(Path.Combine(dir, SidecarReader.FileName), spec.SidecarJson, new UTF8Encoding(false));
        return new BuiltMod { Dir = dir, PlanSha = planSha, RuntimeSha = rtSha, Spec = spec };
    }

    public void Dispose()
    {
        // Fixture folders are kept for inspection when a test fails; the runner calls Cleanup on success only.
    }

    public void Cleanup()
    {
        try { Fs.DeleteTree(Root); } catch { }
    }
}

/// <summary>Whole-tree snapshot used to prove "exact baseline" after failures.</summary>
public static class Fs
{
    public static SortedDictionary<string, string> Snapshot(string root, bool excludeManagerDir = true)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Walk(string dir)
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var rel = Path.GetRelativePath(root, e.FullName);
                if (excludeManagerDir && rel.Replace('\\', '/').StartsWith("mods/.allstars-manager", StringComparison.OrdinalIgnoreCase)) continue;
                if (e is DirectoryInfo d)
                {
                    map["D " + rel] = (d.Attributes & FileAttributes.ReparsePoint) != 0 ? "link" : "";
                    if ((d.Attributes & FileAttributes.ReparsePoint) == 0) Walk(d.FullName);
                }
                else map["F " + rel] = ((FileInfo)e).Length + ":" + Sha.OfFile(e.FullName) + ":" + (e.Attributes & ~FileAttributes.Archive);
            }
        }
        Walk(root);
        return map;
    }

    public static string Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
    {
        var lines = new List<string>();
        foreach (var k in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            a.TryGetValue(k, out var x); b.TryGetValue(k, out var y);
            if (x != y) lines.Add($"{k}: {(x is null ? "<absent>" : "present")} -> {(y is null ? "<absent>" : "present")}{(x is not null && y is not null ? " (content/attributes differ)" : "")}");
        }
        return string.Join("; ", lines.Take(10));
    }

    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((e.Attributes & FileAttributes.ReadOnly) != 0) e.Attributes &= ~FileAttributes.ReadOnly;
        // junctions inside are removed as links (not followed)
        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => (d.Attributes & FileAttributes.ReparsePoint) != 0).ToList())
            try { d.Delete(); } catch { }
        Directory.Delete(dir, true);
    }

    /// <summary>Creates a directory junction (no elevation needed) using the Windows shell; used only inside fixtures.</summary>
    public static void MakeJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException("mklink failed: " + p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
    }
}
