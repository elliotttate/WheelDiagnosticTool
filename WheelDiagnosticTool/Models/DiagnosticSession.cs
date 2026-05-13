using System;
using System.Collections.Generic;

namespace WheelDiagnosticTool.Models;

/// <summary>
/// Singleton holding everything captured during this wizard run. The report
/// writer turns this into the final .txt at the end.
/// </summary>
public sealed class DiagnosticSession
{
    private static readonly Lazy<DiagnosticSession> s_instance = new(() => new DiagnosticSession());
    public static DiagnosticSession Instance => s_instance.Value;

    private DiagnosticSession() { }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string ToolVersion { get; } = "1.0.0";

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
    public List<string> EnumerationNotes { get; } = new();

    // The user-selected device that the rest of the wizard is targeting
    public DiDeviceSnapshot? SelectedDevice { get; set; }

    // Walk-through captures, in order. Each prompt the user followed lives here.
    public List<CaptureStepResult> CaptureSteps { get; } = new();

    // Button identifications (one entry per asked label)
    public List<ButtonIdentification> ButtonIdentifications { get; } = new();

    // FFB
    public FfbProbeResult FfbProbe { get; } = new();

    // Final report location + sharing result
    public string? LocalReportPath { get; set; }
    public string? FilebinUrl { get; set; }
    public string? FilebinError { get; set; }
}
