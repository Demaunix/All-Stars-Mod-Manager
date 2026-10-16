<#
  Zips the two built folders (run tools\build.ps1 first):
      dist\AllStarsModManager  ->  dist-zip\AllStarsModManager-<version>.zip      (top folder "AllStarsModManager")
      dist\AllStarsModCheck    ->  dist-zip\AllStarsModCheck-<version>.zip        (top folder "AllStarsModCheck")
  A third archive, AllStarsModManager-<version>-source.zip (top folder "All-Stars-Mod-Manager"), holds exactly the source files listed in dist\SOURCE-MANIFEST.json, so the\n  source tree hash recorded in BUILD-INFO.json can be recomputed from it.\n  Entry order (ordinal) and time stamps are fixed, so the same input gives the same archive bytes. Writes dist-zip\PACKAGE-INFO.json with each
  archive's SHA-256 and every entry's SHA-256, plus the source tree hash both were built from.
  Usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'dist-zip'
New-Item -ItemType Directory -Force $outDir | Out-Null
$stamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)     # fixed: archive bytes depend only on the files

$products = @(
    @{ Name = 'AllStarsModManager'; Exe = 'AllStarsModManager.exe' },
    @{ Name = 'AllStarsModCheck';   Exe = 'AllStarsModCheck.exe' }
)
$report = [ordered]@{ archives = @() }
foreach ($p in $products) {
    $dist = Join-Path $root "dist\$($p.Name)"
    if (-not (Test-Path (Join-Path $dist $p.Exe))) { throw "build first (tools\build.ps1): $($p.Exe) not found in $dist" }
    $info = Get-Content -LiteralPath (Join-Path $dist 'BUILD-INFO.json') -Raw | ConvertFrom-Json
    $zip = Join-Path $outDir "$($p.Name)-$($info.version).zip"
    if (Test-Path -LiteralPath $zip) { [IO.File]::Delete($zip) }
    $files = Get-ChildItem -LiteralPath $dist -Recurse -File | ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($dist.Length + 1).Replace('\', '/'); Full = $_.FullName; Length = $_.Length } }
    $list = [System.Collections.Generic.List[object]]::new($files)
    $list.Sort([Comparison[object]]{ param($a, $b) [string]::CompareOrdinal($a.Rel, $b.Rel) })
    $entries = @()
    $fs = [IO.File]::Create($zip)
    try {
        $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
        foreach ($f in $list) {
            $name = $p.Name + '/' + $f.Rel
            $e = $za.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $e.LastWriteTime = $stamp
            $in = [IO.File]::OpenRead($f.Full); $o = $e.Open()
            try { $in.CopyTo($o) } finally { $o.Dispose(); $in.Dispose() }
            $entries += [pscustomobject]@{ path = $name; bytes = $f.Length; sha256 = (Get-FileHash -LiteralPath $f.Full -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $za.Dispose()
    } finally { $fs.Dispose() }
    $report.archives += [ordered]@{
        product = $info.product
        version = $info.version
        archive = (Split-Path $zip -Leaf)
        archive_sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        archive_bytes = (Get-Item -LiteralPath $zip).Length
        source_tree_sha256 = $info.source_tree_sha256
        core_library_sha256 = $info.core_library_sha256
        entry_count = $entries.Count
        entries = $entries
    }
    Write-Host ("{0}  {1} bytes  {2} entries" -f (Split-Path $zip -Leaf), (Get-Item -LiteralPath $zip).Length, $entries.Count)
    Write-Host ("  sha256 {0}" -f $report.archives[-1].archive_sha256)
}
# ---- the source archive: exactly the files of dist\SOURCE-MANIFEST.json --------------------------------------------------------------
$manifestPath = Join-Path $root 'dist\SOURCE-MANIFEST.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$infoSrc = (Get-Content -LiteralPath (Join-Path $root 'dist\AllStarsModManager\BUILD-INFO.json') -Raw | ConvertFrom-Json)
$srcZip = Join-Path $outDir "AllStarsModManager-$($infoSrc.version)-source.zip"
if (Test-Path -LiteralPath $srcZip) { [IO.File]::Delete($srcZip) }
$srcList = [System.Collections.Generic.List[object]]::new(@($manifest))
$srcList.Sort([Comparison[object]]{ param($a, $b) [string]::CompareOrdinal($a.path, $b.path) })
$srcEntries = @()
$fs = [IO.File]::Create($srcZip)
try {
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($f in $srcList) {
        $full = Join-Path $root ($f.path -replace '/', '\')
        $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $f.sha256) { throw "$($f.path) changed since the build (manifest $($f.sha256), file $actual); run tools\build.ps1 again" }
        $e = $za.CreateEntry('All-Stars-Mod-Manager/' + $f.path, [IO.Compression.CompressionLevel]::Optimal)
        $e.LastWriteTime = $stamp
        $in = [IO.File]::OpenRead($full); $o = $e.Open()
        try { $in.CopyTo($o) } finally { $o.Dispose(); $in.Dispose() }
        $srcEntries += [pscustomobject]@{ path = 'All-Stars-Mod-Manager/' + $f.path; bytes = $f.bytes; sha256 = $f.sha256 }
    }
    $za.Dispose()
} finally { $fs.Dispose() }
$report.archives += [ordered]@{
    product = 'All-Stars Mod Manager source'
    version = $infoSrc.version
    archive = (Split-Path $srcZip -Leaf)
    archive_sha256 = (Get-FileHash -LiteralPath $srcZip -Algorithm SHA256).Hash.ToLowerInvariant()
    archive_bytes = (Get-Item -LiteralPath $srcZip).Length
    source_tree_sha256 = $infoSrc.source_tree_sha256
    entry_count = $srcEntries.Count
    entries = $srcEntries
}
Write-Host ("{0}  {1} bytes  {2} entries" -f (Split-Path $srcZip -Leaf), (Get-Item -LiteralPath $srcZip).Length, $srcEntries.Count)
Write-Host ("  sha256 {0}" -f $report.archives[-1].archive_sha256)
[IO.File]::WriteAllText((Join-Path $outDir 'PACKAGE-INFO.json'), ($report | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))