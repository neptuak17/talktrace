<#
.SYNOPSIS
    Fails unless every native binary in the publish folder is ARM64.

.DESCRIPTION
    Reads the PE header of every .exe and .dll (DESIGN.md, hard constraint 1).
    - Native files must be ARM64 (machine type 0xAA64). ARM64X hybrids also pass: their
      header says ARM64 and an ARM64 process runs their native ARM64 code (.NET's WPF
      ships one, vcruntime140_cor3.dll). ARM64EC and x64 files fail; both report 0x8664.
    - Managed IL-only assemblies are architecture-neutral and pass; managed assemblies
      precompiled for a specific CPU must be ARM64.
    It also lists any Visual C++ runtime DLLs the native files import, for information
    only: running on a clean Windows install is a goal, not a hard requirement.

.PARAMETER Path
    The publish folder. Default: artifacts\publish in the repo.
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'

$MachineArm64 = 0xAA64
$MachineAmd64 = 0x8664
$MachineI386 = 0x014C
$ClrIlOnly = 0x1
$VcRuntimePattern = '^(vcruntime|msvcp|concrt|vcomp)\d+.*\.dll$'

function Get-PeInfo([string] $File) {
    $b = [IO.File]::ReadAllBytes($File)
    if ($b.Length -lt 64 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $null }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $b.Length -or [BitConverter]::ToUInt32($b, $pe) -ne 0x00004550) { return $null }

    $coff = $pe + 4
    $machine = [BitConverter]::ToUInt16($b, $coff)
    $sectionCount = [BitConverter]::ToUInt16($b, $coff + 2)
    $optionalSize = [BitConverter]::ToUInt16($b, $coff + 16)
    $opt = $coff + 20
    $is64 = [BitConverter]::ToUInt16($b, $opt) -eq 0x20B
    $dirCount = [BitConverter]::ToUInt32($b, $opt + $(if ($is64) { 108 } else { 92 }))
    $dirs = $opt + $(if ($is64) { 112 } else { 96 })
    $sections = $opt + $optionalSize

    $toOffset = {
        param([uint32] $rva)
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $s = $sections + 40 * $i
            $va = [BitConverter]::ToUInt32($b, $s + 12)
            $span = [Math]::Max([BitConverter]::ToUInt32($b, $s + 8), [BitConverter]::ToUInt32($b, $s + 16))
            if ($rva -ge $va -and $rva -lt $va + $span) {
                return [int]($rva - $va + [BitConverter]::ToUInt32($b, $s + 20))
            }
        }
        return -1
    }
    $dirRva = { param([int] $index) if ($index -lt $dirCount) { [BitConverter]::ToUInt32($b, $dirs + 8 * $index) } else { 0 } }

    # CLR header (data directory 14): present only in managed assemblies.
    $managed = $false; $ilOnly = $false
    $clr = & $dirRva 14
    if ($clr -ne 0) {
        $managed = $true
        $off = & $toOffset $clr
        if ($off -ge 0) { $ilOnly = ([BitConverter]::ToUInt32($b, $off + 16) -band $ClrIlOnly) -ne 0 }
    }

    # Load config (data directory 10): a CHPE metadata pointer marks a hybrid (ARM64X or ARM64EC).
    $hybrid = $false
    $loadConfig = & $dirRva 10
    if ($is64 -and $loadConfig -ne 0) {
        $off = & $toOffset $loadConfig
        if ($off -ge 0 -and [BitConverter]::ToUInt32($b, $off) -ge 208) {
            $hybrid = [BitConverter]::ToUInt64($b, $off + 200) -ne 0
        }
    }

    # Import table (data directory 1): the DLLs this file loads.
    $imports = @()
    $importRva = & $dirRva 1
    if (-not $managed -and $importRva -ne 0) {
        $off = & $toOffset $importRva
        while ($off -ge 0 -and $off + 20 -le $b.Length) {
            $nameRva = [BitConverter]::ToUInt32($b, $off + 12)
            if ($nameRva -eq 0) { break }
            $nameOff = & $toOffset $nameRva
            if ($nameOff -ge 0) {
                $end = [Array]::IndexOf($b, [byte]0, $nameOff)
                $imports += [Text.Encoding]::ASCII.GetString($b, $nameOff, $end - $nameOff)
            }
            $off += 20
        }
    }

    [pscustomobject]@{ Machine = $machine; Managed = $managed; IlOnly = $ilOnly; Hybrid = $hybrid; Imports = $imports }
}

$root = (Resolve-Path $Path).Path
$files = Get-ChildItem $root -Recurse -File -Include *.exe, *.dll
if (-not $files) { throw "No .exe or .dll files in $root. Publish first." }

$results = foreach ($file in $files) {
    $info = Get-PeInfo $file.FullName
    $name = $file.FullName.Substring($root.Length + 1)
    if (-not $info) {
        [pscustomobject]@{ File = $name; Kind = 'not a PE file'; Ok = $false; Imports = @() }
        continue
    }
    $kind, $ok = if ($info.Managed) {
        if ($info.IlOnly -and $info.Machine -eq $MachineI386) { 'managed, any CPU', $true }
        elseif ($info.Machine -eq $MachineArm64) { 'managed, ARM64', $true }
        else { ('managed, machine 0x{0:X4}' -f $info.Machine), $false }
    }
    elseif ($info.Machine -eq $MachineArm64 -and $info.Hybrid) { 'native, ARM64X (runs as ARM64)', $true }
    elseif ($info.Machine -eq $MachineArm64) { 'native, ARM64', $true }
    elseif ($info.Machine -eq $MachineAmd64) { 'native, x64 or ARM64EC', $false }
    elseif ($info.Machine -eq $MachineI386) { 'native, x86', $false }
    else { ('native, machine 0x{0:X4}' -f $info.Machine), $false }
    [pscustomobject]@{ File = $name; Kind = $kind; Ok = $ok; Imports = $info.Imports }
}

$native = @($results | Where-Object { $_.Kind -like 'native*' -or -not $_.Ok })
$managed = @($results | Where-Object { $_.Kind -like 'managed*' -and $_.Ok })
$failures = @($results | Where-Object { -not $_.Ok })

Write-Host "Checked $($results.Count) files in $root"
Write-Host ''
$native | Sort-Object File | Format-Table @{ n = 'Native file'; e = { $_.File } }, Kind, @{ n = 'Result'; e = { if ($_.Ok) { 'OK' } else { 'FAIL' } } } -AutoSize | Out-String | Write-Host
Write-Host ("Managed assemblies: {0} ({1} any CPU, {2} precompiled for ARM64); all OK" -f $managed.Count,
    @($managed | Where-Object Kind -eq 'managed, any CPU').Count, @($managed | Where-Object Kind -eq 'managed, ARM64').Count)

$shipped = @($files | ForEach-Object { $_.Name.ToLowerInvariant() })
$vc = $native | ForEach-Object {
    foreach ($import in $_.Imports) {
        if ($import -match $VcRuntimePattern) {
            [pscustomobject]@{ File = $_.File; Imports = $import; Shipped = $shipped -contains $import.ToLowerInvariant() }
        }
    }
}
Write-Host ''
if ($vc) {
    Write-Host 'Visual C++ runtime imports (information only):'
    $vc | Format-Table -AutoSize | Out-String | Write-Host
}
else {
    Write-Host 'Visual C++ runtime imports: none.'
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "FAIL: $($failures.Count) file(s) are not ARM64." -ForegroundColor Red
    exit 1
}
Write-Host 'PASS: every native binary is ARM64.' -ForegroundColor Green
exit 0
