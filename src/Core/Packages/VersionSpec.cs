using System.Text.RegularExpressions;

namespace AllStarsModManager;

/// <summary>What <see cref="VersionSpec.Evaluate"/> could conclude. Only <see cref="Satisfied"/> is an affirmative answer.</summary>
public enum VersionCheck
{
    /// <summary>The version is inside the range.</summary>
    Satisfied,
    /// <summary>The version is outside the range.</summary>
    NotSatisfied,
    /// <summary>The range is the literal UNKNOWN, or the version to test is missing or not a comparable version.</summary>
    Unknown,
    /// <summary>The range text is not in the supported grammar (see docs/PACKAGE-FORMAT.md). It is never partially accepted.</summary>
    InvalidRange
}

/// <summary>
/// Version parsing and range evaluation with a deliberately small, fully specified grammar (docs/PACKAGE-FORMAT.md, "Versions and ranges").
/// Anything outside the grammar is reported as unknown or invalid, never guessed and never partially accepted.
/// </summary>
public static class VersionSpec
{
    public const int MaxVersionLength = 80;
    public const int MaxRangeLength = 200;
    public const int MaxComparators = 16;
    const int MaxNumericComponents = 16;

    /// <summary>A parsed version. Numeric parts are kept as digit strings so that no value can overflow.</summary>
    public sealed record Parsed(string[] Nums, string[] Pre);

    // ASCII only ([0-9] rather than \d, \z rather than $): the text comes from untrusted files.
    static readonly Regex VersionRx = new(
        @"\Av?(?<n>[0-9]+(?:\.[0-9]+){0,15})(?:-(?<p>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static Parsed? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > MaxVersionLength) return null;
        var m = VersionRx.Match(text);
        if (!m.Success) return null;
        var nums = m.Groups["n"].Value.Split('.').Select(StripZeros).ToArray();
        if (nums.Length > MaxNumericComponents) return null;
        var pre = m.Groups["p"].Success ? m.Groups["p"].Value.Split('.') : Array.Empty<string>();
        return new Parsed(nums, pre);
    }

    static string StripZeros(string digits)
    {
        int i = 0;
        while (i < digits.Length - 1 && digits[i] == '0') i++;
        return digits[i..];
    }

    /// <summary>Compares two digit strings without leading zeros (no overflow, any length).</summary>
    static int CompareDigits(string a, string b)
    {
        if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
        int c = string.CompareOrdinal(a, b);
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }

    static bool AllDigits(string s) { foreach (var c in s) if (c is < '0' or > '9') return false; return s.Length > 0; }

