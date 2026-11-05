param(
    [Parameter(Mandatory = $true)][string]$NdkPath,
    [string]$Abi = "arm64-v8a",
    [int]$ApiLevel = 24
)

$ErrorActionPreference = "Stop"
$SourceDirectory = $PSScriptRoot
$BuildDirectory = Join-Path $SourceDirectory ("build/" + $Abi)
$PackageRoot = Resolve-Path (Join-Path $SourceDirectory "../..")
$OutputDirectory = Join-Path $PackageRoot ("Runtime/Plugins/Android/libs/" + $Abi)
# One translation unit; invoke the NDK driver directly instead of requiring host CMake/Ninja.
if ($Abi -ne "arm64-v8a") { throw "The native sampler currently supports Android arm64-v8a only." }
$Compiler = Join-Path $NdkPath "toolchains/llvm/prebuilt/windows-x86_64/bin/clang++.exe"
if (!(Test-Path -LiteralPath $Compiler)) { throw "Android NDK compiler not found: $Compiler" }
New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$Library = Join-Path $BuildDirectory "libperf_watchdog.so"
& $Compiler "--target=aarch64-linux-android$ApiLevel" -std=c++17 -O2 -g -fPIC -shared `
    -fno-omit-frame-pointer -fvisibility=hidden (Join-Path $SourceDirectory "perf_watchdog_android.cpp") `
    -o $Library -static-libstdc++ -ldl -llog
if ($LASTEXITCODE -ne 0) { throw "Android native build failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $Library -Destination (Join-Path $OutputDirectory "libperf_watchdog.so") -Force
Write-Output "Installed Android plugin to $OutputDirectory"
