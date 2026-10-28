param([switch]$SkipTests, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
$previousResolver = $env:MSBuildEnableWorkloadResolver
try {
    # This solution uses no workloads. Avoid a machine-specific MSI workload-info failure.
    $env:MSBuildEnableWorkloadResolver = 'false'
    dotnet build RemoteInspector.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet run --project tests/RemoteInspector.SmokeTests -c Release --no-build -- artifacts/verification
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    }
    $contained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $destination = if ($FrameworkDependent) { 'artifacts/portable-framework-dependent' } else { 'artifacts/portable-win-x64' }
    dotnet publish src/RemoteInspector.Desktop/RemoteInspector.Desktop.csproj -c Release -r win-x64 --self-contained $contained -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    # Remove stale satellite resources left by earlier multilingual publishes.
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $destination))
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $publishRoot.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish cleanup must stay inside artifacts.' }
    $knownCultures = [Globalization.CultureInfo]::GetCultures([Globalization.CultureTypes]::AllCultures).Name
    foreach ($folder in Get-ChildItem -LiteralPath $publishRoot -Directory) {
        if ($folder.Name -eq 'zh-Hans' -or $folder.Name -notin $knownCultures) { continue }
        foreach ($resource in Get-ChildItem -LiteralPath $folder.FullName -Filter '*.resources.dll' -File) {
            if ($resource.DirectoryName -ne $folder.FullName -or $folder.Parent.FullName -ne $publishRoot) { throw 'Unexpected satellite resource path.' }
            Remove-Item -LiteralPath $resource.FullName
        }
        if (-not (Get-ChildItem -LiteralPath $folder.FullName -Force)) { Remove-Item -LiteralPath $folder.FullName }
    }
    # Keep build-time symbols under bin/; they are not required by the portable application.
    foreach ($symbols in Get-ChildItem -LiteralPath $publishRoot -Filter '*.pdb' -File) {
        Remove-Item -LiteralPath $symbols.FullName
    }
    Copy-Item -LiteralPath README.md -Destination $destination
    Write-Host "Ready: $projectRoot/$destination/RemoteInspector.exe"
} finally {
    $env:MSBuildEnableWorkloadResolver = $previousResolver
    Pop-Location
}
