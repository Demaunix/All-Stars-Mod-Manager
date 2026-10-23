namespace AllStarsModManager;

public enum ConflictKind
{
    SharedArchiveMember, SharedWholeFile, SharedShaderCache, FileVersusArchive, DifferentBaseArchive,
    DuplicateId, DeclaredConflict, MissingRequirement, ExclusiveCapability, NativePatchOverlap, LooseAssetCollision,
    // format 1 additions (appended so existing values keep their numbers)
    UnreadableRule, LoadOrder, LoaderRequirement, TargetBuild
}

/// <summary>Whether a finding is a definite fact about the files/declarations, or a statement that something could not be confirmed.</summary>
public enum Certainty { Definite, Unknown }

public sealed class Conflict
{
    public ConflictKind Kind;
    public Severity Severity = Severity.Blocker;
    public string Title = "";
    public string Explanation = "";
    public List<string> Parties = new();     // Candidate.Key values
    public string? Target;
    /// <summary>True when the installed loader itself would reject the combination (as opposed to a declared compatibility rule).</summary>
    public bool LoaderEnforced;
    /// <summary>Unknown = the declaration or the evidence needed to judge it is missing, unreadable or unsupported. It is not a verdict that the mods clash.</summary>
    public Certainty Certainty = Certainty.Definite;
    /// <summary>Stable machine-readable rule name (for example "duplicate-id", "load-order").</summary>
    public string Code = "";
}

/// <summary>One mod that is, or would be, in the active set.</summary>
public sealed class Candidate
{
    public string Key = "";
    public PackageInfo Pkg = null!;
    public string FolderName = "";
    public bool Installed;
    public string? PackageRoot;
    public string? LibraryEntry;

    public string Display => Pkg.DisplayName + (string.IsNullOrEmpty(Pkg.Version) ? "" : " " + Pkg.Version);
    public string? Id => Pkg.Id;
}

public sealed class AdvisoryNote
{
    public string Code = "";
    public string Title = "";
    public string Text = "";
    public List<string> Parties = new();
}

public sealed class ConflictReport
{
    public List<Conflict> Conflicts = new();
    public List<AdvisoryNote> Notes = new();
    public bool HasBlockers => Conflicts.Any(c => c.Severity == Severity.Blocker);
}

/// <summary>
/// Cross-mod rules. The first group mirrors, line for line in effect, what the installed loader does in discover()
/// (host.cpp), prepare() (resources.h) and merge_virtual_xpac() (virtual_xpac.h): the same logical target may be shared
/// only by archive groups with the same base hash and disjoint member keys, or, from ChaoGarage 1.2.0, members combined
/// through an operation's "variants" or "optional". Everything else is declared metadata.
/// </summary>
public static class ConflictAnalyzer
{
    /// <summary>
    /// Evaluates the whole intended set. <paramref name="loader"/> says what is known about the installed (or assumed) mod loader; without it
    /// every loader requirement a package declares is reported as "could not be confirmed", never as met.
    /// </summary>
    public static ConflictReport Analyze(IReadOnlyList<Candidate> active, LoaderEvidence? loader = null)
    {
        loader ??= LoaderEvidence.NotInspected;
        var rep = new ConflictReport();
        LoaderRules(active, loader, rep);
        DeclaredRules(active, rep);
        LoadOrderRules(active, rep);
        LoaderRequirementRules(active, loader, rep);
        TargetBuildRules(active, loader, rep);
        Notes(active, rep);
        foreach (var c in rep.Conflicts) if (c.Code.Length == 0) c.Code = Kebab(c.Kind.ToString());
        return rep;
    }

    static string Kebab(string pascal) => string.Concat(pascal.Select((ch, i) => char.IsUpper(ch) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(ch) : ch.ToString()));

    static string N(Candidate c) => c.Display;

    // ---- rules the loader itself enforces ------------------------------------------------

