using System.Security.Cryptography;
using System.Text;

namespace AllStarsModManager.Transactions;

/// <summary>Raised by tests to model the process dying: the engine must not run any cleanup or rollback code for it.</summary>
public sealed class SimulatedCrash(string point) : Exception("Simulated crash at " + point);

/// <summary>Raised by tests to model an ordinary I/O failure at a chosen point.</summary>
public sealed class InjectedFailure(string point) : IOException("Injected failure at " + point);

public sealed class ExternalChangeException(string message) : IOException(message);

public sealed class TransactionHooks
{
    /// <summary>Called at every meaningful boundary ("stage-begin", "op:3:after", "undo:2:before", ...).</summary>
    public Action<string>? Step;
    internal void At(string point) => Step?.Invoke(point);
}

public sealed class StageItem
{
    public string SourcePath = "";
    public bool IsDirectory;
    public string DestinationRel = "";      // relative to the stage directory
    /// <summary>For an installed package whose manifest is stored under a switched-off name: that file is renamed to mod.ini inside the staged COPY (the library original is untouched), so the install lands in its final state in one step.</summary>
    public string? ActivateManifest;
}

public sealed class StageNewFile
{
    public string DestinationRel = "";
    public byte[] Content = Array.Empty<byte>();
}

public sealed class ChangePlan
{
    public string GameRoot = "";
    public string Txn = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Summary = "";
    public string SnapshotToken = "";
    public List<StageItem> StageItems = new();
    public List<StageNewFile> NewFiles = new();
    public List<JournalOp> Ops = new();
    public List<string> PreviewLines = new();
    public List<string> Notes = new();
    /// <summary>Re-checks the staged copies before anything is changed; returns problems (empty = fine).</summary>
    public Func<string, List<string>>? VerifyStage;
    /// <summary>Re-checks the finished game folder; returns problems (empty = fine). A problem triggers rollback.</summary>
    public Func<List<string>>? VerifyResult;
    /// <summary>Recomputes the snapshot token for the external-change check.</summary>
    public Func<string>? ComputeSnapshot;

    public string StageDir => Path.Combine(TransactionStore.StageRoot(GameRoot), Txn);
    public string BackupDir => Path.Combine(TransactionStore.BackupRoot(GameRoot), Txn);
    public bool HasChanges => Ops.Count > 0;
}

public sealed class ApplyResult
{
    public bool Success, Declined, RolledBack, NeedsAttention, Cancelled;
    public string Message = "";
    public List<string> Details = new();
    public string? Txn;
    /// <summary>Full exception text for support and tests; not shown in routine UI.</summary>
    public string? Technical;
}

/// <summary>
/// Applies a plan to the game folder as a journaled transaction. Every step is written to the journal before and
/// after it happens; any failure or cancellation undoes completed steps in reverse order; a crash is repaired by
/// <see cref="Recover"/> from the journal. Nothing is ever deleted: replaced or removed content is moved aside.
/// </summary>
public sealed class TransactionEngine
{
    readonly string _root;
    readonly IGameProcessProbe _probe;
    readonly TransactionHooks _hooks;

    public TransactionEngine(string gameRoot, IGameProcessProbe probe, TransactionHooks? hooks = null)
    {
        _root = LoaderPath.Full(gameRoot).TrimEnd('\\');
        _probe = probe;
        _hooks = hooks ?? new TransactionHooks();
    }

    static ApplyResult Decline(string msg, string? txn = null) => new() { Declined = true, Message = msg, Txn = txn };

    // ---- apply --------------------------------------------------------------------------

