[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GamePath,
    [Parameter(Mandatory=$true)][string]$Prefab,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$GameLog,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/LinkEts2Nav.Probe/LinkEts2Nav.Probe.csproj'
$arguments = @('run', '--project', $project)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--', '--game-path', $GamePath, '--prefab-svg', $Prefab, '--output', $Output)
if ($GameLog) { $arguments += @('--game-log', $GameLog) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "SVG export failed with exit code $LASTEXITCODE." }
