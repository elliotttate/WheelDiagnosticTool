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

    /// <summary>
    /// Buttons that were pressed when the baseline window closed and never
    /// released during the step. These are either:
    ///   - Phantom / stuck button bits the device reports continuously (a
    ///     real failure mode — joeytman's wheel reports btn10 as pressed
    ///     for every step regardless of user activity).
    ///   - A button the user happens to be holding (paddle, button-box
    ///     toggle, shifter rest position).
    /// Either way, surfacing them per step lets the triager spot why the
    ///   inferred mapping might be missing a button (the baseline-snapshot
    ///   fix correctly suppresses press events for these, but without this
    ///   list the user has no signal as to why their paddle wasn't captured).
    /// </summary>
    public IReadOnlyList<HeldButtonRow> HeldThroughoutStep
    {
        get
        {
            var rows = new List<HeldButtonRow>();
            foreach (var kv in _buttonPrevState)
            {
                if (!kv.Value) continue;
                var (guid, idx) = kv.Key;
                bool hadReleaseDuringStep = false;
                for (int i = 0; i < _events.Count; i++)
                {
                    var ev = _events[i];
                    if (ev.DeviceProductGuid == guid && ev.ButtonIndex == idx && !ev.Pressed)
                    {
                        hadReleaseDuringStep = true;
                        break;
                    }
                }
                if (hadReleaseDuringStep) continue;
                string deviceName = "";
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (_devices[i].ProductGuidData1 == guid) { deviceName = _devices[i].ProductName; break; }
                }
                rows.Add(new HeldButtonRow
                {
                    DeviceProductName = deviceName,
                    DeviceProductGuid = guid,
                    ButtonIndex = idx,
                });
            }
            return rows;
        }
    }

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

            // Buttons — emit press/release events with per-device tagging.
            //
            // During the baseline window we deliberately suppress event
            // emission and just snapshot whatever the user is already
            // holding. That eliminates a real failure mode reported in the
            // wild: a tester left thumb on btn10 between steps, and the
            // paddle-up step then showed btn10 as the "pressed button"
            // instead of the actual paddle-up button. After baseline closes,
            // _buttonPrevState matches the current state, so only NEW
            // press/release transitions emit events.
            var btns = state.Buttons;
            if (btns != null)
            {
                for (int i = 0; i < btns.Length; i++)
                {
                    var key = (d.ProductGuidData1, i);
                    bool now = btns[i];
                    if (!_baselineCaptured)
                    {
                        _buttonPrevState[key] = now;
                        continue;
                    }
                    bool was = _buttonPrevState.TryGetValue(key, out var w) && w;
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

            // Press direction = which side of baseline did the axis travel
            // FARTHER. Previous version compared each sample against the
            // already-updated MaxDeltaFromBaseline — once MaxDelta caught
            // up to the new sample's |signed|, the > comparison was never
            // satisfied again, so PressDirection got pinned to whatever
            // the first non-zero sample's sign was. A small idle blip in
            // one direction could then override a much larger real motion
            // the other way (MackRole's STEER_LEFT reported as +1 despite
            // the axis travelling from baseline=17 down to -32768).
            int below = obs.Baseline - (obs.MinObserved == int.MaxValue ? obs.Baseline : obs.MinObserved);
            int above = (obs.MaxObserved == int.MinValue ? obs.Baseline : obs.MaxObserved) - obs.Baseline;
            if (below > above) obs.PressDirection = -1;
            else if (above > below) obs.PressDirection = +1;
            else obs.PressDirection = 0;
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
