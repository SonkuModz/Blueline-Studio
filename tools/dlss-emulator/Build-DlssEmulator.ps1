<#
.SYNOPSIS
Builds the NHL 19 DLSS build of shadPS4 on this PC.

.DESCRIPTION
Blueline does not ship the DLSS emulator itself. This script makes it on your own machine:
  1. Downloads the shadPS4 source it is based on (Kravickas NHL19-Build, commit b23fb67, GPL-2.0).
  2. Applies Blueline's DLSS changes (nhl19-dlss.patch, next to this script, GPL-2.0).
  3. Downloads NVIDIA's DLSS SDK files from NVIDIA's own GitHub (github.com/NVIDIA/DLSS, v310.9.16),
     after you accept NVIDIA's licence, and checks every file's hash.
  4. Builds it with Visual Studio's Clang, CMake and Ninja.
The result is shadPS4.exe and nvngx_dlss.dll in the output folder. With -InstallTo it also copies them into
your emulator folder, keeping a backup of the shadPS4.exe that was there.

Needs: Windows 10 or 11 x64, Git, and Visual Studio 2022 (or Build Tools) with "Desktop development with C++",
"C++ Clang tools for Windows" and "C++ CMake tools for Windows". About 6 GB of disk and 15 to 40 minutes.
DLSS itself needs an NVIDIA RTX graphics card.

.PARAMETER WorkDir
Where the source, the SDK and the build go. Default: a "work" folder next to this script.

.PARAMETER OutDir
Where the finished emulator files are copied. Default: a "shadPS4-dlss" folder next to this script.

.PARAMETER InstallTo
Optional. Your shadPS4 folder (the one with shadPS4.exe). The new shadPS4.exe and nvngx_dlss.dll are copied in;
the old shadPS4.exe is kept as shadPS4.exe.before-dlss-<date>.

.PARAMETER AcceptNvidiaLicense
Accept NVIDIA's RTX SDK licence without the prompt. Only use this if you have read it:
https://github.com/NVIDIA/DLSS/blob/v310.9.16/LICENSE.txt

.PARAMETER Jobs
Parallel compile jobs. Default: the number of CPU threads.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = (Join-Path $PSScriptRoot 'work'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'shadPS4-dlss'),
    [string]$InstallTo,
    [switch]$AcceptNvidiaLicense,
    [int]$Jobs = [Environment]::ProcessorCount
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is far slower with the progress bar on 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$SourceRepo   = 'https://github.com/Kravickas/shadPS4.git'
$SourceCommit = 'b23fb6716c4416cd90e8d9f3487d3572f6dab833'
$SdkTag       = 'v310.9.16'
$SdkRaw       = "https://raw.githubusercontent.com/NVIDIA/DLSS/$SdkTag"
$Patch        = Join-Path $PSScriptRoot 'nhl19-dlss.patch'

