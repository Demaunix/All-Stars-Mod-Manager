using System.Text;

namespace AllStarsModManager.Compat;

/// <summary>
/// Command line of AllStarsModCheck.exe. It lives in the library (the executable only calls <see cref="Run"/>) so tests can drive the same code
/// in-process and through the real executable.
/// </summary>
public static class CheckCli
{
    public static string Usage => """
        AllStarsModCheck - offline compatibility check for Sonic & SEGA All-Stars Racing loader mods

        Usage:  AllStarsModCheck [folder] [options]

        folder                  A game folder (the one with the game's .exe), a mods folder, a folder of mod
                                folders, or one mod folder (it holds mod.ini). Default: the current folder.

        --add <folder>          Also check a mod that is not installed yet, as if it were added (repeatable).
        --as game|mods|package  Do not detect the kind of folder; treat it as this.
        --loader <profile>      Without a game folder: check against a named loader build you know you have
                                (see --list-loaders). The result then says the loader was assumed.
        --loader-profiles <file>  Also read loader builds from this file (same format as the built-in table).
        --list-loaders          Print the built-in loader profiles and exit.
        --quick                 Do not hash mod files or game files (structure, pins and declarations only).
        --deep                  Also rebuild each changed archive the way the loader does (slow; reads whole archives).
        --json                  Print the result as JSON (schema allstars-check-report/1) instead of text.
        --ascii                 Plain ASCII text (no arrows or typographic dashes).
        --version               Print the version and exit.
        --help                  Print this help and exit.

        The check reads files only. It never runs or loads a mod DLL, never writes to the game folder, settings or
        saves, and needs no network.

        Exit codes:  0 declared compatible   1 insufficient information   2 definite problem
                     3 the folder could not be read   64 wrong command line   70 unexpected error
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var o = new CheckOptions();
        bool json = false, ascii = false, havePath = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next(string what)
                {
                    if (i + 1 >= args.Length) throw new UsageException($"{a} needs {what}.");
                    return CleanPath(args[++i]);
                }
                switch (a)
                {
                    case "--help" or "-h" or "-?" or "/?": output.WriteLine(Usage); return 0;
                    case "--version": output.WriteLine("AllStarsModCheck " + CheckRunner.ToolVersion); return 0;
                    case "--list-loaders":
                        foreach (var p in LoaderProfiles.BuiltIn)
                            output.WriteLine($"{p.Id}\n  {p.Description}\n  host   sha256 {p.HostSha256}\n  proxy  sha256 {p.ProxySha256}\n  capabilities: {string.Join(", ", p.Capabilities)}");
                        return 0;
                    case "--json": json = true; break;
                    case "--ascii": ascii = true; break;
                    case "--quick": o.Quick = true; break;
                    case "--deep": o.Deep = true; break;
                    case "--add": o.Add.Add(Next("a folder")); break;
                    case "--as":
                        o.InputAs = Next("game, mods or package");
                        if (o.InputAs is not ("game" or "mods" or "package")) throw new UsageException("--as must be game, mods or package.");
                        break;
                    case "--loader": o.LoaderProfileId = Next("a profile name"); break;
                    case "--loader-profiles": o.ProfileFile = Next("a file"); break;
                    default:
                        if (a.StartsWith("--") || (a.StartsWith('-') && a.Length > 1)) throw new UsageException(UnknownOption(a));
                        if (havePath) throw new UsageException("Only one folder can be given (use --add for mods that are not installed yet).");
                        o.Path = CleanPath(a); havePath = true;
                        break;
                }
            }
            if (o.Quick && o.Deep) throw new UsageException("--quick and --deep cannot be combined.");
        }
        catch (UsageException e)
        {
            // usage messages are fixed text plus, at most, a plain option name (see UnknownOption); scrubbed as well in case a message ever quotes more
            var typedText = CheckOutput.ScrubPaths(e.Message, ErrorRoots(o));
            error.WriteLine("AllStarsModCheck: " + typedText);
            error.WriteLine("Try --help.");
            return CheckExit.Usage;
        }

        try
        {
            if (!json) o.Progress = msg => error.WriteLine(msg);
            var report = CheckRunner.Run(o);
            output.Write(json ? CheckOutput.ToJson(report) + Environment.NewLine : CheckOutput.ToText(report, ascii));
            return report.ExitCode;
        }
        catch (CheckInputException e)
        {
            // the message can quote an operating-system error that contains a full path (for example a locked profile file)
            error.WriteLine("AllStarsModCheck: " + CheckOutput.ScrubPaths(e.Message, ErrorRoots(o)));
            return CheckExit.InputError;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("AllStarsModCheck: a file could not be read: " + CheckOutput.ScrubPaths(e.Message, ErrorRoots(o)));
            return CheckExit.InputError;
        }
    }

    /// <summary>
    /// "C:\Games\Some Mod\" inside quotes reaches a program as C:\Games\Some Mod" (the backslash escapes the closing quote). Accept that very common
    /// mistake by dropping a lone trailing quote; a path never contains one.
    /// </summary>
    static string CleanPath(string arg) => arg.EndsWith('"') && arg.IndexOf('"') == arg.Length - 1 ? arg[..^1] : arg;

    /// <summary>
    /// What was typed is not repeated: an unknown option can carry a path or anything else after "=". Only a plain option name (ASCII letters, digits and
    /// dashes) is named so the person can see which one was not understood.
    /// </summary>
    static string UnknownOption(string arg)
    {
        var name = arg.Split('=', 2)[0];
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^--?[A-Za-z][A-Za-z0-9-]{0,39}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? $"Unknown option {name}." : "Unknown option (what was typed is not shown).";
    }

    static IEnumerable<string> ErrorRoots(CheckOptions o)
    {
        var roots = new List<string> { o.Path };
        roots.AddRange(o.Add);
        if (o.ProfileFile is not null)
        {
            // building the folder of an invalid path must not itself fail inside an error handler
            try { roots.Add(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.ProfileFile)) ?? ""); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            roots.Add(o.ProfileFile);
        }
        return roots;
    }

    sealed class UsageException(string message) : Exception(message);
}
