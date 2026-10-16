using System.Text;
using System.Text.Json;
using AllStarsModManager.Transactions;

namespace AllStarsModManager.Tests;

/// <summary>A game folder with installed mods, a library, a data folder and a ManagerService wired to a fake process probe.</summary>
public sealed class Scn : IDisposable
{
    public Fx Fx = null!;
    public AppPaths Paths = null!;
    public ManagerService Svc = null!;
    public FixedProbe Probe = new(GameRunState.NotRunning);
    public TransactionHooks Hooks = new();
    public ProbeHolder Holder = new();

    public sealed class ProbeHolder : IGameProcessProbe
    {
        public GameRunState State = GameRunState.NotRunning;
        public ProbeResult Check(string gameRoot) => new(State, State == GameRunState.NotRunning ? "The game is not running." : State == GameRunState.Running ? "The game is running." : "Cannot tell whether the game is running.");
    }

    public static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    public static Scn Rich(string tag = "sc")
    {
        var s = new Scn { Fx = new Fx(tag) };
        var fx = s.Fx;
        // installed: alpha (active), beta (switched off with a reason-suffixed manifest), gamma (active)
        var alpha = PackageTests.Basic("alpha", "Alpha"); alpha.Groups.Clear();
        alpha.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 0), "replace", Bytes("alpha member"))));
        alpha.ConfigFiles["config.ini"] = "; alpha settings\r\n[Alpha]\r\nEnabled=1\r\nSpeed=5\r\n";
        alpha.ExtraFiles["notes/keepme.txt"] = "user note";
        fx.WriteMod(alpha);
        var beta = PackageTests.Basic("beta", "Beta"); beta.Groups.Clear();
        beta.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 1), "replace", Bytes("beta member"))));
        beta.ManifestFileName = "mod.ini.disabled-for-reasons";
        fx.WriteMod(beta);
        var gamma = PackageTests.Basic("gamma", "Gamma"); gamma.Groups.Clear();
        gamma.Groups.Add(GroupSpec.File("Scripts/Other.sbn", Bytes("gamma script")));
        fx.WriteMod(gamma);
        // a library package in Banjo layout: mods/<Delta> + loose files + package description
        var pkgRoot = Path.Combine(fx.Root, "incoming", "delta-pkg");
        var delta = PackageTests.Basic("delta", "Delta"); delta.Groups.Clear(); delta.Version = "2.0.0";
        delta.Groups.Add(GroupSpec.Xpac("Resource/Racers.xpac", (fx.BaseKey("Racers", 0), "replace", Bytes("delta racer"))));
        delta.Groups.Add(GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("delta front end")));
        BuildLooseModPackage(fx, pkgRoot, delta, ("Resource/Movie/D1.usm", Bytes("movie one data")), ("Resource/Movie/D2.usm", Bytes("movie two data!!")));
        s.Paths = new AppPaths(Path.Combine(fx.Root, "data"));
        var env = ManagerEnvironment.Fixture(s.Holder);
        env.Hooks = s.Hooks;
        s.Svc = new ManagerService(s.Paths, env);
        s.Svc.Settings.GameRoot = fx.Game;
        s.Svc.Settings.Save(s.Paths);
        s.Svc.Refresh(false);
        var pv = s.Svc.Importer.Inspect(pkgRoot, s.Svc.Library);
        if (!pv.Usable) throw new InvalidOperationException("fixture package not usable: " + string.Join(";", pv.Issues.Select(i => i.Message)));
        s.Svc.Importer.AddToLibrary(pv);
        s.Svc.Refresh(false);
        s.Svc.VerifyAll();
        return s;
    }

    public static void BuildLooseModPackage(Fx fx, string pkgRoot, ModSpec spec, params (string target, byte[] data)[] loose)
    {
        Directory.CreateDirectory(Path.Combine(pkgRoot, "mods"));
        var assets = new List<object>();
        foreach (var (target, data) in loose)
        {
            var p = Path.Combine(pkgRoot, target.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            assets.Add(new { target, sha256 = Sha.OfBytes(data), bytes = data.Length, source_in_package = target, role = "test movie" });
        }
        spec.SidecarJson = JsonSerializer.Serialize(new
        {
            manifest_schema = "allstars-mod/1", id = spec.Id, name = spec.Name, version = spec.Version,
            status = new { stage = "CANDIDATE_OFFLINE_VERIFIED" },
            package_completeness = new { mod_folder_alone_is_complete = false, required_outside_mod_folder = "loose movies" },
            loose_assets = assets,
            persistence = new { statement = "Saved records stay in the save after this mod is switched off. Another mod could make them visible again." }
        });
        fx.WriteMod(spec, Path.Combine(pkgRoot, "mods"));
    }

    public ModEntry E(string id, string? version = null) => Svc.Entries.First(e => e.Id == id && (version is null || e.Package.Version == version));
    public ModEntry Installed(string folder) => Svc.Entries.First(e => e.InstalledFolder == folder);

    public void Reload(bool verify = true)
    {
        Svc.Refresh(false);
        if (verify) Svc.VerifyAll();
    }

    /// <summary>Set exactly these mods (by id) on; everything else off.</summary>
    public void Want(params string[] ids)
    {
        foreach (var e in Svc.Entries) Svc.Want[e.Key] = false;
        foreach (var id in ids) Svc.Want[E(id).Key] = true;
    }

    public PlanResult Prepare() => Svc.Prepare();

    public HashSet<string> ActiveIds()
    {
        var g = GameFolderScanner.Scan(Fx.Game);
        return g.ActiveFolders.Select(f => f.Package!.Id!).ToHashSet();
    }

    public SortedDictionary<string, string> Snap(bool excludeManager = true) => Fs.Snapshot(Fx.Game, excludeManager);

    public void Dispose() { }
    public void Cleanup() => Fx.Cleanup();
}
