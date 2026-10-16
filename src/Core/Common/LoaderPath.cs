using System.Runtime.InteropServices;
using System.Text;

namespace AllStarsModManager;

public sealed class PathRuleException(string message) : Exception(message);

/// <summary>
/// Path rules ported from the installed loader (files.h relative()/child()/no_reparse()). They decide what the
/// loader will accept, so the manager applies them to everything it reads from a plan or manifest.
/// </summary>
public static class LoaderPath
{
    static readonly char[] ForbiddenChars = "*?\"<>|".ToCharArray();

    public static string Fold(string s) => s.ToLowerInvariant();

    public static string Full(string s) => Path.GetFullPath(s.Replace('/', '\\'));

    /// <summary>Same checks as the loader's relative(): no absolute paths, no "." / ".." / empty segments, no trailing dot or space.</summary>
    public static string Relative(string s)
    {
        if (s.Length == 0 || s.Length >= 2000) throw new PathRuleException("Invalid relative path");
        s = s.Replace('/', '\\');
        if (s[0] == '\\' || s.Contains(':') || s.Contains('\0')) throw new PathRuleException("Absolute paths are not allowed");
        int p = 0;
        while (p < s.Length)
        {
            int e = s.IndexOf('\\', p);
            if (e < 0) e = s.Length;
            var part = s.Substring(p, e - p);
            if (part.Length == 0 || part == "." || part == ".." || part[^1] == '.' || part[^1] == ' ')
                throw new PathRuleException($"Invalid path segment in \"{s}\"");
            if (part.IndexOfAny(ForbiddenChars) >= 0) throw new PathRuleException($"Invalid character in \"{s}\"");
            p = e + 1;
        }
        return s;
    }

    public static string Child(string root, string rel) => Full(root + "\\" + Relative(rel));

    public static bool IsInside(string root, string path)
    {
        var r = Full(root).TrimEnd('\\');
        var p = Full(path);
        return Fold(p).StartsWith(Fold(r) + "\\", StringComparison.Ordinal);
    }

    public static FileAttributes? TryAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static bool IsReparse(string path) => TryAttributes(path) is { } a && (a & FileAttributes.ReparsePoint) != 0;

    /// <summary>Throws unless <paramref name="path"/> is under <paramref name="root"/> and no existing component below the root is a reparse point.</summary>
    public static void NoReparse(string root, string path)
    {
        var r = Full(root).TrimEnd('\\');
        var p = Full(path);
        if (!Fold(p).StartsWith(Fold(r) + "\\", StringComparison.Ordinal)) throw new PathRuleException("Path escapes its root");
        int pos = r.Length;
        for (; ; )
        {
            pos = p.IndexOf('\\', pos + 1);
            var part = pos < 0 ? p : p.Substring(0, pos);
            if (IsReparse(part)) throw new PathRuleException($"A link (reparse point) is in the path: {part}");
            if (pos < 0) break;
        }
    }

    /// <summary>Like NoReparse but returns a bool.</summary>
    public static bool IsPlainInside(string root, string path)
    {
        try { NoReparse(root, path); return true; }
        catch (PathRuleException) { return false; }
    }

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9","CONIN$","CONOUT$" };

    public static bool IsReservedDeviceName(string segment)
    {
        var stem = segment;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem.Substring(0, dot);
        return ReservedNames.Contains(stem.TrimEnd(' '));
    }

    /// <summary>A safe single folder name derived from arbitrary text (never empty, never reserved, no trailing dot/space).</summary>
    public static string SafeFolderName(string raw, string fallback = "Mod")
    {
        var sb = new StringBuilder();
        var bad = Path.GetInvalidFileNameChars();
        foreach (var c in raw.Trim())
            sb.Append(c < 32 || Array.IndexOf(bad, c) >= 0 || c is '*' or '?' or '"' or '<' or '>' or '|' or ':' or '/' or '\\' ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
        if (s.Length == 0 || s == "." || s == "..") s = fallback;
        if (IsReservedDeviceName(s)) s = "_" + s;
        if (s.StartsWith('.')) s = "_" + s.TrimStart('.');
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>Volume root, used to refuse moves that would silently become cross-volume copies.</summary>
    public static string VolumeOf(string path) => Path.GetPathRoot(Full(path)) ?? "";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string path, StringBuilder buf, uint size);

    /// <summary>Case-corrected, long-form version of an existing path (short 8.3 names would otherwise alias one folder under two names).</summary>
    public static string Canonical(string path)
    {
        var full = Full(path);
        var sb = new StringBuilder(1024);
        var n = GetLongPathNameW(full, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : full;
    }
}

/// <summary>Write helpers that never expose a half-written file at the final name.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Environment.TickCount64;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
