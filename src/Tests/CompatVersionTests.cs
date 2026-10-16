namespace AllStarsModManager.Tests;

/// <summary>Version parsing and range evaluation (docs/PACKAGE-FORMAT.md, "Versions and ranges").</summary>
public static class CompatVersionTests
{
    static void Check(string version, string range, VersionCheck expected) =>
        T.Eq(expected, VersionSpec.Evaluate(version, range), $"'{version}' against '{range}'");

    public static void Register()
    {
        T.Area("compat-versions");

        T.Run("version ranges: every supported form, with boundaries on both sides", () =>
        {
            Check("1.0.0", ">=1.0.0", VersionCheck.Satisfied);
            Check("0.9.9", ">=1.0.0", VersionCheck.NotSatisfied);
            Check("1.0.0", ">1.0.0", VersionCheck.NotSatisfied);
            Check("1.0.1", ">1.0.0", VersionCheck.Satisfied);
            Check("1.0.0", "<=1.0.0", VersionCheck.Satisfied);
            Check("1.0.1", "<=1.0.0", VersionCheck.NotSatisfied);
            Check("1.0.0", "<1.0.0", VersionCheck.NotSatisfied);
            Check("0.9.9", "<1.0.0", VersionCheck.Satisfied);
            Check("1.2.3", "1.2.3", VersionCheck.Satisfied);
            Check("1.2.3", "=1.2.3", VersionCheck.Satisfied);
            Check("1.2.3", "==1.2.3", VersionCheck.Satisfied);
            Check("1.2.4", "=1.2.3", VersionCheck.NotSatisfied);
            Check("1.5.0", ">=1.0.0 <2.0.0", VersionCheck.Satisfied);
            Check("2.0.0", ">=1.0.0 <2.0.0", VersionCheck.NotSatisfied);
            Check("1.5.0", ">=1.0.0, <2.0.0", VersionCheck.Satisfied);
            Check("1.5.0", ">= 1.0.0 < 2.0.0", VersionCheck.Satisfied);
            Check("v1.5.0", ">=1.0.0", VersionCheck.Satisfied);
            Check("1.5.0", ">=v1.0.0", VersionCheck.Satisfied);
            Check("1.0", "1.0.0", VersionCheck.Satisfied);       // missing components count as 0
            Check("1.0.0.0", "1.0", VersionCheck.Satisfied);
            Check("1.0.0.1", ">1.0.0", VersionCheck.Satisfied);
            Check("5.0.0", "", VersionCheck.Satisfied);
            Check("5.0.0", "   ", VersionCheck.Satisfied);
            Check("5.0.0", "*", VersionCheck.Satisfied);
            Check("not a version", "*", VersionCheck.Satisfied);   // "any" needs no comparable version
            T.Eq(true, VersionSpec.Satisfies("1.0.0", ">=1.0.0"), "Satisfies true");
            T.Eq(false, VersionSpec.Satisfies("0.1", ">=1.0.0"), "Satisfies false");
            T.Eq<bool?>(null, VersionSpec.Satisfies("1.0.0", "1.x"), "Satisfies null for an invalid range");
        });

        T.Run("version ranges: text outside the grammar is never partly accepted (the 1.1.0 reader accepted several of these in part)", () =>
        {
            foreach (var bad in new[]
            {
                ">=1.0.0 garbage", "garbage >=1.0.0", ">=1.0.0 and <2.0.0", "1.x", "1.*", "^1.0.0", "~1.0", ">=1.0.0 || <0.5", ">=1.0.0;<2", "(>=1.0.0)", ">=", "<", "==", ">=<1.0",
                ">1.0.0-", ">=1..2", "1.0.0 !", "1.0.0@", "latest", "1.0.0-beta_1", ">=1.0.0 >=", "- 1.0.0", ">>1.0", "=>1.0", "1.0 - 2.0", ">=１.０.０"
            })
                Check("1.5.0", bad, VersionCheck.InvalidRange);
            T.Eq<bool?>(null, VersionSpec.Satisfies("1.5.0", ">=1.0.0 garbage"), "partial acceptance is gone");
            T.Eq(VersionCheck.InvalidRange, VersionSpec.Evaluate("1.0.0", new string('1', 201)), "range longer than 200 characters");
            T.Eq(VersionCheck.InvalidRange, VersionSpec.Evaluate("1.0.0", string.Join(" ", Enumerable.Range(1, 17).Select(i => ">=0." + i))), "more than 16 conditions");
            T.Eq(VersionCheck.Satisfied, VersionSpec.Evaluate("20.0.0", string.Join(" ", Enumerable.Range(1, 16).Select(i => ">=0." + i))), "exactly 16 conditions is fine");
            T.True(VersionSpec.RangeError(">=1.0.0 garbage") is not null && VersionSpec.RangeError(">=1.0.0") is null && VersionSpec.RangeError("UNKNOWN") is null, "RangeError reports the reason");
        });

        T.Run("version ranges: UNKNOWN and unusable versions are unknown, never satisfied", () =>
        {
            Check("1.0.0", "UNKNOWN", VersionCheck.Unknown);
            Check("1.0.0", "unknown", VersionCheck.Unknown);
            Check("1.0.0", " Unknown ", VersionCheck.Unknown);
            Check("", ">=1.0.0", VersionCheck.Unknown);
            Check("banana", ">=1.0.0", VersionCheck.Unknown);
            Check("1.0.0-", ">=1.0.0", VersionCheck.Unknown);
            T.Eq<bool?>(null, VersionSpec.Satisfies(null, ">=1.0.0"), "no version");
        });

        T.Run("versions: components larger than any integer type compare correctly (the 1.1.0 reader made 2147483648 and 2147483649 equal)", () =>
        {
            T.Eq<int?>(-1, VersionSpec.Compare("2147483648.0", "2147483649.0"), "just above int range");
            T.Eq<int?>(-1, VersionSpec.Compare("99999999999999999999.0", "100000000000000000000.0"), "20 digits");
            T.Eq<int?>(1, VersionSpec.Compare("1.99999999999999999999999999", "1.99999999999999999999999998"), "26 digits");
            T.Eq<int?>(0, VersionSpec.Compare("1.000000000000000000000000001", "1.1"), "leading zeros");
            T.Eq<int?>(-1, VersionSpec.Compare("1.0.0-99999999999999999999", "1.0.0-100000000000000000000"), "huge numeric pre-release identifier");
            Check("4294967296.0.0", ">=4294967295.0.0", VersionCheck.Satisfied);
            Check("4294967295.0.0", ">4294967296.0.0", VersionCheck.NotSatisfied);
            T.Eq<int?>(null, VersionSpec.Compare(string.Join(".", Enumerable.Repeat("1", 17)), "1.0"), "more than 16 numeric components is not a version");
            T.Eq<int?>(null, VersionSpec.Compare(new string('1', 81), "1.0"), "longer than 80 characters is not a version");
        });

        T.Run("versions: pre-release precedence follows the Semantic Versioning 2.0.0 example chain, build metadata is ignored", () =>
        {
            var chain = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };
            for (int i = 0; i < chain.Length; i++)
                for (int j = 0; j < chain.Length; j++)
                    T.Eq<int?>(i.CompareTo(j), VersionSpec.Compare(chain[i], chain[j]), $"{chain[i]} vs {chain[j]}");
            T.Eq<int?>(0, VersionSpec.Compare("1.0.0+build.1", "1.0.0+build.2"), "build metadata ignored");
            T.Eq<int?>(0, VersionSpec.Compare("1.0.0-rc.1+a", "1.0.0-rc.1+b"), "build metadata ignored on a pre-release");
            T.Eq<int?>(-1, VersionSpec.Compare("1.1.0-beta.3", "1.1.0-beta.3.rp2"), "extra identifier sorts later (shape used by the real packages)");
            T.Eq<int?>(-1, VersionSpec.Compare("0.1.0-alpha.2", "0.1.0-alpha.7"), "numeric identifiers compare as numbers");
            T.Eq<int?>(-1, VersionSpec.Compare("1.0.0-alpha.9", "1.0.0-alpha.10"), "9 < 10, not text order");
            // consequences in ranges
            Check("1.0.0-beta", ">=1.0.0", VersionCheck.NotSatisfied);            // a pre-release is below its release
            Check("1.0.0-beta", "<1.0.0", VersionCheck.Satisfied);
            Check("2.0.0-rc.1", "<2.0.0", VersionCheck.Satisfied);                // so a '<2.0.0' limit does not exclude 2.0.0's own pre-releases
            Check("2.0.0-rc.1", ">=1.0.0 <2.0.0", VersionCheck.Satisfied);
            Check("1.0.0-rc.1", ">=1.0.0-beta", VersionCheck.Satisfied);
            Check("1.0.0", "=1.0.0-rc.1", VersionCheck.NotSatisfied);
            Check("0.1.0-alpha.7.rp2", ">=0.1.0-alpha.7", VersionCheck.Satisfied);
            Check("0.1.0-alpha.2", "=0.1.0-alpha.2", VersionCheck.Satisfied);
        });

        T.Run("versions: hostile text (non-ASCII digits, control characters, trailing newline, empty identifiers) is not a version", () =>
        {
            foreach (var bad in new[] { "１.０.０", "1.0.0-alpha..1", "1.0.0-", "1.0.0+", "1.0.0-alpha.", "1.0.0\u0000", "1.0.0\u0007", "1.0.0 x", "..", "1..0", "1.0.", ".1.0", "-1.0.0", "1.0.0-é" })
                T.True(VersionSpec.ParseVersion(bad) is null, $"'{bad.Replace("\u0000", "\\0").Replace("\u0007", "\\a")}' must not parse");
            T.True(VersionSpec.ParseVersion("1.0.0\n") is not null, "surrounding white space is trimmed first");
            T.True(VersionSpec.ParseVersion("1.0.0-alpha-1") is not null, "hyphens inside identifiers are allowed by the grammar");
            T.True(VersionSpec.ParseVersion("V1.0.0") is null, "only a lowercase v prefix is accepted");
        });
    }
}
