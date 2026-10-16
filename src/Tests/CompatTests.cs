namespace AllStarsModManager.Tests;

/// <summary>Everything added for format 1 and the checker: strict versions, strict description files, runtime header inspection, loader profiles, new rules, the checker.</summary>
public static class CompatTests
{
    public static void Register()
    {
        CompatVersionTests.Register();
        CompatSidecarTests.Register();
        CompatRuleTests.Register();
        CompatCheckerTests.Register();
        LoaderVectorTests.Register();
    }
}
