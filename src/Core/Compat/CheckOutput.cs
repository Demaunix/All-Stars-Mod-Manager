using System.Text;
using System.Text.Json;

namespace AllStarsModManager.Compat;

/// <summary>Plain-language and machine-readable renderings of a <see cref="CheckReport"/>.</summary>
public static class CheckOutput
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Where an absolute Windows path starts: a drive letter and \ or /; the \\?\ and \\.\ device forms; \\server (any letters, not only ASCII, with or
    // without a share); //server/share. Web addresses are matched first (see PathOrUrl), so the "s:/" and "//host/" inside "https://host/" never get here.
    const string PathStart = @"(?:[A-Za-z]:[\\/]|[\\/]{2}[?.][\\/]|\\\\[^\s\\/""'<>|]+|//[^\s\\/""'<>|]+/[^\s\\/""'<>|]+)";

    /// <summary>
    /// One left-to-right scan: a web address (a scheme of two or more letters followed by "://"); a path opened right after a quote, which runs to the closing
    /// quote because operating-system messages quote paths and those can contain spaces (a double-quoted path ends at the next " — Windows names cannot contain
    /// one; a single-quoted path runs to the LAST ' on the line that is followed by white space, punctuation or the end, because an apostrophe is a valid name
    /// character: when that is ambiguous, text between two quoted paths is removed too rather than leaving part of a path); or an unquoted path (ends at white
    /// space or one of " ' &lt; &gt; | * ?). Quoted bodies have no length limit, so a closed quoted path is never cut short; ordinary text is scanned in
    /// linear time and only text built to be slow reaches the time limit (see <see cref="Withheld"/>).
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex PathOrUrl = new(
        @"(?<url>(?<![A-Za-z0-9+.\-])(?<scheme>[A-Za-z][A-Za-z0-9+.\-]{1,31})://[^\s""'<>]*)" +
        @"|(?<q>"")" + PathStart + @"[^""\r\n]*""" +
        @"|(?<q>')" + PathStart + @"[^\r\n]*'(?=[\s.,;:!?)\]}]|$)" +
        @"|" + PathStart + @"[^\s""'<>|*?]*",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>Shown instead of a text that could not be checked in time (only adversarially long text); nothing of it is shown.</summary>
    public const string Withheld = "<text withheld: too long to check for private paths>";

    /// <summary>Stands for an account name while paths are scanned (it is an ordinary path character, so "D:\Backups\First Last\x" stays one path); shown as &lt;user&gt;.</summary>
    const char AccountMark = '\uE000';

    /// <summary>The account name and, when different, the name of the profile folder. Names shorter than 3 characters are not removed (they would mangle ordinary words).</summary>
    public static IReadOnlyList<string> CurrentAccountNames()
    {
        var names = new List<string> { Environment.UserName };
        try { names.Add(System.IO.Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'))); }
        catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException) { }
        return names;
    }

    /// <summary>
    /// Makes a message safe to share. The output policy for every report field and every error message:
    /// 1. paths under the folders that were checked become relative ("&lt;folder&gt;" for the folder itself) — every file the checker reads is under one of
    ///    these roots (game folder, mods, --add folders, the --loader-profiles file and its folder), so operating-system messages about them are handled exactly;
    /// 2. the account name (and profile-folder name) becomes "&lt;user&gt;", also inside paths and web addresses;
    /// 3. any other absolute Windows path becomes "&lt;path&gt;": drive-letter paths with \ or /, UNC paths (server and share may use any letters), the device
    ///    forms \\?\ and \\.\, and file: addresses;
    /// 4. other web addresses (https://…) are kept as they are.
    /// Steps 3 and 4 are a pattern-based fallback for any other text. Text a mod author wrote is free prose: an unquoted path with spaces in it, or a
    /// single-quoted one that is never closed, can leave its later words visible; known folders and the account name are removed regardless. This is a
    /// privacy filter for machine details, not an anonymiser for arbitrary text.
    /// </summary>
    public static string ScrubPaths(string text, IEnumerable<string> roots) => ScrubPaths(text, roots, CurrentAccountNames());

    public static string ScrubPaths(string text, IEnumerable<string> roots, IEnumerable<string> accountNames)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var r in roots.Select(x => x.TrimEnd('\\', '/')).Where(x => x.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(r) + @"[\\/]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(r), "<folder>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        foreach (var user in accountNames.Where(x => x is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(user) + @"(?![A-Za-z0-9])", AccountMark.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        try
        {
            text = PathOrUrl.Replace(text, m =>
                m.Groups["url"].Success ? (m.Groups["scheme"].Value.Equals("file", StringComparison.OrdinalIgnoreCase) ? "<path>" : m.Value)
                : m.Groups["q"].Success ? m.Groups["q"].Value + "<path>" + m.Groups["q"].Value
                : "<path>");
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return Withheld; }
        return text.Replace(AccountMark.ToString(), "<user>");
    }

    public static string ToJson(CheckReport r) => JsonSerializer.Serialize(r, JsonOptions);

    static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch { '→' => "->", '—' or '–' => "-", '…' => "...", '·' => "*", '“' or '”' => "\"", '‘' or '’' => "'", >= ' ' and <= '~' or '\n' or '\r' => c, _ => '?' });
        return sb.ToString();
    }

    static IEnumerable<string> Wrap(string text, int width, string indent, string? restIndent = null)
    {
        restIndent ??= indent;
        var line = new StringBuilder(indent);
        int floor = indent.Length;
        foreach (var word in text.Replace("\r", "").Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > floor && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear().Append(restIndent); floor = restIndent.Length; }
            if (line.Length > floor) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > floor) yield return line.ToString();
    }

    public static string ToText(CheckReport r, bool ascii = false, int width = 100)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        void P(string s, string indent, string? rest = null) { foreach (var l in Wrap(s, width, indent, rest)) L(l); }

        L(r.Tool);
        L("Offline check: no mod code was run, nothing was changed, nothing was tested in the game.");
        L();
        L("Checked : " + r.InputKind switch { "game-folder" => "a game folder", "mods-folder" => "a mods folder (no game folder)", _ => "a single mod folder" });
        L("Loader  : " + r.Loader.Knowledge switch
        {
            "verified" => r.Loader.ProfileSource == "user-supplied"
                ? $"build \"{r.Loader.Profile}\" as described by a profile file you supplied (ChaoGarage.dll and dinput8.dll match its SHA-256 values; that file is your statement, not evidence)"
                : $"known build \"{r.Loader.Profile}\" (ChaoGarage.dll and dinput8.dll match by SHA-256)",
            "assumed" => $"assumed profile \"{r.Loader.Profile}\"{(r.Loader.ProfileSource == "user-supplied" ? " from a profile file you supplied" : "")} (not verified against real files)",
            "unrecognized" => "present, but its hashes match no known build",
            "absent" => "not installed in this folder (ChaoGarage.dll and dinput8.dll are not both there)",
            _ => "not inspected"
        });
        L("Hashes  : " + r.FileHashes switch { "skipped" => "skipped (--quick)", "full-with-archive-rebuild" => "all mod files checked, archives rebuilt", _ => "all mod files checked" });
        L();
        if (r.StartOrder.Count > 0)
        {
            L("Mods in the order the loader starts them:");
            int n = 0;
            foreach (var f in r.StartOrder)
            {
                var p = r.Packages.First(x => x.Folder == f && x.State is "active" or "would-be-added");
                n++;
                L($"  {n,2}. {f}  [{p.Id ?? "no id"}{(p.Version is { Length: > 0 } ? " " + p.Version : "")}]{(p.State == "would-be-added" ? "  (not installed: checked as if added)" : "")}");
            }
            L();
        }

        string banner = r.VerdictValue switch
        {
            CheckVerdict.Incompatible => "RESULT: INCOMPATIBLE (definite problem)",
            CheckVerdict.InsufficientInformation => "RESULT: INSUFFICIENT INFORMATION (no definite problem, but not everything could be confirmed)",
            _ => "RESULT: DECLARED COMPATIBLE (every declared rule is met; this is not a game test)"
        };
        L(banner);
        P(r.Summary, "  ");
        L();

        void Section(string impact, string title, string tag)
        {
            var items = r.Findings.Where(f => f.Impact == impact).ToList();
            if (items.Count == 0) return;
            L(title);
            foreach (var f in items)
            {
                L($"  {tag} {f.Title}");
                if (f.Detail.Length > 0) P(f.Detail, "      ");
                var who = f.Packages.Count > 0 ? "Affects: " + string.Join(", ", f.Packages) + ". " : "";
                P(who + (f.EnforcedBy == "loader" ? "The game's own loader enforces this." : "Rule applied by this tool (declared by the mod or a static check); whether the game's loader also checks it depends on the loader release (PACKAGE-FORMAT.md, section 12)."), "      ");
            }
            L();
        }
        Section("blocker", "Definite problems", "[BLOCKED]");
        Section("unknown", "Could not be confirmed", "[UNKNOWN]");
        Section("note", "Notes", "[note]   ");

        var others = r.Packages.Where(p => p.State is "switched-off" or "unmanaged").ToList();
        if (others.Count > 0)
        {
            L("Left alone (not examined as part of the active set):");
            foreach (var p in others)
            {
                L($"  - {p.Folder}  ({(p.State == "switched-off" ? "switched off" : "not a managed mod")})");
                if (p.Note is not null) P(p.Note, "      ");
            }
            L();
        }

        L("Not covered by this check:");
        foreach (var n in r.NotChecked) P("- " + n, "  ", "    ");
        L();
        L($"Exit code {r.ExitCode}: 0 = declared compatible, 1 = insufficient information, 2 = definite problem, 3 = could not read the input.");
        var text = sb.ToString();
        return ascii ? Ascii(text) : text;
    }
}
