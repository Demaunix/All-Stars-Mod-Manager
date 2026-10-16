namespace AllStarsModManager;

/// <summary>Turns a drag-and-drop file list into the paths worth inspecting (existing, distinct, bounded).</summary>
public static class DropPaths
{
    public static List<string> Normalize(IEnumerable<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string full;
            try { full = Path.GetFullPath(r); } catch (ArgumentException) { continue; }
            if (!File.Exists(full) && !Directory.Exists(full)) continue;
            if (seen.Add(full)) list.Add(full);
            if (list.Count >= 20) break;
        }
        return list;
    }
}

