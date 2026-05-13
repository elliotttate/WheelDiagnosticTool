using System;
using System.Collections.Generic;

namespace WheelDiagnosticTool.Models;

/// <summary>
/// Singleton holding everything captured during this wizard run. The report
/// writer turns this into the final .txt / .json at the end.
/// </summary>
public sealed class DiagnosticSession
{
    private static readonly Lazy<DiagnosticSession> s_instance = new(() => new DiagnosticSession());
    public static DiagnosticSession Instance => s_instance.Value;

    private DiagnosticSession() { }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string ToolVersion { get; } = "1.0.0";
    public string SchemaVersion { get; } = "1";

    // System
    public string OsDescription { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string ComputerArchitecture { get; set; } = "";
    public int LogicalProcessors { get; set; }
    public long PhysicalMemoryBytes { get; set; }
    public string DotNetRuntime { get; set; } = "";

    // Enumeration
    public List<DiDeviceSnapshot> DirectInputDevices { get; } = new();
    public List<HidDeviceSnapshot> HidDevices { get; } = new();
    public List<XInputSnapshot> XInputSlots { get; } = new();
    public List<VendorProcess> VendorProcesses { get; } = new();
    public List<VirtualLayer> VirtualLayers { get; } = new();
    public List<string> EnumerationNotes { get; } = new();

    // The user-selected primary device that the rest of the wizard is built
    // around (the wheel). Pedals/shifters can be other DI devices.
    public DiDeviceSnapshot? SelectedDevice { get; set; }

    // Every device that the capture pages should poll simultaneously. Filled
    // when SelectedDevice is chosen — defaults to all DI devices with a
    // known wheel-vendor VID plus the selected device.
    public List<DiDeviceSnapshot> PolledDevices { get; } = new();

    // Walk-through captures, in order. Each prompt the user followed lives here.
    public List<CaptureStepResult> CaptureSteps { get; } = new();

    // Button identifications (one entry per asked label)
    public List<ButtonIdentification> ButtonIdentifications { get; } = new();

    // FFB
    public FfbProbeResult FfbProbe { get; } = new();

    // Idle jitter rows (computed from IDLE_BASELINE step)
    public List<IdleJitterRow> IdleJitter { get; } = new();

    // Inferred mapping (computed after all captures)
    public List<InferredMappingEntry> InferredMapping { get; } = new();

    // FlatOut runtime prediction (computed after all captures)
    public List<string> FlatOutPredictionLines { get; } = new();

    // Final report locations + sharing result
    public string? LocalReportPath { get; set; }
    public string? LocalJsonPath { get; set; }
    public string? FilebinUrl { get; set; }
    public string? FilebinJsonUrl { get; set; }
    public string? FilebinError { get; set; }
}

/// <summary>
/// Virtualization / hiding layer detected on the system (HidHide, ViGEm,
/// vJoy, Interception, Steam Input). Surfacing these inline lets the
/// triager spot the "device exists in HID but not in DI" pattern fast.
/// </summary>
public sealed class VirtualLayer
{
    public string Name { get; init; } = "";       // "HidHide", "ViGEmBus", "vJoy", ...
    public string Kind { get; init; } = "";       // "Process" or "KernelService"
    public string Detail { get; init; } = "";     // process path / service start type
    public bool IsRunning { get; init; }
}
