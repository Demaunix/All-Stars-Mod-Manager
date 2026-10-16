using System.Text.RegularExpressions;

namespace AllStarsModManager.Transactions;

public sealed class JournalException(string message) : Exception(message);

/// <summary>
/// A journal is a plain file that anyone can edit, and recovery acts on it. Before a loaded journal is allowed to cause any write,
/// its structure, transaction id, ownership of the stage/backup/history folders and every step's role and paths must match what
/// this manager could itself have produced. Anything else is refused without touching the disk.
/// </summary>
public static class JournalGuard
{
    static readonly Regex TxnId = new(@"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{6}$", RegexOptions.CultureInvariant);
    public const int MaxOps = 5000;

    public static bool IsValidTxnId(string? id) => id is not null && TxnId.IsMatch(id);

    static bool Same(string a, string b) => string.Equals(LoaderPath.Full(a).TrimEnd('\\'), LoaderPath.Full(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    static bool Under(string dir, string path) => LoaderPath.IsInside(dir, path);
    static bool DirectChildOf(string dir, string path) => Same(Path.GetDirectoryName(LoaderPath.Full(path).TrimEnd('\\'))!, dir);

    public static void Validate(Journal j, string root)
    {
        root = LoaderPath.Full(root).TrimEnd('\\');
        if (j is null) throw new JournalException("The record is empty.");
        if (j.Version != 1) throw new JournalException("The record has an unknown format version.");
        if (!IsValidTxnId(j.Txn)) throw new JournalException("The record has an invalid change id.");
        if (string.IsNullOrEmpty(j.Game) || !Same(j.Game, root)) throw new JournalException("The record belongs to a different folder.");
        if (!Enum.IsDefined(j.Status)) throw new JournalException("The record has an unknown status.");
        var stage = Path.Combine(TransactionStore.StageRoot(root), j.Txn);
        var backup = Path.Combine(TransactionStore.BackupRoot(root), j.Txn);
        if (string.IsNullOrEmpty(j.StageDir) || !Same(j.StageDir, stage)) throw new JournalException("The record's staging folder is not this change's own staging folder.");
        if (string.IsNullOrEmpty(j.BackupDir) || !Same(j.BackupDir, backup)) throw new JournalException("The record's backup folder is not this change's own backup folder.");
        if (j.Ops is null || j.Ops.Count == 0 || j.Ops.Count > MaxOps) throw new JournalException("The record lists an unreasonable number of steps.");
        var seen = new HashSet<int>();
        foreach (var op in j.Ops)
        {
            if (op is null || op.Seq < 1 || !seen.Add(op.Seq)) throw new JournalException("The record has duplicate or invalid step numbers.");
            if (!Enum.IsDefined(op.Kind) || !Enum.IsDefined(op.State)) throw new JournalException("The record has a step of unknown kind.");
            CheckOp(root, stage, backup, op);
        }
    }

    static void CheckOp(string root, string stage, string backup, JournalOp op)
    {
        string mods = Path.Combine(root, "mods"), res = Path.Combine(root, "Resource"), mgr = TransactionStore.ManagerDir(root);
        string registry = TransactionStore.RegistryPath(root);
        bool InMods(string? p) => p is not null && Under(mods, p) && !Under(mgr, p) && !Same(mgr, p);
        bool InStage(string? p) => p is not null && Under(stage, p);
        bool InBackup(string? p) => p is not null && Under(backup, p);
        bool ModFolder(string? p) => p is not null && DirectChildOf(mods, p) && !Same(mgr, p) && Under(mods, p);
        bool ResFile(string? p)
        {
            if (p is null || !Under(res, p)) return false;
            try { LooseAssetPolicy.ValidateTarget(Path.GetRelativePath(root, LoaderPath.Full(p))); return true; } catch (PathRuleException) { return false; }
        }
        bool ResDir(string? p)
        {
            if (p is null || !(Under(res, p) || Same(res, p))) return false;
            try { var rel = Path.GetRelativePath(root, LoaderPath.Full(p)); LoaderPath.Relative(rel); return !rel.Split('\\').Any(LoaderPath.IsReservedDeviceName); } catch (PathRuleException) { return false; }
        }
        bool ManifestName(string? p) => p is not null && Path.GetFileName(p).StartsWith("mod.ini", StringComparison.OrdinalIgnoreCase);
        bool ModTopLevelIni(string? p) => InMods(p) && p is not null && DirectChildOf(mods, Path.GetDirectoryName(LoaderPath.Full(p))!) && Path.GetExtension(p).Equals(".ini", StringComparison.OrdinalIgnoreCase) && !ManifestName(p);

        string Bad(string what) => $"Step {op.Seq} ({op.Kind}) is not something this manager would do: {what}.";
        if (string.IsNullOrEmpty(op.Path)) throw new JournalException(Bad("no target"));
        foreach (var p in new[] { op.Path, op.From, op.BackupPath, op.UndoPath, op.DisplacedPath })
            if (p is not null)
            {
                string full;
                try { full = LoaderPath.Full(p); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { throw new JournalException(Bad("an invalid path")); }
                if (!Under(root, full)) throw new JournalException(Bad("a path outside the game folder"));
            }
        if (op.DisplacedPath is not null)
        {
            // the one name a replace step may park the old version under: right beside its own target, with the exact generated shape
            var dp = LoaderPath.Full(op.DisplacedPath);
            var prefix = Path.GetFileName(LoaderPath.Full(op.Path)) + ".displaced-";
            bool shaped = Path.GetFileName(dp).Length == prefix.Length + 6 && Path.GetFileName(dp).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(dp)[prefix.Length..].All(Uri.IsHexDigit);
            if (op.Kind != OpKind.ReplaceFile || !shaped || !Same(Path.GetDirectoryName(dp)!, Path.GetDirectoryName(LoaderPath.Full(op.Path))!)) throw new JournalException(Bad("a parking name that is not the one a replace step generates"));
        }
        switch (op.Kind)
        {
            case OpKind.CreateDir:
                if (!(InMods(op.Path) || ResDir(op.Path)) || op.From is not null || op.BackupPath is not null || op.UndoPath is not null) throw new JournalException(Bad("a folder outside the places it may create"));
                break;
            case OpKind.RenameFile:
                {
                    bool toggle = InMods(op.From) && InMods(op.Path) && ManifestName(op.From) && ManifestName(op.Path) && ModFolder(Path.GetDirectoryName(LoaderPath.Full(op.Path))) && Same(Path.GetDirectoryName(LoaderPath.Full(op.From!))!, Path.GetDirectoryName(LoaderPath.Full(op.Path))!);
                    bool takeBack = ResFile(op.From) && InBackup(op.Path);
                    if (!(toggle || takeBack)) throw new JournalException(Bad("a rename that is neither a manifest switch nor taking back an added file"));
                    break;
                }
            case OpKind.MoveDir:
                {
                    bool install = InStage(op.From) && ModFolder(op.Path) && InBackup(op.UndoPath);
                    bool uninstall = ModFolder(op.From) && InBackup(op.Path) && op.UndoPath is null;
                    if (!(install || uninstall)) throw new JournalException(Bad("a folder move that is neither an install nor a removal into the backup area"));
                    break;
                }
            case OpKind.MoveNewFile:
                if (!(InStage(op.From) && (ResFile(op.Path) || Same(op.Path, registry)) && InBackup(op.UndoPath))) throw new JournalException(Bad("adding a file somewhere it may not"));
                break;
            case OpKind.ReplaceFile:
                if (!((Same(op.Path, registry) || ModTopLevelIni(op.Path)) && InStage(op.From) && InBackup(op.BackupPath) && InBackup(op.UndoPath))) throw new JournalException(Bad("replacing a file it may not"));
                break;
        }
    }
}
