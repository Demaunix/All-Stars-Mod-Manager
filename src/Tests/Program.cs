using System.Text.Json;

namespace AllStarsModManager.Tests;

public static partial class Program
{
    public static string ModuleRoot = "";
    /// <summary>Where throw-away fixtures and captures go. Defaults to &lt;module&gt;\work; set ALLSTARS_TEST_WORK to a short path if the module sits deep in the file system (the loader's own cache paths hit the 260-character limit).</summary>
    public static string WorkRoot = "";

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static int Main(string[] args)
    {
        SetProcessDPIAware();   // UI Automation rectangles and SendInput coordinates must use the same (physical) pixels
        ModuleRoot = FindModuleRoot();
        WorkRoot = Environment.GetEnvironmentVariable("ALLSTARS_TEST_WORK") is { Length: > 0 } w ? Path.GetFullPath(w) : Path.Combine(ModuleRoot, "work");
        var mode = args.Length > 0 ? args[0] : "all";
        T.Filter = args.Length > 1 ? args[1] : "";
        var resultsDir = Path.Combine(ModuleRoot, "tests", "results");
        Directory.CreateDirectory(resultsDir);
        if (mode == "fixture")
        {
            var u = UiFixture.Build("ui");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { game = u.Game, data = u.Data, zip = u.ZipToAdd, folder = u.FolderToAdd, badZip = u.BadZip }));
            return 0;
        }
#if PRIVATE_TESTS
        if (mode == "corpus") return CorpusMode(args[1]);
        if (mode == "pe") return PeMode(args.Skip(1).ToArray());
        if (mode == "catalog") return CatalogMode(args.Length > 1 && args[1] == "deep", resultsDir);
#endif
        if (mode == "uidump") return UiDump.Run(resultsDir);
        if (mode == "packagecheck") return PackageCheck.Run("manager", args[1], args[2], args.Length > 3 ? args[3] : Path.Combine(resultsDir, "package-check-manager.json"));
        if (mode == "checkpackage") return PackageCheck.Run("checker", args[1], args[2], args.Length > 3 ? args[3] : Path.Combine(resultsDir, "package-check-checker.json"));
        if (mode == "settings-inproc") { T.Filter = ""; SettingsInProcTests.Register(); return T.Finish(Path.Combine(resultsDir, "settings-inproc-" + (args.Length > 1 ? args[1] : "current") + ".json")); }
        if (mode == "corecompare") return CoreCompare.Run(args[1], args[2], args.Length > 3 ? args[3] : Path.Combine(resultsDir, "core-binary-compare.json"));
        if (mode == "theme") { VisualTests.RegisterThemeOnly(); return T.Finish(Path.Combine(resultsDir, "theme-tests.json")); }
        if (mode == "ui")
        {
            Console.WriteLine($"UI tests against: {UiTests.ExePath}");
            UiTests.Register();
            VisualTests.Register();
            SettingsInProcTests.Register();
            return T.Finish(Path.Combine(resultsDir, "ui-tests.json"));
        }
        Console.WriteLine($"Module root: {ModuleRoot}");
        Console.WriteLine("== formats"); FormatTests.Register();
        Console.WriteLine("== packages"); PackageTests.Register();
        Console.WriteLine("== conflicts"); ConflictTests.Register();
        Console.WriteLine("== shared archive members (ChaoGarage 1.2.0)"); MemberSharingTests.Register();
#if PRIVATE_TESTS
        Console.WriteLine("== loader parity (private oracle)");
        if (File.Exists(OracleTests.OraclePath)) OracleTests.Register();
        else T.Skip("differential: manager vs the loader's own code on generated game folders", "private oracle executable not present in this checkout");
#else
        T.Skip("differential: manager vs the loader's own code on generated game folders", "private oracle and loader source are not part of the public source tree");
#endif
        Console.WriteLine("== transactions"); TransactionTests.Register();
        Console.WriteLine("== import"); ImportTests.Register();
        Console.WriteLine("== guards, launch, profiles"); MiscTests.Register();
#if PRIVATE_TESTS
        Console.WriteLine("== real final Ryo/Banjo packages (private inputs)");
        if (Directory.Exists(RealPackageTests.CandidatesDir)) RealPackageTests.Register();
        else T.Skip("real final Ryo/Banjo candidate packages", "private candidate inputs not found");
#else
        T.Skip("real final Ryo/Banjo candidate packages", "private inputs are not part of the public source tree");
#endif
        Console.WriteLine("== compatibility format and checker"); CompatTests.Register();
        return T.Finish(Path.Combine(resultsDir, mode == "all" ? "core-tests.json" : $"core-tests-{mode}.json"));
    }

    static string FindModuleRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "src", "Core", "AllStarsModManager.Core.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Module root not found.");
    }
}
