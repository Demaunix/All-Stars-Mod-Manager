using System.Diagnostics;
using System.Text.Json;

namespace AllStarsModManager.Tests;

public sealed class TestFailure(string message) : Exception(message);

public sealed record TestResult(string Name, string Area, bool Passed, double Seconds, string? Message);

/// <summary>Minimal test runner (no external packages are available offline).</summary>
public static class T
{
    public static readonly List<TestResult> Results = new();
    /// <summary>Checks that could not run in this checkout (for example because they need private local inputs), with the reason. Never counted as passed.</summary>
    public static readonly List<(string Name, string Reason)> Skipped = new();
    static string _area = "";
    public static string Filter = "";

    public static void Area(string name) => _area = name;

    public static void Run(string name, Action body)
    {
        if (Filter.Length > 0 && !(name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || _area.Contains(Filter, StringComparison.OrdinalIgnoreCase))) return;
        var sw = Stopwatch.StartNew();
        try { body(); Results.Add(new(name, _area, true, sw.Elapsed.TotalSeconds, null)); Console.WriteLine($"  PASS  {name}"); }
        catch (Exception e)
        {
            Results.Add(new(name, _area, false, sw.Elapsed.TotalSeconds, e is TestFailure ? e.Message : e.ToString()));
            Console.WriteLine($"  FAIL  {name}\n        {(e is TestFailure ? e.Message : e.GetType().Name + ": " + e.Message)}");
        }
    }

    public static void Skip(string name, string reason) { Skipped.Add((name, reason)); Console.WriteLine($"  SKIP  {name} ({reason})"); }

    public static void True(bool cond, string message) { if (!cond) throw new TestFailure(message); }
    public static void False(bool cond, string message) { if (cond) throw new TestFailure(message); }
    public static void Eq<TV>(TV expected, TV actual, string what)
    {
        if (!EqualityComparer<TV>.Default.Equals(expected, actual)) throw new TestFailure($"{what}: expected <{expected}> but was <{actual}>");
    }
    public static void Has(IEnumerable<string> haystack, string needle, string what)
    {
        if (!haystack.Any(h => h.Contains(needle, StringComparison.OrdinalIgnoreCase))) throw new TestFailure($"{what}: no entry contains \"{needle}\". Entries: {string.Join(" | ", haystack.Take(6))}");
    }
    public static void HasCode(IEnumerable<Issue> issues, string code, string what)
    {
        if (!issues.Any(i => i.Code == code)) throw new TestFailure($"{what}: expected issue code {code}; got [{string.Join(", ", issues.Select(i => i.Code))}]");
    }
    public static void NoBlockers(IEnumerable<Issue> issues, string what)
    {
        var b = issues.Where(i => i.Severity == Severity.Blocker).ToList();
        if (b.Count > 0) throw new TestFailure($"{what}: unexpected blockers: {string.Join(" | ", b.Select(i => i.Code + ": " + i.Message))}");
    }
    public static void Throws<TE>(Action a, string what) where TE : Exception
    {
        try { a(); } catch (TE) { return; } catch (Exception e) { throw new TestFailure($"{what}: expected {typeof(TE).Name} but got {e.GetType().Name}: {e.Message}"); }
        throw new TestFailure($"{what}: expected {typeof(TE).Name} but nothing was thrown");
    }

    public static int Finish(string resultsFile)
    {
        var passed = Results.Count(r => r.Passed);
        var failed = Results.Count - passed;
        Directory.CreateDirectory(Path.GetDirectoryName(resultsFile)!);
        File.WriteAllText(resultsFile, JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow.ToString("o"),
            total = Results.Count, passed, failed, skipped = Skipped.Count,
            skippedChecks = Skipped.Select(s => new { name = s.Name, reason = s.Reason }),
            byArea = Results.GroupBy(r => r.Area).Select(g => new { area = g.Key, total = g.Count(), passed = g.Count(x => x.Passed) }),
            results = Results.Select(r => new { r.Name, r.Area, r.Passed, r.Seconds, r.Message })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n{passed} passed, {failed} failed, {Skipped.Count} skipped ({Results.Count} run) -> {resultsFile}");
        return failed == 0 ? 0 : 1;
    }
}
