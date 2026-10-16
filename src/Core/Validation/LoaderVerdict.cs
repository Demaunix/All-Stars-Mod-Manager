namespace AllStarsModManager;

/// <summary>
/// "Would the installed loader accept this set?" — only the rules the loader itself enforces. Manager-level rules
/// (declared requirements, loose files, unclear manifests) are deliberately excluded so this can be checked against the
/// loader's own source in tests.
/// </summary>
public static class LoaderVerdict
{
    static readonly HashSet<string> ManagerOnly = new(StringComparer.Ordinal)
    {
        "id-mismatch", "manifest-ambiguous", "manifest-choice-invalid", "manifest-foreign", "loose-source-path", "loose-size", "loose-hash"
    };

    public sealed class Result
    {
        public bool Accept => Reasons.Count == 0;
        public List<string> Reasons = new();
        /// <summary>Coarse classes: dup-id, member, resource, base-archive, structure.</summary>
        public HashSet<string> Classes = new();
    }

    public static Result Evaluate(IReadOnlyList<Candidate> set)
    {
        var r = new Result();
        foreach (var c in set)
        {
            foreach (var i in c.Pkg.AllIssues.Where(i => i.Severity == Severity.Blocker && !ManagerOnly.Contains(i.Code)))
            { r.Reasons.Add($"{c.Display}: {i.Message}"); r.Classes.Add("structure"); }
            if (c.Pkg.Manifest is null || c.Pkg.Manifest.State == ManifestState.NoManifest) { r.Reasons.Add($"{c.Display}: no manifest"); r.Classes.Add("structure"); }
        }
        var rep = ConflictAnalyzer.Analyze(set);
        foreach (var k in rep.Conflicts.Where(c => c.LoaderEnforced))
        {
            r.Reasons.Add(k.Title);
            r.Classes.Add(k.Kind switch
            {
                ConflictKind.DuplicateId => "dup-id",
                ConflictKind.SharedArchiveMember => "member",
                ConflictKind.DifferentBaseArchive => "base-archive",
                _ => "resource"
            });
        }
        return r;
    }
}
