using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AllStarsModManager.Tests;

/// <summary>Drives the real packaged exe through Windows UI Automation (the same interface screen readers use).</summary>
public sealed class UiApp : IDisposable
{
    public Process Process { get; }
    public AutomationElement Window { get; private set; } = null!;
    public string Exe { get; }
    public static string ShotDir => Path.Combine(Program.ModuleRoot, "tests", "results", "ui");

    UiApp(Process p, string exe) { Process = p; Exe = exe; }

    public static UiApp Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var capDir = Path.Combine(Program.WorkRoot, "ui-capture-" + Guid.NewGuid().ToString("N")[..8]);
        psi.ArgumentList.Add("--capture-dir"); psi.ArgumentList.Add(capDir);
        var p = Process.Start(psi) ?? throw new TestFailure("could not start " + exe);
        var app = new UiApp(p, exe) { CaptureDir = capDir };
        app.Window = app.WaitWindow("MainWindow", 40);
        return app;
    }

    public AutomationElement WaitWindow(string automationId, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (Process.HasExited) throw new TestFailure($"the app exited (code {Process.ExitCode}) before {automationId} appeared");
            var el = TryWindow(automationId);
            if (el is not null) return el;
            Thread.Sleep(150);
        }
        throw new TestFailure($"window {automationId} did not appear within {seconds}s");
    }

    /// <summary>Dialogs are owned windows: UI Automation lists them under the main window, and sometimes at the top level.</summary>
    public AutomationElement? TryWindow(string automationId)
    {
        var idCond = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        if (Window is not null)
        {
            try { var owned = Window.FindFirst(TreeScope.Children, idCond); if (owned is not null) return owned; } catch (ElementNotAvailableException) { }
        }
        return AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Process.Id), idCond));
    }

    public static AutomationElement? Find(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static AutomationElement Need(AutomationElement root, string automationId)
        => Find(root, automationId) ?? throw new TestFailure($"UI element '{automationId}' not found");

    public AutomationElement Need(string id) => Need(Window, id);

    public static AutomationElement? FindByName(AutomationElement root, string name, ControlType? type = null)
    {
        Condition c = new PropertyCondition(AutomationElement.NameProperty, name);
        if (type is not null) c = new AndCondition(c, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return root.FindFirst(TreeScope.Descendants, c);
    }

    public static string Text(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) return ((ValuePattern)vp).Current.Value;
        return el.Current.Name;
    }

    public static void Click(AutomationElement el)
    {
        if (!el.Current.IsEnabled) throw new TestFailure($"'{el.Current.AutomationId}' is disabled");
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return; }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return; }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return; }
        throw new TestFailure($"'{el.Current.AutomationId}' cannot be clicked through UI Automation");
    }

    public static bool IsChecked(AutomationElement el) => ((TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    public static void SetText(AutomationElement el, string text) => ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    public static bool WaitFor(Func<bool> cond, double seconds = 15, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            try { if (cond()) return true; } catch (ElementNotAvailableException) { } catch (InvalidOperationException) { }
            Thread.Sleep(120);
        }
        if (what is not null) throw new TestFailure($"timed out after {seconds}s waiting for: {what}");
        return false;
    }

    // the app's own themed message dialog (it replaced the system message box in 1.1.0)
    public static AutomationElement WaitMessageBox(UiApp app, double seconds)
    {
        AutomationElement? box = null;
        WaitFor(() => (box = FindMessageBox(app)) is not null, seconds, "a message dialog");
        return box!;
    }

    public static AutomationElement? FindMessageBox(UiApp app) => app.TryWindow("MessageWindow");

    public static void PressYes(AutomationElement box)
    {
        var b = Find(box, "MessageYes") ?? Find(box, "MessageOk") ?? throw new TestFailure("no Yes/OK button in the message dialog");
        Click(b);
    }

    public static void DismissMessageBoxIfAny(UiApp app)
    {
        WaitFor(() => FindMessageBox(app) is not null, 6);
        for (int i = 0; i < 3; i++)
        {
            var box = FindMessageBox(app);
            if (box is null) return;
            var b = Find(box, "MessageOk") ?? Find(box, "MessageYes");
            if (b is not null) Click(b);
            Thread.Sleep(300);
        }
    }
    public List<AutomationElement> Rows() =>
        Need("ModList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToList();

    public List<string> RowNames() => Rows().Select(r => r.Current.Name).ToList();

    public void Select(AutomationElement item) => ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public void ChooseCombo(string comboId, string itemName)
    {
        var combo = Need(comboId);
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        AutomationElement? item = null;
        WaitFor(() => (item = FindByName(combo, itemName) ?? FindByName(AutomationElement.RootElement, itemName, ControlType.ListItem)) is not null, 5, $"combo item {itemName}");
        Select(item!);
        try { ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); } catch { }
    }

    public string CaptureDir { get; set; } = "";

    /// <summary>
    /// Asks the app (started with --capture-dir) to render its own windows with WPF; returns the picture of the main window.
    /// Desktop pixels are never captured (PrintWindow and screen copies were tried and returned blank/unrelated content).
    /// </summary>
    public string Screenshot(string name, int scalePercent = 100) => CaptureNamed(name, "MainWindow", scalePercent);

    /// <summary>Picture of an open pop-up (menu or combo-box list) rendered by the app itself; index is 1-based in the order the app found them.</summary>
    public string ScreenshotPopup(string name, int index = 1) => CaptureNamed(name, "popup" + index, 100);

    public string ScreenshotWindow(AutomationElement window, string name, int scalePercent = 100) => CaptureNamed(name, window.Current.AutomationId, scalePercent);

    string CaptureNamed(string name, string windowId, int scalePercent)
    {
        if (CaptureDir.Length == 0) throw new TestFailure("this app instance was not started with --capture-dir");
        Directory.CreateDirectory(ShotDir);
        var done = Path.Combine(CaptureDir, name + ".done");
        if (File.Exists(done)) File.Delete(done);
        Thread.Sleep(300);   // let the last layout/render pass finish
        File.WriteAllText(Path.Combine(CaptureDir, "capture-request.txt"), scalePercent == 100 ? name : name + "@" + scalePercent);
        WaitFor(() => File.Exists(done), 15, $"the app to save its picture '{name}'");
        string status = "";
        // the marker exists a moment before the app finishes writing it
        for (int i = 0; i < 40; i++)
        {
            try { status = File.ReadAllText(done); if (status.Length > 0) break; } catch (IOException) { }
            Thread.Sleep(50);
        }
        if (status != "ok") throw new TestFailure("capture failed: " + status);
        var tag = new string(windowId.Where(char.IsLetterOrDigit).ToArray());
        var src = Path.Combine(CaptureDir, $"{name}__{tag}.png");
        if (!File.Exists(src)) throw new TestFailure($"no picture of window '{windowId}' was produced");
        var dest = Path.Combine(ShotDir, name + ".png");
        File.Copy(src, dest, true);
        return dest;
    }
    /// <summary>Closes only the test's own window, the way the title-bar close button does, and waits for the process to end.</summary>
    public int CloseCleanly(double seconds = 15)
    {
        try { ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch { }
        if (!Process.WaitForExit((int)(seconds * 1000)))
        {
            // Never leave a window nobody can close: it is our own test process.
            try { Process.Kill(); Process.WaitForExit(5000); } catch { }
            throw new TestFailure("the app did not exit after its window was closed");
        }
        return Process.ExitCode;
    }

    /// <summary>Closes the window with the same close message the title-bar button sends. Needed while a menu is open: UI Automation's Close is ignored then, a real close message is not.</summary>
    public int CloseWithWindowMessage(double seconds = 15)
    {
        Process.CloseMainWindow();
        if (!Process.WaitForExit((int)(seconds * 1000)))
        {
            try { Process.Kill(); Process.WaitForExit(5000); } catch { }
            throw new TestFailure("the app did not exit after a close message");
        }
        return Process.ExitCode;
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(5000); } } catch { }
        Process.Dispose();
    }
}
