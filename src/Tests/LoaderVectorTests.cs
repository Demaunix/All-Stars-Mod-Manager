using System.Text;
using System.Text.Json;
using AllStarsModManager.Compat;

namespace AllStarsModManager.Tests;

/// <summary>
/// The loader-requirement cases shared with the ChaoGarage loader (vectors/loader-requirements.json, the same file as ChaoGarage's
/// tests/vectors). Each single-mod case runs through the real checker on a fixture whose loader matches a profile with the vector's
/// capabilities; the loader's own outcome for the same file is listed next to it, and the two must agree except where a case names
/// the difference (rules the loader does not apply).
/// </summary>
public static class LoaderVectorTests
{
    static string VectorPath => Path.Combine(Program.ModuleRoot, "src", "Tests", "vectors", "loader-requirements.json");

    static byte[] CaseBytes(JsonElement c, string host, string proxy, string other)
    {
        if (c.TryGetProperty("hex", out var hex)) return Convert.FromHexString(hex.GetString()!);
        if (c.TryGetProperty("repeat", out var r))
            return Encoding.UTF8.GetBytes(r.GetProperty("prefix").GetString() + new string(r.GetProperty("fill").GetString()![0], r.GetProperty("count").GetInt32()) + r.GetProperty("suffix").GetString());
        if (c.TryGetProperty("nest", out var n)) { int k = n.GetInt32(); return Encoding.UTF8.GetBytes("{\"x\":" + new string('[', k) + new string(']', k) + "}"); }
        var t = c.GetProperty("text").GetString()!.Replace("$HOST_UPPER", host.ToUpperInvariant()).Replace("$HOST", host).Replace("$PROXY", proxy).Replace("$OTHER", other);
        return Encoding.UTF8.GetBytes(t);
    }

    public static void Register()
    {
        T.Area("compat-vectors");
        T.Run("shared loader vectors: the checker's verdict for each single-mod case, and agreement with the ChaoGarage 1.1.0 loader's own outcome", () =>
        {
            var v = JsonDocument.Parse(File.ReadAllBytes(VectorPath)).RootElement;
            T.Eq("chaogarage-loader-vectors/1", v.GetProperty("schema").GetString(), "vector schema");
            var self = v.GetProperty("self");
            var caps = string.Join(",", self.GetProperty("capabilities").EnumerateArray().Select(c => "\"" + c.GetString() + "\""));
            var other = self.GetProperty("other_sha256").GetString()!;
            int cases = 0;
            var wrong = new List<string>();
            foreach (var c in v.GetProperty("cases").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString()!;
                using var fx = new Fx("vec");
                var pf = CompatCheckerTests.ProfileFile(fx, caps);
                var host = Sha.OfFile(fx.GamePath("ChaoGarage.dll")); var proxy = Sha.OfFile(fx.GamePath("dinput8.dll"));
                var m = fx.WriteMod(CompatCheckerTests.Spec("vec-mod", "Vec"));
                if (!c.TryGetProperty("absent", out _)) File.WriteAllBytes(Path.Combine(m.Dir, SidecarReader.FileName), CaseBytes(c, host, proxy, other));
                var r = CompatCheckerTests.Cli(fx.Game, "--loader-profiles", pf, "--json");
                int want = c.GetProperty("checker_exit").GetInt32();
                if (want != r.Exit)
                    wrong.Add($"{name}: checker exit {r.Exit}, vector says {want} ({string.Join(", ", JsonDocument.Parse(r.Out).RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()))})");
                // the loader starts exactly the mods the checker declares compatible, unless the case names why they differ
                bool loaderStarts = c.GetProperty("loader").GetString() == "start";
                if (!c.TryGetProperty("divergence", out _)) { if (loaderStarts != (want == 0)) wrong.Add($"{name}: loader and checker disagree without a stated reason"); }
                else if (!(loaderStarts && want != 0)) wrong.Add($"{name}: a stated difference may only be the loader starting what the tools question");
                fx.Cleanup();
                cases++;
            }
            foreach (var w in wrong) Console.WriteLine("        vector mismatch: " + w);
            T.True(wrong.Count == 0, wrong.Count + " vector mismatch(es), listed above");
            T.True(cases >= 60, "all vector cases ran: " + cases);
        });
        T.Run("shared loader vectors: declared start order, checked against the loader's real order (folder names, metal-sonic first)", () =>
        {
            var v = JsonDocument.Parse(File.ReadAllBytes(VectorPath)).RootElement;
            foreach (var oc in v.GetProperty("order_cases").EnumerateArray())
            {
                var name = oc.GetProperty("name").GetString()!;
                using var fx = new Fx("vec");
                var pf = CompatCheckerTests.ProfileFile(fx);
                foreach (var m in oc.GetProperty("mods").EnumerateArray())
                    fx.WriteMod(CompatCheckerTests.Spec(m.GetProperty("id").GetString()!, m.GetProperty("folder").GetString()!, "1.0.0", m.TryGetProperty("text", out var t) ? t.GetString() : null));
                var j = JsonDocument.Parse(CompatCheckerTests.Cli(fx.Game, "--loader-profiles", pf, "--json").Out).RootElement;
                var refused = j.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("code").GetString() == "load-order")
                    .Select(f => f.GetProperty("title").GetString()!.Split(" must start ")[0]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                var want = oc.GetProperty("refused").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList();
                T.Eq(string.Join("|", want), string.Join("|", refused), "order: " + name);
                fx.Cleanup();
            }
        });
    }
}
