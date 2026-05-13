using System;
using System.Collections.Generic;
using Vortice.DirectInput;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Polls a list of DirectInput devices each tick and pushes per-device
/// state into AxisObservation / ButtonEvent accumulators. The capture
/// pages own one of these for the duration of their step.
/// </summary>
public sealed class MultiDevicePoller
{
    private readonly DirectInputService _di;
    private readonly List<DiDeviceSnapshot> _devices;
    private readonly Dictionary<string, AxisObservation> _axes = new();
    private readonly List<ButtonEvent> _events = new();
    private readonly Dictionary<(uint guid, int idx), bool> _buttonPrevState = new();
    private readonly Dictionary<int, int> _povsPrimary = new();
    private readonly DateTime _startUtc;
    private int _baselineFramesRemaining;
    private bool _baselineCaptured;

    public MultiDevicePoller(DirectInputService di, IEnumerable<DiDeviceSnapshot> devices, int baselineFrames = 30)
    {
        _di = di;
        _devices = new List<DiDeviceSnapshot>(devices);
        _baselineFramesRemaining = baselineFrames;
        _startUtc = DateTime.UtcNow;

        // Pre-seed an entry per (device, axis) so the live table doesn't pop
        // in as the user moves things.
        foreach (var d in _devices)
        {
            foreach (var a in d.Axes)
            {
                var key = MakeKey(d.ProductGuidData1, a.Name);
                _axes[key] = new AxisObservation
                {
                    DeviceProductName = d.ProductName,
                    DeviceProductGuid = d.ProductGuidData1,
                    DeviceInstanceGuid = d.InstanceGuid,
                    AxisName = a.Name,
                };
            }
        }
    }

    public bool BaselineCaptured => _baselineCaptured;
    public IReadOnlyList<DiDeviceSnapshot> Devices => _devices;
    public IReadOnlyDictionary<string, AxisObservation> Axes => _axes;
    public IReadOnlyList<ButtonEvent> ButtonEvents => _events;
    public IReadOnlyDictionary<int, int> PrimaryPovs => _povsPrimary;

    public void ResetBaseline(int frames = 30)
    {
        _baselineCaptured = false;
        _baselineFramesRemaining = frames;
        foreach (var a in _axes.Values)
        {
            a.MinObserved = int.MaxValue;
            a.MaxObserved = int.MinValue;
            a.MaxDeltaFromBaseline = 0;
            a.SeenMotion = false;
            a.PressDirection = 0;
        }
        _events.Clear();
        _buttonPrevState.Clear();
        _povsPrimary.Clear();
    }

    public void Tick(DiDeviceSnapshot? primaryWheel)
    {
        foreach (var d in _devices)
        {
            if (!Guid.TryParse(d.InstanceGuid, out var guid)) continue;
            if (!_di.Poll(guid, out var state) || state == null) continue;

            // Axes
            ObserveAxis(d, "lX",        state.X);
            ObserveAxis(d, "lY",        state.Y);
            ObserveAxis(d, "lZ",        state.Z);
            ObserveAxis(d, "lRx",       state.RotationX);
            ObserveAxis(d, "lRy",       state.RotationY);
            ObserveAxis(d, "lRz",       state.RotationZ);
            var sliders = state.Sliders;
            if (sliders != null && sliders.Length > 0) ObserveAxis(d, "slider[0]", sliders[0]);
            if (sliders != null && sliders.Length > 1) ObserveAxis(d, "slider[1]", sliders[1]);

            // Buttons — emit press/release events with per-device tagging
            var btns = state.Buttons;
            if (btns != null)
            {
                for (int i = 0; i < btns.Length; i++)
                {
                    var key = (d.ProductGuidData1, i);
                    bool was = _buttonPrevState.TryGetValue(key, out var w) && w;
                    bool now = btns[i];
                    if (now != was)
                    {
                        _events.Add(new ButtonEvent
                        {
                            DeviceProductName = d.ProductName,
                            DeviceProductGuid = d.ProductGuidData1,
                            ButtonIndex = i,
                            Pressed = now,
                            TimeOffsetSec = (DateTime.UtcNow - _startUtc).TotalSeconds,
                        });
                        _buttonPrevState[key] = now;
                    }
                }
            }

            // POVs — only track from the primary wheel to keep the dict small;
            // the report cares about hat direction, not the per-device redundancy.
            if (primaryWheel != null && d.ProductGuidData1 == primaryWheel.ProductGuidData1)
            {
                var povs = state.PointOfViewControllers;
                if (povs != null)
                {
                    for (int i = 0; i < povs.Length; i++)
                        _povsPrimary[i] = povs[i];
                }
            }
        }

        if (!_baselineCaptured)
        {
            _baselineFramesRemaining--;
            if (_baselineFramesRemaining <= 0)
            {
                foreach (var a in _axes.Values)
                    a.Baseline = a.LastValue;
                _baselineCaptured = true;
            }
        }
    }

