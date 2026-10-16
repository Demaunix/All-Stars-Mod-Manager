<#
  Builds the portable programs from this source tree:
      dist\AllStarsModManager   the manager (WPF) plus AllStarsModCheck.exe, docs and examples
      dist\AllStarsModCheck     the checker alone (console), docs and examples
  and writes BUILD-INFO.json (inside each folder) and SOURCE-MANIFEST.json (next to dist) that bind the output to the exact source files.

  Everything is compiled from this tree: no pre-built binary is used or compared against. Output does not depend on the folder the tree is in
  (the compiler's source paths are mapped; see src\Directory.Build.props), so a build from a copy of the same tree gives the same Core library bytes.

  Requirements: the .NET 10 SDK (10.0.x) with its Windows Desktop targeting pack, Windows x64. No network access is needed: the project uses no NuGet
  packages and the SDK carries the reference packs. The script keeps the SDK's per-user caches inside <tree>\work so nothing is written to your profile.

  Usage:   powershell -ExecutionPolicy Bypass -File tools\build.ps1            (from anywhere; it locates the tree itself)
#>
[CmdletBinding()]
param([string]$Dotnet = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }     # no byte-order mark, on Windows PowerShell 5.1 and 7

# --- locate the SDK ---------------------------------------------------------------------------------------
function Find-Dotnet([string]$hint) {
    $candidates = @()
    if ($hint) { $candidates += $hint }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return $c } }
    throw 'The .NET SDK was not found. Install the .NET 10 SDK, or pass -Dotnet <path to dotnet.exe>.'
}
$dotnet = Find-Dotnet $Dotnet
$sdk = (& $dotnet --version).Trim()
if (-not $sdk.StartsWith('10.')) { throw "The .NET 10 SDK is required; found $sdk ($dotnet)." }
$env:DOTNET_CLI_HOME = Join-Path $root 'work\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root 'work\nuget'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# --- source tree hash: every file that determines the build and its documentation ------------------------------------
# Rule: SHA-256 over the text "<sha256>  <relative path>\n..." for these files sorted by ordinal path (forward slashes), excluding bin, obj and work.
$srcRoots = @('src', 'tools', 'docs', 'examples', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', '.gitignore', '.gitattributes')
$files = foreach ($r in $srcRoots) {
    $p = Join-Path $root $r
    if (Test-Path -LiteralPath $p -PathType Container) { Get-ChildItem -LiteralPath $p -Recurse -File -Force } elseif (Test-Path -LiteralPath $p -PathType Leaf) { Get-Item -LiteralPath $p -Force }
}
$files = $files | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -notmatch '\\tests\\results\\' }
$entries = foreach ($f in $files) {
    [pscustomobject]@{ path = $f.FullName.Substring($root.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $f.Length }
}
$list = [System.Collections.Generic.List[object]]::new($entries)
$list.Sort([Comparison[object]]{ param($a, $b) [string]::CompareOrdinal($a.path, $b.path) })     # ordinal, so the hash does not depend on the machine's culture
$entries = @($list)
$manifestText = ($entries | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
$sourceHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestText))).Replace('-', '').ToLowerInvariant()
Write-Host "source tree sha256: $sourceHash ($($entries.Count) files)"
$short = $sourceHash.Substring(0, 16)