    public ApplyResult Apply(ChangePlan plan, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        if (!string.Equals(LoaderPath.Full(plan.GameRoot).TrimEnd('\\'), _root, StringComparison.OrdinalIgnoreCase))
            return Decline("This change was prepared for a different game folder.");
        if (!plan.HasChanges) return Decline("There is nothing to change.");
        var guard = Guard(plan.SnapshotToken, plan.ComputeSnapshot);
        if (guard is not null) return guard;

        try { PrepareManagerDir(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathRuleException)
        { return Decline("The manager's work folder could not be prepared: " + e.Message); }

        FileStream? lockFs = TryLock();
        if (lockFs is null) return Decline("Another copy of the manager seems to be changing this folder. Close it and try again.");
        using (lockFs)
        {
            CleanupOrphanStages();
            Journal? j = null;
            bool stageCreated = false;
            try
            {
                progress?.Report("Preparing files…");
                _hooks.At("stage-begin");
                stageCreated = true;
                Directory.CreateDirectory(plan.StageDir);
                LoaderPath.NoReparse(_root, plan.StageDir);
                StageAll(plan, ct, progress);
                _hooks.At("stage-done");
                var problems = plan.VerifyStage?.Invoke(plan.StageDir) ?? new();
                if (problems.Count > 0)
                {
                    CleanupStage(plan.StageDir);
                    return new ApplyResult { Declined = true, Message = "The copied files did not pass the safety checks, so nothing was changed.", Details = problems, Txn = plan.Txn };
                }
                var again = Guard(plan.SnapshotToken, plan.ComputeSnapshot);
                if (again is not null) { CleanupStage(plan.StageDir); return again; }

                var candidate = new Journal
                {
                    Txn = plan.Txn, Game = _root, Summary = plan.Summary, Status = TxnStatus.Applying, CreatedUtc = DateTime.UtcNow,
                    StageDir = plan.StageDir, BackupDir = plan.BackupDir, Ops = plan.Ops.Select(Clone).ToList()
                };
                JournalGuard.Validate(candidate, _root);   // our own plan must satisfy the same rules recovery will insist on
                FinalizeOpsAfterStaging(candidate);
                j = candidate;
                Directory.CreateDirectory(plan.BackupDir);
                LoaderPath.NoReparse(_root, plan.BackupDir);
                TransactionStore.SaveCurrent(_root, j);
                _hooks.At("journal-written");

                foreach (var op in j.Ops)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(op.Description);
                    Execute(j, op);
                }
                _hooks.At("post-check");
                var after = plan.VerifyResult?.Invoke() ?? new();
                if (after.Count > 0) throw new ExternalChangeException("The result did not pass the final check: " + string.Join(" ", after));
                _hooks.At("commit-before");
                j.Status = TxnStatus.Committed;
                j.FinishedUtc = DateTime.UtcNow;
                TransactionStore.SaveCurrent(_root, j);
                _hooks.At("commit-saved");
                TransactionStore.Archive(_root, j);
                CleanupStage(plan.StageDir);
                return new ApplyResult { Success = true, Txn = plan.Txn, Message = "Changes applied.", Details = j.Ops.Select(o => o.Description).ToList() };
            }
            catch (SimulatedCrash) { throw; }
            catch (Exception e) when (e is not SimulatedCrash)
            {
                bool cancelled = e is OperationCanceledException;
                if (j is null)
                {
                    if (stageCreated) { try { CleanupStage(plan.StageDir); } catch { } }
                    return new ApplyResult { Declined = !cancelled, Cancelled = cancelled, Message = cancelled ? "Cancelled. Nothing was changed." : "Nothing was changed: " + e.Message, Txn = plan.Txn, Technical = e.ToString() };
                }
                var r = RollBack(j, (cancelled ? "Cancelled by the user. " : "") + e.Message);
                r.Cancelled = cancelled;
                r.Technical ??= e.ToString();
                return r;
            }
        }
    }

    ApplyResult? Guard(string expectedToken, Func<string>? compute)
    {
        var pr = _probe.Check(_root);
        if (!pr.SafeToChange) return Decline(pr.Message);
        if (File.Exists(TransactionStore.CurrentPath(_root))) return Decline("An earlier change did not finish. Restore the previous state first.");
        if (!File.Exists(Path.Combine(_root, GameFolderInfo.ExeName))) return Decline("This does not look like an All-Stars Racing folder.");
        if (compute is not null && compute() != expectedToken)
            return Decline("The game folder changed after these changes were prepared (another program or window touched it). Refresh and review the changes again.");
        return null;
    }

    void PrepareManagerDir()
    {
        var mods = Path.Combine(_root, "mods");
        var attr = LoaderPath.TryAttributes(mods);
        if (attr is null) Directory.CreateDirectory(mods);
        else if ((attr.Value & FileAttributes.ReparsePoint) != 0 || (attr.Value & FileAttributes.Directory) == 0) throw new PathRuleException("The mods folder is not a plain folder.");
        LoaderPath.NoReparse(_root, TransactionStore.ManagerDir(_root));
        Directory.CreateDirectory(TransactionStore.ManagerDir(_root));
        LoaderPath.NoReparse(_root, TransactionStore.ManagerDir(_root));
    }

