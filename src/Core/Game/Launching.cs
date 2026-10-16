using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AllStarsModManager;

/// <summary>Finds the Steam-registered All-Stars Racing install (read-only: two fields of one manifest file and the library list).</summary>
public sealed class SteamLocator
{
    public const int AppId = 34190;
    readonly IReadOnlyList<string> _libraries;

    public SteamLocator(IEnumerable<string>? libraryRoots = null) => _libraries = (libraryRoots ?? DiscoverLibraries()).ToList();

    public static IEnumerable<string> DiscoverLibraries()
    {
        var roots = new List<string>();
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s) && Directory.Exists(s) && !roots.Contains(s, StringComparer.OrdinalIgnoreCase)) roots.Add(s); }
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            Add((k?.GetValue("SteamPath") as string)?.Replace('/', '\\'));
        }
        catch { }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            Add(k?.GetValue("InstallPath") as string);
        }
        catch { }
        foreach (var steam in roots.ToList())
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (IOException) { }
        }
        return roots;
    }

    /// <summary>Install folders registered for the game's app id. Only the app id and install directory are read from the manifest.</summary>
    public IEnumerable<string> RegisteredInstalls()
    {
        foreach (var lib in _libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            var acf = Path.Combine(apps, $"appmanifest_{AppId}.acf");
            if (!File.Exists(acf)) continue;
            string? dir = null; bool idOk = false;
            try
            {
                foreach (var line in File.ReadLines(acf))
                {
                    var m = Regex.Match(line, "^\\s*\"(appid|installdir)\"\\s+\"([^\"]*)\"\\s*$");
                    if (!m.Success) continue;
                    if (m.Groups[1].Value == "appid") idOk = m.Groups[2].Value == AppId.ToString();
                    else dir = m.Groups[2].Value;
                }
            }
            catch (IOException) { continue; }
            if (!idOk || string.IsNullOrWhiteSpace(dir)) continue;
            string full;
            try { full = LoaderPath.Child(Path.Combine(apps, "common"), dir); } catch (PathRuleException) { continue; }
            if (Directory.Exists(full)) yield return full;
        }
    }

    public bool IsRegistered(string gameRoot)
    {
        var want = LoaderPath.Canonical(gameRoot).TrimEnd('\\');
        return RegisteredInstalls().Any(r => string.Equals(LoaderPath.Canonical(r).TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase));
    }

    public string? FirstRegistered() => RegisteredInstalls().FirstOrDefault();
}

public enum LaunchMode { Unavailable, SteamRegisteredInstall }

public sealed record LaunchPlan(LaunchMode Mode, string Reason, string? Uri)
{
    public bool CanLaunch => Mode == LaunchMode.SteamRegisteredInstall;
}

public interface IGameLauncher
{
    void Launch(LaunchPlan plan);
}

public static class LaunchPlanner
{
    public static readonly string SteamUri = $"steam://rungameid/{SteamLocator.AppId}";

    /// <summary>
    /// Steam's app id always starts the registered install, never an arbitrary folder, so launching is offered only
    /// when the selected folder is that install. Nothing in a mod's metadata can influence this.
    /// </summary>
    public static LaunchPlan Plan(GameFolderInfo? game, SteamLocator steam, bool noLaunch)
    {
        if (noLaunch) return new(LaunchMode.Unavailable, "Launching is switched off in this mode (test folder).", null);
        if (game is null || !game.Exists || !game.HasGameExe) return new(LaunchMode.Unavailable, "Choose your game folder first.", null);
        if (!steam.IsRegistered(game.Root))
            return new(LaunchMode.Unavailable, "Steam starts the game from its own registered folder, not from this one. Launch it from Steam, or select the Steam install folder.", null);
        return new(LaunchMode.SteamRegisteredInstall, "Starts the game through Steam.", SteamUri);
    }
}

/// <summary>The only place the manager asks the shell to start the game: a fixed steam:// address built from the app id.</summary>
public sealed class SteamUriLauncher : IGameLauncher
{
    public void Launch(LaunchPlan plan)
    {
        if (plan.Mode != LaunchMode.SteamRegisteredInstall || plan.Uri != LaunchPlanner.SteamUri)
            throw new InvalidOperationException("Launching is not available for this folder.");
        Process.Start(new ProcessStartInfo(LaunchPlanner.SteamUri) { UseShellExecute = true });
    }
}

/// <summary>Records launch requests instead of acting on them (tests and fixture mode).</summary>
public sealed class RecordingLauncher : IGameLauncher
{
    public List<LaunchPlan> Requests { get; } = new();
    public void Launch(LaunchPlan plan) => Requests.Add(plan);
}

/// <summary>Opens folders and plain text/config files with the user's default handler. Never opens executables or scripts.</summary>
public static class ShellOpen
{
    static readonly HashSet<string> SafeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ini", ".txt", ".md", ".json", ".cfg", ".log", ".csv" };

    public static bool CanOpenFile(string path) => SafeExtensions.Contains(Path.GetExtension(path));

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder.TrimEnd('\\') + "\"") { UseShellExecute = false });
    }

    public static void OpenFile(string path)
    {
        if (!CanOpenFile(path)) throw new InvalidOperationException("Only text and settings files can be opened from here.");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
