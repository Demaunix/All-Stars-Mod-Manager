using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllStarsModManager.Transactions;

public enum OpKind { CreateDir, RenameFile, MoveDir, MoveNewFile, ReplaceFile }
public enum OpState { Pending, Started, Done, Undoing, Undone }
public enum TxnStatus { Planned, Applying, Committed, RollingBack, RolledBack, NeedsAttention }

public sealed class JournalOp
{
    public int Seq { get; set; }
    public OpKind Kind { get; set; }
    public OpState State { get; set; }
    /// <summary>What this step does, in words the user can read.</summary>
    public string Description { get; set; } = "";
    /// <summary>The object that is created/changed (final location).</summary>
    public string Path { get; set; } = "";
    /// <summary>Where the object comes from (stage file or old name).</summary>
    public string? From { get; set; }
    /// <summary>Where the previous content is kept for ReplaceFile.</summary>
    public string? BackupPath { get; set; }
    /// <summary>Where the result is moved to when this step is undone (never deleted).</summary>
    public string? UndoPath { get; set; }
    /// <summary>ReplaceFile only: the one name, recorded before the step starts, under which the replaced version waits beside the file during the swap.</summary>
    public string? DisplacedPath { get; set; }
    public string? PreSha { get; set; }
    public string? PostSha { get; set; }
    public string? Fingerprint { get; set; }
}

public sealed class Journal
{
    public int Version { get; set; } = 1;
    public string Txn { get; set; } = "";
    public string Game { get; set; } = "";
    public string Summary { get; set; } = "";
    public TxnStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string StageDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public List<JournalOp> Ops { get; set; } = new();
}

public static class TransactionStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ManagerDir(string gameRoot) => System.IO.Path.Combine(gameRoot, "mods", GameFolderInfo.ManagerDirName);
    public static string CurrentPath(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "current.json");
    public static string HistoryDir(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "history");
    public static string StageRoot(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "stage");
    public static string BackupRoot(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "backups");
    public static string RegistryPath(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "assets.json");
    public static string LockPath(string gameRoot) => System.IO.Path.Combine(ManagerDir(gameRoot), "lock");

    public static bool HasUnfinished(GameFolderInfo g) => File.Exists(CurrentPath(g.Root));

    public static Journal? LoadCurrent(string gameRoot)
    {
        var p = CurrentPath(gameRoot);
        if (!File.Exists(p)) return null;
        return JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(p), Json);
    }

    /// <summary>Every write the manager makes to its own storage first proves the target is inside its folder and reached without links.</summary>
    public static void CheckOwned(string gameRoot, string path)
    {
        LoaderPath.NoReparse(gameRoot, path);
        var mgr = ManagerDir(gameRoot);
        if (!LoaderPath.IsInside(mgr, path) && !string.Equals(LoaderPath.Full(path).TrimEnd('\\'), LoaderPath.Full(mgr).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new PathRuleException("A write outside the manager's own folder was refused.");
    }

    public static void SaveCurrent(string gameRoot, Journal j)
    {
        var p = CurrentPath(gameRoot);
        CheckOwned(gameRoot, p);
        AtomicFile.WriteAllBytes(p, JsonSerializer.SerializeToUtf8Bytes(j, Json));
    }

    /// <summary>Writes a file into history\&lt;txn&gt;\ with link checks at every level.</summary>
    public static void WriteHistoryFile(string gameRoot, string txn, string fileName, byte[] bytes)
    {
        if (!JournalGuard.IsValidTxnId(txn)) throw new JournalException("Refusing to file a record with an invalid change id.");
        var hist = HistoryDir(gameRoot);
        CheckOwned(gameRoot, hist); Directory.CreateDirectory(hist); CheckOwned(gameRoot, hist);
        var dir = System.IO.Path.Combine(hist, txn);
        CheckOwned(gameRoot, dir); Directory.CreateDirectory(dir); CheckOwned(gameRoot, dir);
        var file = System.IO.Path.Combine(dir, fileName);
        CheckOwned(gameRoot, file);
        AtomicFile.WriteAllBytes(file, bytes);
    }

    /// <summary>Moves the finished journal into history. The history file is the record; current.json is removed last.</summary>
    public static void Archive(string gameRoot, Journal j)
    {
        WriteHistoryFile(gameRoot, j.Txn, "journal.json", JsonSerializer.SerializeToUtf8Bytes(j, Json));
        var cur = CurrentPath(gameRoot);
        CheckOwned(gameRoot, cur);
        if (File.Exists(cur)) File.Delete(cur);
    }
    /// <summary>Filed changes, oldest first by the time they were made (change ids alone do not sort within one second).</summary>
    public static IEnumerable<Journal> History(string gameRoot)
    {
        var dir = HistoryDir(gameRoot);
        if (!Directory.Exists(dir) || !LoaderPath.IsPlainInside(gameRoot, dir)) return Enumerable.Empty<Journal>();
        var list = new List<Journal>();
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            if (LoaderPath.IsReparse(d)) continue;
            var f = System.IO.Path.Combine(d, "journal.json");
            if (!File.Exists(f)) continue;
            Journal? j = null;
            try { j = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(f), Json); } catch (JsonException) { }
            // a history record must sit in the folder named after its own change id
            if (j is not null && j.Txn == System.IO.Path.GetFileName(d)) list.Add(j);
        }
        return list.OrderBy(j => j.CreatedUtc).ThenBy(j => j.Txn, StringComparer.Ordinal);
    }
}
