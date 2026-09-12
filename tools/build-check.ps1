<#
.SYNOPSIS
    Full semantic build check for this mod's Space Engineers scripts.

.DESCRIPTION
    Compiles every .cs under the mod's Data/Scripts against the real Space Engineers
    assemblies, exactly as SE does at world load. Catches BOTH syntax errors and
    semantic ones (undeclared fields, wrong types, missing overloads).

    This complements the CI "Syntax check" workflow, which can only parse syntax --
    Keen's Bin64 assemblies are proprietary and cannot be shipped to GitHub runners.
    Run this before pushing script changes, and always before publishing to the Workshop.

    Nothing is written to the repo; the assembly goes to your temp folder.

.PARAMETER Bin64
    Path to the SE Bin64 folder. Defaults to $env:SE_BIN64, then common Steam locations.

.EXAMPLE
    pwsh -File tools/build-check.ps1
    pwsh -File tools/build-check.ps1 -Bin64 "E:\Steam\steamapps\common\SpaceEngineers\Bin64"
#>
[CmdletBinding()]
param([string]$Bin64)

$ErrorActionPreference = 'Stop'

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 2 }

# --- locate the SE assemblies -------------------------------------------------
if (-not $Bin64) { $Bin64 = $env:SE_BIN64 }
if (-not $Bin64) {
    $candidates = @(
        'C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64',
        'D:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64',
        'E:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64'
    )
    $Bin64 = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Bin64 -or -not (Test-Path $Bin64)) {
    Fail "Could not find the SE Bin64 folder. Pass -Bin64 <path> or set `$env:SE_BIN64."
}

# --- locate a Roslyn compiler (C# 6 needs Roslyn, not the .NET Framework csc) --
$csc = Get-ChildItem -Path @(
        'C:\Program Files\Microsoft Visual Studio',
        'C:\Program Files (x86)\Microsoft Visual Studio'
    ) -Filter 'csc.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\Roslyn\*' } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) {
    Fail "No Roslyn csc.exe found. Install Visual Studio Build Tools (the .NET Framework 4.8 workload)."
}

# --- locate this mod's scripts ------------------------------------------------
$repoRoot  = Split-Path -Parent $PSScriptRoot
$scriptDirs = Get-ChildItem -Path $repoRoot -Directory -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName.EndsWith([IO.Path]::Combine('Data','Scripts'), [StringComparison]::OrdinalIgnoreCase) }
if (-not $scriptDirs) { Fail "No Data/Scripts folder found under $repoRoot." }

$sources = foreach ($d in $scriptDirs) {
    Get-ChildItem -Path $d.FullName -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\.vs\*' }
}
if (-not $sources) { Fail "No .cs files found under $($scriptDirs.FullName)." }

# --- build the response file --------------------------------------------------
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$out = Join-Path $env:TEMP ('se-build-check-' + (Split-Path $repoRoot -Leaf) + '.dll')
$rsp = Join-Path $env:TEMP 'se-build-check.rsp'

$lines = @('/target:library', '/langversion:6', '/nostdlib+', "/out:`"$out`"")
foreach ($d in 'mscorlib','System','System.Core','System.Xml','System.Data','netstandard') {
    $lines += "/r:`"$fw\$d.dll`""
}
foreach ($d in 'Sandbox.Common','Sandbox.Game','Sandbox.Graphics','SpaceEngineers.Game',
               'SpaceEngineers.ObjectBuilders','VRage','VRage.Game','VRage.Library',
               'VRage.Math','VRage.Render','VRage.Scripting','VRage.Input','VRage.Network',
               'ProtoBuf.Net','ProtoBuf.Net.Core','System.Collections.Immutable') {
    $p = Join-Path $Bin64 "$d.dll"
    # Mods need different subsets - Tiered B&R uses ProtoBuf, the InfoLCD mods do not - so
    # an absent assembly is skipped rather than fatal. A genuinely missing reference then
    # surfaces as a CS0246 below, which is the clearer error anyway.
    if (-not (Test-Path $p)) { continue }
    $lines += "/r:`"$p`""
}
foreach ($s in $sources) { $lines += "`"$($s.FullName)`"" }
Set-Content -Path $rsp -Value $lines -Encoding utf8

Write-Host "Compiling $($sources.Count) file(s) against $Bin64" -ForegroundColor Cyan
$output = & $csc "@$rsp" 2>&1
$errors = $output | Where-Object { $_ -match ': error ' }

if ($errors) {
    $errors | Select-Object -First 40 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    if ($errors.Count -gt 40) { Write-Host "... and $($errors.Count - 40) more." -ForegroundColor Red }
    Write-Host "`nFAILED: $($errors.Count) compiler error(s)." -ForegroundColor Red
    exit 1
}

$warnings = $output | Where-Object { $_ -match ': warning CS' }
Write-Host "OK: 0 errors, $($warnings.Count) warning(s)." -ForegroundColor Green
exit 0