    FileStream? TryLock()
    {
        try { return new FileStream(TransactionStore.LockPath(_root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    static JournalOp Clone(JournalOp o) => new()
    {
        Seq = o.Seq, Kind = o.Kind, State = OpState.Pending, Description = o.Description, Path = o.Path, From = o.From, BackupPath = o.BackupPath,
        UndoPath = o.UndoPath, PreSha = o.PreSha, PostSha = o.PostSha, Fingerprint = o.Fingerprint
    };

    // ---- staging ------------------------------------------------------------------------

    void StageAll(ChangePlan plan, CancellationToken ct, IProgress<string>? progress)
    {
        foreach (var it in plan.StageItems)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(plan.StageDir, it.DestinationRel);
            Assert.InsideStage(plan.StageDir, dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            progress?.Report("Copying " + Path.GetFileName(it.SourcePath.TrimEnd('\\')) + "…");
            if (it.IsDirectory) SafeCopy.Tree(it.SourcePath, dest, ct);
            else SafeCopy.File(it.SourcePath, dest, ct);
            if (it.ActivateManifest is { } name)
            {
                if (!it.IsDirectory || name.Contains('\\') || name.Contains('/') || !name.StartsWith("mod.ini", StringComparison.OrdinalIgnoreCase)) throw new PathRuleException("An invalid manifest name was requested.");
                var from = Path.Combine(dest, name); var to = Path.Combine(dest, "mod.ini");
                if (!File.Exists(from) || File.Exists(to)) throw new IOException("The staged copy does not have the expected manifest layout.");
                File.Move(from, to);
            }
        }
        foreach (var nf in plan.NewFiles)
        {
            var dest = Path.Combine(plan.StageDir, nf.DestinationRel);
            Assert.InsideStage(plan.StageDir, dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(nf.Content);
            fs.Flush(true);
        }
    }

    /// <summary>Hashes and fingerprints what staging produced, so steps can later prove they moved exactly that.</summary>
    void FinalizeOpsAfterStaging(Journal j)
    {
        foreach (var op in j.Ops)
        {
            switch (op.Kind)
            {
                case OpKind.MoveNewFile when op.From is not null: op.PostSha = Sha.OfFile(op.From); break;
                case OpKind.ReplaceFile when op.From is not null: op.PostSha = Sha.OfFile(op.From); break;
                case OpKind.MoveDir when op.From is not null && op.Fingerprint is null: op.Fingerprint = Fingerprint.OfTree(op.From); break;
            }
        }
    }

    // ---- executing one step -------------------------------------------------------------

    void Execute(Journal j, JournalOp op)
    {
        Assert.Scope(_root, op);
        Precheck(op);   // pure checks first: a step that cannot even start is never recorded as started, so rollback leaves it alone
        if (op.Kind == OpKind.ReplaceFile && op.DisplacedPath is null) op.DisplacedPath = op.Path + ".displaced-" + Guid.NewGuid().ToString("N")[..6];   // written to the journal with "Started", before anything can be left behind
        op.State = OpState.Started;
        TransactionStore.SaveCurrent(_root, j);
        _hooks.At($"op:{op.Seq}:before");
        try { Perform(op); }
        catch (ReplaceRaceException)
        {
            // someone wrote to the file between our check and the swap; their content was put back untouched, so this step did not happen
            op.State = OpState.Pending;
            TransactionStore.SaveCurrent(_root, j);
            throw;
        }
        op.State = OpState.Done;
        TransactionStore.SaveCurrent(_root, j);
        _hooks.At($"op:{op.Seq}:after");
    }

    void Precheck(JournalOp op)
    {
        switch (op.Kind)
        {
            case OpKind.RenameFile:
                if (!File.Exists(op.From!) || File.Exists(op.Path)) throw new ExternalChangeException($"\"{Path.GetFileName(op.From!)}\" changed while preparing: {op.Description}");
                if (op.PreSha is not null && Sha.OfFile(op.From!) != op.PreSha) throw new ExternalChangeException($"\"{Path.GetFileName(op.From!)}\" was edited while preparing.");
                break;
            case OpKind.MoveDir:
                if (!Directory.Exists(op.From!) || Directory.Exists(op.Path) || File.Exists(op.Path)) throw new ExternalChangeException($"Folder state changed while preparing: {op.Description}");
                if (op.Fingerprint is not null && Fingerprint.OfTree(op.From!) != op.Fingerprint) throw new ExternalChangeException($"The contents of \"{Path.GetFileName(op.From!)}\" changed while preparing.");
                Assert.SameVolume(op.From!, op.Path);
                break;
            case OpKind.MoveNewFile:
                if (!File.Exists(op.From!)) throw new ExternalChangeException("A prepared file is missing: " + Path.GetFileName(op.Path));
                if (File.Exists(op.Path) || Directory.Exists(op.Path)) throw new ExternalChangeException($"{Path.GetFileName(op.Path)} appeared before it could be added; the manager never overwrites files.");
                Assert.SameVolume(op.From!, op.Path);
                break;
            case OpKind.ReplaceFile:
                if (!File.Exists(op.Path) || Sha.OfFile(op.Path) != op.PreSha) throw new ExternalChangeException($"{Path.GetFileName(op.Path)} was changed by another program while preparing.");
                Assert.SameVolume(op.From!, op.Path);
                break;
        }
    }

    void Perform(JournalOp op)
    {
        switch (op.Kind)
        {
            case OpKind.CreateDir:
                if (!Directory.Exists(op.Path)) Directory.CreateDirectory(op.Path);
                break;
            case OpKind.RenameFile:
                Directory.CreateDirectory(Path.GetDirectoryName(op.Path)!);
                Retry.Io(() => File.Move(op.From!, op.Path));
                break;
            case OpKind.MoveDir:
                Directory.CreateDirectory(Path.GetDirectoryName(op.Path)!);
                Retry.Io(() => Directory.Move(op.From!, op.Path));
                break;
            case OpKind.MoveNewFile:
                Directory.CreateDirectory(Path.GetDirectoryName(op.Path)!);
                Retry.Io(() => File.Move(op.From!, op.Path));
                break;
            case OpKind.ReplaceFile:
                ExecuteReplace(op);
                break;
        }
    }
    void ExecuteReplace(JournalOp op)
    {
        if (!File.Exists(op.Path) || Sha.OfFile(op.Path) != op.PreSha) throw new ExternalChangeException($"{Path.GetFileName(op.Path)} was changed by another program while preparing.");
        Directory.CreateDirectory(Path.GetDirectoryName(op.BackupPath!)!);
        if (File.Exists(op.BackupPath!))
        {
            if (Sha.OfFile(op.BackupPath!) != op.PreSha) throw new ExternalChangeException("A different backup already exists: " + op.BackupPath);
        }
        else
        {
            File.Copy(op.Path, op.BackupPath!, overwrite: false);
            if (Sha.OfFile(op.BackupPath!) != op.PreSha) throw new IOException("The backup copy did not verify.");
        }
        _hooks.At($"op:{op.Seq}:mid");
        Assert.SameVolume(op.From!, op.Path);
        SafeReplace.Run(op.From!, op.Path, op.PreSha!, op.DisplacedPath ?? throw new InvalidOperationException("The displaced-file name was not recorded."), Path.GetDirectoryName(op.BackupPath!)!, _hooks.At);
    }
    // ---- state tests --------------------------------------------------------------------

    enum Where { Before, After, Unknown }

    Where Locate(JournalOp op)
    {
        switch (op.Kind)
        {
            case OpKind.CreateDir:
                // A link standing where the folder should be is not something this step created.
                return Directory.Exists(op.Path) && !LoaderPath.IsReparse(op.Path) ? Where.After : Where.Before;
            case OpKind.RenameFile:
                {
                    bool from = File.Exists(op.From!), to = File.Exists(op.Path);
                    if (from && !to) return Where.Before;
                    // the file we moved must still be exactly what we moved; anything else was edited or replaced by someone else
                    if (to && !from) return op.PreSha is null || Sha.OfFile(op.Path) == op.PreSha ? Where.After : Where.Unknown;
                    // both names exist: a move removes its source, so unless the target still holds the moved content the rename never happened
                    if (from && to) return Sha.OfFile(op.Path) == op.PreSha ? Where.Unknown : Where.Before;
                    return Where.Unknown;
                }
            case OpKind.MoveDir:
                {
                    bool from = Directory.Exists(op.From!), to = Directory.Exists(op.Path);
                    if (from && !to) return Where.Before;
                    // the folder we moved must still hold exactly the content we moved (added, removed or edited files make it someone else's work too)
                    if (to && !from) return op.Fingerprint is null || Fingerprint.OfTree(op.Path) == op.Fingerprint ? Where.After : Where.Unknown;
                    if (from && to) return op.State == OpState.Done ? Where.Unknown : Where.Before;   // a move removes its source
                    // an earlier (interrupted) undo already moved it into the backup area
                    if (!from && !to && op.UndoPath is not null && Directory.Exists(op.UndoPath) && (op.Fingerprint is null || Fingerprint.OfTree(op.UndoPath) == op.Fingerprint)) return Where.Before;
                    return Where.Unknown;
                }
            case OpKind.MoveNewFile:
                {
                    bool from = File.Exists(op.From!), to = File.Exists(op.Path);
                    if (from && !to) return Where.Before;
                    if (to && !from) return Sha.OfFile(op.Path) == op.PostSha ? Where.After : Where.Unknown;
                    if (from && to) return op.State == OpState.Done ? Where.Unknown : Where.Before;   // a move removes its source: this target is someone else's file
                    // already moved aside by an earlier (interrupted) rollback
                    if (!from && !to && op.UndoPath is not null && File.Exists(op.UndoPath) && Sha.OfFile(op.UndoPath) == op.PostSha) return Where.Before;
                    return Where.Unknown;
                }
            case OpKind.ReplaceFile:
                {
                    if (!File.Exists(op.Path)) return Where.Unknown;
                    var sha = Sha.OfFile(op.Path);
                    if (sha == op.PostSha && sha != op.PreSha) return Where.After;
                    if (sha == op.PreSha) return Where.Before;
                    return Where.Unknown;
                }
        }
        return Where.Unknown;
    }

    void UndoOne(JournalOp op)
    {
        Assert.Scope(_root, op);
        switch (op.Kind)
        {
            case OpKind.CreateDir:
                if (Directory.Exists(op.Path) && !Directory.EnumerateFileSystemEntries(op.Path).Any()) Directory.Delete(op.Path);
                break;
            case OpKind.RenameFile:
                Retry.Io(() => File.Move(op.Path, op.From!));
                break;
            case OpKind.MoveDir:
                {
                    // an install is undone into the backup area (never back into temporary staging, which is cleaned up); a removal goes back where it was
                    var dest = op.UndoPath ?? op.From!;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (Directory.Exists(dest) || File.Exists(dest)) throw new ExternalChangeException($"\"{Path.GetFileName(dest)}\" already exists where the folder would be put back; nothing was moved.");
                    Assert.SameVolume(op.Path, dest);
                    Retry.Io(() => Directory.Move(op.Path, dest));
                    break;
                }
            case OpKind.MoveNewFile:
                {
                    var dest = op.UndoPath ?? throw new InvalidOperationException("No undo location for " + op.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest)) dest = dest + "." + Guid.NewGuid().ToString("N")[..6];
                    Retry.Io(() => File.Move(op.Path, dest));
                    break;
                }
            case OpKind.ReplaceFile:
                {
                    if (!File.Exists(op.BackupPath!) || Sha.OfFile(op.BackupPath!) != op.PreSha) throw new ExternalChangeException("The backup of " + Path.GetFileName(op.Path) + " is missing or damaged; the file was left as it is.");
                    var keep = op.UndoPath ?? op.Path + ".replaced";
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    if (File.Exists(keep)) keep += "." + Guid.NewGuid().ToString("N")[..6];
                    File.Copy(op.Path, keep, false);
                    var tmp = op.Path + ".restore-" + Guid.NewGuid().ToString("N")[..6];
                    File.Copy(op.BackupPath!, tmp, false);
                    if (Sha.OfFile(tmp) != op.PreSha) { File.Delete(tmp); throw new IOException("The restored copy did not verify."); }
                    _hooks.At($"undo:{op.Seq}:replace");
                    KeepDisplacedLeftovers(op);   // whatever an interrupted forward run left under the recorded name is kept before that name is used again
                    try { SafeReplace.Run(tmp, op.Path, op.PostSha!, op.DisplacedPath ?? op.Path + ".displaced-" + Guid.NewGuid().ToString("N")[..6], Path.GetDirectoryName(op.BackupPath!)!, p => _hooks.At("undo-" + p)); }
                    catch (ReplaceRaceException e) { try { File.Delete(tmp); } catch (IOException) { } throw new ExternalChangeException(e.Message); }
                    KeepDisplacedLeftovers(op);
                    break;
                }
        }
    }

    /// <summary>
    /// A crash (or an I/O error) between the two halves of a replace leaves the version that was there before under the name recorded in the
    /// journal for this step (<see cref="JournalOp.DisplacedPath"/>). Once the file itself has been put back, exactly that one file is moved
    /// (never deleted) into this step's backup folder. Files that merely look similar are not this step's and are never touched.
    /// </summary>
    void KeepDisplacedLeftovers(JournalOp op)
    {
        if (op.DisplacedPath is null) return;
        // only ever called from UndoAll/UndoOne, whose first act for every step is Assert.Scope (allowed folders, no link on the way, including DisplacedPath)
        if (!File.Exists(op.DisplacedPath) || LoaderPath.IsReparse(op.DisplacedPath)) return;
        var keepDir = Path.GetDirectoryName(op.BackupPath!)!;
        Directory.CreateDirectory(keepDir);
        var dest = Path.Combine(keepDir, Path.GetFileName(op.DisplacedPath) + ".leftover");
        if (File.Exists(dest)) dest += "." + Guid.NewGuid().ToString("N")[..6];
        File.Move(op.DisplacedPath, dest);
    }

    // ---- rollback / recovery ------------------------------------------------------------

    ApplyResult RollBack(Journal j, string reason)
    {
        j.Status = TxnStatus.RollingBack;
        j.FailureReason = reason;
        try
        {
            TransactionStore.SaveCurrent(_root, j);
            UndoAll(j);
            j.Status = TxnStatus.RolledBack;
            j.FinishedUtc = DateTime.UtcNow;
            TransactionStore.SaveCurrent(_root, j);
            TransactionStore.Archive(_root, j);
            CleanupStage(j.StageDir);
            return new ApplyResult { RolledBack = true, Txn = j.Txn, Message = "Nothing was changed: " + reason };
        }
        catch (SimulatedCrash) { throw; }
        catch (Exception e)
        {
            try { j.Status = TxnStatus.NeedsAttention; TransactionStore.SaveCurrent(_root, j); } catch { }
            return new ApplyResult
            {
                NeedsAttention = true, Txn = j.Txn, Technical = e.ToString(),
                Message = "The change failed and could not be fully undone: " + e.Message + " Use \"Restore previous state\" to try again; nothing has been deleted.",
                Details = j.Ops.Where(o => o.State != OpState.Pending && o.State != OpState.Undone).Select(o => o.Description).ToList()
            };
        }
    }

    void UndoAll(Journal j)
    {
        foreach (var op in Enumerable.Reverse(j.Ops))
        {
            if (op.State is OpState.Pending or OpState.Undone) continue;
            Assert.Scope(_root, op);   // before anything is inspected, moved or collected: allowed folders only, and no link anywhere on the way
            op.State = OpState.Undoing;
            TransactionStore.SaveCurrent(_root, j);
            _hooks.At($"undo:{op.Seq}:before");
            var where = Locate(op);
            if (where == Where.Before && op.Kind == OpKind.ReplaceFile) KeepDisplacedLeftovers(op);   // e.g. an earlier restore stopped between the halves of its swap
            if (where == Where.After) UndoOne(op);
            else if (where == Where.Unknown)
                throw new ExternalChangeException($"\"{Path.GetFileName(op.Path)}\" is not in the expected state, so it was left alone ({op.Description}).");
            _hooks.At($"undo:{op.Seq}:mid");
            op.State = OpState.Undone;
            TransactionStore.SaveCurrent(_root, j);
            _hooks.At($"undo:{op.Seq}:after");
        }
    }

    /// <summary>Finishes or undoes whatever an interrupted run left behind. Safe to run repeatedly. Everything is inspected and done while holding the lock.</summary>
    public ApplyResult Recover(CancellationToken ct = default)
    {
        var pr = _probe.Check(_root);
        if (!pr.SafeToChange) return Decline(pr.Message);
        if (!ManagerDirIsUsable(out var problem)) return problem is null ? new ApplyResult { Success = true, Message = "Nothing to restore." } : Decline(problem);
        using var lockFs = TryLock();
        if (lockFs is null) return Decline("Another copy of the manager seems to be using this folder.");
        Journal? j;
        try { j = TransactionStore.LoadCurrent(_root); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        { return Decline("The unfinished-change record is unreadable. Nothing was changed. Details: " + e.Message); }
        if (j is null)
        {
            CleanupOrphanStages();
            return new ApplyResult { Success = true, Message = "Nothing to restore." };
        }
        try { JournalGuard.Validate(j, _root); }
        catch (JournalException e) { return Decline("The unfinished-change record does not look like one this manager wrote, so nothing was changed. " + e.Message); }
        try
        {
            if (j.Status == TxnStatus.Committed)
            {
                TransactionStore.Archive(_root, j);
                CleanupStage(j.StageDir);
                return new ApplyResult { Success = true, Txn = j.Txn, Message = "The earlier change had finished; its record was filed." };
            }
        }
        catch (PathRuleException e) { return Decline("The manager's own folders could not be used safely (" + e.Message + "), so nothing was changed."); }
        // Planned/Applying/RollingBack/NeedsAttention: go back to the state before the change.
        return RollBack(j, j.FailureReason ?? "The earlier change was interrupted.");
    }

    /// <summary>The manager's work folder must exist and be a plain folder reached without links. Returns false with a message (or null = nothing recorded at all).</summary>
    bool ManagerDirIsUsable(out string? problem)
    {
        problem = null;
        var dir = TransactionStore.ManagerDir(_root);
        if (!Directory.Exists(dir)) return false;
        try { LoaderPath.NoReparse(_root, dir); }
        catch (PathRuleException e) { problem = "The manager's work folder is reached through a link (" + e.Message + "), so nothing was changed."; return false; }
        return true;
    }

    /// <summary>Undo a finished change if the files are still exactly as that change left them. The lock is taken before anything is read.</summary>
    public ApplyResult UndoCommitted(string txn)
    {
        var pr = _probe.Check(_root);
        if (!pr.SafeToChange) return Decline(pr.Message);
        if (!ManagerDirIsUsable(out var problem)) return Decline(problem ?? "That change is not in the history.");
        using var lockFs = TryLock();
        if (lockFs is null) return Decline("Another copy of the manager seems to be using this folder.");
        if (File.Exists(TransactionStore.CurrentPath(_root))) return Decline("An earlier change did not finish. Restore the previous state first.");
        var j = TransactionStore.History(_root).FirstOrDefault(x => x.Txn == txn);
        if (j is null) return Decline("That change is not in the history.");
        if (j.Status != TxnStatus.Committed) return Decline("Only a finished change can be undone this way.");
        try { JournalGuard.Validate(j, _root); }
        catch (JournalException e) { return Decline("That change's record does not look like one this manager wrote, so nothing was changed. " + e.Message); }
        foreach (var op in j.Ops)
            if (Locate(op) != Where.After)
                return Decline($"Files have changed since that change was made (\"{Path.GetFileName(op.Path)}\"), so it cannot be undone safely. Nothing was changed.");
        foreach (var op in j.Ops) op.State = OpState.Done;
        j.Status = TxnStatus.RollingBack;
        j.FailureReason = "Undone by the user.";
        TransactionStore.SaveCurrent(_root, j);
        try
        {
            UndoAll(j);
            j.Status = TxnStatus.RolledBack;
            j.FinishedUtc = DateTime.UtcNow;
            // Keep the original record and add the undo record next to it.
            TransactionStore.WriteHistoryFile(_root, j.Txn, "journal-undone.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(j, TransactionStore.Json));
            File.Delete(TransactionStore.CurrentPath(_root));
            return new ApplyResult { Success = true, Txn = j.Txn, Message = "The change was undone." };
        }
        catch (SimulatedCrash) { throw; }
        catch (Exception e)
        {
            try { j.Status = TxnStatus.NeedsAttention; TransactionStore.SaveCurrent(_root, j); } catch { }
            return new ApplyResult { NeedsAttention = true, Txn = j.Txn, Message = "Undo stopped part-way: " + e.Message + " Use \"Restore previous state\" to finish; nothing has been deleted." };
        }
    }
    /// <summary>With no unfinished change on record, any staged copy left behind by an interrupted run is just temporary data.</summary>
    void CleanupOrphanStages()
    {
        var root = TransactionStore.StageRoot(_root);
        if (!Directory.Exists(root) || File.Exists(TransactionStore.CurrentPath(_root)) || LoaderPath.IsReparse(root)) return;
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            if (LoaderPath.IsReparse(d)) continue;
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void CleanupStage(string stage)
    {
        // The stage folder only ever holds the manager's own temporary copies.
        if (!Directory.Exists(stage)) return;
        if (!LoaderPath.IsInside(TransactionStore.StageRoot(_root), stage) || !LoaderPath.IsPlainInside(_root, stage)) return;
        try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class ReplaceRaceException(string message) : IOException(message);

/// <summary>
/// Replaces a file with new content in a way that keeps whatever was really there: the swap is atomic (File.Replace), the displaced
/// file is kept next to it and checked against what the caller expected. If it is something else (written by another program after
/// the caller looked), the swap is reversed and nothing of anyone's is lost.
/// </summary>
static class SafeReplace
{
    public static void Run(string source, string dest, string expectedDisplacedSha, string displacedPath, string preserveDir, Action<string>? at = null)
    {
        var ourSha = Sha.OfFile(source);
        Directory.CreateDirectory(preserveDir);   // before anything is swapped: if this fails, nothing has happened yet
        File.Replace(source, dest, displacedPath, ignoreMetadataErrors: true);
        at?.Invoke("replace:swapped");
        if (Sha.OfFile(displacedPath) == expectedDisplacedSha) { File.Delete(displacedPath); return; }   // the displaced copy is an exact duplicate of a verified backup
        // Not what we expected: swap back. The swap-back's own "displaced" file is written straight into the manager's backup folder, never to
        // a staging or temporary path that gets cleaned up, so whatever sat at dest at that instant is durable from the moment it moves.
        // After this call dest holds the other program's first version again.
        var held = Path.Combine(preserveDir, Path.GetFileName(dest) + ".swapback-" + Guid.NewGuid().ToString("N")[..8]);
        File.Replace(displacedPath, dest, held, ignoreMetadataErrors: true);
        var kept = "";
        if (File.Exists(held))
        {
            if (Sha.OfFile(held) == ourSha) File.Delete(held);   // hash-proven to be the manager's own staged bytes
            else
            {
                // Somebody's newer version: keep it, under a clearer name if that works, otherwise under the name it already has.
                var safe = Path.Combine(preserveDir, Path.GetFileName(dest) + ".external-" + Guid.NewGuid().ToString("N")[..8]);
                try { at?.Invoke("replace:preserve"); File.Move(held, safe); held = safe; } catch (IOException) { }
                kept = $" A second version written at the same moment was kept as \"{held}\".";
            }
        }
        throw new ReplaceRaceException($"\"{Path.GetFileName(dest)}\" was changed by another program at the moment it was being replaced. Their version was kept and nothing was lost." + kept);
    }
}

static class Retry
{
    public static void Io(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException e) when (i < 2 && e is not InjectedFailure && e is not ExternalChangeException && !(e is FileNotFoundException) && !(e is DirectoryNotFoundException))
            {
                // Typically a virus scanner or indexer holding the file for a moment.
                Thread.Sleep(120 * (i + 1));
            }
        }
    }
}

static class Assert
{
    public static void InsideStage(string stage, string path)
    {
        if (!LoaderPath.IsInside(stage, path)) throw new PathRuleException("A staged path escaped the staging folder.");
    }

    public static void SameVolume(string a, string b)
    {
        if (!string.Equals(LoaderPath.VolumeOf(a), LoaderPath.VolumeOf(b), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The source and destination are on different drives; the manager only does instant same-drive moves so it can always undo them.");
    }

    /// <summary>Everything a step touches must be inside the game folder's mods or Resource tree and reached without links.</summary>
    public static void Scope(string root, JournalOp op)
    {
        foreach (var p in new[] { op.Path, op.From, op.BackupPath, op.UndoPath, op.DisplacedPath })
        {
            if (p is null) continue;
            var allowedMods = Path.Combine(root, "mods");
            var allowedRes = Path.Combine(root, "Resource");
            if (!(LoaderPath.IsInside(allowedMods, p) || LoaderPath.IsInside(allowedRes, p)))
                throw new PathRuleException($"A step would touch a location outside the manager's allowed folders: {p}");
            LoaderPath.NoReparse(root, p);
        }
    }
}

/// <summary>Content fingerprints for folders: every relative name with the SHA-256 of its content, never following links. Any added, removed or edited file (even a same-size edit) changes it.</summary>
public static class Fingerprint
{
    public static string OfTree(string dir)
    {
        var lines = new List<string>();
        Walk(dir, dir, lines);
        lines.Sort(StringComparer.Ordinal);
        return Sha.OfBytes(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }

    static void Walk(string root, string dir, List<string> lines)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0) throw new PathRuleException("A link inside a folder was found: " + e.FullName);
            var rel = Path.GetRelativePath(root, e.FullName);
            if (e is DirectoryInfo d) { lines.Add("D|" + rel); Walk(root, d.FullName, lines); }
            else if (e is FileInfo f) lines.Add($"F|{rel}|{f.Length}|{Sha.OfFile(f.FullName)}");
        }
    }
}

/// <summary>Verified copying: refuses links, hashes while reading, reads back what it wrote.</summary>
public static class SafeCopy
{
    public static void File(string source, string dest, CancellationToken ct)
    {
        if (LoaderPath.IsReparse(source)) throw new PathRuleException("Refusing to copy a link: " + source);
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                h.AppendData(buf, 0, n);
                dst.Write(buf, 0, n);
            }
            dst.Flush(true);
        }
        var expected = Convert.ToHexStringLower(h.GetHashAndReset());
        if (Sha.OfFile(dest, ct: ct) != expected) throw new IOException("A copied file did not verify: " + Path.GetFileName(dest));
        System.IO.File.SetLastWriteTimeUtc(dest, System.IO.File.GetLastWriteTimeUtc(source));
    }

    public static void Tree(string source, string dest, CancellationToken ct)
    {
        if (LoaderPath.IsReparse(source)) throw new PathRuleException("Refusing to copy a link: " + source);
        Directory.CreateDirectory(dest);
        foreach (var e in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0) throw new PathRuleException("Refusing to copy a link: " + e.FullName);
            var target = Path.Combine(dest, e.Name);
            if (e is DirectoryInfo d) Tree(d.FullName, target, ct);
            else File(e.FullName, target, ct);
        }
        Directory.SetLastWriteTimeUtc(dest, Directory.GetLastWriteTimeUtc(source));
    }
}