    static void LoaderRules(IReadOnlyList<Candidate> active, LoaderEvidence loader, ConflictReport rep)
    {
        // IDs must be unique (exact comparison, as the loader's std::set<wstring>).
        foreach (var grp in active.Where(c => c.Id is not null).GroupBy(c => c.Id!, StringComparer.Ordinal).Where(g => g.Count() > 1))
            rep.Conflicts.Add(new Conflict
            {
                Kind = ConflictKind.DuplicateId,
                LoaderEnforced = true,
                Title = $"Two copies of \"{grp.Key}\"",
                Explanation = $"{string.Join(" and ", grp.Select(N))} use the same mod ID. The loader allows only one mod per ID, so the game would start with no mods. Keep one version switched on.",
                Parties = grp.Select(c => c.Key).ToList(),
                Target = grp.Key
            });
        foreach (var grp in active.Where(c => c.Id is not null).GroupBy(c => c.Id!, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = grp.Select(c => c.Id!).Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count > 1)
                rep.Notes.Add(new AdvisoryNote { Code = "id-case", Title = "IDs that differ only by letter case", Text = $"{string.Join(", ", distinct)} are treated as different by the loader but look alike.", Parties = grp.Select(c => c.Key).ToList() });
        }

        var claims = new Dictionary<string, List<(Candidate C, PlanGroup G)>>(StringComparer.Ordinal);
        foreach (var c in active)
        {
            if (c.Pkg.Plan is null) continue;
            foreach (var g in c.Pkg.Plan.Groups.Where(g => g.Valid))
            {
                if (!claims.TryGetValue(g.LogicalKey, out var list)) claims[g.LogicalKey] = list = new();
                list.Add((c, g));
            }
        }
        var sharing = MemberSharing.For(loader);
        foreach (var (target, list) in claims.Where(kv => kv.Value.Count > 1))
        {
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    Pair(list[i], list[j], rep, sharing);
            if (sharing.Any) SharedMembers(list, loader, sharing, rep);
        }
    }

    /// <summary>What the loader does when two mods change the same archive member: ChaoGarage 1.2.0 capabilities xpac-member-variants and xpac-member-optional.</summary>
    readonly record struct MemberSharing(bool Variants, bool Optional, bool Confirmed)
    {
        public bool Any => Variants || Optional;
        /// <summary>From the loader's profile when it is known; otherwise both are assumed, and what only they allow is reported as "could not be confirmed".</summary>
        public static MemberSharing For(LoaderEvidence loader) => loader.CanAnswerCapabilities && loader.Profile is { } p
            ? new(p.Capabilities.Contains("xpac-member-variants", StringComparer.Ordinal), p.Capabilities.Contains("xpac-member-optional", StringComparer.Ordinal), true)
            : new(true, true, false);
    }

    /// <summary>What one mod (or, after a variant combined two, the combination) put at a member, as the loader's VirtualChange.</summary>
    sealed class MemberChange
    {
        public string Sha = "";
        public bool Optional;
        public List<PlanVariant> Variants = new();
        public List<Candidate> Owners = new();
    }

    static string Shown(IReadOnlyCollection<uint> keys) => string.Join(", ", keys.Take(6).Select(k => k.ToString("X8"))) + (keys.Count > 6 ? $" and {keys.Count - 6} more" : "");
    static string Items(int n) => n == 1 ? "item" : $"{n} items";
    static string Names(IEnumerable<Candidate> owners) => string.Join(" + ", owners.Select(N));

