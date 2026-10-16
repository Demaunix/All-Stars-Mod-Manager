using System.Text;
using System.Windows.Automation;

namespace AllStarsModManager.Tests;

/// <summary>Diagnostic: writes the accessibility tree of the running app (what UI Automation / screen readers see).</summary>
public static class UiDump
{
    public static int Run(string resultsDir)
    {
        var u = UiFixture.Build("dump");
        using var app = UiApp.Start(UiTests.ExePath, "--fixture", u.Game, "--data-dir", u.Data);
        Thread.Sleep(7000);
        var sb = new StringBuilder();
        void Walk(AutomationElement e, int depth)
        {
            if (depth > 16) return;
            var c = e.Current;
            sb.AppendLine($"{new string(' ', depth * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} id=\"{c.AutomationId}\" name=\"{c.Name}\" enabled={c.IsEnabled}");
            var w = TreeWalker.ControlViewWalker;
            for (var ch = w.GetFirstChild(e); ch is not null; ch = w.GetNextSibling(ch)) Walk(ch, depth + 1);
        }
        Walk(app.Window, 0);
        File.WriteAllText(Path.Combine(resultsDir, "ui-tree.txt"), sb.ToString());
        Console.WriteLine(sb.ToString().Split('\n').Length + " lines");
        app.CloseCleanly();
        return 0;
    }
}