    private static string MakeKey(uint guid, string axisName) => $"{guid:X8}::{axisName}";

    private void ObserveAxis(DiDeviceSnapshot d, string axisName, int value)
    {
        var key = MakeKey(d.ProductGuidData1, axisName);
        if (!_axes.TryGetValue(key, out var obs))
        {
            obs = new AxisObservation
            {
                DeviceProductName = d.ProductName,
                DeviceProductGuid = d.ProductGuidData1,
                DeviceInstanceGuid = d.InstanceGuid,
                AxisName = axisName,
            };
            _axes[key] = obs;
        }

        obs.LastValue = value;
        if (value < obs.MinObserved) obs.MinObserved = value;
        if (value > obs.MaxObserved) obs.MaxObserved = value;

        if (_baselineCaptured)
        {
            int delta = Math.Abs(value - obs.Baseline);
            if (delta > obs.MaxDeltaFromBaseline) obs.MaxDeltaFromBaseline = delta;
            if (delta > 1500) obs.SeenMotion = true;

            int signed = value - obs.Baseline;
            if (Math.Abs(signed) > Math.Abs(obs.PressDirection * obs.MaxDeltaFromBaseline))
                obs.PressDirection = signed > 0 ? +1 : -1;
        }
    }

    /// <summary>
    /// Pick the axis with the largest MaxDeltaFromBaseline across all devices
    /// + tag a confidence based on how big the travel is and whether a second
    /// axis came close to it.
    /// </summary>
    public (AxisObservation? top, CaptureConfidence confidence, string reason) PickDominant(int motionThreshold = 1500)
    {
        AxisObservation? top = null;
        AxisObservation? second = null;
        foreach (var a in _axes.Values)
        {
            if (a.MaxDeltaFromBaseline < motionThreshold) continue;
            if (top == null || a.MaxDeltaFromBaseline > top.MaxDeltaFromBaseline)
            {
                second = top;
                top = a;
            }
            else if (second == null || a.MaxDeltaFromBaseline > second.MaxDeltaFromBaseline)
            {
                second = a;
            }
        }

        if (top == null) return (null, CaptureConfidence.Missed, $"no axis exceeded {motionThreshold} from baseline");

        // Ambiguous if a different physical axis (not the same axis on a
        // different device) is within 30% of the leader's travel.
        if (second != null
            && !string.Equals(second.AxisName, top.AxisName, StringComparison.Ordinal)
            && second.MaxDeltaFromBaseline > (top.MaxDeltaFromBaseline * 0.7))
        {
            return (top, CaptureConfidence.Ambiguous,
                $"top={top.AxisName}@{top.MaxDeltaFromBaseline} vs runner-up={second.AxisName}@{second.MaxDeltaFromBaseline}");
        }

        if (top.MaxDeltaFromBaseline >= 20000) return (top, CaptureConfidence.High, $"{top.MaxDeltaFromBaseline} units of travel");
        return (top, CaptureConfidence.Low, $"{top.MaxDeltaFromBaseline} units of travel (under high-confidence threshold of 20000)");
    }
}