    /// <summary>
    /// Members several mods change in one archive (same original), resolved as the loader's merge_virtual_xpac() does (virtual_xpac.h, 1.2.0): the mods'
    /// archives are folded in folder-name order (host.cpp discover(), without moving metal-sonic first), and for each member both sides change:
    /// a variant of either side made for exactly the other side's content (two different such variants: refused); else the right (later) side's
    /// optional change gives way; else the left side's; else the pair is refused. A combination made through a variant carries no variants and is
    /// not optional, so a third mod combines with it only through a variant made for the combined content.
    /// </summary>
    static void SharedMembers(List<(Candidate C, PlanGroup G)> list, LoaderEvidence loader, MemberSharing sharing, ConflictReport rep)
    {
        foreach (var byBase in list.Where(x => x.G.Format == "xpac").GroupBy(x => x.G.BaseSha, StringComparer.Ordinal))
        {
            var order = byBase.GroupBy(x => x.C.Key).Select(g => g.First()).OrderBy(x => x.C.FolderName, StringComparer.Ordinal).ToList();
            if (order.Count < 2) continue;
            string target = order[0].G.LogicalDisplay;

            var state = new Dictionary<uint, MemberChange>();
            var refused = new List<(List<Candidate> Left, Candidate Right, uint Key, bool TwoVersions)>();
            var combined = new List<(string How, uint Key)>();
            foreach (var (c, g) in order)
                foreach (var op in g.Ops)
                {
                    var rc = new MemberChange { Sha = op.Payload.Sha, Optional = op.Optional, Variants = op.Variants, Owners = { c } };
                    if (!state.TryGetValue(op.Key, out var lc)) { state[op.Key] = rc; continue; }
                    var rv = sharing.Variants ? rc.Variants.FirstOrDefault(v => v.OverSha == lc.Sha) : null;
                    var lv = sharing.Variants ? lc.Variants.FirstOrDefault(v => v.OverSha == rc.Sha) : null;
                    if (rv is not null && lv is not null && rv.Payload.Sha != lv.Payload.Sha) refused.Add((lc.Owners, c, op.Key, true));
                    else if (rv is not null || lv is not null)
                    {
                        var (maker, under) = rv is not null ? (rc.Owners, lc.Owners) : (lc.Owners, rc.Owners);
                        combined.Add(($"{Names(maker)}'s version made for {Names(under)}'s change is used (both changes)", op.Key));
                        state[op.Key] = new MemberChange { Sha = (rv ?? lv)!.Payload.Sha, Owners = lc.Owners.Concat(rc.Owners).ToList() };
                    }
                    else if (sharing.Optional && rc.Optional) combined.Add(($"{Names(rc.Owners)}'s optional change gives way; {Names(lc.Owners)}'s is used", op.Key));
                    else if (sharing.Optional && lc.Optional)
                    {
                        combined.Add(($"{Names(lc.Owners)}'s optional change gives way; {Names(rc.Owners)}'s is used", op.Key));
                        state[op.Key] = rc;
                    }
                    else refused.Add((lc.Owners, c, op.Key, false));     // the loader stops here; the left side is kept so later mods are still compared with it
                }

            foreach (var grp in refused.GroupBy(r => (Left: string.Join("\n", r.Left.Select(x => x.Key)), Right: r.Right.Key, r.TwoVersions)))
            {
                var first = grp.First();
                var keys = grp.Select(r => r.Key).ToList();
                var parties = first.Left.Select(x => x.Key).Append(first.Right.Key).Distinct().ToList();
                string who = $"{Names(first.Left)} and {N(first.Right)}";
                rep.Conflicts.Add(first.TwoVersions
                    ? new Conflict { Kind = ConflictKind.SharedArchiveMember, LoaderEnforced = true, Parties = parties, Target = target, Code = "shared-archive-member-two-versions",
                        Title = $"Both offer their own version of the same data in {target}",
                        Explanation = $"{who} each bring a version of the same {Items(keys.Count)} inside {target} ({Shown(keys)}) made for the other's change, and the two versions differ. The loader does not guess between them: it starts the game with no mods." }
                    : new Conflict { Kind = ConflictKind.SharedArchiveMember, LoaderEnforced = true, Parties = parties, Target = target,
                        Title = $"Both change the same data in {target}",
                        Explanation = $"{who} both replace or add the same {Items(keys.Count)} inside {target} ({Shown(keys)}), and neither says how to combine with the other: no version made for the other's exact content fits, and neither change is marked optional. The loader refuses this even when the replacement data is identical, and then starts the game with no mods. Mods that change different items in the same archive can be combined." });
            }

            if (sharing.Confirmed)
            {
                if (combined.Count > 0)
                    rep.Notes.Add(new AdvisoryNote { Code = "shared-member-combined", Title = $"Shared data in {target} is combined", Parties = order.Select(x => x.C.Key).ToList(),
                        Text = $"Several mods change the same items inside {target}, and their plans say how to combine them; the loader does this: " +
                               string.Join("; ", combined.GroupBy(x => x.How).Select(h => $"{h.Key} for {Items(h.Count())} ({Shown(h.Select(x => x.Key).ToList())})")) + "." });
                continue;
            }
            // Loader not confirmed: what only member sharing makes possible is reported as unconfirmed. Earlier loaders refuse every shared member.
            var refusedKeys = refused.Select(r => r.Key).ToHashSet();
            for (int i = 0; i < order.Count; i++)
                for (int j = i + 1; j < order.Count; j++)
                {
                    var (a, b) = (order[i], order[j]);
                    var keys = a.G.Keys.Intersect(b.G.Keys).Where(k => !refusedKeys.Contains(k)).ToList();
                    if (keys.Count == 0) continue;
                    string why = loader.Knowledge switch
                    {
                        LoaderKnowledge.Unrecognized => "the installed loader build is not one this program knows",
                        LoaderKnowledge.Absent => "the loader files (ChaoGarage.dll and dinput8.dll) are not both present",
                        _ => "no loader was inspected (give a game folder or choose a loader profile)"
                    };
                    rep.Conflicts.Add(new Conflict { Kind = ConflictKind.SharedArchiveMember, Certainty = Certainty.Unknown, Code = "shared-archive-member-unconfirmed",
                        Severity = loader.Knowledge == LoaderKnowledge.Unrecognized ? Severity.Blocker : Severity.Warning,
                        Parties = { a.C.Key, b.C.Key }, Target = target,
                        Title = $"{a.C.Pkg.DisplayName} and {b.C.Pkg.DisplayName} share data in {target} only with ChaoGarage 1.2.0 or newer",
                        Explanation = $"{N(a.C)} and {N(b.C)} both change the same {Items(keys.Count)} inside {target} ({Shown(keys)}). Their plans say how to combine them (a version made for the other's change, or a change marked optional), which ChaoGarage 1.2.0 and newer use; earlier loaders refuse the pair and start the game with no mods. Which loader you have cannot be confirmed: {why}." });
                }
        }
    }

