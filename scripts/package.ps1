<#
.SYNOPSIS
    Builds the Talktrace installer: publish, ARM64 check, models, then the MSI.

.DESCRIPTION
    1. Reads the version from Directory.Build.props.
    2. Publishes the app and the CLI (the worker the app launches) into a fresh
       artifacts\package\Talktrace-<version> folder.
    3. Runs verify-arm64.ps1 on it, and stops if any native file isn't ARM64.
    4. Adds README.md, THIRD-PARTY-NOTICES.md and .NET's own third-party notices.
    5. Copies the Fast profile's models into models\, checking each file's SHA-256, from
       TALKTRACE_MODELS_DIR if set, else %LOCALAPPDATA%\Talktrace\models (get-models.ps1).
    6. Refuses to go on if the folder holds audio or video, transcripts, recording sidecars,
       logs, any model file not listed below, or a folder path from this machine.
    7. Builds installer\Talktrace.Installer.wixproj into artifacts\Talktrace-<version>-arm64.msi.

.EXAMPLE
    .\scripts\package.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$runtime = 'win-arm64'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repo 'artifacts'

# The models the installer carries, as get-models.ps1 installs them; the hashes pin the exact
# files that were tested. Only what the app loads, plus the segmentation model's licence.
$models = [ordered]@{
    'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\encoder.int8.onnx' = 'a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab'
    'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\decoder.int8.onnx' = 'b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e'
    'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\joiner.int8.onnx'  = '7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2'
    'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8\tokens.txt'        = 'ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d'
    'silero_vad.onnx'                                              = '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
    'sherpa-onnx-pyannote-segmentation-3-0\model.onnx'             = '220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079'
    'sherpa-onnx-pyannote-segmentation-3-0\LICENSE'                = '14d7016ad68e7394d6e6b78d96cc2ae431c905287b89674cfdf021e79e62b8ba'
    'nemo_en_titanet_small.onnx'                                   = 'ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e'
}
$modelSource = if ($env:TALKTRACE_MODELS_DIR) { $env:TALKTRACE_MODELS_DIR } else { Join-Path $env:LOCALAPPDATA 'Talktrace\models' }

# The version lives in one place.
[xml] $props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no three-part <Version> (found '$version')." }

$staging = Join-Path $artifacts "package\Talktrace-$version"
$msi = Join-Path $artifacts "Talktrace-$version-arm64.msi"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'The .NET SDK (dotnet) was not found.' }

# Runs a native tool. Windows PowerShell 5.1 turns a native tool's stderr into errors,
# so relax that here, keep the output, and judge by the exit code.
function Invoke-Native([string] $Exe, [string[]] $Arguments, [string] $Failure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "$Failure (exit code $LASTEXITCODE)."
    }
}

Write-Host "Packaging Talktrace $version for $runtime"

foreach ($old in $staging, $msi) {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $staging | Out-Null

# ContinuousIntegrationBuild makes the build deterministic and replaces this machine's
# source folder (and with it the user name) with /_/ in the assemblies, their symbol
# files and the symbol files' source link map.
foreach ($project in 'src\Talktrace.App', 'src\Talktrace.Cli') {
    Write-Host "Publishing $project..."
    Invoke-Native $dotnet @('publish', (Join-Path $repo $project), '-c', 'Release', '-r', $runtime,
        '--self-contained', 'true', '-o', $staging, '-p:ContinuousIntegrationBuild=true', '--nologo') "Publishing $project failed"
}

Write-Host ''
& (Join-Path $PSScriptRoot 'verify-arm64.ps1') -Path $staging
if ($LASTEXITCODE -ne 0) { throw 'The ARM64 check failed, so no installer was made.' }
Write-Host ''

Copy-Item -LiteralPath (Join-Path $repo 'README.md'), (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $staging

# .NET's own notices, from the runtime pack the publish used.
$runtimeConfig = Get-Content -LiteralPath (Join-Path $staging 'Talktrace.runtimeconfig.json') -Raw | ConvertFrom-Json
$netVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$netNotices = Join-Path $packages "microsoft.netcore.app.runtime.$runtime\$netVersion\THIRD-PARTY-NOTICES.TXT"
if (-not (Test-Path -LiteralPath $netNotices)) { throw ".NET's third-party notices weren't found at $netNotices." }
Copy-Item -LiteralPath $netNotices -Destination (Join-Path $staging 'DOTNET-THIRD-PARTY-NOTICES.txt')

Write-Host "Copying the models from $modelSource..."
foreach ($file in $models.Keys) {
    $source = Join-Path $modelSource $file
    if (-not (Test-Path -LiteralPath $source)) { throw "Model file missing: $source. Run scripts\get-models.ps1 first." }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $models[$file]) { throw "SHA-256 mismatch for $source. Expected $($models[$file]), got $hash." }
    $target = Join-Path $staging "models\$file"
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

# Nothing private may ship: no recordings, transcripts, sidecars, logs or unlisted model files.
$expectedModels = @($models.Keys | ForEach-Object { "models\$_" })
$forbidden = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($staging.Length + 1)
        $_.Extension -in '.wav', '.mp3', '.m4a', '.mp4', '.wma', '.aac', '.flac', '.ogg', '.opus', '.webm', '.log' -or
        $_.Name -like '*transcript*.txt' -or $_.Name -like '*.talktrace.json' -or
        (($_.Extension -eq '.onnx' -or $relative -like 'models\*') -and $expectedModels -notcontains $relative)
    })
if ($forbidden.Count -gt 0) {
    $forbidden | ForEach-Object { Write-Host "  $($_.FullName.Substring($staging.Length + 1))" }
    throw "The package folder holds $($forbidden.Count) file(s) that must never ship. No installer was made."
}

# Nor this machine's folders (the repo's or the user profile's, which hold the user
# name), plain or JSON-escaped, in Talktrace's own files.
$localPaths = foreach ($path in $repo, $env:USERPROFILE) { $path; $path.Replace('\', '\\') }
$leaks = @(Get-ChildItem -LiteralPath $staging -File -Filter 'Talktrace*' | Where-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $texts = [Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes)
        @(foreach ($text in $texts) { foreach ($path in $localPaths) {
                    if ($text.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $true } } }).Count -gt 0
    })
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Write-Host "  $($_.Name)" }
    throw "$($leaks.Count) file(s) contain a folder path from this machine. No installer was made."
}

Write-Host 'Building the installer...'
$wixproj = Join-Path $repo 'installer\Talktrace.Installer.wixproj'
$wixOut = Join-Path $artifacts 'package\installer'
Invoke-Native $dotnet @('build', $wixproj, '-c', 'Release', "-p:StagingDir=$staging", '-o', $wixOut, '--nologo') 'Building the installer failed'
Move-Item -LiteralPath (Join-Path $wixOut 'Talktrace.msi') -Destination $msi

$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
$size = ($files | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host "Wrote $msi"
Write-Host ("  {0} files, {1:N0} MB installed, {2:N0} MB installer" -f $files.Count, ($size / 1MB), ((Get-Item -LiteralPath $msi).Length / 1MB))
