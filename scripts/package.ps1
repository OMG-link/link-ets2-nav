[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\packages'
$stage = Join-Path $output ('.build-' + [Guid]::NewGuid().ToString('N'))
$speechProject = Join-Path $root 'tools\LinkEts2Nav.Speech\LinkEts2Nav.Speech.csproj'
$hostProject = Join-Path $root 'dev-host\LinkEts2Nav.Host\LinkEts2Nav.Host.csproj'
$speech = Join-Path $stage 'speech'
$standalone = Join-Path $stage 'host'
$common = Join-Path $stage 'common'
$hostLegal = Join-Path $stage 'host-legal'
$legal = 'Licenses\LinkEts2Nav'
$modelPath = 'Assets\LinkEts2Nav\SpeechModel'
$modelFiles = @('model.onnx', 'voices.bin', 'tokens.txt', 'lexicon-zh.txt', 'lexicon-us-en.txt', 'LICENSE', 'README.md')
$pluginName = 'LinkEts2Nav-Plugin-win-x64.zip'
$hostName = 'LinkEts2Nav-Standalone-win-x64.zip'
. (Join-Path $PSScriptRoot 'speech-model.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Invoke-Checked([string]$command, [string[]]$arguments) {
    & $command @arguments
    if ($LASTEXITCODE -ne 0) { throw "$command failed with exit code $LASTEXITCODE." }
}

function Copy-File([string]$from, [string]$to) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
    [IO.File]::Copy($from, $to, $true)
}

function Write-Text([string]$path, [string[]]$lines) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))
}

function Publish-App([string]$project, [string]$destination) {
    Invoke-Checked 'dotnet' @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $destination, '--nologo', '--verbosity', 'quiet')
}

function Add-Tree($archive, [string]$directory, [string]$prefix) {
    $base = [IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    foreach ($file in [IO.Directory]::EnumerateFiles($directory, '*', [IO.SearchOption]::AllDirectories)) {
        if ([IO.Path]::GetExtension($file) -eq '.pdb') { continue }
        $entry = $prefix + $file.Substring($base.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $entry,
            [IO.Compression.CompressionLevel]::Optimal)
    }
}

# Include the exact local source of the host, native map libraries and build overrides used
# for this release. Read only source project paths; runtime settings and untracked assets do not belong here.
function Add-Source($archive, [string]$repo, [string]$prefix, [string[]]$paths, [switch]$LocalSources) {
    $names = @(Invoke-Checked 'git' (@('-C', $repo, '-c', 'core.quotepath=false', 'ls-files', '--cached', '--') + $paths))
    if ($LocalSources) {
        $newFiles = @(Invoke-Checked 'git' (@('-C', $repo, '-c', 'core.quotepath=false', 'ls-files', '--others', '--exclude-standard', '--') + $paths))
        $names += @($newFiles | Where-Object { $_ -match '\.(cs|csproj|razor|ps1|props|targets)$' })
    }
    foreach ($name in $names) {
        $file = Join-Path $repo $name
        if (-not [IO.File]::Exists($file) -or $name -match '\.pot(\.bak)?$') { continue }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $prefix + $name)
    }
}