    /// <summary>
    /// Precedence as in Semantic Versioning 2.0.0 (build metadata ignored), except that missing numeric components count as 0
    /// (1.2 equals 1.2.0). Negative if a&lt;b, 0 equal, positive a&gt;b, null if either side is not a comparable version.
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        var x = ParseVersion(a); var y = ParseVersion(b);
        if (x is null || y is null) return null;
        return Compare(x, y);
    }

    public static int Compare(Parsed x, Parsed y)
    {
        int n = Math.Max(x.Nums.Length, y.Nums.Length);
        for (int i = 0; i < n; i++)
        {
            int c = CompareDigits(i < x.Nums.Length ? x.Nums[i] : "0", i < y.Nums.Length ? y.Nums[i] : "0");
            if (c != 0) return c;
        }
        if (x.Pre.Length == 0 && y.Pre.Length == 0) return 0;
        if (x.Pre.Length == 0) return 1;     // a release is newer than any pre-release of it
        if (y.Pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            string p = x.Pre[i], q = y.Pre[i];
            bool pn = AllDigits(p), qn = AllDigits(q);
            int c;
            if (pn && qn) c = CompareDigits(StripZeros(p), StripZeros(q));
            else if (pn) c = -1;             // numeric identifiers sort before alphanumeric ones
            else if (qn) c = 1;
            else { c = string.CompareOrdinal(p, q); c = c < 0 ? -1 : c > 0 ? 1 : 0; }
            if (c != 0) return c;
        }
        return x.Pre.Length.CompareTo(y.Pre.Length) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    enum Op { Eq, Ge, Le, Gt, Lt }
    sealed record Comparator(Op Op, Parsed Version);

    /// <summary>Parses a complete range. Returns null (with the reason) unless the WHOLE text is in the grammar.</summary>
    static List<Comparator>? ParseRange(string spec, out string? error)
    {
        error = null;
        var list = new List<Comparator>();
        int i = 0;
        static bool Sep(char c) => c is ' ' or '\t' or '\r' or '\n' or ',';
        static bool VChar(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '-' or '+';
        while (true)
        {
            while (i < spec.Length && Sep(spec[i])) i++;
            if (i >= spec.Length) break;
            var op = Op.Eq;
            if (spec.AsSpan(i).StartsWith(">=")) { op = Op.Ge; i += 2; }
            else if (spec.AsSpan(i).StartsWith("<=")) { op = Op.Le; i += 2; }
            else if (spec.AsSpan(i).StartsWith("==")) { op = Op.Eq; i += 2; }
            else if (spec[i] == '=') { op = Op.Eq; i++; }
            else if (spec[i] == '>') { op = Op.Gt; i++; }
            else if (spec[i] == '<') { op = Op.Lt; i++; }
            while (i < spec.Length && (spec[i] == ' ' || spec[i] == '\t')) i++;   // ">= 1.0.0" is allowed, ">=,1.0.0" is not
            int start = i;
            while (i < spec.Length && VChar(spec[i])) i++;
            if (i == start) { error = i < spec.Length ? $"unexpected character '{Printable(spec[i])}'" : "an operator is not followed by a version"; return null; }
            var v = ParseVersion(spec[start..i]);
            if (v is null) { error = $"\"{Printable(spec[start..i])}\" is not a version"; return null; }
            if (list.Count >= MaxComparators) { error = $"more than {MaxComparators} conditions"; return null; }
            list.Add(new Comparator(op, v));
        }
        if (list.Count == 0) { error = "no condition"; return null; }
        return list;
    }

    static string Printable(string s) => s.Length > 24 ? new string(s.Take(24).Select(Printable).ToArray()) + "…" : new string(s.Select(Printable).ToArray());
    static char Printable(char c) => c is < ' ' or > '~' ? '?' : c;

    /// <summary>Why a range was rejected, or null when it is valid (or empty, "*" or UNKNOWN, which are valid).</summary>
    public static string? RangeError(string? spec)
    {
        if (IsAny(spec) || IsUnknownLiteral(spec)) return null;
        if (spec!.Length > MaxRangeLength) return $"longer than {MaxRangeLength} characters";
        ParseRange(spec, out var error);
        return error;
    }

    static bool IsAny(string? spec) => string.IsNullOrWhiteSpace(spec) || spec.Trim() == "*";
    static bool IsUnknownLiteral(string? spec) => spec is not null && spec.Trim().Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates <paramref name="version"/> against <paramref name="spec"/>: all conditions must hold (AND). Conditions are separated by
    /// white space or commas; each is an optional operator (=, ==, &gt;=, &lt;=, &gt;, &lt;) and a version. An empty range or "*" accepts everything.
    /// Pre-release versions follow plain precedence (1.0.0-rc.1 is below 1.0.0, so it does not satisfy "&gt;=1.0.0" but does satisfy "&lt;2.0.0").
    /// </summary>
    public static VersionCheck Evaluate(string? version, string? spec)
    {
        if (IsAny(spec)) return VersionCheck.Satisfied;
        if (IsUnknownLiteral(spec)) return VersionCheck.Unknown;
        if (spec!.Length > MaxRangeLength) return VersionCheck.InvalidRange;
        var range = ParseRange(spec, out _);
        if (range is null) return VersionCheck.InvalidRange;
        var v = ParseVersion(version);
        if (v is null) return VersionCheck.Unknown;
        foreach (var c in range)
        {
            int r = Compare(v, c.Version);
            bool ok = c.Op switch { Op.Ge => r >= 0, Op.Le => r <= 0, Op.Gt => r > 0, Op.Lt => r < 0, _ => r == 0 };
            if (!ok) return VersionCheck.NotSatisfied;
        }
        return VersionCheck.Satisfied;
    }

    /// <summary>
    /// true / false when the range could be evaluated; null when it cannot (empty is "any" and gives true; UNKNOWN, an invalid range or an
    /// unusable version give null). Callers that need to tell those apart use <see cref="Evaluate"/>.
    /// </summary>
    public static bool? Satisfies(string? version, string? spec) => Evaluate(version, spec) switch
    {
        VersionCheck.Satisfied => true,
        VersionCheck.NotSatisfied => false,
        _ => null
    };
}
