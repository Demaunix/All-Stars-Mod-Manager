using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace AllStarsModManager.Tests;

/// <summary>
/// Compares two builds of the managed Core library without running them: every method body (IL bytes), type/method/field names and signatures,
/// the string, user-string and blob heaps, resources, and assembly identity. Used to relate one build of the Core library to another (for example a rebuild in
/// another folder). Only the module id (MVID) and similar build stamps are expected to differ.
/// </summary>
public static class CoreCompare
{
    sealed class Image
    {
        public string Path = "", Sha256 = "", Mvid = "", AssemblyName = "", Version = "";
        public long Bytes;
        public Dictionary<string, string> Methods = new();       // "Type::Name(sig)" -> sha256(IL + local signature)
        public SortedSet<string> Types = new(), Fields = new(), Resources = new();
        public Dictionary<string, string> Heaps = new();         // heap name -> sha256
        public int TypeCount, MethodCount, MethodsWithBody;
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    static string Sha(byte[] b) => Hex(SHA256.HashData(b));

    static Image Load(string path)
    {
        var img = new Image { Path = path, Sha256 = Hex(SHA256.HashData(File.ReadAllBytes(path))), Bytes = new FileInfo(path).Length };
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var asm = md.GetAssemblyDefinition();
        img.AssemblyName = md.GetString(asm.Name); img.Version = asm.Version.ToString();
        img.Mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString();
        var block = pe.GetMetadata().GetContent().ToArray();
        foreach (var (name, idx) in new[] { ("#Strings", HeapIndex.String), ("#US", HeapIndex.UserString), ("#Blob", HeapIndex.Blob) })
        {
            int size = md.GetHeapSize(idx), off = md.GetHeapMetadataOffset(idx);
            img.Heaps[name] = size == 0 ? "(empty)" : Sha(block.AsSpan(off, size).ToArray());
        }
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var tn = (t.Namespace.IsNil ? "" : md.GetString(t.Namespace) + ".") + md.GetString(t.Name);
            img.Types.Add(tn); img.TypeCount++;
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                img.Fields.Add(tn + "::" + md.GetString(f.Name) + ":" + Hex(md.GetBlobBytes(f.Signature)) + ":" + (int)f.Attributes);
            }
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                img.MethodCount++;
                var key = tn + "::" + md.GetString(m.Name) + ":" + Hex(md.GetBlobBytes(m.Signature)) + ":" + (int)m.Attributes;
                string body = "(no body)";
                if (m.RelativeVirtualAddress != 0)
                {
                    var mb = pe.GetMethodBody(m.RelativeVirtualAddress);
                    var local = mb.LocalSignature.IsNil ? "" : Hex(md.GetBlobBytes(md.GetStandaloneSignature(mb.LocalSignature).Signature));
                    body = Sha(mb.GetILBytes()!) + ":" + local + ":" + mb.MaxStack + ":" + mb.ExceptionRegions.Length;
                    img.MethodsWithBody++;
                }
                img.Methods[key] = body;
            }
        }
        foreach (var rh in md.ManifestResources) img.Resources.Add(md.GetString(md.GetManifestResource(rh).Name));
        return img;
    }

    public static int Run(string pathA, string pathB, string outFile)
    {
        var a = Load(pathA); var b = Load(pathB);
        var onlyA = a.Methods.Keys.Except(b.Methods.Keys).ToList();
        var onlyB = b.Methods.Keys.Except(a.Methods.Keys).ToList();
        var different = a.Methods.Where(kv => b.Methods.TryGetValue(kv.Key, out var o) && o != kv.Value).Select(kv => kv.Key).ToList();
        bool same = onlyA.Count == 0 && onlyB.Count == 0 && different.Count == 0
                    && a.Types.SetEquals(b.Types) && a.Fields.SetEquals(b.Fields) && a.Resources.SetEquals(b.Resources)
                    && a.AssemblyName == b.AssemblyName && a.Version == b.Version && a.Heaps.All(h => b.Heaps[h.Key] == h.Value);
        var report = new
        {
            a = new { a.Path, a.Sha256, a.Bytes, a.Mvid, a.AssemblyName, a.Version, a.TypeCount, a.MethodCount, a.MethodsWithBody, a.Heaps },
            b = new { b.Path, b.Sha256, b.Bytes, b.Mvid, b.AssemblyName, b.Version, b.TypeCount, b.MethodCount, b.MethodsWithBody, b.Heaps },
            byte_identical = a.Sha256 == b.Sha256,
            managed_program_identical = same,
            method_bodies_compared = a.Methods.Count - onlyA.Count,
            method_bodies_identical = a.Methods.Count - onlyA.Count - different.Count,
            methods_only_in_a = onlyA, methods_only_in_b = onlyB, methods_with_different_bodies = different,
            types_equal = a.Types.SetEquals(b.Types), fields_equal = a.Fields.SetEquals(b.Fields), resources_equal = a.Resources.SetEquals(b.Resources),
            expected_differences = "module id (MVID) and other build stamps; debug information is not part of this comparison"
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"A {a.Sha256} ({a.Bytes} bytes)  B {b.Sha256} ({b.Bytes} bytes)");
        Console.WriteLine($"byte identical: {report.byte_identical}; managed program identical: {same}; method bodies identical {report.method_bodies_identical}/{report.method_bodies_compared}; types {a.TypeCount}/{b.TypeCount}; MVID {a.Mvid} vs {b.Mvid}");
        return same ? 0 : 1;
    }
}
