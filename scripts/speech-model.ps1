# Shared by the development launcher and the offline package builder (Windows PowerShell 5).
function Test-NavSpeechModel {
    param([Parameter(Mandatory)][string]$Directory)
    foreach ($name in 'model.onnx', 'voices.bin', 'tokens.txt', 'lexicon-zh.txt', 'lexicon-us-en.txt') {
        $file = [IO.FileInfo]::new([IO.Path]::Combine($Directory, $name))
        if (-not $file.Exists -or $file.Length -eq 0) { return $false }
    }
    return $true
}

function Update-NavSpeechModelDirectory {
    param([string]$Destination, [scriptblock]$Populate)
    $ErrorActionPreference = 'Stop'
    $hashes = [ordered]@{
        'model.onnx'        = '05D793700B0B507F760C183C0E39BDD6A97B43D32FDC7BF7B54A5179F1B11222'
        'voices.bin'        = 'E64A5A581D8C2A350D848F51C3121657CD83AA07ED6109172177345874A7244C'
        'tokens.txt'        = '931AB2DF2400CD65D580A22402024C2347CED8AE9EA300E545144B1AACC48E14'
        'lexicon-zh.txt'    = 'FEA060803DB3E59193987F1A3436F56917BDA09F3B3595B6588E46AE60CEBF93'
        'lexicon-us-en.txt' = '7DAAAB53A181BE9885B853A8582BF1838186317E5DADACBCEF9C426D6FA0DA14'
        'LICENSE'           = 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'
        'README.md'         = 'E2D39356EA4D67AC15C39A7E5A238EA12B0F6FB8A55BB0C87BC2F659EB1CF7AC'
    }
    function Test-PayloadHashes([string]$Directory) {
        foreach ($name in $hashes.Keys) {
            $path = [IO.Path]::Combine($Directory, $name)
            if (-not [IO.File]::Exists($path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$name]) {
                return $false
            }
        }
        return $true
    }

    $Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
    $parent = [IO.Path]::GetDirectoryName($Destination)
    if (-not $parent) { throw 'A model directory beneath an existing volume is required.' }
    [void][IO.Directory]::CreateDirectory($parent)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Destination.ToUpperInvariant()))).Replace('-', '')
    } finally { $sha.Dispose() }
    $lock = [Threading.Mutex]::new($false, ('Global\LinkEts2Nav.SpeechModel.' + $key))
    $acquired = $false
    $work = $null
    $canClean = $true
    try {
        try { [void]$lock.WaitOne() } catch [Threading.AbandonedMutexException] { } # Its previous owner exited.
        $acquired = $true
        if (Test-PayloadHashes $Destination) { return }
        if ([IO.Directory]::Exists($Destination)) {
            $existing = Get-Item -LiteralPath $Destination -Force
            if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cannot replace the linked model directory $Destination." }
            foreach ($entry in (Get-ChildItem -LiteralPath $Destination -Force)) {
                if ($entry.PSIsContainer -or -not $hashes.Contains($entry.Name) -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Cannot replace the model directory containing the unrelated entry $($entry.FullName)."
                }
            }
        }
        $stageName = '.nav-speech-' + [Guid]::NewGuid().ToString('N')
        $work = [IO.Path]::Combine($parent, $stageName)
        $payload = [IO.Path]::Combine($work, 'payload')
        [void][IO.Directory]::CreateDirectory($payload)
        & $Populate $payload $work $hashes.Keys
        if (-not (Test-PayloadHashes $payload)) { throw "Kokoro model file verification failed for $Destination." }

        $previous = [IO.Path]::Combine($work, 'previous')
        if ([IO.Directory]::Exists($Destination)) { [IO.Directory]::Move($Destination, $previous) }
        try { [IO.Directory]::Move($payload, $Destination) }
        catch {
            if ([IO.Directory]::Exists($previous)) {
                $canClean = $false
                [IO.Directory]::Move($previous, $Destination)
                $canClean = $true
            }
            throw
        }
    } finally {
        try {
            if ($canClean -and $work -and [IO.Directory]::Exists($work) -and
                [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($work)) -eq $parent -and [IO.Path]::GetFileName($work) -eq $stageName) {
                [IO.Directory]::Delete($work, $true)
            }
        } finally {
            if ($acquired) { $lock.ReleaseMutex() }
            $lock.Dispose()
        }
    }
}

function Install-NavSpeechModel {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    $sourceDirectory = [IO.Path]::GetFullPath($Source)
    Update-NavSpeechModelDirectory $Destination {
        param($payload, $work, $names)
        foreach ($name in $names) {
            [IO.File]::Copy([IO.Path]::Combine($sourceDirectory, $name), [IO.Path]::Combine($payload, $name))
        }
    }
}

function Get-NavSpeechModel {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $modelName = 'kokoro-int8-multi-lang-v1_1'
    $modelRoot = Join-Path ([IO.Path]::GetFullPath($ProjectRoot)) 'artifacts\speech\models'
    $modelDirectory = Join-Path $modelRoot $modelName
    Update-NavSpeechModelDirectory $modelDirectory {
        param($payload, $work, $names)
        $url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2'
        $archiveSha = '259C9C8BC906D6078E4E0570421885DABB68B539C2F970FCBB890A58EEAF3E9C'
        # A separately downloaded release archive can seed an offline development checkout.
        $archive = Join-Path $modelRoot ($modelName + '.tar.bz2')
        if (-not [IO.File]::Exists($archive)) {
            $archive = Join-Path $work 'download.tar.bz2'
            Write-Host 'Downloading the Kokoro INT8 speech model (147 MB)...'
            $curl = (Get-Command curl.exe -CommandType Application -ErrorAction Stop).Source
            & $curl --disable --fail --location --proto '=https' --proto-redir '=https' --silent --show-error --retry 2 --connect-timeout 30 --max-time 900 --output $archive $url | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Kokoro download failed (curl exit code $LASTEXITCODE). Source: $url" }
        }
        if (([IO.FileInfo]::new($archive)).Length -ne 147052523 -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveSha) {
            throw "Kokoro archive size or SHA-256 mismatch at $archive. Expected $archiveSha."
        }
        Write-Host 'Extracting and verifying the Kokoro speech model...'
        $members = foreach ($name in $names) {
            if ($name -eq 'model.onnx') { "$modelName/model.int8.onnx" } else { "$modelName/$name" }
        }
        $tar = (Get-Command tar.exe -CommandType Application -ErrorAction Stop).Source
        & $tar -xf $archive -C $payload --strip-components 1 @members | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Kokoro extraction failed (tar exit code $LASTEXITCODE)." }
        [IO.File]::Move((Join-Path $payload 'model.int8.onnx'), (Join-Path $payload 'model.onnx'))
    }
    return $modelDirectory
}
