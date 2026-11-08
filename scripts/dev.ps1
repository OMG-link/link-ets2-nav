[CmdletBinding()]
param(
    [ValidateSet('Check', 'Restore', 'Build', 'Deploy', 'Run', 'Test')]
    [string]$Action = 'Check',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('All', 'Unit', 'Integration')]
    [string]$TestScope = 'All',
    [string]$TestFilter,
    [switch]$NoRestore,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ($Action -ne 'Test' -and ($PSBoundParameters.ContainsKey('TestScope') -or $TestFilter -or $NoRestore -or $NoBuild)) {
    throw 'TestScope, TestFilter, NoRestore and NoBuild are only available with -Action Test.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $projectRoot 'ETS2LA'
$pluginProject = Join-Path $projectRoot 'Plugins\LinkEts2Nav\LinkEts2Nav.csproj'
$speechProject = Join-Path $projectRoot 'tools\LinkEts2Nav.Speech\LinkEts2Nav.Speech.csproj'
$speechOutput = Join-Path $projectRoot "tools\LinkEts2Nav.Speech\bin\$Configuration\net10.0\win-x64"
$hostProject = Join-Path $frameworkRoot 'ETS2LA\ETS2LA.csproj'
$hostOutput = Join-Path $frameworkRoot "ETS2LA\bin\$Configuration\net10.0"

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $projectRoot
try {
    $missing = @()
    $requiredTools = @('git', 'dotnet')
    if ($Action -ne 'Test' -or ($TestScope -ne 'Unit' -and -not $NoBuild)) { $requiredTools += 'bun' }
    foreach ($tool in $requiredTools) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            $missing += $tool
            Write-Host "MISSING: $tool"
        }
    }
    if (Get-Command git -ErrorAction SilentlyContinue) { Invoke-Checked git @('--version') }
    if (Get-Command bun -ErrorAction SilentlyContinue) { Invoke-Checked bun @('--version') }
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $sdkList = @(& dotnet --list-sdks)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list .NET SDKs.' }
        $sdkList | ForEach-Object { Write-Host $_ }
        if (-not ($sdkList | Where-Object { $_ -match '^10\.\d+\.\d+\s' })) { $missing += '.NET 10 SDK' }
    }
    foreach ($required in @($hostProject, (Join-Path $frameworkRoot 'TruckLib\TruckLib\TruckLib.csproj'))) {
        if (-not (Test-Path -LiteralPath $required)) { $missing += "Submodule file: $required" }
    }
    if ($missing.Count -gt 0) {
        throw ("Prerequisites missing: " + ($missing -join ', ') + ". See README.md. No system software was installed.")
    }
    if ($Action -eq 'Check') { Write-Host 'Development prerequisites found.'; exit 0 }

    if ($Action -eq 'Test') {
        $testTarget = switch ($TestScope) {
            'Unit' { Join-Path $projectRoot 'tests/LinkEts2Nav.UnitTests/LinkEts2Nav.UnitTests.csproj' }
            'Integration' { Join-Path $projectRoot 'tests/DevHost.IntegrationTests/DevHost.IntegrationTests.csproj' }
            'All' { Join-Path $projectRoot 'tests/Tests.slnx' }
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        if (-not $NoRestore -and -not $NoBuild) {
            # Shared projects still use GetText.Extractor, but tests do not need the host executable.
            Push-Location $frameworkRoot
            try { Invoke-Checked dotnet @('tool', 'restore') }
            finally { Pop-Location }
            Invoke-Checked dotnet @('restore', $testTarget)
        }
        $testArguments = @('test', $testTarget, '-c', $Configuration, '--no-restore')
        if ($NoBuild) { $testArguments += '--no-build' }
        if ($TestFilter) { $testArguments += @('--filter', $TestFilter) }
        Invoke-Checked dotnet $testArguments
        Write-Host ('Test scope {0} completed in {1:N1}s.' -f $TestScope, $timer.Elapsed.TotalSeconds)
        exit 0
    }

    # Host builds invoke GetText.Extractor; restore its manifest from the host root.
    Push-Location $frameworkRoot
    try { Invoke-Checked dotnet @('tool', 'restore') }
    finally { Pop-Location }
    Invoke-Checked dotnet @('restore', $hostProject)
    Invoke-Checked dotnet @('restore', $pluginProject)
    Invoke-Checked dotnet @('restore', $speechProject, '-r', 'win-x64')
    if ($Action -eq 'Restore') {
        Invoke-Checked dotnet @('restore', (Join-Path $projectRoot 'LinkEts2Nav.slnx'))
    }
    if ($Action -eq 'Restore') { exit 0 }

    Invoke-Checked dotnet @('build', $hostProject, '-c', $Configuration, '--no-restore')
    Invoke-Checked dotnet @('build', $pluginProject, '-c', $Configuration, '--no-restore')
    Invoke-Checked dotnet @('build', $speechProject, '-c', $Configuration, '-r', 'win-x64', '--no-restore')
    if ($Action -eq 'Build') { exit 0 }

    $pluginOutput = Join-Path $projectRoot "Plugins\LinkEts2Nav\bin\$Configuration\net10.0"
    $deployment = Join-Path $hostOutput 'Plugins'
    New-Item -ItemType Directory -Path $deployment -Force | Out-Null
    # Deploy only this plugin, not framework DLLs or unrelated installed plugins.
    foreach ($name in @('LinkEts2Nav.dll', 'LinkEts2Nav.pdb')) {
        $source = Join-Path $pluginOutput $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $deployment -Force }
    }
    $speechDeployment = Join-Path $hostOutput 'Tools\LinkEts2Nav.Speech'
    New-Item -ItemType Directory -Path $speechDeployment -Force | Out-Null
    Get-ChildItem -LiteralPath $speechOutput -File | Copy-Item -Destination $speechDeployment -Force
    $speechPack = Join-Path $projectRoot 'artifacts\speech\qwen-longanhuan-v3.1'
    if (Test-Path -LiteralPath (Join-Path $speechPack 'manifest.json')) {
        $packDeployment = Join-Path $hostOutput 'Assets\LinkEts2Nav\SpeechPack'
        New-Item -ItemType Directory -Path $packDeployment -Force | Out-Null
        Get-ChildItem -LiteralPath $speechPack -File | Copy-Item -Destination $packDeployment -Force
        Write-Host "Local speech pack deployed to $packDeployment."
    }
    Write-Host "Plugin deployed to $deployment. Enable it in ETS2LA's plugin manager."
    if ($Action -eq 'Run') {
        Push-Location $hostOutput
        try { Invoke-Checked dotnet @((Join-Path $hostOutput 'ETS2LA.dll')) }
        finally { Pop-Location }
    }
}
finally { Pop-Location }
