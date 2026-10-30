<#
.SYNOPSIS
    Downloads the models Talktrace needs from the sherpa-onnx GitHub releases.

.DESCRIPTION
    This script is the only part of Talktrace that uses the network. It downloads each
    model, checks its SHA-256 hash, and extracts archives with Windows' built-in tar.
    Models already present are skipped, so it is safe to re-run.

.PARAMETER Profile
    Fast (English, the default), Multilingual, or All.

.PARAMETER Destination
    Where to put the models. Default: %LOCALAPPDATA%\Talktrace\models, which the app
    searches after the models folder next to its exe.

.EXAMPLE
    .\scripts\get-models.ps1
    .\scripts\get-models.ps1 -Profile All
#>
[CmdletBinding()]
param(
    [Alias('Profile')]
    [ValidateSet('Fast', 'Multilingual', 'All')]
    [string] $ModelProfile = 'Fast',

    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Talktrace\models')
)

$ErrorActionPreference = 'Stop'

$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$base = 'https://github.com/k2-fsa/sherpa-onnx/releases/download'

# Hashes come from GitHub's release-asset digests where GitHub shows one; the pyannote
# and TitaNet assets predate digests, so theirs were recorded on first download (2026-10-30).
# Check names against https://k2-fsa.github.io/sherpa/onnx/ before changing them.
# 'speaker-recongition-models' is misspelled in sherpa-onnx's own release; keep it.
$models = @(
    @{
        Name     = 'Parakeet TDT 0.6B v2, int8 (Fast profile)'
        Profiles = @('Fast')
        Url      = "$base/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2"
        Sha256   = '157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad'
        Present  = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\encoder.int8.onnx'
    }
    @{
        Name     = 'Parakeet TDT 0.6B v3, int8 (Multilingual profile)'
        Profiles = @('Multilingual')
        Url      = "$base/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"
        Sha256   = '5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf'
        Present  = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8\encoder.int8.onnx'
    }
    @{
        Name     = 'Silero VAD'
        Profiles = @('Fast', 'Multilingual')
        Url      = "$base/asr-models/silero_vad.onnx"
        Sha256   = '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
        Present  = 'silero_vad.onnx'
    }
    @{
        Name     = 'pyannote segmentation 3.0'
        Profiles = @('Fast', 'Multilingual')
        Url      = "$base/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2"
        Sha256   = '24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488'
        Present  = 'sherpa-onnx-pyannote-segmentation-3-0\model.int8.onnx'
    }
    @{
        Name     = 'TitaNet small speaker embeddings'
        Profiles = @('Fast', 'Multilingual')
        Url      = "$base/speaker-recongition-models/nemo_en_titanet_small.onnx"
        Sha256   = 'ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e'
        Present  = 'nemo_en_titanet_small.onnx'
    }
)

# Runs curl or tar. Windows PowerShell 5.1 turns anything a native tool writes to stderr
# (such as curl's progress bar) into an error when output is redirected, so relax that here
# and rely on the exit code instead.
function Invoke-Native([string] $Exe, [string[]] $Arguments, [string] $Failure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ } | Write-Host }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$Failure ($(Split-Path $Exe -Leaf) exit code $LASTEXITCODE)." }
}

$selected = $models | Where-Object { $ModelProfile -eq 'All' -or $_.Profiles -contains $ModelProfile }
$downloads = Join-Path $Destination '.downloads'
New-Item -ItemType Directory -Force -Path $Destination, $downloads | Out-Null

Write-Host "Talktrace models, profile $ModelProfile, into $Destination"
Write-Host 'This downloads from github.com (k2-fsa/sherpa-onnx releases).'
Write-Host ''

foreach ($model in $selected) {
    if (Test-Path (Join-Path $Destination $model.Present)) {
        Write-Host "Already present: $($model.Name)"
        continue
    }

    $fileName = Split-Path $model.Url -Leaf
    $partial = Join-Path $downloads "$fileName.partial"
    Write-Host "Downloading $($model.Name)..."
    Invoke-Native $curl @('--fail', '--location', '--retry', '3', '--no-progress-meter', '--output', $partial, $model.Url) "Download failed for $fileName"

    $hash = (Get-FileHash -Path $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $model.Sha256) {
        Remove-Item $partial -Force
        throw "SHA-256 mismatch for $fileName. Expected $($model.Sha256), got $hash. The file was deleted."
    }

    if ($fileName.EndsWith('.tar.bz2')) {
        # Extract into a staging folder first, so an interrupted run never looks complete.
        $staging = Join-Path $downloads ($fileName -replace '\.tar\.bz2$', '')
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        New-Item -ItemType Directory -Path $staging | Out-Null
        Invoke-Native $tar @('-xjf', $partial, '-C', $staging) "Could not extract $fileName"
        foreach ($dir in Get-ChildItem $staging -Directory) {
            $target = Join-Path $Destination $dir.Name
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
            Move-Item $dir.FullName $target
        }
        Remove-Item $staging -Recurse -Force
        Remove-Item $partial -Force
    }
    else {
        Move-Item $partial (Join-Path $Destination $fileName) -Force
    }

    if (-not (Test-Path (Join-Path $Destination $model.Present))) {
        throw "$($model.Name) was downloaded but $($model.Present) is missing."
    }
    Write-Host "Installed: $($model.Name)"
}

Remove-Item $downloads -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "Done. Models are in $Destination"
