# Build a single-exe launcher that embeds the multi-file Release publish as
# a zip resource. On run, the launcher extracts to %LOCALAPPDATA%\
# WheelDiagnosticTool\app\ and starts the inner WheelDiagnosticTool.exe.
#
# This replaces the failed IExpress approach (silent-exit) and the failed
# PublishSingleFile-on-WinUI approach (0xC000027B in Microsoft.UI.Xaml).
# The launcher itself is non-WinUI, so PublishSingleFile works for it.

param(
    [string]$Version = "0.1.2",
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = "Stop"

$publishDir = Join-Path $RepoRoot "WheelDiagnosticTool\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
if (-not (Test-Path $publishDir)) {
    throw "Publish folder not found at $publishDir. Run 'dotnet publish -c Release -p:Platform=x64' first."
}

$resDir = Join-Path $RepoRoot "Launcher\Resources"
New-Item -ItemType Directory -Path $resDir -Force | Out-Null
$zipPath = Join-Path $resDir "WheelDiagnosticTool.zip"

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Zipping publish folder into Launcher resource (~30s)..."
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
$zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  → $zipPath ($zipMB MB)"

Write-Host "Publishing Launcher (single-file, self-contained, net10)..."
$proj = Join-Path $RepoRoot "Launcher\Launcher.csproj"
$pubOut = Join-Path $RepoRoot "Launcher\bin\Release\net10.0-windows\win-x64\publish"
if (Test-Path $pubOut) { Remove-Item -Recurse -Force $pubOut }

Push-Location (Join-Path $RepoRoot "Launcher")
try {
    $env:MSYS_NO_PATHCONV = "1"
    & dotnet publish $proj -c Release -r win-x64 --self-contained true -v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$launcherExe = Join-Path $pubOut "WheelDiagnosticToolSetup.exe"
if (-not (Test-Path $launcherExe)) {
    throw "Launcher publish produced no exe at $launcherExe"
}

$final = Join-Path $RepoRoot ("WheelDiagnosticTool-v{0}-Setup.exe" -f $Version)
if (Test-Path $final) { Remove-Item $final -Force }
Copy-Item $launcherExe $final

$mb = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $final ($mb MB)"
