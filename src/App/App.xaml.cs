using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace AllStarsModManager.App;

/// <summary>Command-line options. All are optional; with none, the manager starts normally and only reads.</summary>
public sealed class StartupOptions
{
    public string? DataDir;
    public string? Game;               // --game <folder>: use this folder (remembered)
    public string? Fixture;            // --fixture <folder>: a practice copy; launching disabled; not remembered
    public bool NoLaunch;              // --no-launch
    public List<string> AddPaths = new();   // --add <folder|zip> (repeatable)
    public string? ScreenshotPath;     // --screenshot <png>: save a picture of the window after loading, then exit
    public string? CaptureDir;         // --capture-dir <folder>: (testing) save pictures of the app's own open windows whenever <folder>\capture-request.txt appears
    public bool Help;
    public string? Error;
    public bool IsTestRun => Fixture is not null || DataDir is not null;

    public const string HelpText =
        "All-Stars Mod Manager\n\n" +
        "Options (all optional):\n" +
        "  --data-dir <folder>    keep settings, profiles and the library here instead of next to the program\n" +
        "  --game <folder>        use this game folder (and remember it)\n" +
        "  --fixture <folder>     work on a practice copy of the game folder: launching is switched off and the folder is not remembered\n" +
        "  --no-launch            never offer to start the game\n" +
        "  --add <folder|.zip>    open the Add mod window for this folder or archive (can be repeated)\n" +
        "  --screenshot <file>    save a picture of the window after it has loaded, then exit\n" +
        "  --capture-dir <folder> (testing) save pictures of this program's own windows when <folder>\\capture-request.txt appears\n" +
        "  --help                 show this text\n\n" +
        "With no options the manager starts, looks for your Steam install (read-only) and waits for you.";

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() { if (i + 1 >= args.Length) { o.Error = $"{args[i]} needs a value."; return ""; } return args[++i]; }
            switch (args[i].ToLowerInvariant())
            {
                case "--data-dir": o.DataDir = Next(); break;
                case "--game": o.Game = Next(); break;
                case "--fixture": o.Fixture = Next(); break;
                case "--no-launch": o.NoLaunch = true; break;
                case "--add": o.AddPaths.Add(Next()); break;
                case "--screenshot": o.ScreenshotPath = Next(); break;
                case "--capture-dir": o.CaptureDir = Next(); break;
                case "--help": case "-h": case "/?": o.Help = true; break;
                default: o.Error = $"Unknown option \"{args[i]}\"."; break;
            }
        }
        return o;
    }
}

public static class Screenshots
{
    /// <summary>Saves every visible window of this program as <c>{name}__{AutomationId or title}.png</c>. Only the program's own WPF visuals are rendered, never desktop pixels.</summary>
    public static List<string> SaveAll(string dir, string name, double scale = 1.0)
    {
        Directory.CreateDirectory(dir);
        var saved = new List<string>();
        foreach (Window w in Application.Current.Windows)
        {
            if (!w.IsVisible || w.Content is not FrameworkElement { ActualWidth: > 10 }) continue;
            var id = System.Windows.Automation.AutomationProperties.GetAutomationId(w);
            var tag = new string((id.Length > 0 ? id : w.Title).Where(char.IsLetterOrDigit).ToArray());
            var path = Path.Combine(dir, $"{name}__{tag}.png");
            Save(w, path, scale);
            saved.Add(path);
        }
        // Open menus and combo-box lists live in their own pop-up surfaces; render those too ({name}__popup1.png, ...).
        int n = 0;
        foreach (PresentationSource src in PresentationSource.CurrentSources)
        {
            if (src is not System.Windows.Interop.HwndSource { RootVisual: FrameworkElement { ActualWidth: > 10, IsVisible: true } root } || root is Window) continue;
            var path = Path.Combine(dir, $"{name}__popup{++n}.png");
            SaveElement(root, Brushes.Transparent, path, scale);
            saved.Add(path);
        }
        return saved;
    }

    public static void Save(Window w, string path, double scale = 1.0) => SaveElement((FrameworkElement)w.Content, w.Background ?? Brushes.White, path, scale);

    static void SaveElement(FrameworkElement content, Brush background, string path, double scale)
    {
        int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
        // Render the visual tree on top of the window's own background so the saved picture shows what the user sees.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }
        // scale > 1 renders the same layout at a higher pixel density (what a 125%..200% Windows scale setting does to the pixels).
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var opts = StartupOptions.Parse(e.Args);
        if (opts.Help || opts.Error is not null)
        {
            MessageDialog.Tell(null, "All-Stars Mod Manager", (opts.Error is null ? "" : opts.Error + "\n\n") + StartupOptions.HelpText, opts.Error is null ? DialogKind.Info : DialogKind.Warning);
            Shutdown(opts.Error is null ? 0 : 2);
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            Log(ex.Exception);
            MessageDialog.Tell(MainWindow, "All-Stars Mod Manager", "Something unexpected went wrong. Nothing in your game folder is left half-changed by the manager (an unfinished change is detected and can be restored on the next start).\n\n" + ex.Exception.Message, DialogKind.Warning);
        };

        var paths = AppPaths.Portable(opts.DataDir);
        ManagerEnvironment env;
        bool fixture = opts.Fixture is not null;
        if (fixture || opts.NoLaunch)
        {
            env = ManagerEnvironment.Fixture();
            env.NoLaunch = true;
        }
        else env = new ManagerEnvironment();
        var svc = new ManagerService(paths, env);
        if (fixture) svc.SelectGame(opts.Fixture, remember: false);
        else if (opts.Game is not null) svc.SelectGame(opts.Game);
        var vm = new MainViewModel(svc, fixture);
        var win = new MainWindow(vm, opts);
        MainWindow = win;
        win.Show();
    }

    static void Log(Exception e)
    {
        try
        {
            var dir = AppPaths.Portable(null).DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "manager.log"), $"{DateTime.UtcNow:o}  {e}\n", new UTF8Encoding(false));
        }
        catch { }
    }
}
