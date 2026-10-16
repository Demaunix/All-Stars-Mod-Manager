<#
  Mutation sanity check: proves that the safety tests can fail. For each mutant, a scratch copy of the source gets one deliberate
  defect, is built, and the relevant test areas must report at least one failure (killed). An unmutated copy is run first as a control.
  Usage: powershell -File tools\mutation-check.ps1
#>
param([string[]]$Only = @(), [string]$Label = 'full')
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not $env:ALLSTARS_TEST_WORK) { $env:ALLSTARS_TEST_WORK = Join-Path $root 'work\fixtures' }     # (git-ignored)
$env:DOTNET_CLI_HOME = Join-Path $root 'work\dotnet-home'; $env:NUGET_PACKAGES = Join-Path $root 'work\nuget'; $env:DOTNET_NOLOGO = '1'
$outDir = Join-Path $root 'tests\results\mutation'
New-Item -ItemType Directory -Force $outDir | Out-Null

# id, file (relative to src), find (exact text), replace, test filters to run
$mutants = @(
  @{ id='M01-shared-rtsc-allowed'; file='Core\Validation\Conflicts.cs'; find='if (a.G.Format == "rtsc_merge" && b.G.Format == "rtsc_merge") {'; repl='if (a.G.Format == "rtsc_merge" && b.G.Format == "rtsc_merge") { return;'; filters=@('conflicts','loader-parity') },
  @{ id='M02-identical-member-allowed'; file='Core\Validation\Conflicts.cs'; find='if (shared.Count > 0)'; repl='if (shared.Count > 0 && false)'; filters=@('conflicts','loader-parity') },
  @{ id='M03-numeric-key-misnormalised'; file='Core\Packages\PackageInspector.cs'; find='if (j.Kind == JKind.Number) return j.U32();'; repl='if (j.Kind == JKind.Number) return j.U32() ^ 1u;'; filters=@('conflicts','loader-parity') },
  @{ id='M04-case-sensitive-targets'; file='Core\Packages\PackageInspector.cs'; find='pg.LogicalKey = LoaderPath.Fold(rel);'; repl='pg.LogicalKey = rel;'; filters=@('conflicts','loader-parity') },
  @{ id='M05-file-vs-archive-ignored'; file='Core\Validation\Conflicts.cs'; find='else if (ax != bx && (a.G.Format == "file" || b.G.Format == "file")) {'; repl='else if (ax != bx && (a.G.Format == "file" || b.G.Format == "file")) { return;'; filters=@('conflicts','loader-parity') },
  @{ id='M06-duplicate-id-allowed'; file='Core\Validation\Conflicts.cs'; find='GroupBy(c => c.Id!, StringComparer.Ordinal).Where(g => g.Count() > 1))'; repl='GroupBy(c => c.Id!, StringComparer.Ordinal).Where(g => g.Count() > 1000))'; filters=@('conflicts','loader-parity') },
  @{ id='M07-undo-moves-nothing'; file='Core\Transactions\TransactionEngine.cs'; find='Retry.Io(() => Directory.Move(op.Path, dest));'; repl='/* mutated: undo does nothing */'; filters=@('transaction-faults') },
  @{ id='M08-external-change-check-off'; file='Core\Transactions\TransactionEngine.cs'; find='if (compute is not null && compute() != expectedToken)'; repl='if (compute is not null && false)'; filters=@('transactions') },
  @{ id='M09-game-running-guard-off'; file='Core\Transactions\TransactionEngine.cs'; find='if (!pr.SafeToChange) return Decline(pr.Message);
        if (File.Exists(TransactionStore.CurrentPath(_root)))'; repl='if (File.Exists(TransactionStore.CurrentPath(_root)))'; filters=@('transactions') },
  @{ id='M10-zip-traversal-allowed'; file='Core\Library\SafeZip.cs'; find='if (s == "." || s == "..") throw'; repl='if (s == ".") throw'; filters=@('import') },
  @{ id='M11-links-not-detected'; file='Core\Common\LoaderPath.cs'; find='public static bool IsReparse(string path) => TryAttributes(path) is { } a && (a & FileAttributes.ReparsePoint) != 0;'; repl='public static bool IsReparse(string path) => false;'; filters=@('transactions','packages','import') },
  @{ id='M12-asset-overwrite-allowed'; file='Core\Transactions\TransactionEngine.cs'; find='throw new ExternalChangeException($"{Path.GetFileName(op.Path)} appeared before it could be added; the manager never overwrites files.");'; repl='File.Delete(op.Path);'; filters=@('transactions','transaction-faults') },
  @{ id='M13-config-edit-drops-line-spacing'; file='Core\Formats\LoaderIni.cs'; find='l.Text = l.Text.Substring(0, l.ValueStart) + value;'; repl='l.Text = l.Key + "=" + value;'; filters=@('formats') },
  @{ id='M14-lowercase-pin-not-enforced'; file='Core\Common\Hashing.cs'; find="foreach (var c in s) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;`n        return true;"; repl="foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;`n        return true;"; filters=@('formats','packages') },
  @{ id='M16-journal-guard-off'; file='Core\Transactions\JournalGuard.cs'; find='public static void Validate(Journal j, string root)
    {'; repl='public static void Validate(Journal j, string root)
    {
        return;'; filters=@('transactions','transaction-faults') },
  @{ id='M17-fingerprint-ignores-content'; file='Core\Transactions\TransactionEngine.cs'; find='lines.Add($"F|{rel}|{f.Length}|{Sha.OfFile(f.FullName)}");'; repl='lines.Add($"F|{rel}|{f.Length}");'; filters=@('transactions','transaction-faults') },
  @{ id='M18-rename-undo-ignores-content'; file='Core\Transactions\TransactionEngine.cs'; find='if (to && !from) return op.PreSha is null || Sha.OfFile(op.Path) == op.PreSha ? Where.After : Where.Unknown;'; repl='if (to && !from) return Where.After;'; filters=@('transactions','transaction-faults') },
  @{ id='M19-zip-checksum-ignored'; file='Core\Library\SafeZip.cs'; find='if (crc != e.Crc32) throw'; repl='if (false) throw'; filters=@('import') },
  @{ id='M20-encrypted-zip-not-detected'; file='Core\Library\SafeZip.cs'; find='if (ZipHeaders.HasEncryptedEntry(zipPath))'; repl='if (false)'; filters=@('import') },
  @{ id='M21-replace-overwrites-race'; file='Core\Transactions\TransactionEngine.cs'; find='SafeReplace.Run(op.From!, op.Path, op.PreSha!, op.DisplacedPath ?? throw new InvalidOperationException("The displaced-file name was not recorded."), Path.GetDirectoryName(op.BackupPath!)!, _hooks.At);'; repl='File.Move(op.From!, op.Path, overwrite: true);'; filters=@('transaction-faults') },
  @{ id='M28-displaced-leftover-not-kept'; file='Core\Transactions\TransactionEngine.cs'; find='KeepDisplacedLeftovers(op);'; repl='/* mutant */'; filters=@('transaction-faults') },
  @{ id='M29-swap-back-deletes-held-without-hash-check'; file='Core\Transactions\TransactionEngine.cs'; find='if (Sha.OfFile(held) == ourSha) File.Delete(held);'; repl='if (true) File.Delete(held);'; filters=@('transaction-faults') },
  @{ id='M32-undo-skips-scope-and-link-check'; file='Core\Transactions\TransactionEngine.cs'; find='Assert.Scope(_root, op);   // before anything is inspected, moved or collected: allowed folders only, and no link anywhere on the way'; repl='/* mutant */'; filters=@('transaction-faults') },
  @{ id='M30-parking-name-not-validated'; file='Core\Transactions\JournalGuard.cs'; find='if (op.Kind != OpKind.ReplaceFile || !shaped || !Same('; repl='if (false && !Same('; filters=@('transaction-faults') },
  @{ id='M31-leftovers-collected-by-pattern'; file='Core\Transactions\TransactionEngine.cs'; find='File.Move(op.DisplacedPath, dest);'; repl='foreach (var lf in Directory.GetFiles(Path.GetDirectoryName(op.Path)!, Path.GetFileName(op.Path) + ".displaced-??????")) File.Move(lf, Path.Combine(keepDir, Path.GetFileName(lf) + ".x"));'; filters=@('transaction-faults') },
  @{ id='M27-swap-back-discards-newest-outside-write'; file='Core\Transactions\TransactionEngine.cs'; find='try { at?.Invoke("replace:preserve"); File.Move(held, safe); held = safe; } catch (IOException) { }'; repl='File.Delete(held);'; filters=@('transaction-faults') },
  @{ id='M22-undo-reads-before-lock'; file='Core\Transactions\TransactionEngine.cs'; find='        using var lockFs = TryLock();
        if (lockFs is null) return Decline("Another copy of the manager seems to be using this folder.");
        if (File.Exists(TransactionStore.CurrentPath(_root))) return Decline("An earlier change did not finish. Restore the previous state first.");'; repl='        if (File.Exists(TransactionStore.CurrentPath(_root))) return Decline("An earlier change did not finish. Restore the previous state first.");'; filters=@('transaction-faults') },
  @{ id='M23-storage-writes-follow-links'; file='Core\Transactions\Journal.cs'; find='        LoaderPath.NoReparse(gameRoot, path);
        var mgr = ManagerDir(gameRoot);'; repl='        var mgr = ManagerDir(gameRoot);'; filters=@('transaction-faults') },
  @{ id='M24-stale-settings-snapshot-accepted'; file='Core\Transactions\Planner.cs'; find='if (loadedSha is not null && pre != loadedSha)'; repl='if (false)'; filters=@('transaction-faults') },
  @{ id='M25-install-keeps-disabled-manifest-name'; file='Core\Transactions\Planner.cs'; find='ActivateManifest = c.Pkg.Manifest!.State == ManifestState.Active ? null : c.Pkg.Manifest.FileName'; repl='ActivateManifest = null'; filters=@('transaction-faults') },
  @{ id='M26-shared-extra-file-staged-twice'; file='Core\Transactions\Planner.cs'; find='if (!plannedCreates.Add(key))'; repl='if (false)'; filters=@('transaction-faults') },  @{ id='M15-recovery-overwrites-unknown-state'; file='Core\Transactions\TransactionEngine.cs'; find='else if (where == Where.Unknown)
                throw new ExternalChangeException'; repl='else if (where == Where.Unknown && false)
                throw new ExternalChangeException'; filters=@('transaction-faults') },
  # ---- format 1 / checker mutants ----
  @{ id='C01-range-partial-accept'; file='Core\Packages\VersionSpec.cs'; find='if (v is null) { error = $"\"{Printable(spec[start..i])}\" is not a version"; return null; }'; repl='if (v is null) { continue; }'; filters=@('compat') },
  @{ id='C02-digit-length-ignored'; file='Core\Packages\VersionSpec.cs'; find='if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;'; repl='/* mutant */'; filters=@('compat') },
  @{ id='C03-prerelease-above-release'; file='Core\Packages\VersionSpec.cs'; find='if (x.Pre.Length == 0) return 1;     // a release is newer than any pre-release of it'; repl='if (x.Pre.Length == 0) return -1;'; filters=@('compat') },
  @{ id='C04-sidecar-syntax-gate-off'; file='Core\Packages\SidecarReader.cs'; find='try { StrictJson.Parse(bytes); }'; repl='try { }'; filters=@('compat') },
  @{ id='C05-object-shorthand-dropped'; file='Core\Packages\SidecarReader.cs'; find='case JsonValueKind.Object: list.Add(v); break;'; repl='case JsonValueKind.Object: break;'; filters=@('compat') },
  @{ id='C06-invalid-relation-passes'; file='Core\Packages\SidecarReader.cs'; find='            Invalid = invalid
        });'; repl='            Invalid = null
        });'; filters=@('compat') },
  @{ id='C07-future-schema-applied'; file='Core\Packages\SidecarReader.cs'; find='if (!sc.Interpretable) return sc;     // declarations are not read at all: nothing partial is ever applied'; repl='/* mutant */'; filters=@('compat') },
  @{ id='C08-pe-machine-not-checked'; file='Core\Packages\PackageInspector.cs'; find='if (!pe.IsX86)'; repl='if (false)'; filters=@('compat') },
  @{ id='C09-missing-exports-not-flagged'; file='Core\Packages\PackageInspector.cs'; find='var missing = RequiredRuntimeExports.Where(n => !pe.ExportNames.Contains(n, StringComparer.Ordinal)).ToList();'; repl='var missing = new List<string>();'; filters=@('compat') },
  @{ id='C10-load-order-never-violated'; file='Core\Validation\Conflicts.cs'; find='bool ok = mustBeAfter ? Pos(c) > Pos(other) : Pos(c) < Pos(other);'; repl='bool ok = true;'; filters=@('compat') },
  @{ id='C11-loader-unconfirmed-dropped'; file='Core\Validation\Conflicts.cs'; find='            if (unknown.Count > 0)'; repl='            if (false)'; filters=@('compat') },
  @{ id='C12-loader-recognised-by-either-hash'; file='Core\Game\LoaderProfiles.cs'; find='.FirstOrDefault(p => p.HostSha256 == hostSha && p.ProxySha256 == proxySha);'; repl='.FirstOrDefault(p => p.HostSha256 == hostSha || p.ProxySha256 == proxySha);'; filters=@('compat') },
  @{ id='C13-verdict-ignores-unknown'; file='Core\Compat\CheckRunner.cs'; find='report.Unknowns > 0 ? CheckVerdict.InsufficientInformation'; repl='false ? CheckVerdict.InsufficientInformation'; filters=@('compat') },
  @{ id='C14-declared-conflict-out-of-range-flagged'; file='Core\Validation\Conflicts.cs'; find='if (v == VersionCheck.NotSatisfied) continue;'; repl='if (false) continue;'; filters=@('compat') },
  @{ id='C15-unreadable-rule-only-warns'; file='Core\Packages\PackageInspector.cs'; find='pkg.Issues.Add(new(p.Safety && p.Blocking ? Severity.Blocker : Severity.Warning, code,'; repl='pkg.Issues.Add(new(Severity.Warning, code,'; filters=@('compat') },
  @{ id='C16-damaged-description-ignored'; file='Core\Packages\SidecarReader.cs'; find='catch (PlanFormatException e) { issues.Add(Unreadable("it is not plain JSON in the subset the game loader''s own reader accepts (" + PackageInfo.Clean(e.Message, 100) + ")")); return null; }'; repl='catch (PlanFormatException) { return null; }'; filters=@('compat') },
  @{ id='C17-native-unknown-only-a-note'; file='Core\Compat\CheckRunner.cs'; find='Impact = n.Code is "native-unknown" or "free-text-conflict" ? "unknown" : "note",'; repl='Impact = "note",'; filters=@('compat') },
  @{ id='C18-target-build-match-ignored'; file='Core\Validation\Conflicts.cs'; find='if (actual is not null && sc.TargetExecutableSha256.Contains(actual)) continue;'; repl='if (true) continue;'; filters=@('compat') },
  @{ id='C20-explicit-null-read-as-absent'; file='Core\Packages\SidecarReader.cs'; find='return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v);'; repl='return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;'; filters=@('compat') },
  @{ id='C21-export-address-not-checked'; file='Core\Packages\PackageInspector.cs'; find='var noCode = RequiredRuntimeExports.Where(n => pe.ExportKinds.TryGetValue(n, out var k) && k is ExportKind.NoAddress or ExportKind.Invalid).ToList();'; repl='var noCode = new List<string>();'; filters=@('compat') },
  @{ id='C22-added-packages-keyed-by-folder-name'; file='Core\Compat\CheckRunner.cs'; find='Key = $"add:{++addNo}"'; repl='Key = "add:" + pkg.FolderName'; filters=@('compat') },
  @{ id='C23-free-text-conflict-note-dropped'; file='Core\Validation\Conflicts.cs'; find='fs.FreeTextConflicts.Count > 0 &&'; repl='fs.FreeTextConflicts.Count > 1000 &&'; filters=@('compat') },
  @{ id='C24-paths-not-scrubbed'; file='Core\Compat\CheckRunner.cs'; find='f.Detail = Sc(f.Detail)!;'; repl='f.Detail = f.Detail;'; filters=@('compat') },
  @{ id='C25-duplicate-candidate-keys-allowed'; file='Core\Validation\ActiveSetValidator.cs'; find='if (intended.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != intended.Count) throw new ArgumentException("Candidate keys must be unique.", nameof(intended));'; repl='/* mutant */'; filters=@('compat') },
  @{ id='C26-folder-name-collision-not-reported'; file='Core\Compat\CheckRunner.cs'; find='.Where(g => g.Count() > 1 && g.Any(e => e.State == "would-be-added")).ToList())'; repl='.Where(g => g.Count() > 1000 && g.Any(e => e.State == "would-be-added")).ToList())'; filters=@('compat') },
  @{ id='C27-unsorted-export-names-accepted'; file='Core\Packages\PeInspector.cs'; find='else if (previousName is not null && previousName.AsSpan().SequenceCompareTo(nameBytes) >= 0) r.ExportNamesSorted = false;'; repl='else if (false) r.ExportNamesSorted = false;'; filters=@('compat') },
  @{ id='C28-start-order-not-scrubbed'; file='Core\Compat\CheckRunner.cs'; find='report.StartOrder = report.StartOrder.Select(x => Sc(x)!).ToList();'; repl='/* mutant */'; filters=@('compat') },
  @{ id='C29-input-error-text-not-scrubbed'; file='Core\Compat\CheckCli.cs'; find='error.WriteLine("AllStarsModCheck: " + CheckOutput.ScrubPaths(e.Message, ErrorRoots(o)));'; repl='error.WriteLine("AllStarsModCheck: " + e.Message);'; filters=@('compat') },
  @{ id='C30-non-ascii-export-names-accepted'; file='Core\Packages\PeInspector.cs'; find='if (nameBytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0) r.ExportNamesSorted = false;'; repl='if (false) r.ExportNamesSorted = false;'; filters=@('compat') },
  @{ id='C31-unknown-option-value-echoed'; file='Core\Compat\CheckCli.cs'; find='? $"Unknown option {name}." : "Unknown option (what was typed is not shown).";'; repl='? $"Unknown option {arg}." : $"Unknown option {arg}.";'; filters=@('compat') },
  @{ id='C32-web-address-not-kept'; file='Core\Compat\CheckOutput.cs'; find='@"(?<url>(?<![A-Za-z0-9+.\-])(?<scheme>[A-Za-z][A-Za-z0-9+.\-]{1,31})://[^\s""''<>]*)" +
        @"|(?<q>'; repl='@"(?<q>'; filters=@('compat') },
  @{ id='C33-unc-name-ascii-only'; file='Core\Compat\CheckOutput.cs'; find='\\\\[^\s\\/""''<>|]+|//'; repl='\\\\[A-Za-z0-9._$-]+\\[A-Za-z0-9._$-]+|//'; filters=@('compat') },
  @{ id='C34-quoted-path-ends-at-space'; file='Core\Compat\CheckOutput.cs'; find='@"|(?<q>"")" + PathStart + @"[^""\r\n]*""" +'; repl=''; filters=@('compat') },
  @{ id='C37-apostrophe-ends-path'; file='Core\Compat\CheckOutput.cs'; find='@"[^\r\n]*''(?=[\s.,;:!?)\]}]|$)"'; repl='@"[^\r\n]*?''(?=[\s.,;:!?)\]}]|$)"'; filters=@('compat') },
  @{ id='C38-folder-description-absent'; file='Core\Packages\SidecarReader.cs'; find='if ((attributes & FileAttributes.Directory) != 0) { issues.Add(Unreadable("it is a folder, not a file")); return null; }'; repl='if ((attributes & FileAttributes.Directory) != 0) return null;'; filters=@('compat') },
  @{ id='C39-linked-description-ignored'; file='Core\Packages\SidecarReader.cs'; find='if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add(Unreadable("it is a link; only a real file inside the mod folder is read")); return null; }'; repl='if ((attributes & FileAttributes.ReparsePoint) != 0) return null;'; filters=@('compat') },
  @{ id='C35-file-address-kept'; file='Core\Compat\CheckOutput.cs'; find='Equals("file", StringComparison.OrdinalIgnoreCase) ? "<path>" : m.Value'; repl='Equals("file", StringComparison.OrdinalIgnoreCase) ? m.Value : m.Value'; filters=@('compat') },
  @{ id='C36-account-name-breaks-path-scan'; file='Core\Compat\CheckOutput.cs'; find='AccountMark.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase'; repl='"<user>", System.Text.RegularExpressions.RegexOptions.IgnoreCase'; filters=@('compat') },
  @{ id='C19-checker-hash-pass-skipped'; file='Core\Compat\CheckRunner.cs'; find='if (!o.Quick)
        {
            o.Progress?.Invoke("Checking file hashes'; repl='if (false)
        {
            o.Progress?.Invoke("Checking file hashes'; filters=@('compat') }
)

# Deletion guard: only module-owned scratch folders (work\mut-*) may ever be removed, and never through a link.
function Assert-ScratchPath([string]$path) {
    $workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work')).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if (-not $full.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to touch ${full}: outside ${workRoot}" }
    if (-not ([IO.Path]::GetFileName($full)).StartsWith('mut-', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to touch ${full}: not a mut-* scratch folder" }
    $cur = $full
    while ($cur.Length -gt $workRoot.Length) {
        if (Test-Path -LiteralPath $cur) {
            $item = Get-Item -LiteralPath $cur -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "refusing to touch ${full}: link at ${cur}" }
        }
        $cur = Split-Path $cur -Parent
    }
}

function Copy-Source([string]$dest) {
    Assert-ScratchPath $dest
    if (Test-Path -LiteralPath $dest) {
        # no link may exist anywhere below the scratch folder either
        if (Get-ChildItem -LiteralPath $dest -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue) { throw "refusing to delete ${dest}: it contains a link" }
        [IO.Directory]::Delete($dest, $true)
    }
    New-Item -ItemType Directory -Force $dest | Out-Null
    & robocopy (Join-Path $root 'src') (Join-Path $dest 'src') /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
}
# A filter whose tests may legitimately all be skipped in a public checkout (the private loader-parity oracle is not part of it).
$mayRunNothing = @('loader-parity')
function Build-And-Run([string]$dir, [string]$mode, [string[]]$filters) {
    $b = & $dotnet build (Join-Path $dir 'src\Tests\AllStarsModManager.Tests.csproj') -c Release --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { return @{ built = $false; valid = $false; failed = -1; passed = 0; why = 'does not compile'; text = ($b | Select-Object -Last 5) -join ' | ' } }
    $exe = Join-Path $dir 'src\Tests\bin\Release\net10.0-windows\AllStarsModManager.Tests.exe'
    $failed = 0; $passed = 0; $names = @(); $valid = $true; $why = @()
    foreach ($f in $filters) {
        $o = & $exe $mode $f 2>&1
        $code = $LASTEXITCODE
        $line = $o | Select-String -Pattern '(\d+) passed, (\d+) failed' | Select-Object -Last 1
        if (-not $line) { $valid = $false; $why += "filter '$f': no result summary (exit code $code)"; continue }
        $p = [int]$line.Matches[0].Groups[1].Value; $fl = [int]$line.Matches[0].Groups[2].Value
        if ($code -notin 0, 1) { $valid = $false; $why += "filter '$f': test process ended with exit code $code" }
        if (($code -eq 0) -and ($fl -gt 0)) { $valid = $false; $why += "filter '$f': exit code 0 but failures reported" }
        if (($code -eq 1) -and ($fl -eq 0)) { $valid = $false; $why += "filter '$f': exit code 1 but no failure reported (crash after the summary?)" }
        if (($p + $fl -eq 0) -and ($mayRunNothing -notcontains $f)) { $valid = $false; $why += "filter '$f': no test ran" }
        $passed += $p; $failed += $fl
        $names += ($o | Select-String -Pattern '^\s+FAIL\s+(.*)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() })
    }
    return @{ built = $true; valid = $valid; why = ($why -join '; '); failed = $failed; passed = $passed; failing = ($names | Select-Object -Unique -First 5) }
}
# Every mutant must apply to the current source; check all of them first so a stale text fails in seconds, not after the control run.
$stale = @()
foreach ($m in $mutants) {
    if ($Only.Count -gt 0 -and -not ($Only | Where-Object { $m.id.StartsWith($_) })) { continue }
    $src = ([IO.File]::ReadAllText((Join-Path $root ('src\' + $m.file)))) -replace "`r`n", "`n"
    if (-not $src.Contains(($m.find -replace "`r`n", "`n"))) { $stale += $m.id }
}
if ($stale.Count -gt 0) { throw "mutants whose text is not in the current source: $($stale -join ', ')" }

$results = @()
# control
$ctl = Join-Path $root 'work\mut-control'
Copy-Source $ctl
$c = Build-And-Run $ctl 'mutctl' (@('conflicts','loader-parity','transactions','transaction-faults','import','formats','packages','compat') | Select-Object -Unique)
Write-Host "control (unmutated): built=$($c.built) valid=$($c.valid) passed=$($c.passed) failed=$($c.failed) $($c.why)"
$results += [pscustomobject]@{ id = 'CONTROL-unmutated'; outcome = 'CONTROL'; built = $c.built; valid = $c.valid; passed = $c.passed; failed = $c.failed; killed = $null; failing_tests = @(); note = $c.why }
if (-not $c.built -or -not $c.valid -or $c.failed -ne 0 -or $c.passed -le 0) { throw "control copy does not pass cleanly (built=$($c.built) valid=$($c.valid) passed=$($c.passed) failed=$($c.failed) $($c.why)); mutation results would be meaningless" }

foreach ($m in $mutants) {
    if ($Only.Count -gt 0 -and -not ($Only | Where-Object { $m.id.StartsWith($_) })) { continue }
    $dir = Join-Path $root ("work\mut-" + $m.id)
    Copy-Source $dir
    $path = Join-Path $dir ('src\' + $m.file)
    $text = [IO.File]::ReadAllText($path)
    $find = $m.find -replace "`r`n", "`n"; $textN = $text -replace "`r`n", "`n"
    if (-not $textN.Contains($find)) { throw "mutant $($m.id): text to mutate not found in $($m.file)" }
    $mutated = $textN.Replace($find, ($m.repl -replace "`r`n", "`n"))
    [IO.File]::WriteAllText($path, $mutated)
    $r = Build-And-Run $dir ('mut' + $m.id.Substring(1, 2)) $m.filters
    # Only a mutant that compiles, is run to a valid result and makes an assertion fail counts as killed. A mutant that does not compile, or whose test run
    # is invalid (no summary, crash, no tests executed), is reported as INVALID and fails the check: it says nothing about test sensitivity.
    $outcome = if (-not $r.built) { 'INVALID-BUILD' } elseif (-not $r.valid) { 'INVALID-RUN' } elseif ($r.failed -gt 0) { 'KILLED' } else { 'SURVIVED' }
    Write-Host ("{0,-42} built={1} valid={2} passed={3} failed={4} -> {5} {6}" -f $m.id, $r.built, $r.valid, $r.passed, $r.failed, $outcome, $r.why)
    $results += [pscustomobject]@{ id = $m.id; outcome = $outcome; built = $r.built; valid = $r.valid; passed = $r.passed; failed = $r.failed; killed = ($outcome -eq 'KILLED'); failing_tests = $r.failing; note = $r.why }
}
[IO.File]::WriteAllText((Join-Path $outDir ("mutation-results-$Label.json")), ($results | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
$mut = @($results | Where-Object { $_.outcome -ne 'CONTROL' })
$killedCount = @($mut | Where-Object { $_.outcome -eq 'KILLED' }).Count
$bad = @($mut | Where-Object { $_.outcome -ne 'KILLED' })
Write-Host "mutants run: $($mut.Count); killed (compiled, valid run, assertion failed): $killedCount; survived: $(@($mut | Where-Object { $_.outcome -eq 'SURVIVED' }).Count); invalid: $(@($mut | Where-Object { $_.outcome -like 'INVALID*' }).Count)"
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host (" not killed: {0} {1} {2}" -f $_.id, $_.outcome, $_.note) }; exit 1 }