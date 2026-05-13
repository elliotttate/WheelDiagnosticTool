# Build a self-extracting setup .exe wrapping the multi-file Release publish.
#
# Why not PublishSingleFile? WinUI 3 + WindowsAppSDK 1.8 + PublishSingleFile
# produces a binary that 0xC000027B-crashes inside Microsoft.UI.Xaml.dll
# during XAML init when the DLLs are in the .NET self-extract temp dir.
# Known issue tracked across microsoft/microsoft-ui-xaml and
# microsoft/WindowsAppSDK; not fixed as of WAS 1.8.
#
# Workaround: ship the multi-file publish, but wrap it in a self-extracting
# installer so the user still only downloads/runs one .exe.
#
# The installer:
#   1. IExpress wraps WheelDiagnosticTool.zip + launch.cmd into a CAB exe.
#   2. On run, IExpress extracts the CAB to %TEMP% and runs launch.cmd.
#   3. launch.cmd expands the inner zip to %LOCALAPPDATA%\WheelDiagnosticTool\app\
#      (or refreshes it if older), then starts WheelDiagnosticTool.exe.
#   4. Subsequent launches can use the inner exe directly — the user can pin
#      it to the start menu / taskbar after first run.

param(
    [string]$Version = "0.1.2",
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = "Stop"

$publishDir = Join-Path $RepoRoot "WheelDiagnosticTool\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
if (-not (Test-Path $publishDir)) {
    throw "Publish folder not found at $publishDir. Run 'dotnet publish -c Release -p:Platform=x64' first."
}

$stage = Join-Path $RepoRoot "sfx-stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Path $stage | Out-Null

# 1. Zip the publish folder. Optimal compression matters — 218MB → ~85MB.
$innerZip = Join-Path $stage "WheelDiagnosticTool.zip"
Write-Host "Zipping publish folder (this takes ~30s for ~85MB output)..."
Compress-Archive -Path "$publishDir\*" -DestinationPath $innerZip -CompressionLevel Optimal
$zipMB = [math]::Round((Get-Item $innerZip).Length / 1MB, 1)
Write-Host "  → $innerZip  ($zipMB MB)"

# 2. Write the launcher .cmd that runs after IExpress extracts.
#    %~dp0 is the IExpress temp-extract dir, where WheelDiagnosticTool.zip will live.
$launchCmd = @'
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "APPROOT=%LOCALAPPDATA%\WheelDiagnosticTool"
set "APPDIR=%APPROOT%\app"
set "APPEXE=%APPDIR%\WheelDiagnosticTool.exe"

if not exist "%APPROOT%" mkdir "%APPROOT%"
if exist "%APPDIR%" rmdir /s /q "%APPDIR%"
mkdir "%APPDIR%"

echo Extracting Wheel Diagnostic Tool to %APPDIR% ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%~dp0WheelDiagnosticTool.zip' -DestinationPath '%APPDIR%' -Force"
if errorlevel 1 (
    echo.
    echo Failed to extract the inner archive. Aborting.
    pause
    exit /b 1
)

if not exist "%APPEXE%" (
    echo.
    echo Expected %APPEXE% after extraction, but it is missing.
    pause
    exit /b 1
)

echo Launching Wheel Diagnostic Tool...
start "" "%APPEXE%"
'@
$launchPath = Join-Path $stage "launch.cmd"
Set-Content -LiteralPath $launchPath -Value $launchCmd -Encoding ASCII
Write-Host "  → $launchPath"

# 3. Write the IExpress SED config.
$output = Join-Path $RepoRoot ("WheelDiagnosticTool-v{0}-Setup.exe" -f $Version)
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$output
FriendlyName=Wheel Diagnostic Tool $Version Setup
AppLaunched=cmd /c launch.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="launch.cmd"
FILE1="WheelDiagnosticTool.zip"
[SourceFiles]
SourceFiles0=$stage
[SourceFiles0]
%FILE0%=
%FILE1%=
"@
$sedPath = Join-Path $stage "WheelDiagnosticTool.sed"
Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII
Write-Host "  → $sedPath"

# 4. Run IExpress. /N = no interactive mode, /Q = quiet.
Write-Host "Running iexpress (this takes ~20s)..."
$ie = Start-Process -FilePath "$env:WINDIR\System32\iexpress.exe" -ArgumentList "/N", "/Q", $sedPath -Wait -PassThru -NoNewWindow
if ($ie.ExitCode -ne 0) {
    throw "iexpress failed with exit code $($ie.ExitCode)"
}

if (-not (Test-Path $output)) {
    throw "iexpress reported success but $output was not produced"
}

$outMB = [math]::Round((Get-Item $output).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $output  ($outMB MB)"
Write-Host "Stage dir kept at $stage for debugging."
