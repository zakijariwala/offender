<#
.SYNOPSIS
    Builds Offender.

.DESCRIPTION
    Dev builds are framework-dependent and run on the installed .NET runtime.
    The -Release switch publishes a single NativeAOT executable with no runtime
    dependency, which is the configuration the footprint targets assume.

.EXAMPLE
    .\build.ps1                # fast dev build
    .\build.ps1 -Release       # shipping NativeAOT exe
    .\build.ps1 -Release -Run  # build it and launch it
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Offender\Offender.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install the .NET SDK: winget install --id Microsoft.DotNet.SDK.10 -e"
}

if (-not (dotnet --list-sdks)) {
    throw "The .NET runtime is installed but no SDK is. Install one: winget install --id Microsoft.DotNet.SDK.10 -e"
}

if ($Release) {
    Write-Host 'Publishing NativeAOT build...' -ForegroundColor Cyan
    Write-Host 'This needs the Visual Studio C++ build tools for the native linker.' -ForegroundColor DarkGray

    dotnet publish $project -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $exe = Join-Path $PSScriptRoot 'src\Offender\bin\Release\net8.0-windows\win-x64\publish\Offender.exe'
} else {
    Write-Host 'Building (framework-dependent)...' -ForegroundColor Cyan

    dotnet build $project -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $exe = Join-Path $PSScriptRoot 'src\Offender\bin\Debug\net8.0-windows\win-x64\Offender.exe'
}

if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host "Built: $exe ($size MB)" -ForegroundColor Green
} else {
    throw "Expected output not found at $exe"
}

if ($Run) {
    Get-Process Offender -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process $exe
    Write-Host 'Launched. Right-click the panel or the tray icon for settings.' -ForegroundColor Green
}
