param(
    [ValidateSet("Release", "RelWithDebInfo")][string]$Configuration = "RelWithDebInfo"
)

$ErrorActionPreference = "Stop"
$SourceDirectory = $PSScriptRoot
$BuildDirectory = Join-Path $SourceDirectory "build/x86_64"
$PackageRoot = Resolve-Path (Join-Path $SourceDirectory "../..")
$OutputDirectory = Join-Path $PackageRoot "Runtime/Plugins/x86_64"
$SymbolDirectory = Join-Path $BuildDirectory "symbols"

New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $SymbolDirectory | Out-Null

cmake -S $SourceDirectory -B $BuildDirectory -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE" }
cmake --build $BuildDirectory --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "Windows native build failed with exit code $LASTEXITCODE" }

$Library = Join-Path $BuildDirectory "$Configuration/perf_watchdog.dll"
$Symbols = Join-Path $BuildDirectory "$Configuration/perf_watchdog.pdb"
if (!(Test-Path -LiteralPath $Library)) { throw "Native library was not produced: $Library" }

Copy-Item -LiteralPath $Library -Destination (Join-Path $OutputDirectory "perf_watchdog.dll") -Force
if (Test-Path -LiteralPath $Symbols) {
    Copy-Item -LiteralPath $Symbols -Destination (Join-Path $SymbolDirectory "perf_watchdog.pdb") -Force
}
Write-Output "Installed Windows x64 plugin to $OutputDirectory"
Write-Output "Archive symbols from $SymbolDirectory with the matching player build"
