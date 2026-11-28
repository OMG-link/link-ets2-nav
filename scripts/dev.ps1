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
    [switch]$NoBuild,
    [string]$SpeechPackPath,
    [switch]$NavigationOnly
)
$ErrorActionPreference = 'Stop'
if ($NavigationOnly -and $Action -ne 'Run') { throw 'NavigationOnly is only available with -Action Run.' }
if ($NavigationOnly -and -not $PSBoundParameters.ContainsKey('Configuration')) { $Configuration = 'Release' }
if ($Action -ne 'Test' -and ($PSBoundParameters.ContainsKey('TestScope') -or $TestFilter -or $NoRestore -or $NoBuild)) {
    throw 'TestScope, TestFilter, NoRestore and NoBuild are only available with -Action Test.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $projectRoot 'ETS2LA'
$pluginProject = Join-Path $projectRoot 'Plugins\LinkEts2Nav\LinkEts2Nav.csproj'
$speechProject = Join-Path $projectRoot 'tools\LinkEts2Nav.Speech\LinkEts2Nav.Speech.csproj'
$speechOutput = Join-Path $projectRoot "tools\LinkEts2Nav.Speech\bin\$Configuration\net10.0\win-x64"
$ets2laProject = Join-Path $frameworkRoot 'ETS2LA\ETS2LA.csproj'
$ets2laOutput = Join-Path $frameworkRoot "ETS2LA\bin\$Configuration\net10.0"
$hostProject = $ets2laProject
$hostOutput = $ets2laOutput
$hostAssembly = 'ETS2LA.dll'
if ($NavigationOnly) {
    $hostProject = Join-Path $projectRoot 'dev-host\LinkEts2Nav.Host\LinkEts2Nav.Host.csproj'
    $hostOutput = Join-Path $projectRoot "dev-host\LinkEts2Nav.Host\bin\$Configuration\net10.0"
    $hostAssembly = 'LinkEts2Nav.Host.dll'
}

$speechPack = Join-Path $projectRoot 'artifacts\speech\qwen-longanhuan-v3.1'
if ($SpeechPackPath) {
    if ($Action -notin @('Deploy', 'Run')) { throw 'SpeechPackPath is only available with -Action Deploy or Run.' }
    $speechPack = if ([IO.Path]::IsPathRooted($SpeechPackPath)) { [IO.Path]::GetFullPath($SpeechPackPath) } else { [IO.Path]::GetFullPath((Join-Path $projectRoot $SpeechPackPath)) }
    if (-not ((Test-Path -LiteralPath (Join-Path $speechPack 'manifest.json')) -or (Test-Path -LiteralPath (Join-Path $speechPack 'CityNames\manifest.json')))) { throw 'SpeechPackPath must contain manifest.json or CityNames/manifest.json.' }
}

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $projectRoot
try {
    $missing = @()
    $requiredTools = @('git', 'dotnet')
    if (-not $NavigationOnly -and ($Action -ne 'Test' -or ($TestScope -ne 'Unit' -and -not $NoBuild))) { $requiredTools += 'bun' }
    foreach ($tool in $requiredTools) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            $missing += $tool
            Write-Host "MISSING: $tool"
        }
    }
    if (Get-Command git -ErrorAction SilentlyContinue) { Invoke-Checked git @('--version') }
    if (-not $NavigationOnly -and (Get-Command bun -ErrorAction SilentlyContinue)) { Invoke-Checked bun @('--version') }
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $sdkList = @(& dotnet --list-sdks)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot list .NET SDKs.' }
        $sdkList | ForEach-Object { Write-Host $_ }
        if (-not ($sdkList | Where-Object { $_ -match '^10\.\d+\.\d+\s' })) { $missing += '.NET 10 SDK' }
    }
    foreach ($required in @($hostProject, (Join-Path $frameworkRoot 'TruckLib\TruckLib\TruckLib.csproj'),
        (Join-Path $projectRoot 'dev-host\TruckLib.Sii\TruckLib.Sii\TruckLib.Sii.csproj'),
        (Join-Path $projectRoot 'dev-host\TruckLib.HashFs\TruckLib.HashFs\TruckLib.HashFs.csproj'))) {
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
    if (-not $NavigationOnly) { Invoke-Checked dotnet @('restore', $pluginProject) }
    Invoke-Checked dotnet @('restore', $speechProject, '-r', 'win-x64')
    if ($Action -eq 'Restore') {
        Invoke-Checked dotnet @('restore', (Join-Path $projectRoot 'LinkEts2Nav.slnx'))
    }
    if ($Action -eq 'Restore') { exit 0 }

    Invoke-Checked dotnet @('build', $hostProject, '-c', $Configuration, '--no-restore')
    if (-not $NavigationOnly) { Invoke-Checked dotnet @('build', $pluginProject, '-c', $Configuration, '--no-restore') }
    Invoke-Checked dotnet @('build', $speechProject, '-c', $Configuration, '-r', 'win-x64', '--no-restore')
    if ($Action -eq 'Build') { exit 0 }

    if ($NavigationOnly) {
        $settings = Join-Path $hostOutput 'Settings'
        $oldSettings = Join-Path $ets2laOutput 'Settings'
        if ($Configuration -eq 'Release' -and -not (Test-Path -Path (Join-Path $oldSettings 'LinkEts2Nav.*.json') -PathType Leaf)) {
            $oldSettings = Join-Path $frameworkRoot 'ETS2LA\bin\Debug\net10.0\Settings'
        }
        if (Test-Path -LiteralPath $oldSettings) {
            New-Item -ItemType Directory -Path $settings -Force | Out-Null
            Get-ChildItem -LiteralPath $oldSettings -Filter 'LinkEts2Nav.*.json' -File | ForEach-Object {
                $destination = Join-Path $settings $_.Name
                if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $_.FullName -Destination $destination }
            }
        }
    }
    else {
        $pluginOutput = Join-Path $projectRoot "Plugins\LinkEts2Nav\bin\$Configuration\net10.0"
        $deployment = Join-Path $hostOutput 'Plugins'
        New-Item -ItemType Directory -Path $deployment -Force | Out-Null
        # Deploy only this plugin, not framework DLLs or unrelated installed plugins.
        foreach ($name in @('LinkEts2Nav.dll', 'LinkEts2Nav.pdb')) {
            $source = Join-Path $pluginOutput $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $deployment -Force }
        }
        Write-Host "Plugin deployed to $deployment. Enable it in ETS2LA's plugin manager."
    }
    $speechDeployment = Join-Path $hostOutput 'Tools\LinkEts2Nav.Speech'
    New-Item -ItemType Directory -Path $speechDeployment -Force | Out-Null
    Get-ChildItem -LiteralPath $speechOutput -File | Copy-Item -Destination $speechDeployment -Force
    if ((Test-Path -LiteralPath (Join-Path $speechPack 'manifest.json')) -or (Test-Path -LiteralPath (Join-Path $speechPack 'CityNames\manifest.json'))) {
        $packDeployment = Join-Path $hostOutput 'Assets\LinkEts2Nav\SpeechPack'
        New-Item -ItemType Directory -Path $packDeployment -Force | Out-Null
        # A selected pack replaces the active manifests. Unreferenced old WAVs are
        # harmless; never leave a previous provider's manifest active by accident.
        if (-not (Test-Path -LiteralPath (Join-Path $speechPack 'manifest.json'))) {
            $oldManifest = Join-Path $packDeployment 'manifest.json'
            if (Test-Path -LiteralPath $oldManifest) { Remove-Item -LiteralPath $oldManifest -Force }
        }
        Get-ChildItem -LiteralPath $speechPack -File | Copy-Item -Destination $packDeployment -Force
        foreach ($subpack in @('CityNames', 'Journey')) {
            $sourceSubpack = Join-Path $speechPack $subpack
            $targetSubpack = Join-Path $packDeployment $subpack
            if (Test-Path -LiteralPath (Join-Path $sourceSubpack 'manifest.json')) {
                New-Item -ItemType Directory -Path $targetSubpack -Force | Out-Null
                Get-ChildItem -LiteralPath $sourceSubpack -File | Copy-Item -Destination $targetSubpack -Force
            } else {
                $oldManifest = Join-Path $targetSubpack 'manifest.json'
                if (Test-Path -LiteralPath $oldManifest) { Remove-Item -LiteralPath $oldManifest -Force }
            }
        }
        Write-Host "Local speech pack deployed to $packDeployment."
    }

    . (Join-Path $PSScriptRoot 'speech-model.ps1')
    $modelDeployment = Join-Path $hostOutput 'Assets\LinkEts2Nav\SpeechModel'
    if (-not (Test-NavSpeechModel -Directory $modelDeployment)) {
        try {
            $speechSettingsPath = Join-Path $hostOutput 'Settings\LinkEts2Nav.Speech.json'
            $speechSettings = if (Test-Path -LiteralPath $speechSettingsPath -PathType Leaf) {
                Get-Content -LiteralPath $speechSettingsPath -Raw | ConvertFrom-Json
            }
            $selectedModel = $speechSettings.modelDirectory
            if ($selectedModel -and -not [IO.Path]::IsPathRooted($selectedModel)) {
                $selectedModel = Join-Path $hostOutput $selectedModel
            }
            if ($speechSettings.enabled -ne $false -and
                (-not $selectedModel -or [IO.Path]::GetFullPath($selectedModel).TrimEnd('\', '/') -eq $modelDeployment)) {
                $modelSource = Get-NavSpeechModel -ProjectRoot $projectRoot
                Install-NavSpeechModel -Source $modelSource -Destination $modelDeployment
            }
        }
        catch {
            Write-Warning "Could not install the default local speech model. Available recordings and the alert tone can still play. $($_.Exception.Message)"
        }
    }
    if ($Action -eq 'Run') {
        Push-Location $hostOutput
        try { Invoke-Checked dotnet @((Join-Path $hostOutput $hostAssembly)) }
        finally { Pop-Location }
    }
}
finally { Pop-Location }