    static void Pair((Candidate C, PlanGroup G) a, (Candidate C, PlanGroup G) b, ConflictReport rep, MemberSharing sharing)
    {
        if (a.C.Key == b.C.Key) return;
        var parties = new List<string> { a.C.Key, b.C.Key };
        string target = a.G.LogicalDisplay;
        string who = $"{N(a.C)} and {N(b.C)}";
        bool ax = a.G.Format == "xpac", bx = b.G.Format == "xpac";
        if (ax && bx)
        {
            if (a.G.BaseSha != b.G.BaseSha)
            {
                rep.Conflicts.Add(new Conflict { Kind = ConflictKind.DifferentBaseArchive, LoaderEnforced = true, Parties = parties, Target = target,
                    Title = $"Different versions of {target} expected",
                    Explanation = $"{who} were each made for a different version of {target}. The loader can only combine changes made to the same original archive." });
                return;
            }
            if (sharing.Any) return;      // members both change: SharedMembers() resolves them as the loader does
            var shared = a.G.Keys.Intersect(b.G.Keys).ToList();
            if (shared.Count > 0)
            {
                var shown = string.Join(", ", shared.Take(6).Select(k => k.ToString("X8"))) + (shared.Count > 6 ? $" and {shared.Count - 6} more" : "");
                rep.Conflicts.Add(new Conflict { Kind = ConflictKind.SharedArchiveMember, LoaderEnforced = true, Parties = parties, Target = target,
                    Title = $"Both change the same data in {target}",
                    Explanation = $"{who} both replace or add the same {(shared.Count == 1 ? "item" : $"{shared.Count} items")} inside {target} ({shown}). The loader refuses this even when the replacement data is identical, and then starts the game with no mods. Mods that change different items in the same archive can be combined." });
            }
            return;
        }
        ConflictKind kind;
        string what;
        if (a.G.Format == "rtsc_merge" && b.G.Format == "rtsc_merge") { kind = ConflictKind.SharedShaderCache; what = $"{who} both supply the shader cache {target}. The loader treats each shader cache file as belonging to a single mod, so the pair is refused (even though the cache contents could in principle be merged)."; }
        else if (ax != bx && (a.G.Format == "file" || b.G.Format == "file")) { kind = ConflictKind.FileVersusArchive; what = $"One of {who} replaces the whole file {target} while the other changes items inside it. The loader cannot combine a whole-file replacement with anything else."; }
        else { kind = ConflictKind.SharedWholeFile; what = $"{who} both replace the file {target}. The loader allows only one mod per replaced file (there is no merge for this kind of change)."; }
        rep.Conflicts.Add(new Conflict { Kind = kind, LoaderEnforced = true, Parties = parties, Target = target, Title = $"Both replace {target}", Explanation = what + " The game would start with no mods." });
    }

    // ---- declared rules (read from allstars-mod.json; enforced by the manager and the checker, NOT by the game's loader) ----------

    static string Range(string versions) => versions is "" or "*" ? "" : " " + versions;
    static string Why(string note) => string.IsNullOrWhiteSpace(note) ? "" : " Reason given: " + note.Trim();

