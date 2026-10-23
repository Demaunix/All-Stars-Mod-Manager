using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace AllStarsModManager.Tests;

/// <summary>
/// Checks for the 1.1.0 visual redesign: palette contrast, every screen state rendered by the app itself, long names,
/// the empty list, keyboard access and rendering at higher pixel density. Pictures are the app's own WPF renders
/// (--capture-dir), never desktop pixels. Behaviour of the manager itself is covered by UiTests and the core tests.
/// </summary>
public static class VisualTests
{
    static string ExePath => UiTests.ExePath;

    // ---- palette contrast ------------------------------------------------------------------------------
    static double Lin(double c) { c /= 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    static double Lum(string hex) => 0.2126 * Lin(int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber)) + 0.7152 * Lin(int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber)) + 0.0722 * Lin(int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber));
    static double Ratio(string a, string b) { var x = Lum(a); var y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    public static void RegisterThemeOnly()
    {
        T.Area("theme");
        T.Run("theme: text colours keep their contrast (WCAG AA 4.5:1) on every surface they are drawn on", () =>
        {
            var xaml = File.ReadAllText(Path.Combine(Program.ModuleRoot, "src", "App", "Theme", "Palette.xaml"));
            var c = Regex.Matches(xaml, "<Color x:Key=\"(\\w+)Color\">(#[0-9A-Fa-f]{6})</Color>").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            T.True(c.Count >= 20, "palette parsed: " + c.Count);
            var pairs = new (string fg, string bg)[]
            {
                ("Text", "Window"), ("Text", "Panel"), ("Text", "Inset"), ("Text", "Button"), ("Text", "ButtonHover"), ("Text", "HoverRow"), ("Text", "Selected"),
                ("Text", "InfoBg"), ("Text", "WarnBg"), ("Text", "DangerBg"), ("Text", "OkBg"),
                ("Muted", "Window"), ("Muted", "Panel"), ("Muted", "Inset"), ("Muted", "Button"), ("Muted", "HoverRow"), ("Muted", "Selected"),
                ("Faint", "Window"), ("Faint", "Panel"), ("Faint", "Inset"),
                ("AccentText", "Window"), ("AccentText", "Panel"), ("AccentText", "Inset"), ("AccentText", "Button"),
                ("Ok", "Window"), ("Ok", "Panel"), ("Ok", "Inset"), ("Ok", "HoverRow"), ("Ok", "Selected"),
                ("Warn", "Window"), ("Warn", "Panel"), ("Warn", "Inset"), ("Warn", "HoverRow"), ("Warn", "Selected"), ("Warn", "WarnBg"),
                ("Danger", "Window"), ("Danger", "Panel"), ("Danger", "Inset"), ("Danger", "HoverRow"), ("Danger", "Selected"), ("Danger", "DangerBg"), ("Danger", "Button"),
                ("Info", "Window"), ("Info", "Panel"), ("Info", "Inset"), ("Info", "Selected"), ("Info", "InfoBg"),
            };            var bad = pairs.Where(p => Ratio(c[p.fg], c[p.bg]) < 4.5).Select(p => $"{p.fg} {c[p.fg]} on {p.bg} {c[p.bg]} = {Ratio(c[p.fg], c[p.bg]):0.00}").ToList();
            T.True(bad.Count == 0, "below 4.5:1: " + string.Join("; ", bad));
            // white text on the blue fills (active tab, Apply button) and on the check-box tick
            T.True(Ratio("#FFFFFF", c["Accent"]) >= 4.5, $"white on the accent blue = {Ratio("#FFFFFF", c["Accent"]):0.00}");
            T.True(Ratio("#FFFFFF", c["ButtonHover"]) >= 4.5, "white on a hovered tab");
            Console.WriteLine($"        lowest ratio among {pairs.Length} text pairs: {pairs.Min(p => Ratio(c[p.fg], c[p.bg])):0.00}:1");
        });

        T.Run("theme: the neutral gray look of the references is kept (no racing decoration, gradients, custom display font or oversized badge)", () =>
        {
            var app = Path.Combine(Program.ModuleRoot, "src", "App");
            var all = string.Concat(Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories).Select(File.ReadAllText)) + string.Concat(Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")).Select(File.ReadAllText));
            foreach (var banned in new[] { "CheckerBand", "FinishLine", "SpeedLines", "GoldFill", "PlayFill", "HeaderWash", "Bahnschrift", "LinearGradientBrush", "RadialGradientBrush", "EmptyFlag" })
                T.False(all.Contains(banned), "the rejected racing styling is back: " + banned);
            var xaml = File.ReadAllText(Path.Combine(app, "Theme", "Palette.xaml"));
            T.True(xaml.Contains("Segoe UI"), "the ordinary Windows UI font");
            var controls = File.ReadAllText(Path.Combine(app, "Theme", "Controls.xaml"));
            T.False(System.Text.RegularExpressions.Regex.IsMatch(controls, "FontWeight=\"(Bold|Black|ExtraBold)\""), "no heavy type");
        });

        T.Run("theme: every window and dialog uses the shared dark theme, and no system message box is left in the app code", () =>
        {
            var app = Path.Combine(Program.ModuleRoot, "src", "App");
            foreach (var f in Directory.EnumerateFiles(app, "*.cs", SearchOption.TopDirectoryOnly))
                T.False(File.ReadAllText(f).Contains("MessageBox."), $"{Path.GetFileName(f)} still uses the system message box");
            foreach (var f in Directory.EnumerateFiles(app, "*Window.xaml", SearchOption.TopDirectoryOnly))
                T.True(File.ReadAllText(f).Contains("<local:ThemedWindow"), $"{Path.GetFileName(f)} is not a ThemedWindow");
            var dialogs = File.ReadAllText(Path.Combine(app, "Dialogs.cs"));
            T.Eq(3, Regex.Matches(dialogs, @"class \w+ : ThemedWindow").Count, "the three code-built dialogs derive from ThemedWindow");
            var xamlAll = string.Concat(Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories).Select(File.ReadAllText));
            T.False(Regex.IsMatch(xamlAll, "Background=\"White\"|Background=\"#FFFFFF\""), "no white panels");
        });
    }

    // ---- helpers -------------------------------------------------------------------------------------------
    static string Names(AutomationElement el) => string.Join("\n", el.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name));

    static (int w, int h) PngSize(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return (dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight);
    }

    static void Inside(AutomationElement el, System.Windows.Rect win, string what)
    {
        var b = el.Current.BoundingRectangle;
        T.True(b.Left >= win.Left - 1 && b.Right <= win.Right + 1 && b.Top >= win.Top - 1 && b.Bottom <= win.Bottom + 1, $"{what} lies inside the window ({b} vs {win})");
    }

    static void ClickByName(AutomationElement root, string name)
    {
        var b = UiApp.FindByName(root, name, ControlType.Button) ?? throw new TestFailure($"button '{name}' not found");
        UiApp.Click(b);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    static bool AppIsForeground(UiApp app) { GetWindowThreadProcessId(GetForegroundWindow(), out var pid); return pid == (uint)app.Process.Id; }

    /// <summary>Keys go out only while the app under test owns the foreground window, so they can never reach another program.</summary>
    static bool Key(UiApp app, byte vk, bool ctrl = false)
    {
        if (!AppIsForeground(app)) return false;
        if (ctrl) { keybd_event(0x11, 0, 0, UIntPtr.Zero); Thread.Sleep(60); }
        keybd_event(vk, 0, 0, UIntPtr.Zero); Thread.Sleep(40); keybd_event(vk, 0, 2, UIntPtr.Zero);
        if (ctrl) { Thread.Sleep(60); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
        Thread.Sleep(350);
        return AppIsForeground(app);
    }

    /// <summary>Looks for an element in the app's own top-level windows only (menus and pop-ups included), never across the whole desktop.</summary>
    static AutomationElement? FindInApp(UiApp app, string id)
    {
        var pid = new PropertyCondition(AutomationElement.ProcessIdProperty, app.Process.Id);
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, pid))
        {
            var el = top.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (el is not null) return el;
        }
        return null;
    }

    static string? FocusedId() { try { return AutomationElement.FocusedElement?.Current.AutomationId; } catch (ElementNotAvailableException) { return null; } }
    static string FocusedDescription() { try { var e = AutomationElement.FocusedElement; return e is null ? "(none)" : $"{e.Current.AutomationId}|{e.Current.Name}|{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}"; } catch (ElementNotAvailableException) { return "(gone)"; } }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

    /// <summary>Brings only the app's own window to the foreground (own-window minimize/restore, never a key press). False if Windows refuses.</summary>
    static bool EnsureForeground(UiApp app)
    {
        var hwnd = new IntPtr(app.Window.Current.NativeWindowHandle);
        var wp = (WindowPattern)app.Window.GetCurrentPattern(WindowPattern.Pattern);
        for (int attempt = 0; attempt < 3 && !AppIsForeground(app); attempt++)
        {
            SetForegroundWindow(hwnd);
            if (UiApp.WaitFor(() => AppIsForeground(app), 2)) break;
            wp.SetWindowVisualState(WindowVisualState.Minimized); Thread.Sleep(400);
            wp.SetWindowVisualState(WindowVisualState.Normal); Thread.Sleep(600);
            SetForegroundWindow(hwnd);
            UiApp.WaitFor(() => AppIsForeground(app), 2);
        }
        return AppIsForeground(app);
    }

    static byte[] Pixels(string path, out int w, out int h)
    {
        using var fs = File.OpenRead(path);
        var fr = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var conv = new FormatConvertedBitmap(fr, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        w = conv.PixelWidth; h = conv.PixelHeight;
        var buf = new byte[w * h * 4];
        conv.CopyPixels(buf, w * 4, 0);
        return buf;
    }

    /// <summary>Counts pixels that differ noticeably between two of the app's own renders inside a region (client coordinates).</summary>
    static int DiffIn(string a, string b, System.Windows.Rect region)
    {
        var pa = Pixels(a, out var w, out var h); var pb = Pixels(b, out var w2, out var h2);
        T.True(w == w2 && h == h2, "the two pictures have the same size");
        int x0 = Math.Max(0, (int)region.Left), x1 = Math.Min(w, (int)Math.Ceiling(region.Right)), y0 = Math.Max(0, (int)region.Top), y1 = Math.Min(h, (int)Math.Ceiling(region.Bottom));
        int n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            int i = (y * w + x) * 4;
            if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) > 30) n++;
        }
        return n;
    }

    sealed class EmptyFx
    {
        public Fx Fx = new("uiempty");
        public string Game => Fx.Game;
        public string Data => Path.Combine(Fx.Root, "data");
    }

    public static void Register()
    {
        RegisterThemeOnly();
        T.Area("visual");

        T.Run("visual: every screen state is rendered by the app itself (selected mod and its four tabs, pending + conflict, no match, manager settings page, dialogs, pop-up list)", () =>
        {
            var f = UiFixture.Build("uivis");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            var shots = new List<string>();
            shots.Add(app.Screenshot("20-main-normal"));
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null && UiApp.Text(app.Need("DetailTitle")).Contains("Quick Start"), 10, "details of Quick Start");
            shots.Add(app.Screenshot("21-selected-overview"));
            UiApp.Click(app.Need("TabCompat")); Thread.Sleep(300);
            shots.Add(app.Screenshot("22-details-compat"));
            UiApp.Click(app.Need("TabSettings"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Config_SkipIntro") is not null, 10, "configure rows");
            T.True(Names(app.Window).Contains("edited") == false, "no 'edited' marker before a change");
            UiApp.SetText(app.Need("Config_SkipIntro"), "0");
            UiApp.WaitFor(() => Names(app.Window).Contains("edited"), 10, "the 'edited' marker (an unsaved change is labelled, not only coloured)");
            shots.Add(app.Screenshot("23-details-configure-edited"));
            UiApp.Click(app.Need("ResetSettingsButton"));
            UiApp.WaitFor(() => !Names(app.Window).Contains("edited"), 10, "reset to clear the marker");
            UiApp.Click(app.Need("TabFiles")); Thread.Sleep(300);
            shots.Add(app.Screenshot("24-details-files"));
            UiApp.Click(app.Need("TabOverview"));

            // pending change next to a conflict
            UiApp.Click(app.Need("Check_beta_inst"));
            UiApp.WaitFor(() => UiTests.Pending(app).StartsWith("1 change"), 10);
            UiApp.Click(app.Need("Check_ten_stage_drift_inst"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "Banner_blockers") is not null, 10, "the conflict banner");
            var all = Names(app.Window);
            T.True(all.Contains("Will switch on"), "pending tag has words");
            T.True(all.Contains("Conflicts with") || all.Contains("problem"), "the conflict is stated in words");
            shots.Add(app.Screenshot("25-pending-and-conflict"));
            UiApp.Click(app.Need("DiscardButton"));
            UiApp.WaitFor(() => UiTests.Pending(app) == "No changes waiting", 10, "discard");

            // search with no result
            UiApp.SetText(app.Need("SearchBox"), "zzzz-nothing");
            UiApp.WaitFor(() => UiApp.Find(app.Window, "NoMatchText") is not null, 10, "the 'no mods match' message");
            T.Eq("0 of 9 mods shown", UiApp.Text(app.Need("ShownText")), "the shown count");
            shots.Add(app.Screenshot("26-no-match"));
            UiApp.Click(app.Need("ClearFilterButton"));
            UiApp.WaitFor(() => app.Rows().Count == 9, 10, "clear search and filter");
            T.Eq("", UiApp.Text(app.Need("SearchBox")), "search box cleared");

            // the second page
            UiApp.Click(app.Need("TabManager"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "GameFolderBox") is not null, 10, "the manager settings page");
            T.Eq(f.Game, UiApp.Text(app.Need("GameFolderBox")), "the page shows the chosen game folder");
            T.True(UiApp.Text(app.Need("AboutText")).Contains("1.3.0"), "about names the version: " + UiApp.Text(app.Need("AboutText")));
            T.True(UiApp.Text(app.Need("AboutText")).Contains("no update service"), "no fake update promise");
            shots.Add(app.Screenshot("27-manager-settings"));
            UiApp.Click(app.Need("TabMods"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "ModList") is not null, 10);

            // dialogs
            UiApp.Click(app.Need("SaveProfileButton"));
            var prompt = app.WaitWindow("PromptWindow", 10);
            shots.Add(app.ScreenshotWindow(prompt, "28-prompt-dialog"));
            ClickByName(prompt, "Cancel");
            UiApp.WaitFor(() => app.TryWindow("PromptWindow") is null, 10);
            UiApp.Click(app.Need("UndoLastButton"));
            var box = UiApp.WaitMessageBox(app, 10);
            T.True(UiApp.Text(UiApp.Need(box, "MessageText")).Contains("Go back to how the mods folder was"), "the confirmation wording is the manager's own");
            shots.Add(app.ScreenshotWindow(box, "29-confirm-dialog"));
            UiApp.Click(UiApp.Need(box, "MessageNo"));
            UiApp.WaitFor(() => app.TryWindow("MessageWindow") is null, 10);
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.Click(app.Need("TabSettings"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "EditTextButton") is not null, 10);
            UiApp.Click(app.Need("EditTextButton"));
            var te = app.WaitWindow("TextEditWindow", 10);
            shots.Add(app.ScreenshotWindow(te, "30-text-edit-dialog"));
            ClickByName(te, "Cancel");
            UiApp.WaitFor(() => app.TryWindow("TextEditWindow") is null, 10);

            // an open combo-box list (its own pop-up surface)
            var combo = app.Need("FilterCombo");
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            Thread.Sleep(500);
            shots.Add(app.ScreenshotPopup("31-combo-popup"));
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            foreach (var s in shots) T.True(File.Exists(s) && new FileInfo(s).Length > (s.Contains("popup") ? 400 : 3000), "picture written: " + s);
            Console.WriteLine($"        {shots.Count} app-rendered pictures in {UiApp.ShotDir}");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: the Add and profile menus are themed (rendered pop-ups)", () =>
        {
            var f = UiFixture.Build("uimenu");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            UiApp.Click(app.Need("AddModButton"));
            UiApp.WaitFor(() => FindInApp(app, "MenuAddZip") is not null, 10, "the Add menu");
            Thread.Sleep(400);
            T.True(File.Exists(app.ScreenshotPopup("32-add-menu")), "Add menu picture");
            T.Eq(0, app.CloseWithWindowMessage(), "exit code with a menu open");
            using var app2 = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app2, 9);
            UiApp.Click(app2.Need("ProfileMoreButton"));
            UiApp.WaitFor(() => FindInApp(app2, "MenuRenameProfile") is not null, 10, "the profile menu");
            Thread.Sleep(400);
            T.True(File.Exists(app2.ScreenshotPopup("33-profile-menu")), "profile menu picture");
            T.Eq(0, app2.CloseWithWindowMessage(), "exit code with a menu open");
            f.Scn.Cleanup();
        });

        T.Run("visual: very long names, IDs, versions and paths are trimmed or wrapped, never push controls out of the window (normal and minimum size)", () =>
        {
            var f = UiFixture.BuildLong();
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 10);
            var win = app.Window.Current.BoundingRectangle;
            app.Screenshot("40-long-names");
            var longRow = app.Rows().First(r => r.Current.Name.Contains("Super Mega"));
            app.Select(longRow);
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null && UiApp.Text(app.Need("DetailTitle")).Contains("Super Mega"), 10, "details of the long mod");
            Thread.Sleep(400);
            app.Screenshot("41-long-selected");
            void CheckAll(string label)
            {
                var w = app.Window.Current.BoundingRectangle;
                foreach (var id in new[] { "GameTitle", "GamePath", "DetailTitle", "DetailSubtitle", "ModList", "DetailTabs", "PendingText", "ApplyButton", "PlayButton", "SearchBox", "AddModButton" })
                    Inside(app.Need(id), w, label + ": " + id);
                var list = app.Need("ModList").Current.BoundingRectangle;
                foreach (var r in app.Rows()) { var b = r.Current.BoundingRectangle; T.True(b.Left >= list.Left - 1 && b.Right <= list.Right + 1, $"{label}: row '{r.Current.Name}' stays inside the list ({b} vs {list})"); }
            }
            CheckAll("normal");
            var tp = (TransformPattern)app.Window.GetCurrentPattern(TransformPattern.Pattern);
            tp.Resize(880, 480); Thread.Sleep(900);
            T.True(UiApp.Find(app.Window, "DetailTitle") is not null, "details still shown");
            CheckAll("minimum");
            app.Screenshot("42-long-names-minimum");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: an empty mod list explains itself and offers Add mod; Apply and Play stay off", () =>
        {
            var e = new EmptyFx();
            using var app = UiApp.Start(ExePath, "--fixture", e.Game, "--data-dir", e.Data);
            UiApp.WaitFor(() => UiApp.Find(app.Window, "EmptyText") is not null && UiApp.Text(UiApp.Need(app.Window, "EmptyText")).Contains("No mods were found"), 40, "the empty-list text");
            UiApp.WaitFor(() => !UiApp.Text(app.Need("BusyText")).Contains("Checking") && !UiApp.Text(app.Need("BusyText")).Contains("Reading"), 40, "reading to finish");
            T.Eq(0, app.Rows().Count, "no rows");
            T.True(app.Need("EmptyAddButton").Current.IsEnabled, "Add mod is offered in the empty state");
            T.False(app.Need("ApplyButton").Current.IsEnabled, "Apply off");
            T.False(app.Need("PlayButton").Current.IsEnabled, "Play off");
            app.Screenshot("43-empty-list");
            T.Eq(0, app.CloseCleanly(), "exit code");
            e.Fx.Cleanup();
        });

        T.Run("visual: the title bar is in dark mode (read back from Windows) and the window carries the app icon", () =>
        {
            var f = UiFixture.Build("uititle");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            var hwnd = new IntPtr(app.Window.Current.NativeWindowHandle);
            // The caption/text/border colour attributes (Windows 11) are write-only in DWM, so only dark mode can be read back.
            int hrDark = DwmGetWindowAttribute(hwnd, 20, out var dark, 4);
            Console.WriteLine($"        DWM read-back: dark mode hr={hrDark} value={dark}; OS {Environment.OSVersion.Version} (caption colours are write-only: set by the app, not readable, not captured)");
            T.Eq(0, hrDark, "dark title bar attribute readable"); T.Eq(1, dark, "immersive dark mode is on");
            // the window icon exists: UI Automation exposes no icon, so read it back from Windows (WM_GETICON)
            var small = SendMessageW(hwnd, 0x7F, new IntPtr(0), IntPtr.Zero);
            T.True(small != IntPtr.Zero, "the window has a small icon (title bar / taskbar)");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: every interactive control accepts keyboard focus and has an accessible name (UI Automation view)", () =>
        {
            var f = UiFixture.Build("uifocus");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            foreach (var id in new[] { "TabMods", "TabManager", "SearchBox", "FilterCombo", "ProfileCombo", "SaveProfileButton", "ProfileMoreButton", "AddModButton", "RefreshButton", "UndoLastButton", "OpenFolderButton", "Check_alpha_inst", "DetailsSplitter" })
            {
                var el = app.Need(id);
                T.True(el.Current.IsKeyboardFocusable, $"{id} can take keyboard focus");
                T.False(string.IsNullOrWhiteSpace(el.Current.Name), $"{id} has an accessible name");
            }
            foreach (var id in new[] { "ApplyButton", "DiscardButton", "PlayButton" }) T.False(string.IsNullOrWhiteSpace(app.Need(id).Current.Name) && string.IsNullOrWhiteSpace(app.Need(id).Current.HelpText), $"{id} has a name or help text even while disabled");
            foreach (var r in app.Rows()) T.False(string.IsNullOrWhiteSpace(r.Current.Name), "every row has an accessible name");
            // a disabled control says why (Play) and the pending tag is a word, not just a colour
            T.True(app.Need("PlayButton").Current.HelpText.Length > 10, "Play explains why it is off");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: real key presses work: Ctrl+F, Tab order, arrow keys and Space (keys are sent only while the test window is the foreground window)", () =>
        {
            var f = UiFixture.Build("uikeys");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            // bring only our own window to the front: minimize and restore it through its own window pattern (never a key press)
            var hwnd = new IntPtr(app.Window.Current.NativeWindowHandle);
            var wp = (WindowPattern)app.Window.GetCurrentPattern(WindowPattern.Pattern);
            for (int attempt = 0; attempt < 3 && !AppIsForeground(app); attempt++)
            {
                SetForegroundWindow(hwnd);
                if (UiApp.WaitFor(() => AppIsForeground(app), 2)) break;
                wp.SetWindowVisualState(WindowVisualState.Minimized); Thread.Sleep(400);
                wp.SetWindowVisualState(WindowVisualState.Normal); Thread.Sleep(600);
                SetForegroundWindow(hwnd);
                UiApp.WaitFor(() => AppIsForeground(app), 2);
            }
            var ctx = GetWindowDpiAwarenessContext(hwnd);
            Console.WriteLine($"        window DPI awareness = {GetAwarenessFromDpiAwarenessContext(ctx)} (0 unaware, 1 system, 2 per-monitor), DPI = {GetDpiForWindow(hwnd)}");
            if (!AppIsForeground(app))
            {
                app.CloseCleanly(); f.Scn.Cleanup();
                throw new TestFailure("NOT RUN: the test window could not be brought to the foreground, so no key presses were sent (environment limit, not an app result)");
            }
            var alpha = app.Rows().First(r => r.Current.Name.Contains("Alpha"));
            app.Select(alpha); alpha.SetFocus(); Thread.Sleep(300);
            T.True(Key(app, 0x20), "Space sent");            // VK_SPACE on the focused row toggles its mod
            UiApp.WaitFor(() => UiTests.Pending(app).StartsWith("1 change"), 10, "Space on a focused row switched Alpha off (pending)");
            T.False(UiApp.IsChecked(app.Need("Check_alpha_inst")), "Alpha's box is now clear");
            Key(app, 0x20);
            UiApp.WaitFor(() => UiTests.Pending(app) == "No changes waiting", 10, "second Space puts it back");
            // focus stays on the row after a toggle, so the arrow keys still move the selection
            var before = UiApp.Text(app.Need("DetailTitle"));
            T.True(Key(app, 0x28), "Down sent");   // VK_DOWN
            UiApp.WaitFor(() => UiApp.Text(app.Need("DetailTitle")) != before, 10, "Down moves the selection to the next mod after a toggle");
            var order = new List<string?>();
            var described = new List<string>();
            bool tabValid = false; string tabWhy = "";
            for (int attempt = 1; attempt <= 3 && !tabValid; attempt++)
            {
                // An attempt is valid only when every key press was actually sent to the test window (Key returns false when it is not the foreground window) and the window
                // still had the foreground afterwards. A key sequence that was sent correctly and gives the wrong order is a real failure (checked below, not retried).
                order.Clear(); described.Clear();
                bool sent = EnsureForeground(app);
                sent &= Key(app, 0x46, ctrl: true);
                UiApp.WaitFor(() => FocusedId() == "SearchBox", 5, "Ctrl+F moves focus to the search box");
                for (int i = 0; i < 12 && sent; i++) { sent &= Key(app, 0x09); order.Add(FocusedId()); described.Add(FocusedDescription()); }   // VK_TAB
                tabValid = sent && AppIsForeground(app);
                tabWhy = $"attempt {attempt}: every key sent={sent}, foreground afterwards={AppIsForeground(app)}";
                if (!tabValid) Console.WriteLine("        INVALID ATTEMPT (key presses did not all reach the window; repeated): " + tabWhy);
            }
            if (!tabValid) throw new TestFailure("NOT RUN: the key presses never all reached the test window (environment, not an application result). " + tabWhy);            Console.WriteLine("        Tab order after the search box: " + string.Join("  >  ", described));
            T.Eq("FilterCombo", order[0], "first Tab stop after the search box");
            T.True(order.IndexOf("ProfileCombo") > order.IndexOf("FilterCombo") && order.IndexOf("SaveProfileButton") > order.IndexOf("ProfileCombo"), "the Tab order follows the visual order left to right");
            app.Screenshot("50-keyboard-focus");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: keyboard focus is VISIBLE on representative controls of both pages (the app's own renders differ inside the control when it holds keyboard focus); folder controls are enabled when idle", () =>
        {
            var f = UiFixture.BuildLinked();
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 10);
            var hwnd = new IntPtr(app.Window.Current.NativeWindowHandle);
            if (!EnsureForeground(app)) { app.CloseCleanly(); f.Scn.Cleanup(); throw new TestFailure("NOT RUN: the test window could not be brought to the foreground (keyboard focus needs an active window)"); }
            T.Eq(96u, GetDpiForWindow(hwnd), "the pixel mapping below assumes 96 DPI (one pixel = one device-independent pixel)");
            var origin = new POINT(); ClientToScreen(hwnd, ref origin);
            var results = new List<string>();
            // Every probe records the preconditions of its two captures: the test window is the foreground window, the "other" control holds keyboard focus for the off
            // picture, the target holds it for the on picture, and both still hold after the capture. An attempt in which any of them failed is INVALID (something else
            // took the foreground or the focus while the picture was taken) and is repeated at most twice more; it is never counted as a result. An attempt in which all of them
            // held and the control still shows no focus indicator is a real failure and fails immediately: the assertion is not weakened.
            void Probe(string label, AutomationElement target, AutomationElement other, int minPixels = 40)
            {
                int invalid = 0; string last = "";
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    bool fgStart = EnsureForeground(app);
                    other.SetFocus(); Thread.Sleep(350);
                    bool otherFocused = other.Current.HasKeyboardFocus && AppIsForeground(app);
                    var off = app.Screenshot("focus-probe-off");
                    bool otherStill = other.Current.HasKeyboardFocus && AppIsForeground(app);
                    target.SetFocus(); Thread.Sleep(350);
                    bool targetFocused = target.Current.HasKeyboardFocus && AppIsForeground(app);
                    var on = app.Screenshot("focus-probe-on");
                    bool targetStill = target.Current.HasKeyboardFocus && AppIsForeground(app);
                    bool valid = fgStart && otherFocused && otherStill && targetFocused && targetStill;
                    last = $"{label} attempt {attempt}: foreground at start={fgStart}, off-picture other focused={otherFocused}/{otherStill}, on-picture target focused={targetFocused}/{targetStill}";
                    if (!valid) { invalid++; Console.WriteLine("        INVALID ATTEMPT (preconditions did not hold; repeated): " + last); continue; }
                    var b = target.Current.BoundingRectangle;
                    var region = new System.Windows.Rect(b.Left - origin.X - 4, b.Top - origin.Y - 4, b.Width + 8, b.Height + 8);
                    int n = DiffIn(off, on, region);
                    results.Add($"{label}: {n}");
                    T.True(n >= minPixels, $"{label}: no visible focus indicator (only {n} pixels changed inside the control) although every precondition held: {last}");
                    File.Copy(on, Path.Combine(UiApp.ShotDir, "52-focus-" + new string(label.Where(char.IsLetterOrDigit).ToArray()) + ".png"), true);
                    return;
                }
                throw new TestFailure($"NOT RUN: the focus/foreground preconditions never held in 3 attempts ({invalid} invalid), so no result for {label} (environment, not an application result). Last: {last}");
            }            var tabMods = app.Need("TabMods");
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Alpha")));
            foreach (var id in new[] { "SearchBox", "FilterCombo", "ProfileCombo", "SaveProfileButton", "ProfileMoreButton", "AddModButton", "RefreshButton", "UndoLastButton", "OpenFolderButton", "Check_alpha_inst" })
                Probe(id, app.Need(id), tabMods);   // (a tab header is not probed: focusing it also selects it, which would change the picture for another reason)
            Probe("row Alpha", app.Rows().First(r => r.Current.Name.Contains("Alpha")), tabMods);
            Probe("DetailsSplitter", app.Need("DetailsSplitter"), tabMods, 25);
            // second page
            UiApp.Click(app.Need("TabManager"));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "BrowseGameButton") is not null, 10, "the Manager settings page");
            foreach (var id in new[] { "BrowseGameButton", "DetectGameButton", "AddLinkedButton", "RemoveLinkedButton", "TechnicalCheck" })
                T.True(app.Need(id).Current.IsEnabled, id + " is enabled when the manager is idle");
            var tabManager = app.Need("TabManager");
            foreach (var id in new[] { "BrowseGameButton", "DetectGameButton", "AddLinkedButton", "RemoveLinkedButton", "TechnicalCheck" })
                Probe(id, app.Need(id), tabManager);
            var item = UiApp.Need(app.Window, "LinkedFolders").FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)) ?? throw new TestFailure("the linked-folder list has no item");
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Probe("linked folder list item", item, tabManager);
            Console.WriteLine("        changed pixels inside each control when focused: " + string.Join("; ", results));
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: layout pressure of scaled screens: the window is usable at the device-independent sizes that 125%, 150% and 200% Windows scaling leave on common screens (emulated by window size, NOT by changing the Windows display scale)", () =>
        {
            var f = UiFixture.Build("uiscale");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null, 10);
            var tp = (TransformPattern)app.Window.GetCurrentPattern(TransformPattern.Pattern);
            // usable area in device-independent pixels (screen / scale, minus a 40 DIP taskbar): the window cannot be smaller than its minimum (880 x 480)
            foreach (var (name, w, h) in new[] { ("1366x768-at-125", 1093, 574), ("1920x1080-at-150", 1280, 680), ("1920x1080-at-200", 960, 500), ("1366x768-at-150", 910, 480) })
            {
                tp.Resize(w, h); Thread.Sleep(900);
                var win = app.Window.Current.BoundingRectangle;
                T.True(win.Width >= w - 2 && win.Height >= h - 2, $"{name}: window is {win.Width:0}x{win.Height:0}, wanted at least {w}x{h}");
                foreach (var id in new[] { "TabMods", "TabManager", "GameTitle", "SearchBox", "FilterCombo", "ProfileCombo", "SaveProfileButton", "ProfileMoreButton", "ModList", "AddModButton", "RefreshButton", "DetailTitle", "DetailTabs", "PendingText", "UndoLastButton", "OpenFolderButton", "DiscardButton", "PlayButton", "ApplyButton" })
                {
                    var el = app.Need(id);
                    T.False(el.Current.IsOffscreen, $"{name}: {id} is off screen");
                    Inside(el, win, $"{name}: {id}");
                }
                var rects = new[] { "SaveProfileButton", "ProfileMoreButton", "ProfileCombo", "FilterCombo", "SearchBox" }.Select(id => (id, r: app.Need(id).Current.BoundingRectangle)).ToList();
                for (int i = 0; i < rects.Count; i++) for (int j = i + 1; j < rects.Count; j++)
                    T.False(rects[i].r.Left < rects[j].r.Right - 1 && rects[j].r.Left < rects[i].r.Right - 1 && rects[i].r.Top < rects[j].r.Bottom - 1 && rects[j].r.Top < rects[i].r.Bottom - 1, $"{name}: {rects[i].id} overlaps {rects[j].id}");
                var bar = new[] { "UndoLastButton", "OpenFolderButton", "DiscardButton", "PlayButton", "ApplyButton", "PendingText" }.Select(id => (id, r: app.Need(id).Current.BoundingRectangle)).ToList();
                for (int i = 0; i < bar.Count; i++) for (int j = i + 1; j < bar.Count; j++)
                    T.False(bar[i].r.Left < bar[j].r.Right - 1 && bar[j].r.Left < bar[i].r.Right - 1 && bar[i].r.Top < bar[j].r.Bottom - 1 && bar[j].r.Top < bar[i].r.Bottom - 1, $"{name}: {bar[i].id} overlaps {bar[j].id}");
                var list = app.Need("ModList").Current.BoundingRectangle;
                T.True(list.Height >= 80, $"{name}: the list keeps room for rows ({list.Height:0} px)");
                app.Screenshot("46-layout-" + name);
            }
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });

        T.Run("visual: higher-density RASTER renders (125/150/200%) of the same layout are produced; image output only, not Windows scaling", () =>
        {
            var f = UiFixture.Build("uiraster");
            using var app = UiApp.Start(ExePath, UiTests.Args(f));
            UiTests.WaitLoaded(app, 9);
            app.Select(app.Rows().First(r => r.Current.Name.Contains("Quick Start")));
            UiApp.WaitFor(() => UiApp.Find(app.Window, "DetailTitle") is not null, 10);
            var basePath = app.Screenshot("44-raster-100");
            var (bw, bh) = PngSize(basePath);
            foreach (var pct in new[] { 125, 150, 200 })
            {
                var p = app.Screenshot($"44-raster-{pct}", pct);
                var (w, h) = PngSize(p);
                T.True(Math.Abs(w - bw * pct / 100.0) <= 2 && Math.Abs(h - bh * pct / 100.0) <= 2, $"{pct}% picture is {w}x{h}, expected about {bw * pct / 100.0:0}x{bh * pct / 100.0:0}");
            }
            Console.WriteLine("        NOTE: these are re-rasterised pictures of an unchanged layout; they prove image output only. The Windows display scale was not changed.");
            T.Eq(0, app.CloseCleanly(), "exit code");
            f.Scn.Cleanup();
        });
    }
}