using System.IO.Compression;
using System.Text;

namespace AllStarsModManager.Tests;

/// <summary>A representative practice game folder + manager data folder for UI checks. Nothing here is a real game file.</summary>
public sealed class UiFixture
{
    public Scn Scn = null!;
    public string Game => Scn.Fx.Game;
    public string Data => Scn.Paths.DataDir;
    public string ZipToAdd = "";
    public string FolderToAdd = "";
    public string BadZip = "";

    static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>The usual practice folder plus one mod whose name, ID, version and description are far longer than any column.</summary>
    public static UiFixture BuildLong(string tag = "uilong")
    {
        var u = Build(tag);
        var fx = u.Scn.Fx;
        var big = PackageTests.Basic("an-extremely-long-mod-identifier-used-only-to-test-the-layout", "LongNameMod", "12.34.56-alpha.78+build.9abcdef0123456789"); big.Groups.Clear();
        big.Name = "The Super Mega Ultra Extended Edition of the Absolutely Unnecessarily Long Named Character Pack for Sonic and SEGA All-Stars Racing (Remastered, Reloaded and Revisited)";
        big.SidecarJson = "{\"id\":\"an-extremely-long-mod-identifier-used-only-to-test-the-layout\",\"name\":\"" + big.Name + "\",\"version\":\"12.34.56-alpha.78+build.9abcdef0123456789\",\"summary\":\"" + string.Concat(Enumerable.Repeat("This description is deliberately very long so that wrapping, trimming and scrolling can be inspected. ", 5)) + "\",\"status\":{\"stage\":\"CANDIDATE_OFFLINE_VERIFIED\"}}";
        fx.WriteMod(big);
        u.Scn.Reload();
        return u;
    }

    /// <summary>The usual practice folder with one linked (older-mods) folder, so the Manager settings page has a list item.</summary>
    public static UiFixture BuildLinked(string tag = "uilink")
    {
        var u = Build(tag);
        u.Scn.Svc.Settings.LinkedFolders.Add(Path.GetDirectoryName(u.FolderToAdd)!);
        u.Scn.Svc.Settings.Save(u.Scn.Paths);
        return u;
    }

    public static UiFixture Build(string tag = "ui")
    {
        var u = new UiFixture { Scn = Scn.Rich(tag) };
        var fx = u.Scn.Fx;
        // an installed mod with description file, status text, persistence note and a config whose own switch is off
        var quick = PackageTests.Basic("quick-start", "Quick Start", "1.2.0"); quick.Groups.Clear();
        quick.ConfigFiles["config.ini"] = "; Quick Start settings\r\n[QuickStart]\r\nEnabled=0\r\nSkipIntro=1\r\n";
        quick.SidecarJson = "{\"id\":\"quick-start\",\"name\":\"Quick Start\",\"version\":\"1.2.0\",\"summary\":\"Skips the intro movies and goes straight to the menu.\",\"status\":{\"stage\":\"CANDIDATE_OFFLINE_VERIFIED\"},\"persistence\":{\"statement\":\"Nothing is stored in your save by this mod.\",\"disable_note\":\"No saved data is affected by switching this off.\"},\"human_gameplay_requirements\":[\"Start the game and confirm the intro is skipped.\"]}";
        fx.WriteMod(quick);
        // two mods that change the same archive member: one on, one waiting (switched off with a reason)
        var honest = PackageTests.Basic("honest-gauges", "Honest Gauges", "0.3.0"); honest.Groups.Clear();
        honest.Groups.Add(GroupSpec.Xpac("Resource/Racers.xpac", (fx.BaseKey("Racers", 2), "replace", Bytes("honest gauge data"))));
        fx.WriteMod(honest);
        var drift = PackageTests.Basic("ten-stage-drift", "Ten Stage Drift", "0.2.0"); drift.Groups.Clear();
        drift.Groups.Add(GroupSpec.Xpac("Resource/Racers.xpac", (fx.BaseKey("Racers", 2), "replace", Bytes("drift data"))));
        drift.ManifestFileName = "mod.ini.disabled-for-Honest-Gauges";
        fx.WriteMod(drift);
        // a mod with no name in its manifest (older style) and a damaged one
        var plain = PackageTests.Basic("metal-sonic", "Metal Sonic", "1.1.0-beta.3"); plain.Name = null; plain.Groups.Clear();
        plain.Groups.Add(GroupSpec.Xpac("Resource/Base.xpac", (fx.BaseKey("Base", 3), "replace", Bytes("metal"))));
        plain.ManifestFileName = "mod.ini.disabled-by-manager";
        fx.WriteMod(plain);
        var broken = PackageTests.Basic("damaged-one", "Damaged One", "0.0.1"); broken.WrongRuntimeHash = true; broken.ManifestFileName = "mod.ini.disabled-old";
        fx.WriteMod(broken);
        u.Scn.Reload();

        // things to add through the UI
        var incoming = Path.Combine(fx.Root, "to-add"); Directory.CreateDirectory(incoming);
        var fresh = PackageTests.Basic("fresh-mod", "Fresh Mod", "1.0.0"); fresh.Groups.Clear();
        fresh.Groups.Add(GroupSpec.File("Scripts/FrontEndScripts.sbn", Bytes("fresh script")));
        var built = fx.WriteMod(fresh, Path.Combine(incoming, "wrap"));
        u.ZipToAdd = Path.Combine(incoming, "Fresh Mod 1.0.0.zip");
        using (var fs = File.Create(u.ZipToAdd))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var f in Directory.EnumerateFiles(built.Dir, "*", SearchOption.AllDirectories))
                z.CreateEntry("Fresh Mod/" + Path.GetRelativePath(built.Dir, f).Replace('\\', '/')).Open().Using(s => s.Write(File.ReadAllBytes(f)));
        var folderMod = PackageTests.Basic("folder-mod", "Folder Mod", "0.5.0"); folderMod.Groups.Clear();
        u.FolderToAdd = fx.WriteMod(folderMod, Path.Combine(incoming, "plainfolder")).Dir;
        u.BadZip = Path.Combine(incoming, "evil.zip");
        using (var fs = File.Create(u.BadZip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            z.CreateEntry("../outside.txt").Open().Using(s => s.Write(Bytes("x")));
        return u;
    }
}

static class StreamExt
{
    public static void Using(this Stream s, Action<Stream> a) { using (s) a(s); }
}