    static void DeclaredRules(IReadOnlyList<Candidate> active, ConflictReport rep)
    {
        foreach (var c in active)
        {
            var sc = c.Pkg.Sidecar;
            if (sc is null || !sc.Interpretable || (sc.Id is { Length: > 0 } sid && c.Id is not null && sid != c.Id)) continue;
            foreach (var r in sc.Requires)
            {
                if (r.Invalid is not null) { Unreadable(rep, c, "requires", r); continue; }
                var other = active.FirstOrDefault(o => o.Id == r.Id && o.Key != c.Key);
                if (other is null)
                {
                    rep.Conflicts.Add(new Conflict { Kind = ConflictKind.MissingRequirement, Parties = { c.Key }, Target = r.Id,
                        Title = $"{c.Pkg.DisplayName} needs {r.Id}",
                        Explanation = $"{N(c)} says it needs \"{r.Id}\"{Range(r.Versions)} to be switched on.{Why(r.Note)}" });
                    continue;
                }
                switch (VersionSpec.Evaluate(other.Pkg.Version, r.Versions))
                {
                    case VersionCheck.NotSatisfied:
                        rep.Conflicts.Add(new Conflict { Kind = ConflictKind.MissingRequirement, Parties = { c.Key, other.Key }, Target = r.Id,
                            Title = $"{c.Pkg.DisplayName} needs a different {other.Pkg.DisplayName}",
                            Explanation = $"{N(c)} needs \"{r.Id}\" {r.Versions}, but {N(other)} is selected.{Why(r.Note)}" });
                        break;
                    case VersionCheck.Unknown:
                        rep.Conflicts.Add(new Conflict { Kind = ConflictKind.MissingRequirement, Certainty = Certainty.Unknown, Severity = Severity.Warning, Code = "requirement-version-unknown", Parties = { c.Key, other.Key }, Target = r.Id,
                            Title = $"Cannot tell whether {other.Pkg.DisplayName} is the version {c.Pkg.DisplayName} needs",
                            Explanation = $"{N(c)} needs \"{r.Id}\"{Range(r.Versions)}, but the version of {N(other)} is {(string.IsNullOrWhiteSpace(other.Pkg.Version) ? "not stated" : "not comparable")} (or the requirement says UNKNOWN), so the requirement cannot be confirmed.{Why(r.Note)}" });
                        break;
                    case VersionCheck.InvalidRange:     // already caught when the file was read (Relation.Invalid); kept as a safeguard
                        Unreadable(rep, c, "requires", r);
                        break;
                }
            }
            foreach (var r in sc.ConflictsWith)
            {
                if (r.Invalid is not null) { Unreadable(rep, c, "conflicts_with", r); continue; }
                foreach (var other in active.Where(o => o.Id == r.Id && o.Key != c.Key))
                {
                    var v = VersionSpec.Evaluate(other.Pkg.Version, r.Versions);
                    if (v == VersionCheck.NotSatisfied) continue;
                    if (rep.Conflicts.Any(x => x.Kind == ConflictKind.DeclaredConflict && x.Parties.Contains(c.Key) && x.Parties.Contains(other.Key))) continue;
                    // Satisfied = the conflict applies. Unknown/Invalid = fail closed: the mod is present and the range cannot rule it out.
                    bool sure = v == VersionCheck.Satisfied;
                    rep.Conflicts.Add(new Conflict { Kind = ConflictKind.DeclaredConflict, Certainty = sure ? Certainty.Definite : Certainty.Unknown, Parties = { c.Key, other.Key }, Target = r.Id,
                        Title = $"{c.Pkg.DisplayName} does not work with {other.Pkg.DisplayName}",
                        Explanation = (sure
                            ? $"{N(c)} declares that it conflicts with {r.Id}{Range(r.Versions)}.{Why(r.Note)}"
                            : $"{N(c)} declares that it conflicts with {r.Id}{Range(r.Versions)}, and the version of {N(other)} cannot be compared with that, so a clash cannot be ruled out.{Why(r.Note)}") });
                }
            }
        }

        foreach (var grp in active.SelectMany(c => (c.Pkg.Sidecar is { Interpretable: true } s ? s.CapabilitiesExclusive : new()).Select(cap => (cap, c))).GroupBy(x => x.cap, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.c.Key).Distinct().Count() > 1))
            rep.Conflicts.Add(new Conflict { Kind = ConflictKind.ExclusiveCapability, Parties = grp.Select(x => x.c.Key).Distinct().ToList(), Target = grp.Key,
                Title = $"Both need exclusive use of {grp.Key}",
                Explanation = $"{string.Join(" and ", grp.Select(x => N(x.c)).Distinct())} each claim sole ownership of \"{grp.Key}\". Only one of them can start." });

        var patches = active.SelectMany(c => (c.Pkg.Sidecar is { Interpretable: true } s ? s.NativePatches : new()).Select(p => (p, c))).ToList();
        for (int i = 0; i < patches.Count; i++)
            for (int j = i + 1; j < patches.Count; j++)
            {
                var (p, c) = patches[i]; var (q, d) = patches[j];
                if (c.Key == d.Key) continue;
                if ((ulong)p.Va < (ulong)q.Va + q.Length && (ulong)q.Va < (ulong)p.Va + p.Length)
                    rep.Conflicts.Add(new Conflict { Kind = ConflictKind.NativePatchOverlap, Parties = { c.Key, d.Key }, Target = $"0x{p.Va:X}",
                        Title = "Both modify the same program code",
                        Explanation = $"{N(c)} and {N(d)} declare changes to overlapping parts of the game program (0x{p.Va:X}–0x{(ulong)p.Va + p.Length - 1:X} and 0x{q.Va:X}–0x{(ulong)q.Va + q.Length - 1:X}). The second one would refuse to start." });
            }

