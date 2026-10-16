<#
  Recomputes the two SHA-256 pins in a mod's mod.ini (plan_sha256 and runtime_sha256) from the files they name, and rewrites only those two lines.
  The game's loader refuses a mod whose pins do not match its files, and then starts the game with no mods at all, so run this after every rebuild
  of the runtime DLL and every edit of the plan.

  Usage:   powershell -File tools\Update-ModHashes.ps1 -ModFolder "C:\path\to\My Mod"
           powershell -File tools\Update-ModHashes.ps1 -ModFolder "C:\path\to\My Mod" -WhatIf      (shows the change, writes nothing)
  Reads and writes only that folder's mod.ini. Hashes are written in lowercase, as the loader requires.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$ModFolder)
$ErrorActionPreference = 'Stop'
$ModFolder = (Resolve-Path -LiteralPath $ModFolder).Path
$iniPath = Join-Path $ModFolder 'mod.ini'
if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { throw "No mod.ini in '$ModFolder'." }

$bytes = [IO.File]::ReadAllBytes($iniPath)
if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'mod.ini starts with a byte-order mark; save it as plain UTF-8 or ANSI first.' }
$text = [Text.Encoding]::UTF8.GetString($bytes)
$nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = [System.Collections.Generic.List[string]]($text -split "`r?`n")

# find the [mod] section and the values of plan= and runtime=
$inMod = $false; $modStart = -1; $modEnd = $lines.Count; $values = @{}
for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i].Trim()
    if ($l -match '^\[(.+)\]$') {
        if ($inMod) { $modEnd = $i; break }
        if ($Matches[1] -ieq 'mod') { $inMod = $true; $modStart = $i }
        continue
    }
    if ($inMod -and $l -match '^([^=;#]+)=(.*)$') { $values[$Matches[1].Trim().ToLowerInvariant()] = $Matches[2].Trim() }
}
if ($modStart -lt 0) { throw 'mod.ini has no [mod] section.' }

function Pin([string]$key, [string]$shaKey) {
    if (-not $values.ContainsKey($key) -or $values[$key].Length -eq 0) { throw "mod.ini has no '$key=' value." }
    $rel = $values[$key]
    if ($rel -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($rel)) { throw "'$key=$rel' must be a relative path without '..'." }
    $full = [IO.Path]::GetFullPath((Join-Path $ModFolder $rel))
    if (-not $full.StartsWith($ModFolder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "'$key=$rel' points outside the mod folder." }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "The file '$rel' named by '$key=' does not exist." }
    $sha = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    $found = $false
    for ($i = $modStart + 1; $i -lt $modEnd; $i++) {
        if ($lines[$i] -match "^\s*$shaKey\s*=") { $lines[$i] = "$shaKey=$sha"; $found = $true; break }
    }
    if (-not $found) { $lines.Insert($modEnd, "$shaKey=$sha"); $script:modEnd++ }
    "{0,-15} {1}  ({2})" -f $shaKey, $sha, $rel
}
$report = @((Pin 'plan' 'plan_sha256'), (Pin 'runtime' 'runtime_sha256'))
$report | ForEach-Object { Write-Host $_ }
$newText = ($lines -join $nl)
if ($newText -eq $text) { Write-Host 'mod.ini already has the right pins.'; return }
if ($PSCmdlet.ShouldProcess($iniPath, 'rewrite plan_sha256 and runtime_sha256')) {
    [IO.File]::WriteAllText($iniPath, $newText, (New-Object Text.UTF8Encoding($false)))
    Write-Host 'mod.ini updated.'
}