function Add-HostSource([string]$destination, $deps) {
    [void][IO.Directory]::CreateDirectory($destination)
    $source = [IO.Compression.ZipFile]::Open((Join-Path $destination 'Source.zip'), [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-Source $source $root '' @('LICENSE','README.md','.gitmodules','global.json','Directory.Build.targets',
            'Plugins/LinkEts2Nav','tools/LinkEts2Nav.Speech','dev-host/LinkEts2Nav.Host',
            ':(top,glob)dev-host/*.cs','dev-host/prepare-local-host.ps1','dev-host/ui','scripts/dev.ps1',
            'scripts/package.ps1','scripts/speech-model.ps1','scripts/ProbeRunner') -LocalSources
        $upstream = @('LICENSE.txt','README.md','dotnet-tools.json','.gitmodules','Assets/Sounds/pre_alert.wav')
        $upstream += @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' -and $_.Name -like 'ETS2LA.*' } |
            ForEach-Object { $_.Name.Split('/')[0] })
        Add-Source $source (Join-Path $root 'ETS2LA') 'ETS2LA/' $upstream
        foreach ($lib in @('ETS2LA/TruckLib','dev-host/TruckLib.Sii','dev-host/TruckLib.HashFs')) {
            $projectName = Split-Path $lib -Leaf
            Add-Source $source (Join-Path $root $lib) ($lib + '/') @($projectName,'LICENSE','README.md')
            Copy-File (Join-Path $root "$lib/LICENSE") (Join-Path $destination "$projectName-LICENSE.txt")
        }
        Copy-File (Join-Path $root 'ETS2LA/LICENSE.txt') (Join-Path $destination 'ETS2LA-GPL.txt')
    } finally { $source.Dispose() }
    $locations = @('Source.zip contains the source projects, local build adaptations and build scripts for this host.',
        'Build on Windows with the .NET 10 SDK. From the extracted ETS2LA directory run dotnet tool restore,',
        'then publish dev-host/LinkEts2Nav.Host/LinkEts2Nav.Host.csproj with -c Release -r win-x64 --self-contained true.',
        'The upstream revisions below identify the source checkouts before local build adaptations.')
    foreach ($repoPath in @('.', 'ETS2LA', 'ETS2LA/TruckLib', 'dev-host/TruckLib.HashFs', 'dev-host/TruckLib.Sii')) {
        $repo = Join-Path $root $repoPath
        $rev = Invoke-Checked 'git' @('-C', $repo, 'rev-parse', 'HEAD')
        $url = switch ($repoPath) {
            '.' {'https://github.com/OMG-link/link-ets2-nav'}
            'ETS2LA' {'https://github.com/ETS2LA/ETS2LA'}
            'ETS2LA/TruckLib' {'https://github.com/ETS2LA/TruckLib'}
            'dev-host/TruckLib.HashFs' {'https://github.com/OMG-link/TruckLib.HashFs'}
            'dev-host/TruckLib.Sii' {'https://github.com/OMG-link/TruckLib.Sii'}
        }
        $locations += "$repoPath  $url/tree/$rev"
    }
    Write-Text (Join-Path $destination 'Sources.txt') $locations
}

function Add-PackageNotices([string]$project, $deps, [string]$destination) {
    $assets = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $project) 'obj/project.assets.json') | ConvertFrom-Json
    $roots = @($assets.packageFolders.PSObject.Properties.Name)
    $index = @('Package | License | Package page | Upstream source (and revision when supplied)')
    foreach ($entry in $deps.libraries.PSObject.Properties) {
        if ($entry.Value.type -notin @('package','runtimepack')) { continue }
        $id, $version = $entry.Name.Split('/')
        $id = $id -replace '^runtimepack\.', ''
        $relative = $id.ToLowerInvariant() + '/' + $version.ToLowerInvariant()
        $installed = $roots | ForEach-Object { Join-Path $_ $relative } | Where-Object { [IO.Directory]::Exists($_) } | Select-Object -First 1
        if (-not $installed) { throw "Missing restored package metadata for $id $version." }
        $spec = Get-ChildItem -LiteralPath $installed -Filter '*.nuspec' -File | Select-Object -First 1
        [xml]$xml = [IO.File]::ReadAllText($spec.FullName)
        $meta = $xml.package.metadata
        $license = $meta.SelectSingleNode("*[local-name()='license']")
        $licenseText = if ($license) { $license.InnerText } else { [string]$meta.licenseUrl }
        $source = if ($meta.repository.url) { "$($meta.repository.url) $($meta.repository.commit)" } else { [string]$meta.projectUrl }
        $index += "$id $version | $licenseText | https://www.nuget.org/packages/$id/$version | $source"
        $to = Join-Path $destination ($id + '-' + $version)
        foreach ($file in Get-ChildItem -LiteralPath $installed -File) {
            if ($file.Name -match '(?i)^(licen[sc]e|copying|notice)|third[-_. ]*party') { Copy-File $file.FullName (Join-Path $to $file.Name) }
        }
        if ($license -and $license.GetAttribute('type') -eq 'file') {
            $file = Join-Path $installed $license.InnerText
            Copy-File $file (Join-Path $to ([IO.Path]::GetFileName($file)))
        }
    }
    Write-Text (Join-Path $destination 'Packages.txt') $index
}