# --- clean the two output folders (only ours, never through a link) -----------------------------------------------------------
$distBase = [IO.Path]::GetFullPath((Join-Path $root 'dist')).TrimEnd('\')
function Reset-Folder([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($distBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to clean $full" }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "refusing to clean ${full}: it is a link" }
        if (Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue) { throw "refusing to clean ${full}: it contains a link" }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
$distApp = Join-Path $root 'dist\AllStarsModManager'
$distChk = Join-Path $root 'dist\AllStarsModCheck'
Reset-Folder $distApp
Reset-Folder $distChk

# --- publish ---------------------------------------------------------------------------------------------------------------------
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', "-p:SourceHash=$short", '-p:DebugType=none', '-p:DebugSymbols=false', '-p:PublishSingleFile=false', '-p:ContinuousIntegrationBuild=true', '--nologo', '-v', 'q')
# A clean compile first (--no-incremental): a stale obj folder from an earlier build with other settings (for example a debug-symbol build made by the
# test project) must never leak into the shipped files. Then publish without compiling again.
foreach ($proj in @(@{ P = 'src\App\AllStarsModManager.App.csproj'; Out = $distApp; What = 'manager' }, @{ P = 'src\Check\AllStarsModCheck.csproj'; Out = $distChk; What = 'checker' })) {
    $csproj = Join-Path $root $proj.P
    & $dotnet build $csproj @common --no-incremental
    if ($LASTEXITCODE -ne 0) { throw "build of the $($proj.What) failed ($LASTEXITCODE)" }
    & $dotnet publish $csproj @common --no-build -o $proj.Out
    if ($LASTEXITCODE -ne 0) { throw "publish of the $($proj.What) failed ($LASTEXITCODE)" }
}
# --- the Core library must be the same bytes in both outputs (one source, one compile result) ---------------------
$coreA = (Get-FileHash -LiteralPath (Join-Path $distApp 'AllStarsModManager.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$coreC = (Get-FileHash -LiteralPath (Join-Path $distChk 'AllStarsModManager.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($coreA -ne $coreC) { throw "the Core library differs between the two publishes ($coreA vs $coreC); the build is not deterministic" }

# --- the manager package also carries the checker --------------------------------------------------------------------------------
foreach ($n in 'AllStarsModCheck.exe', 'AllStarsModCheck.dll', 'AllStarsModCheck.runtimeconfig.json', 'AllStarsModCheck.deps.json') {
    Copy-Item -LiteralPath (Join-Path $distChk $n) -Destination $distApp -Force
}

# --- documentation and examples next to the programs ----------------------------------------------------------------------------------
function Add-Docs([string]$dest, [string[]]$docNames, [bool]$withImages) {
    foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md') { Copy-Item -LiteralPath (Join-Path $root $f) -Destination $dest -Force }
    New-Item -ItemType Directory -Force (Join-Path $dest 'docs') | Out-Null
    foreach ($d in $docNames) { Copy-Item -LiteralPath (Join-Path $root "docs\$d") -Destination (Join-Path $dest 'docs') -Force }
    if ($withImages -and (Test-Path -LiteralPath (Join-Path $root 'docs\images'))) { Copy-Item -LiteralPath (Join-Path $root 'docs\images') -Destination (Join-Path $dest 'docs') -Recurse -Force }
    # examples: the two packages and the runtime source, not build leftovers
    $ex = Join-Path $dest 'examples'
    New-Item -ItemType Directory -Force $ex | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'examples\README.md') -Destination $ex -Force
    foreach ($m in 'minimal-mod', 'minimal-mod-addon', 'runtime-src') { Copy-Item -LiteralPath (Join-Path $root "examples\$m") -Destination $ex -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $dest 'tools') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'tools\Update-ModHashes.ps1') -Destination (Join-Path $dest 'tools') -Force
}
$allDocs = @('PACKAGE-FORMAT.md', 'AUTHOR-GUIDE.md', 'COMPATIBILITY-CHECKER.md', 'LOADER-INTEGRATION.md', 'ROLLBACK-AND-SAFETY.md', 'PROVENANCE.md', 'BUILDING.md', 'DEVELOPER-GUIDE.md')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $distApp -Force
Add-Docs $distApp $allDocs $true
Copy-Item -LiteralPath (Join-Path $root 'docs\README-CHECK.md') -Destination (Join-Path $distChk 'README.md') -Force
Add-Docs $distChk @('PACKAGE-FORMAT.md', 'AUTHOR-GUIDE.md', 'COMPATIBILITY-CHECKER.md', 'LOADER-INTEGRATION.md', 'PROVENANCE.md') $false

# --- build info -------------------------------------------------------------------------------------------------------------------------------
function Write-BuildInfo([string]$dir, [string]$product, [string]$mainExe) {
    $outFiles = Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object { $_.FullName.Substring($dir.Length + 1).Replace('\', '/') } | Where-Object { $_.Name -ne 'BUILD-INFO.json' } | ForEach-Object {
        [pscustomobject]@{ path = $_.FullName.Substring($dir.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
    }
    $exe = Join-Path $dir $mainExe
    [ordered]@{
        product = $product
        version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
        source_tree_sha256 = $sourceHash
        source_file_count = $entries.Count
        source_hash_rule = 'sha256 of the text "<sha256>  <path>\n" lines for the files in SOURCE-MANIFEST.json, ordinal path order, LF between lines, no trailing LF'
        dotnet_sdk = $sdk
        target = 'net10.0-windows, win-x64, framework-dependent (needs the .NET 10 Windows Desktop Runtime for the manager, the .NET 10 Runtime for the checker)'
        core_library_sha256 = $coreA
        main_executable = $mainExe
        main_executable_sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        network_or_installer = 'none: no service, no auto-update, no installer'
        files = $outFiles
    } | ConvertTo-Json -Depth 5 | ForEach-Object { Write-Utf8 (Join-Path $dir 'BUILD-INFO.json') $_ }
}
Write-BuildInfo $distApp 'All-Stars Mod Manager' 'AllStarsModManager.exe'
Write-BuildInfo $distChk 'All-Stars Mod Check' 'AllStarsModCheck.exe'
Write-Utf8 (Join-Path $root 'dist\SOURCE-MANIFEST.json') ($entries | ConvertTo-Json -Depth 3)
Write-Host "manager : $distApp"
Write-Host "checker : $distChk"
Write-Host "core    : $coreA"
