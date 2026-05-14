using System;
using System.Collections.Generic;

namespace WheelDiagnosticTool.Models;

/// <summary>
/// Per-axis tracking that we accumulate while the user follows a prompt.
/// Keyed by (DeviceProductGuid, AxisName) so two physical devices each with
/// their own "lX" get separate observations (Fanatec wheel + ClubSport
/// pedals + H-shifter is the canonical case).
/// </summary>
public sealed class AxisObservation
{
    public string DeviceProductName { get; init; } = "";
    public uint DeviceProductGuid { get; init; }
    public string DeviceInstanceGuid { get; init; } = "";
    public string AxisName { get; init; } = "";
    public int Baseline { get; set; }
    public int MinObserved { get; set; } = int.MaxValue;
    public int MaxObserved { get; set; } = int.MinValue;
    public int LastValue { get; set; }
    public bool SeenMotion { get; set; }
    public int MaxDeltaFromBaseline { get; set; }
    public int PressDirection { get; set; } // -1, 0, +1

    public int Range => (MinObserved == int.MaxValue || MaxObserved == int.MinValue) ? 0 : MaxObserved - MinObserved;
    public string Key => $"{DeviceProductGuid:X8}::{AxisName}";
}

/// <summary>
/// A single button press/release event in time during a capture step. We
/// keep these per-event (not just "pressed during step") so paddle/gear
/// reports can say exactly which button fired in what order.
/// </summary>
public sealed class ButtonEvent
{
    public string DeviceProductName { get; init; } = "";
    public uint DeviceProductGuid { get; init; }
    public int ButtonIndex { get; init; }
    public bool Pressed { get; init; } // true=press, false=release
    public double TimeOffsetSec { get; init; } // relative to step start
}

/// <summary>
/// A button that was pressed when the step's baseline window closed and
/// was never released during the step. Either the user is holding it on
/// purpose or it is a phantom/stuck button bit the device reports
/// continuously. Surfaced separately from ButtonEvents so the report can
/// flag it without it polluting paddle/gear inference.
/// </summary>
public sealed class HeldButtonRow
{
    public string DeviceProductName { get; init; } = "";
    public uint DeviceProductGuid { get; init; }
    public int ButtonIndex { get; init; }
}

/// <summary>
/// Result of a single guided capture step. Stores every axis observation
/// plus the picked dominant axis, so the report can show both "what we
/// decided" and "what we considered". Confidence reflects how sure the
/// dominant-axis pick is.
/// </summary>
public enum CaptureConfidence
{
    NotApplicable, // step doesn't have a single dominant axis (e.g. button step)
    High,          // > 20000 axis travel + clear winner
    Low,           // 5000..20000 travel
    Missed,        // < 5000 — almost no motion, user probably didn't do the action
    Ambiguous,     // two or more axes had similar travel
}

public sealed class CaptureStepResult
{
    public string StepId { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string DominantAxisName { get; set; } = "";
    public string DominantDeviceProductName { get; set; } = "";
    public uint DominantDeviceProductGuid { get; set; }
    public int DominantBaseline { get; set; }
    public int DominantMin { get; set; }
    public int DominantMax { get; set; }
    public int DominantPressDirection { get; set; }
    public CaptureConfidence Confidence { get; set; } = CaptureConfidence.NotApplicable;
    public string? ConfidenceReason { get; set; }
    public List<AxisObservation> Axes { get; init; } = new();
    public List<ButtonEvent> ButtonEvents { get; init; } = new();
    public List<HeldButtonRow> ButtonsHeldThroughout { get; init; } = new();
    public Dictionary<int, int> PovValues { get; init; } = new(); // pov index -> last value (single-device, primary wheel)
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    public bool Skipped { get; set; }
    public string? UserNote { get; set; }

    // Per-axis bleed for crosstalk steps: how much OTHER axes moved while
    // the dominant one was being actuated.
    public List<AxisObservation> CrosstalkBleed { get; init; } = new();
}

/// <summary>
/// Records one identified button (e.g. "Upshift paddle" -> button 5 on device X).
/// </summary>
public sealed class ButtonIdentification
{
    public string Label { get; init; } = "";
    public int ButtonIndex { get; set; } = -1;
    public string DeviceProductName { get; set; } = "";
    public uint DeviceProductGuid { get; set; }
    public bool Skipped { get; set; }
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// One FFB effect probe result.
/// </summary>
public sealed class FfbEffectResult
{
    public string EffectName { get; init; } = "";
    public int HResult { get; set; }
    public bool CreateSucceeded { get; set; }
    public bool? UserFelt { get; set; } // null = not asked, true/false = answered

    // For direction-meaningful effects (constant force left / right):
    // "left" / "right" / "none" / null. Lets the report cross-reference
    // axis direction against FFB direction (the AccuForce-style mismatch
    // where lX moves correctly but constant-force sign is inverted).
    public string? UserDirection { get; set; }

    // The intended direction the effect should have pulled if the device
    // followed the standard sign convention. Set by the prompt code.
    public string? IntendedDirection { get; set; }

    public string? Note { get; set; }
}

public sealed class FfbProbeResult
{
    public bool ExclusiveAcquireOk { get; set; }
    public int AcquireHResult { get; set; }
    public bool AutoCenterSetOk { get; set; }
    public int AutoCenterHResult { get; set; }
    public List<FfbEffectResult> Effects { get; init; } = new();
    public List<string> RunningVendorProcessesAtProbeTime { get; init; } = new();
    public bool Skipped { get; set; }
}

/// <summary>
/// Per-axis idle-jitter analysis (from the 5s hands-off step).
/// </summary>
public sealed class IdleJitterRow
{
    public string DeviceProductName { get; init; } = "";
    public uint DeviceProductGuid { get; init; }
    public string AxisName { get; init; } = "";
    public int Mean { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public double StandardDeviation { get; set; }
    public int SampleCount { get; set; }
    public string Verdict { get; set; } = ""; // "quiet", "noisy", "drifting"
}

/// <summary>
/// Inferred-mapping output produced by analyzing all captures together.
/// </summary>
public sealed class InferredMappingEntry
{
    public string Action { get; init; } = ""; // STEER, THROTTLE, BRAKE, CLUTCH, HANDBRAKE, PADDLE_UP, etc.
    public string SourceDeviceName { get; set; } = "";
    public uint SourceDeviceProductGuid { get; set; }
    public string Detail { get; set; } = ""; // "lY (inverted/released-high)"
    public CaptureConfidence Confidence { get; set; } = CaptureConfidence.Missed;
    public string Note { get; set; } = "";
}