function New-Bundle([string]$name, [bool]$includeHost) {
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $stage $name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-Tree $archive $common ''
        Add-Tree $archive $speech 'Tools/LinkEts2Nav.Speech/'
        if ($includeHost) {
            Add-Tree $archive $standalone ''
            Add-Tree $archive $hostLegal 'Licenses/LinkEts2Nav/Host/'
        } else {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $standalone 'LinkEts2Nav.dll'), 'Plugins/LinkEts2Nav.dll')
        }
    } finally { $archive.Dispose() }
}

try {
    [void][IO.Directory]::CreateDirectory($stage)
    Push-Location (Join-Path $root 'ETS2LA')
    try { Invoke-Checked 'dotnet' @('tool', 'restore') } finally { Pop-Location }
    Publish-App $speechProject $speech
    Publish-App $hostProject $standalone
    foreach ($required in @('LinkEts2Nav.Speech.exe','coreclr.dll','hostfxr.dll','sherpa-onnx-c-api.dll','onnxruntime.dll')) {
        if (-not [IO.File]::Exists((Join-Path $speech $required))) { throw "Speech publish output is missing $required." }
    }
    foreach ($required in @('LinkEts2Nav.Host.exe','LinkEts2Nav.dll','coreclr.dll','Assets/Sounds/pre_alert.wav','libdeflate.dll')) {
        if (-not [IO.File]::Exists((Join-Path $standalone $required))) { throw "Standalone publish output is missing $required." }
    }
    $model = Get-NavSpeechModel -ProjectRoot $root
    Install-NavSpeechModel -Source $model -Destination (Join-Path $common $modelPath)
    foreach ($name in $modelFiles) {
        if (-not [IO.File]::Exists((Join-Path $common "$modelPath/$name"))) { throw "Packaged model is missing $name." }
    }
    Copy-File (Join-Path $root 'LICENSE') (Join-Path $common "$legal/LinkEts2Nav-MIT.txt")
    $hostDeps = Get-Content -Raw -LiteralPath (Join-Path $standalone 'LinkEts2Nav.Host.deps.json') | ConvertFrom-Json
    $speechDeps = Get-Content -Raw -LiteralPath (Join-Path $speech 'LinkEts2Nav.Speech.deps.json') | ConvertFrom-Json
    Add-PackageNotices $speechProject $speechDeps (Join-Path $common "$legal/Speech")
    Add-PackageNotices $hostProject $hostDeps (Join-Path $hostLegal 'Packages')
    Add-HostSource $hostLegal $hostDeps
    New-Bundle $pluginName $false
    New-Bundle $hostName $true
    foreach ($name in @($pluginName, $hostName)) {
        $built = Join-Path $stage $name
        $final = Join-Path $output $name
        if ([IO.File]::Exists($final)) { [IO.File]::Replace($built, $final, (Join-Path $stage ("old-" + $name))) }
        else { [IO.File]::Move($built, $final) }
        Write-Host "Created $final"
    }
} finally {
    if ([IO.Directory]::Exists($stage) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -eq [IO.Path]::GetFullPath($output) -and
        [IO.Path]::GetFileName($stage) -match '^\.build-[a-f0-9]{32}$') { Remove-Item -LiteralPath $stage -Recurse -Force }
}