        foreach (var grp in active.SelectMany(c => (c.Pkg.Sidecar is { Interpretable: true } s ? s.LooseAssets : new()).Select(a => (a, c))).GroupBy(x => x.a.Target.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
        {
            var shas = grp.Select(x => x.a.Sha).Distinct(StringComparer.Ordinal).ToList();
            if (shas.Count > 1)
                rep.Conflicts.Add(new Conflict { Kind = ConflictKind.LooseAssetCollision, Parties = grp.Select(x => x.c.Key).Distinct().ToList(), Target = grp.Key,
                    Title = $"Different versions of {grp.Key}",
                    Explanation = $"{string.Join(" and ", grp.Select(x => N(x.c)).Distinct())} each ship a different file for {grp.Key}. The manager never overwrites an existing file." });
        }
    }

    static void Unreadable(ConflictReport rep, Candidate c, string field, Relation r)
    {
        if (rep.Conflicts.Any(x => x.Kind == ConflictKind.UnreadableRule && x.Parties.Contains(c.Key) && x.Target == field + ":" + r.Id)) return;
        rep.Conflicts.Add(new Conflict { Kind = ConflictKind.UnreadableRule, Certainty = Certainty.Unknown, Parties = { c.Key }, Target = field + ":" + r.Id,
            Title = $"{c.Pkg.DisplayName} has a rule that cannot be read",
            Explanation = $"{N(c)}: the \"{field}\" entry for \"{r.Id}\" cannot be evaluated. {r.Invalid} Because it may protect against a bad combination, it is not treated as satisfied." });
    }

    // ---- load order (declared; checked against the order the loader actually uses) ---------------------------------------

    static void LoadOrderRules(IReadOnlyList<Candidate> active, ConflictReport rep)
    {
        var order = StartupOrder.Compute(active, c => c.FolderName, c => c.Id);
        int Pos(Candidate c) => order.IndexOf(c);
        foreach (var c in active)
        {
            var sc = c.Pkg.Sidecar;
            if (sc is null || !sc.Interpretable || (sc.Id is { Length: > 0 } sid && c.Id is not null && sid != c.Id)) continue;
            void Check(IEnumerable<OrderRef> refs, bool mustBeAfter)
            {
                foreach (var r in refs)
                    foreach (var other in active.Where(o => o.Id == r.Id && o.Key != c.Key && o.Id != c.Id))
                    {
                        bool ok = mustBeAfter ? Pos(c) > Pos(other) : Pos(c) < Pos(other);
                        if (ok) continue;
                        var dir = mustBeAfter ? "after" : "before";
                        bool metal = other.Id == "metal-sonic" || c.Id == "metal-sonic";
                        string reciprocal = (mustBeAfter ? other.Pkg.Sidecar?.LoadAfter : other.Pkg.Sidecar?.LoadBefore)?.Any(x => x.Id == c.Id) == true
                            ? " The two descriptions ask for opposite orders, so they cannot both be met." : "";
                        string fix = metal && !mustBeAfter && other.Id == "metal-sonic"
                            ? " The loader always starts the mod with ID \"metal-sonic\" first, so nothing can start before it."
                            : $" Rename the folder of {N(mustBeAfter ? c : other)} (currently \"{(mustBeAfter ? c : other).FolderName}\") or of {N(mustBeAfter ? other : c)} (currently \"{(mustBeAfter ? other : c).FolderName}\") so that the first one sorts later in plain character order (digits, then capital letters, then lowercase letters).";
                        rep.Conflicts.Add(new Conflict
                        {
                            Kind = ConflictKind.LoadOrder, Parties = { c.Key, other.Key }, Target = r.Id, Code = "load-order",
                            Title = $"{c.Pkg.DisplayName} must start {dir} {other.Pkg.DisplayName}",
                            Explanation = $"{N(c)} declares that it must start {dir} \"{r.Id}\". The loader starts mods in folder-name order (Metal Sonic first), which here is: {string.Join(" → ", order.Select(x => x.Pkg.DisplayName))}.{fix}{reciprocal}"
                        });
                    }
            }
            Check(sc.LoadAfter, mustBeAfter: true);
            Check(sc.LoadBefore, mustBeAfter: false);
        }
    }

    // ---- shared-runtime (loader) requirements ------------------------------------------------------------------------------------

    static void LoaderRequirementRules(IReadOnlyList<Candidate> active, LoaderEvidence loader, ConflictReport rep)
    {
        foreach (var c in active)
        {
            var sc = c.Pkg.Sidecar;
            if (sc is null || !sc.Interpretable || !sc.Loader.Declared) continue;
            if (sc.Id is { Length: > 0 } sid && c.Id is not null && sid != c.Id) continue;
            var req = sc.Loader;
            var definite = new List<string>();
            var unknown = new List<string>();

            // Hash pins compare real file hashes whenever a loader was inspected, recognised or not.
            void Pin(List<string> wanted, string? actual, string what)
            {
                if (wanted.Count == 0) return;
                if (actual is null) unknown.Add($"the {what} was not inspected, so the required build cannot be confirmed");
                else if (!wanted.Contains(actual)) definite.Add($"it requires one of {wanted.Count} specific {what} build(s), and the installed {what} is not one of them");
            }
            Pin(req.HostSha256, loader.HostSha, "loader host (ChaoGarage.dll)");
            Pin(req.ProxySha256, loader.ProxySha, "bootstrap proxy (dinput8.dll)");

            bool needsProfile = req.HostAbi is not null || req.ModIniSchema is not null || req.PlanSchema is not null || req.Capabilities.Count > 0;
            if (needsProfile)
            {
                if (!loader.CanAnswerCapabilities || loader.Profile is null)
                {
                    string why = loader.Knowledge switch
                    {
                        LoaderKnowledge.Unrecognized => "the installed loader build is not one this program knows, so what it supports cannot be confirmed (builds are recognised by file hash, never by a version label)",
                        LoaderKnowledge.Absent => "the loader files (ChaoGarage.dll and dinput8.dll) are not both present",
                        _ => "no loader was inspected (give a game folder or choose a loader profile)"
                    };
                    unknown.Add(why);
                }
                else
                {
                    var p = loader.Profile;
                    if (req.HostAbi is { } abi) { if (p.HostAbi == 0) unknown.Add("the loader profile does not record its host interface version"); else if (p.HostAbi != abi) definite.Add($"it needs host interface version {abi}, but this loader provides version {p.HostAbi}"); }
                    if (req.ModIniSchema is { } ms) { if (p.ModIniSchema == 0) unknown.Add("the loader profile does not record its mod.ini version"); else if (p.ModIniSchema != ms) definite.Add($"it needs mod.ini version {ms}, but this loader reads version {p.ModIniSchema}"); }
                    if (req.PlanSchema is { } ps) { if (p.PlanSchema == 0) unknown.Add("the loader profile does not record its plan version"); else if (p.PlanSchema != ps) definite.Add($"it needs plan version {ps}, but this loader reads version {p.PlanSchema}"); }
                    var missing = req.Capabilities.Where(x => !p.Capabilities.Contains(x, StringComparer.Ordinal)).ToList();
                    if (missing.Count > 0) definite.Add($"it needs loader capabilities this build is not known to provide: {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", …" : "")}");
                }
            }

            string src = loader.Knowledge switch
            {
                LoaderKnowledge.Verified => $"loader build \"{loader.Profile?.Id}\" (hash-matched{(loader.ProfileIsUserSupplied ? ", described by a profile file you supplied" : "")})",
                LoaderKnowledge.Assumed => $"the assumed loader profile \"{loader.Profile?.Id}\" (not verified against real files)",
                LoaderKnowledge.Unrecognized => "an unrecognised loader build",
                LoaderKnowledge.Absent => "this game folder (no complete loader)",
                _ => "no inspected loader"
            };
            if (definite.Count > 0)
                rep.Conflicts.Add(new Conflict { Kind = ConflictKind.LoaderRequirement, Parties = { c.Key }, Target = "loader", Code = "loader-requirement",
                    Title = $"{c.Pkg.DisplayName} needs a different loader",
                    Explanation = $"{N(c)} declares loader requirements that {src} does not meet: {string.Join("; ", definite)}." });
            if (unknown.Count > 0)
                rep.Conflicts.Add(new Conflict { Kind = ConflictKind.LoaderRequirement, Certainty = Certainty.Unknown, Code = "loader-requirement-unconfirmed",
                    // Only an unrecognised build is a hard stop in the manager; a missing or uninspected loader leaves the usual warnings in place.
                    Severity = loader.Knowledge == LoaderKnowledge.Unrecognized ? Severity.Blocker : Severity.Warning,
                    Parties = { c.Key }, Target = "loader",
                    Title = $"{c.Pkg.DisplayName}: loader requirements cannot be confirmed",
                    Explanation = $"{N(c)} declares loader requirements, but {string.Join("; ", unknown)}. Nothing is assumed to be met." });
        }
    }

    // ---- target game build (descriptive in the file; compared when a game executable is available) ----------------------

    static void TargetBuildRules(IReadOnlyList<Candidate> active, LoaderEvidence loader, ConflictReport rep)
    {
        foreach (var c in active)
        {
            var sc = c.Pkg.Sidecar;
            if (sc is null || !sc.Interpretable || sc.TargetExecutableSha256.Count == 0) continue;
            if (sc.Id is { Length: > 0 } sid && c.Id is not null && sid != c.Id) continue;
            var actual = loader.GameExecutableSha;
            if (actual is not null && sc.TargetExecutableSha256.Contains(actual)) continue;
            rep.Conflicts.Add(new Conflict
            {
                Kind = ConflictKind.TargetBuild, Certainty = Certainty.Unknown, Severity = Severity.Warning, Code = "target-build",
                Parties = { c.Key }, Target = "game executable",
                Title = actual is null ? $"{c.Pkg.DisplayName}: game version not compared" : $"{c.Pkg.DisplayName} was made for a different game build",
                Explanation = actual is null
                    ? $"{N(c)} says which game executable build it was made and tested with, but no game executable was inspected, so this cannot be compared."
                    : $"{N(c)} says it was made and tested with a different build of the game executable than the one installed here. It may still work (its own code decides), but nothing here confirms it."
            });
        }
    }

    // ---- advisory notes ----------------------------------------------------------------

    static void Notes(IReadOnlyList<Candidate> active, ConflictReport rep)
    {
        var runtimeMods = active.Where(c => c.Pkg.RuntimeFullPath is not null || c.Pkg.Manifest?.RuntimeRel is not null).ToList();
        var tested = new List<(Candidate A, Candidate B, Relation R)>();
        foreach (var c in active)
            foreach (var r in c.Pkg.Sidecar is { Interpretable: true } s ? s.ComposesWith : new())
            {
                var other = active.FirstOrDefault(o => o.Id == r.Id && o.Key != c.Key);
                if (other is null || r.Invalid is not null || VersionSpec.Evaluate(other.Pkg.Version, r.Versions) != VersionCheck.Satisfied) continue;
                tested.Add((c, other, r));
            }
        foreach (var (a, b, r) in tested)
            rep.Notes.Add(new AdvisoryNote
            {
                Code = "tested-together",
                Title = $"{a.Pkg.DisplayName} + {b.Pkg.DisplayName}: tested together offline",
                Text = $"{N(a)} says it was checked together with {N(b)} outside the game. That is not the same as in-game testing.{(r.Evidence.Length > 0 ? " Evidence: " + r.Evidence : "")}{(r.Note.Length > 0 ? " " + r.Note : "")}",
                Parties = { a.Key, b.Key }
            });
        foreach (var c in active)
            if (c.Pkg.Sidecar is { Interpretable: true } fs && fs.FreeTextConflicts.Count > 0 && !(fs.Id is { Length: > 0 } fid && c.Id is not null && fid != c.Id))
                rep.Notes.Add(new AdvisoryNote
                {
                    Code = "free-text-conflict",
                    Title = $"{c.Pkg.DisplayName} declares an incompatibility that is not checked automatically",
                    Text = $"{N(c)} describes a conflict in words only, so it cannot be compared with the mods you have: {string.Join(" | ", fs.FreeTextConflicts)}",
                    Parties = { c.Key }
                });
        if (runtimeMods.Count > 1)
        {
            var untestedPairs = new List<string>();
            for (int i = 0; i < runtimeMods.Count; i++)
                for (int j = i + 1; j < runtimeMods.Count; j++)
                    if (!tested.Any(t => (t.A == runtimeMods[i] && t.B == runtimeMods[j]) || (t.A == runtimeMods[j] && t.B == runtimeMods[i])))
                        untestedPairs.Add($"{runtimeMods[i].Pkg.DisplayName} + {runtimeMods[j].Pkg.DisplayName}");
            if (untestedPairs.Count > 0)
            {
                int total = runtimeMods.Count * (runtimeMods.Count - 1) / 2;
                var shown = string.Join("; ", untestedPairs.Take(3));
                rep.Notes.Add(new AdvisoryNote
                {
                    Code = "native-unknown",
                    Title = "Native code interactions are unknown",
                    Text = $"{runtimeMods.Count} of these mods run their own native code inside the game. Not clashing on data does not mean they work together, and the manager cannot test that. " +
                           (untestedPairs.Count == total ? $"None of the {total} combinations has a recorded test" : $"{untestedPairs.Count} of {total} combinations have no recorded test") +
                           $" (for example {shown}{(untestedPairs.Count > 3 ? "; …" : "")}).",
                    Parties = runtimeMods.Select(c => c.Key).ToList()
                });
            }
        }
        var order = StartupOrder.Compute(active, c => c.FolderName, c => c.Id);
        if (order.Count > 1)
            rep.Notes.Add(new AdvisoryNote
            {
                Code = "startup-order",
                Title = "Startup order",
                Text = "The loader starts mods in folder-name order (Metal Sonic first). It has no priority setting, so the manager does not rename folders for you: " + string.Join(" → ", order.Select(c => c.Pkg.DisplayName)) + ". A mod can declare that it must start before or after another (load_order); that is checked against this order.",
                Parties = order.Select(c => c.Key).ToList()
            });
    }
}