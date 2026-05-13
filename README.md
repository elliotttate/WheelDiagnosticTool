# Wheel Diagnostic Tool

A standalone Windows desktop app that walks a user through every kind of input a sim-racing wheel can produce, captures the result, and uploads a paste-able diagnostic to filebin.net so it can be shared in one URL.

Purpose: replace the "user reports wheel issue → we ask for a log → they send a log → we ask follow-up questions" cycle with a single, exhaustive first-contact report.

Companion to the in-game `WheelDiagnostics.txt` that the FlatOut/PlayAll engine produces — same field naming, same vendor-software / HID / DirectInput / XInput sections, plus a guided per-axis walkthrough and an interactive FFB probe.

## What it captures

- **System** — OS, build, RAM, CPU count, .NET runtime.
- **DirectInput 8** — every game controller, axes (with HID usage page/usage), buttons, POVs, FFB capability flags.
- **XInput** — slots 0..3, subtype (catches "wheel showing up as a gamepad" cases).
- **Raw HID enumeration** — every HID device with a wheel/pedal/shifter vendor VID, regardless of whether DI can see it. The mismatch between this list and the DI list is the most common cause of "shifter isn't detected".
- **Running vendor software** — G HUB, Pit House, MOZA, Fanatec, vJoy, FreeJoy, Steam, reWASD, JoyToKey... anything that commonly holds an exclusive lock on the wheel.
- **Guided captures** — center / left / right steering, throttle, brake, clutch, handbrake, paddle up/down, every H-shifter gear, every POV direction. Each step records baseline + observed min/max + press direction + which buttons fired during the step.
- **Button identification** — for each common rim action (horn, headlights, pause, view, boost, respawn, wipers, ...) the user presses the button on the rim; the tool records the DirectInput button index.
- **Force-feedback probe** — acquires the device exclusively, plays Constant / Spring / Damper / Sine effects, records the `CreateEffect` HRESULT and asks the user whether they felt each one. Same HRESULT trail the game records in `WheelDiagnostics.txt` (see `FODeviceFilteringManager.cpp:3254`).

## Output

A single `.txt` written to `%LOCALAPPDATA%\WheelDiagnosticTool\WheelDiagnostics-<DeviceName>-<timestamp>.txt`. After the report is written the tool:

1. Uploads it to [filebin.net](https://filebin.net) under a randomly-generated bin name.
2. Copies the resulting URL to the system clipboard.

Filebin bins auto-expire after about a week — long enough for a triage round, not forever.

## Build

Requirements:

- Windows 10 1809 (build 17763) or newer
- Visual Studio 2022 17.10+ with the `.NET desktop development` and `Windows application development` workloads (the latter is what installs the WinUI 3 / Windows App SDK templates)
- .NET 8 SDK

```powershell
cd WheelDiagnosticTool
dotnet restore
dotnet build -c Release -p:Platform=x64
```

To produce a self-contained single-folder publish:

```powershell
dotnet publish -c Release -p:Platform=x64 -p:SelfContained=true -p:PublishSingleFile=false -p:WindowsAppSDKSelfContained=true
```

The output ends up under `WheelDiagnosticTool\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`. Zip the publish folder and that is the distributable.

## Run

After `dotnet build`, double-click `WheelDiagnosticTool.exe` from `bin\x64\Debug\net8.0-windows10.0.19041.0\` or run it from the publish folder.

The wizard takes 3–5 minutes. Every step has a Skip button — skip anything the hardware does not support.

## Dependencies

- `Microsoft.WindowsAppSDK` 1.6 — WinUI 3 + Win32 windowing
- `CommunityToolkit.Mvvm` — ObservableObject / RelayCommand
- `Vortice.DirectInput` — managed DirectInput8 bindings (replacement for the abandoned SharpDX.DirectInput)
- `HidSharp` — HID-level device enumeration without a SetupAPI P/Invoke wall

## Layout

```
WheelDiagnosticTool/
├── App.xaml / App.xaml.cs           // entry point
├── MainWindow.xaml / .cs            // host with header + frame
├── Models/                          // session state and capture records
├── Services/                        // DI / XInput / HID / vendor / FFB / upload
└── Views/                           // one Page per wizard step
```

## Field-for-field parity with the in-game diagnostic

The report's section headings deliberately mirror the in-game `WheelDiagnostics.txt` (see `Flatout/Code/Sources/DeviceFiltering/FODeviceFilteringManager.cpp:2462` in fox3) so a triager reading both files reads them the same way: same VID/PID decoding, same device-type labels, same HID usage decoding, same FFB HRESULT formatting. That parity is intentional — when this tool's report says "Acquire hr=0x800700AA" the triager can grep the same string in the in-game log and confirm both tools saw the same exclusive-lock contention.
