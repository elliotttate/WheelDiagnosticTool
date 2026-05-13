using System;
using System.Collections.Generic;

namespace WheelDiagnosticTool.Models;

/// <summary>
/// Per-axis tracking that we accumulate while the user follows a prompt.
/// We record idle baseline, observed min/max, the dominant axis (the one that
/// moved the most), and the press direction.
/// </summary>
public sealed class AxisObservation
{
    public string AxisName { get; init; } = "";
    public int Baseline { get; set; }
    public int MinObserved { get; set; } = int.MaxValue;
    public int MaxObserved { get; set; } = int.MinValue;
    public int LastValue { get; set; }
    public bool SeenMotion { get; set; }
    public int MaxDeltaFromBaseline { get; set; }
    public int PressDirection { get; set; } // -1, 0, +1

    public int Range => (MinObserved == int.MaxValue || MaxObserved == int.MinValue) ? 0 : MaxObserved - MinObserved;
}

/// <summary>
/// Result of a single guided capture step (e.g. "press throttle fully").
/// Stores every axis observation plus the picked dominant axis, so the
/// report can show both "what we decided" and "what we considered."
/// </summary>
public sealed class CaptureStepResult
{
    public string StepId { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string DominantAxisName { get; set; } = "";
    public int DominantBaseline { get; set; }
    public int DominantMin { get; set; }
    public int DominantMax { get; set; }
    public int DominantPressDirection { get; set; }
    public List<AxisObservation> Axes { get; init; } = new();
    public List<int> ButtonsPressed { get; init; } = new();
    public Dictionary<int, int> PovValues { get; init; } = new(); // pov index -> last value
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    public bool Skipped { get; set; }
    public string? UserNote { get; set; }
}

/// <summary>
/// Records one identified button (e.g. "Upshift paddle" -> button 5).
/// </summary>
public sealed class ButtonIdentification
{
    public string Label { get; init; } = "";
    public int ButtonIndex { get; set; } = -1;
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