# NVIDIA files: path in NVIDIA's repo -> path in the SDK folder the build reads, and the SHA-256 they must have.
$SdkFiles = @(
    @{ From = 'LICENSE.txt';                          To = 'LICENSE.txt';         Sha = 'd4216e39ebef5f9b50a6712ebb37beeb5379862a67733a9999c651f21592aaf0' }
    @{ From = 'lib/Windows_x86_64/x64/nvsdk_ngx_d.lib'; To = 'lib/nvsdk_ngx_d.lib'; Sha = 'bc43df9ec032004fde681d3e3afd01cde239e03a0125b822e88674c84287f99c' }
    @{ From = 'lib/Windows_x86_64/rel/nvngx_dlss.dll';  To = 'bin/nvngx_dlss.dll';  Sha = 'd00b5b0ab29c3ca101b70c3a5ecb3a01b7ef33766a3383a28a18623083e6123e' }
)
$Headers = [ordered]@{
    'nvsdk_ngx.h'                      = 'dc38e7467cf415379c9d12ae1b6e4a494c453ed92720fb53e92aecb523e7b848'
    'nvsdk_ngx_defs.h'                 = 'ea23f33497cd274860d1c25a97644fce807dcb0037c594547203343103fad03e'
    'nvsdk_ngx_defs_dlssd.h'           = '2e97fe1595b1334f71b51724f2cb67496e8c296a7a2eec5606da53055882e716'
    'nvsdk_ngx_defs_dlssg.h'           = 'c29765ab9e0125f9d724ae9811cc2abe03738d17486ec8eee80d78a1504549e1'
    'nvsdk_ngx_defs_vk.h'              = '0a24d0861ace7d6b9362a67b7f08bea1b33ea123c5ce730b19133de8a891d031'
    'nvsdk_ngx_helpers.h'              = '5bcbadfe7478b802cf6d3aca4dc5ddd7d0889b99726e69c63f9e9bd555f44471'
    'nvsdk_ngx_helpers_cuda.h'         = 'f2851a76107bdf4fbcb7b261f3c35573c312d8ecb4bc18fb02d9e2cec7705042'
    'nvsdk_ngx_helpers_d3d.h'          = 'ec75224f36ed6580baaf250fa83620405ad81a1d98c2c436654a0c9cf6a6b8ba'
    'nvsdk_ngx_helpers_dlssd.h'        = 'a25fdda925b479dfe869593aeba311ef08793da54e0391f2fe2fe756cbef5532'
    'nvsdk_ngx_helpers_dlssd_cuda.h'   = '7e35cde214d905346a487b9a28c05d35e1c55b480e237fe3de69580a7e332730'
    'nvsdk_ngx_helpers_dlssd_d3d.h'    = '6df9d02c6f47feee3afc8d12340a9f554a7ddce4ddacb1b6a13a42b26c2c6293'
    'nvsdk_ngx_helpers_dlssd_vk.h'     = '11df8fbda532a97fe867c8ef2b40058b49b7898183710f3468e55b1eedb8f40b'
    'nvsdk_ngx_helpers_dlssg.h'        = '6881d21419c40c78ec948b326902ece09fc112b0223144d51c4905f3fced7a1f'
    'nvsdk_ngx_helpers_dlssg_d3d.h'    = '6335ebcb95de39a361e87fe0257d6ce0723e5d8deaa15db3b35afacf2b3f4220'
    'nvsdk_ngx_helpers_dlssg_vk.h'     = 'b371d90b9f4372b9a415e68a0afbdc2f31b7b552e219a9637d848749bc965316'
    'nvsdk_ngx_helpers_vk.h'           = 'c192bff72138f12f770db48e12c1d8f712dfc81ea0e8e055ebb27d8dd1b31623'
    'nvsdk_ngx_loader.h'               = 'abdf5f66a692620538357d29fdf78e3e2a677ee8140b7a638962ecb766585e75'
    'nvsdk_ngx_params.h'               = '943bc8cc5cdae03b6303016fbad3183636f2335ae27a2d18776798c3b4efabbc'
    'nvsdk_ngx_params_dlssd.h'         = 'd41e3ba8f1d72583b55273092c99bf426dc4a0d46de827229e25d5b1325a370e'
    'nvsdk_ngx_params_dlssg.h'         = 'c24fa5aa68ffa2808c828b75cfa32843f1606f80a145f84055ac450d7e6c74ad'
    'nvsdk_ngx_standalone_common.h'    = '38413c3338b0aea0202c07da95d426bfee7080a42913823fa2d9dd585b087b53'
    'nvsdk_ngx_standalone_cuda.h'      = 'b23436ed5040051ac8e81554c9af080f2463c941435bbc3b82222e5431a09499'
    'nvsdk_ngx_vk.h'                   = '2d364ce7132881eb669e9498fd570d74cba563b1fbebc82c235f8e1ad2dd8b6d'
}
foreach ($h in $Headers.GetEnumerator()) { $SdkFiles += @{ From = "include/$($h.Key)"; To = "include/$($h.Key)"; Sha = $h.Value } }

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host ''; Write-Host $text -ForegroundColor Red; exit 1 }
# Windows PowerShell 5.1 turns a native program's stderr (git prints progress there) into errors that stop the script,
# so native programs run with errors relaxed and only their exit code counts.
function Native([string]$exe, [string[]]$arguments, [switch]$Quiet) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & $exe @arguments *> $null }
        else { & $exe @arguments 2>&1 | ForEach-Object { Write-Host "$_" } }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
}
function Run([string]$exe, [string[]]$arguments) {
    $code = Native $exe $arguments
    if ($code -ne 0) { Fail "Failed ($code): $exe $($arguments -join ' ')" }
}
function Sha([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

Write-Host 'Blueline: build the NHL 19 DLSS emulator (shadPS4 + NVIDIA DLSS)' -ForegroundColor White
Write-Host "Work folder:   $WorkDir"
Write-Host "Output folder: $OutDir"
if (-not (Test-Path -LiteralPath $Patch)) { Fail "nhl19-dlss.patch is missing. Keep it next to this script." }

# ---------------------------------------------------------------- 1. tools
Step 'Checking tools'
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) { Fail "Git was not found. Install it (winget install Git.Git), open a new window and run this again." }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { Fail "Visual Studio was not found. Install Visual Studio 2022 Build Tools with 'Desktop development with C++', 'C++ Clang tools for Windows' and 'C++ CMake tools for Windows'." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.VC.Llvm.Clang -property installationPath | Select-Object -First 1
if (-not $vs) { Fail "Visual Studio is missing the C++ tools or 'C++ Clang tools for Windows'. Add them in the Visual Studio Installer (Modify)." }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { Fail "vcvars64.bat not found in $vs." }

# cmake, ninja and clang-cl as the Visual Studio command prompt sees them
$probe = cmd /c "`"$vcvars`" >nul 2>&1 && where cmake 2>nul & where ninja 2>nul & where clang-cl 2>nul"
$missing = @('cmake', 'ninja', 'clang-cl') | Where-Object { -not ($probe -match "\\$_\.exe$") }
if ($missing) { Fail "Not found in the Visual Studio prompt: $($missing -join ', '). Add 'C++ CMake tools for Windows' and 'C++ Clang tools for Windows' in the Visual Studio Installer, or install Ninja (winget install Ninja-build.Ninja)." }
Write-Host "Git:           $($git.Source)"
Write-Host "Visual Studio: $vs"

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$src = Join-Path $WorkDir 'shadPS4'
$sdk = Join-Path $WorkDir 'dlss-sdk'
$build = Join-Path $WorkDir 'build'

# ---------------------------------------------------------------- 2. NVIDIA licence and SDK
Step "NVIDIA DLSS SDK $SdkTag"
$license = Join-Path $sdk 'LICENSE.txt'
New-Item -ItemType Directory -Force -Path $sdk | Out-Null
if (-not (Test-Path -LiteralPath $license) -or (Sha $license) -ne $SdkFiles[0].Sha) {
    Invoke-WebRequest -UseBasicParsing -Uri "$SdkRaw/LICENSE.txt" -OutFile $license
}
$accepted = Join-Path $sdk '.license-accepted'
if (-not (Test-Path -LiteralPath $accepted)) {
    if (-not $AcceptNvidiaLicense) {
        Write-Host ''
        Write-Host 'DLSS is NVIDIA software under the NVIDIA RTX SDKs licence. Blueline does not ship it; it is downloaded'
        Write-Host "from NVIDIA's GitHub for you to use on this PC. Read the licence before you go on:"
        Write-Host "  $license"
        Write-Host "  https://github.com/NVIDIA/DLSS/blob/$SdkTag/LICENSE.txt"
        Write-Host ''
        $answer = Read-Host 'Type yes to accept the NVIDIA licence and download the SDK files'
        if ($answer -ne 'yes') { Fail 'Stopped: the NVIDIA licence was not accepted. Nothing was built.' }
    }
    Set-Content -LiteralPath $accepted -Value "Accepted $(Get-Date -Format s)" -Encoding ASCII
}
foreach ($f in $SdkFiles) {
    $to = Join-Path $sdk $f.To
    if ((Test-Path -LiteralPath $to) -and (Sha $to) -eq $f.Sha) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
    Write-Host "  $($f.From)"
    Invoke-WebRequest -UseBasicParsing -Uri "$SdkRaw/$($f.From)" -OutFile $to
    if ((Sha $to) -ne $f.Sha) { Remove-Item -LiteralPath $to; Fail "$($f.From) did not match its expected hash. Nothing was built." }
}
Write-Host "All $($SdkFiles.Count) SDK files present and checked."

# ---------------------------------------------------------------- 3. source
Step 'shadPS4 source (Kravickas NHL19-Build b23fb67)'
if (-not (Test-Path -LiteralPath (Join-Path $src '.git'))) {
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    Run git @('-C', $src, 'init', '-q')
    Run git @('-C', $src, 'config', 'core.autocrlf', 'false')
    Run git @('-C', $src, 'remote', 'add', 'origin', $SourceRepo)
}
$head = $null
if ((Native git @('-C', $src, 'rev-parse', '-q', '--verify', 'HEAD') -Quiet) -eq 0) { $head = (git -C $src rev-parse HEAD) }
if ($head -ne $SourceCommit) {
    Run git @('-C', $src, 'fetch', '--depth', '1', 'origin', $SourceCommit)
    Run git @('-C', $src, 'checkout', '-q', '--force', $SourceCommit)
}
Write-Host 'Submodules (this is the slow download)...'
if ((Native git @('-C', $src, '-c', 'core.autocrlf=false', 'submodule', 'update', '--init', '--recursive', '--depth', '1', '--jobs', '8')) -ne 0) {
    Write-Host 'Shallow submodule fetch failed; trying full history.'
    Run git @('-C', $src, '-c', 'core.autocrlf=false', 'submodule', 'update', '--init', '--recursive', '--jobs', '8')
}

Step 'Applying the Blueline DLSS patch'
if ((Native git @('-C', $src, 'apply', '--reverse', '--check', $Patch) -Quiet) -eq 0) {
    Write-Host 'Already applied.'
} else {
    if ((Native git @('-C', $src, 'apply', '--check', $Patch)) -ne 0) { Fail "The patch does not apply cleanly. Delete $src and run this again." }
    Run git @('-C', $src, 'apply', $Patch)
    Write-Host 'Applied.'
}

# ---------------------------------------------------------------- 4. build
Step "Building (Release, $Jobs jobs). This takes a while."
$sdkCmake = $sdk -replace '\\', '/'
$cmd = Join-Path $WorkDir 'build.cmd'
@"
@echo off
call "$vcvars" >nul || exit /b 1
if not exist "$build\CMakeCache.txt" (
  cmake -G Ninja -S "$src" -B "$build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang-cl -DCMAKE_CXX_COMPILER=clang-cl "-DDLSS_SDK_DIR=$sdkCmake" || exit /b 1
)
cmake --build "$build" --parallel $Jobs || exit /b 1
"@ | Set-Content -LiteralPath $cmd -Encoding ASCII
if ((Native cmd @('/c', $cmd)) -ne 0) { Fail "The build failed. The messages above say why. Run this again to continue where it stopped." }

$exe = Join-Path $build 'shadps4.exe'
$dll = Join-Path $build 'nvngx_dlss.dll'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $dll)) { Fail 'The build finished but shadps4.exe or nvngx_dlss.dll is missing.' }
if (-not (Select-String -LiteralPath $exe -Pattern 'dlss_enabled' -SimpleMatch -Quiet)) { Fail 'The built emulator has no DLSS setting. Was the NVIDIA SDK found? Delete the build folder and run again.' }

# ---------------------------------------------------------------- 5. output
Step 'Copying the result'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $OutDir 'shadPS4.exe') -Force
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutDir 'nvngx_dlss.dll') -Force
Copy-Item -LiteralPath (Join-Path $src 'LICENSE') -Destination (Join-Path $OutDir 'LICENSE-shadPS4-GPL-2.0.txt') -Force
Copy-Item -LiteralPath $license -Destination (Join-Path $OutDir 'LICENSE-NVIDIA-RTX-SDK.txt') -Force
Write-Host "Done: $OutDir"

if ($InstallTo) {
    if (-not (Test-Path -LiteralPath (Join-Path $InstallTo 'shadPS4.exe'))) { Fail "No shadPS4.exe in $InstallTo. Nothing was installed." }
    $backup = Join-Path $InstallTo ("shadPS4.exe.before-dlss-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Copy-Item -LiteralPath (Join-Path $InstallTo 'shadPS4.exe') -Destination $backup
    Copy-Item -LiteralPath (Join-Path $OutDir 'shadPS4.exe') -Destination (Join-Path $InstallTo 'shadPS4.exe') -Force
    Copy-Item -LiteralPath (Join-Path $OutDir 'nvngx_dlss.dll') -Destination (Join-Path $InstallTo 'nvngx_dlss.dll') -Force
    Write-Host "Installed into $InstallTo (old exe kept as $(Split-Path $backup -Leaf))."
}

Write-Host ''
Write-Host 'Next: copy shadPS4.exe and nvngx_dlss.dll into your shadPS4 folder (keep a copy of the old exe), or run this'
Write-Host 'again with -InstallTo "<your shadPS4 folder>". Then in Blueline Studio, Play tab, turn on "DLSS (DLAA)".'
