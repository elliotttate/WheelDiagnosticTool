using System;
using System.Collections.Generic;
using System.Linq;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Walks the captured DiagnosticSession and produces the high-level
/// "inferred mapping" + the FlatOut/PlayAll runtime prediction. The
/// inferred mapping is what the *tool* saw the user do. The FlatOut
/// prediction is what the *game* would do given the device's identity,
/// using the known-device rules baked into InputDriverPC.cpp /
/// WheelDeviceDB.cpp. Comparing them is the fast path for spotting
/// game-vs-hardware misroute bugs.
/// </summary>
public static class MappingAnalysis
{
    public static void Run(DiagnosticSession s)
    {
        s.InferredMapping.Clear();
        s.FlatOutPredictionLines.Clear();

        BuildInferredMapping(s);
        BuildFlatOutPrediction(s);
    }

    private static void BuildInferredMapping(DiagnosticSession s)
    {
        AddSingleAxisAction(s, "STEER",     pickFromLeftRight: true);
        AddSingleAxisAction(s, "THROTTLE",  fromStepIds: new[] { "PEDAL_THROTTLE" });
        AddSingleAxisAction(s, "BRAKE",     fromStepIds: new[] { "PEDAL_BRAKE" });
        AddSingleAxisAction(s, "CLUTCH",    fromStepIds: new[] { "PEDAL_CLUTCH" });
        AddSingleAxisAction(s, "HANDBRAKE", fromStepIds: new[] { "PEDAL_HANDBRAKE" });

        AddButtonAction(s, "PADDLE_UP",   "PADDLE_UP");
        AddButtonAction(s, "PADDLE_DOWN", "PADDLE_DOWN");
        for (int i = 1; i <= 7; i++) AddButtonAction(s, $"GEAR_{i}", $"GEAR_{i}");
        AddButtonAction(s, "GEAR_R", "GEAR_R");

        AddPovAction(s);
    }

    /// <summary>
    /// POV / D-pad detection. Three patterns observed in the wild:
    ///   1. Real DirectInput POV control fires (state.PointOfViewControllers
    ///      transitions to a non-(-1) value). Easy case.
    ///   2. The rim D-pad is encoded as 4 momentary half-axes — each
    ///      direction is its own axis going 0..max when pressed and
    ///      returning to 0 when released. Common Fanatec FunkySwitch
    ///      pattern. No POV ever fires in DI; the user sees "POV 0
    ///      ended at value -1" and (Mobilistic's report) concludes the
    ///      tool didn't detect their D-pad.
    ///   3. The D-pad is plain buttons (one per direction).
    /// We surface all three explicitly so the report says what it is,
    /// instead of leaving the data buried in the per-axis observation table.
    /// </summary>
    private static void AddPovAction(DiagnosticSession s)
    {
        var entry = new InferredMappingEntry { Action = "POV / D-PAD" };
        var step = s.CaptureSteps.FirstOrDefault(c => c.StepId == "POV_ALL");
        if (step == null)
        {
            entry.Confidence = CaptureConfidence.Missed;
            entry.Note = "POV step not run";
            s.InferredMapping.Add(entry);
            return;
        }
        if (step.Skipped)
        {
            entry.Confidence = CaptureConfidence.NotApplicable;
            entry.Note = "step skipped";
            s.InferredMapping.Add(entry);
            return;
        }

        // Pattern 1: a real DI POV control was active at end of step. The
        // PovValues we record is "last value seen", which is the safe
        // signal here — if any POV ended pressed, it's a real POV.
        foreach (var kv in step.PovValues)
        {
            if (kv.Value >= 0)
            {
                entry.SourceDeviceName = s.SelectedDevice?.ProductName ?? "";
                entry.SourceDeviceProductGuid = s.SelectedDevice?.ProductGuidData1 ?? 0;
                entry.Detail = $"DirectInput POV index {kv.Key} (last value {kv.Value}/100°)";
                entry.Confidence = CaptureConfidence.High;
                s.InferredMapping.Add(entry);
                return;
            }
        }

        // Pattern 2: no POV fired, but multiple axes on the primary wheel
        // spiked during the step. We look specifically on the SELECTED
        // device because pedal devices may also wiggle by accident.
        var primaryGuid = s.SelectedDevice?.ProductGuidData1 ?? 0;
        var primarySpikes = step.Axes
            .Where(a => a.DeviceProductGuid == primaryGuid && a.MaxDeltaFromBaseline > 5000)
            .OrderByDescending(a => a.MaxDeltaFromBaseline)
            .ToList();

        if (primarySpikes.Count >= 2)
        {
            var axisList = string.Join(", ", primarySpikes.Take(8).Select(a => a.AxisName));
            entry.SourceDeviceName = primarySpikes[0].DeviceProductName;
            entry.SourceDeviceProductGuid = primaryGuid;
            entry.Detail = $"axis-encoded D-pad — {primarySpikes.Count} momentary half-axes ({axisList})";
            entry.Confidence = CaptureConfidence.High;
            entry.Note = "No DI POV control fired; each direction is reported as its own axis spiking 0→max " +
                        "when pressed (Fanatec FunkySwitch / custom-wheel pattern). Engine binding code must " +
                        "treat these as buttons / per-direction axes, NOT as a POV.";
            s.InferredMapping.Add(entry);
            return;
        }

        // Pattern 3: nothing on axes, but buttons fired (D-pad as plain buttons).
        var pressedButtons = step.ButtonEvents
            .Where(ev => ev.Pressed && ev.DeviceProductGuid == primaryGuid)
            .GroupBy(ev => ev.ButtonIndex)
            .Select(g => g.Key)
            .OrderBy(b => b)
            .ToList();

        if (pressedButtons.Count >= 4)
        {
            entry.SourceDeviceName = s.SelectedDevice?.ProductName ?? "";
            entry.SourceDeviceProductGuid = primaryGuid;
            entry.Detail = $"button-encoded D-pad — buttons {string.Join(", ", pressedButtons)}";
            entry.Confidence = CaptureConfidence.High;
            entry.Note = "Each D-pad direction fires its own button index.";
            s.InferredMapping.Add(entry);
            return;
        }

        if (pressedButtons.Count > 0)
        {
            entry.SourceDeviceName = s.SelectedDevice?.ProductName ?? "";
            entry.SourceDeviceProductGuid = primaryGuid;
            entry.Detail = $"click only — buttons {string.Join(", ", pressedButtons)}";
            entry.Confidence = CaptureConfidence.Low;
            entry.Note = "No POV control, axis-encoded directions, or 4+ direction buttons detected — only " +
                        "the D-pad click-in (or whatever button(s) fired). User may not have pressed all " +
                        "four directions, or the device encodes directions differently.";
            s.InferredMapping.Add(entry);
            return;
        }

        entry.Confidence = CaptureConfidence.Missed;
        entry.Note = "no POV control, axes, or buttons activated during the POV step";
        s.InferredMapping.Add(entry);
    }

    private static void AddSingleAxisAction(DiagnosticSession s, string action,
        bool pickFromLeftRight = false, string[]? fromStepIds = null)
    {
        var entry = new InferredMappingEntry { Action = action };

        CaptureStepResult? source = null;
        if (pickFromLeftRight)
        {
            // For STEER: prefer whichever of LEFT/RIGHT showed more travel.
            var left  = s.CaptureSteps.FirstOrDefault(c => c.StepId == "STEER_LEFT" && !c.Skipped);
            var right = s.CaptureSteps.FirstOrDefault(c => c.StepId == "STEER_RIGHT" && !c.Skipped);
            source = (left, right) switch
            {
                (null, null) => null,
                (not null, null) => left,
                (null, not null) => right,
                _ => Math.Abs(right!.DominantMax - right.DominantBaseline) > Math.Abs(left!.DominantBaseline - left.DominantMin) ? right : left
            };
        }
        else if (fromStepIds != null)
        {
            foreach (var id in fromStepIds)
            {
                var step = s.CaptureSteps.FirstOrDefault(c => c.StepId == id);
                if (step == null) continue;
                if (step.Skipped) { entry.Confidence = CaptureConfidence.NotApplicable; entry.Note = "step skipped by user"; break; }
                source = step; break;
            }
        }

        if (source == null || string.IsNullOrEmpty(source.DominantAxisName))
        {
            entry.Confidence = source?.Skipped == true ? CaptureConfidence.NotApplicable : CaptureConfidence.Missed;
            entry.Note = source?.Skipped == true ? "step skipped" : "no axis exceeded threshold";
            s.InferredMapping.Add(entry);
            return;
        }

        entry.SourceDeviceName = source.DominantDeviceProductName;
        entry.SourceDeviceProductGuid = source.DominantDeviceProductGuid;
        entry.Confidence = source.Confidence;

        string baseDetail = $"{source.DominantAxisName} (range {source.DominantMin}..{source.DominantMax}, travel {source.DominantMax - source.DominantMin})";

        // For pedals: figure out if the resting position was at one extreme
        // (released-high) by comparing baseline against axis range.
        bool releasedHigh = action != "STEER"
            && source.DominantBaseline > 20000
            && source.DominantPressDirection < 0;
        bool releasedLow = action != "STEER"
            && source.DominantBaseline < -20000
            && source.DominantPressDirection > 0;
        bool centeredIdle = action != "STEER"
            && Math.Abs(source.DominantBaseline) < 8000;

        if (releasedHigh) baseDetail += " — released-high / inverted (axis sits at max, drops when pressed)";
        else if (releasedLow) baseDetail += " — released-low / standard (axis sits at min, rises when pressed)";
        else if (action == "STEER") baseDetail += DescribeSteeringDirection(s, source);
        else if (centeredIdle) baseDetail += " — center-rest pedal (Hall-effect or load cell zero-baseline)";

        entry.Detail = baseDetail;
        if (!string.IsNullOrEmpty(source.ConfidenceReason)) entry.Note = source.ConfidenceReason;

        s.InferredMapping.Add(entry);
    }

    private static string DescribeSteeringDirection(DiagnosticSession s, CaptureStepResult source)
    {
        var left = s.CaptureSteps.FirstOrDefault(c => c.StepId == "STEER_LEFT" && !c.Skipped);
        var right = s.CaptureSteps.FirstOrDefault(c => c.StepId == "STEER_RIGHT" && !c.Skipped);
        if (left == null || right == null)
            return $" — center-rest, dir {source.DominantPressDirection}";

        bool sameAxis =
            left.DominantDeviceProductGuid == right.DominantDeviceProductGuid
            && string.Equals(left.DominantAxisName, right.DominantAxisName, StringComparison.Ordinal);
        if (!sameAxis || string.IsNullOrEmpty(left.DominantAxisName))
            return $" — center-rest, left={left.DominantAxisName}, right={right.DominantAxisName}";

        string leftDir = left.DominantPressDirection < 0 ? "decreases" :
                         left.DominantPressDirection > 0 ? "increases" : "does not move";
        string rightDir = right.DominantPressDirection > 0 ? "increases" :
                          right.DominantPressDirection < 0 ? "decreases" : "does not move";

        if (left.DominantPressDirection < 0 && right.DominantPressDirection > 0)
            return $" — center-rest, normal direction (left {leftDir}, right {rightDir})";
        if (left.DominantPressDirection > 0 && right.DominantPressDirection < 0)
            return $" — center-rest, REVERSED direction (left {leftDir}, right {rightDir})";

        return $" — center-rest, unusual direction (left {leftDir}, right {rightDir})";
    }

    private static void AddButtonAction(DiagnosticSession s, string action, string stepId)
    {
        var entry = new InferredMappingEntry { Action = action };
        var step = s.CaptureSteps.FirstOrDefault(c => c.StepId == stepId);
        if (step == null) { entry.Confidence = CaptureConfidence.Missed; entry.Note = "step not run"; s.InferredMapping.Add(entry); return; }
        if (step.Skipped) { entry.Confidence = CaptureConfidence.NotApplicable; entry.Note = "step skipped"; s.InferredMapping.Add(entry); return; }

        // Unique pressed buttons in this step
        var presses = step.ButtonEvents.Where(ev => ev.Pressed)
            .GroupBy(ev => (ev.DeviceProductGuid, ev.ButtonIndex))
            .Select(g => g.First())
            .ToList();

        if (presses.Count == 0)
        {
            entry.Confidence = CaptureConfidence.Missed;
            entry.Note = "no button fired during this step";
            // For paddle steps, an axis may also do the work — surface that too.
            if (!string.IsNullOrEmpty(step.DominantAxisName))
            {
                entry.SourceDeviceName = step.DominantDeviceProductName;
                entry.Detail = $"axis {step.DominantAxisName} (paddle on an axis?) travel {step.DominantMax - step.DominantMin}";
                entry.Confidence = step.Confidence;
            }
        }
        else if (presses.Count == 1)
        {
            entry.SourceDeviceName = presses[0].DeviceProductName;
            entry.SourceDeviceProductGuid = presses[0].DeviceProductGuid;
            entry.Detail = $"button {presses[0].ButtonIndex}";
            entry.Confidence = CaptureConfidence.High;
        }
        else
        {
            entry.SourceDeviceName = presses[0].DeviceProductName;
            entry.SourceDeviceProductGuid = presses[0].DeviceProductGuid;
            entry.Detail = "buttons: " + string.Join(", ", presses.Select(p => $"{p.DeviceProductName}::btn{p.ButtonIndex}"));
            entry.Confidence = CaptureConfidence.Ambiguous;
            entry.Note = "multiple buttons fired";
        }
        s.InferredMapping.Add(entry);
    }

    /// <summary>
    /// Codifies the major known-device rules from the FlatOut engine
    /// (WheelDeviceDB / WheelDeviceClassifier / InputDriverPC repair paths)
    /// in plain prose so the report says exactly what the engine would do.
    /// Not exhaustive — it covers the families that come up most often in
    /// Sentry. Unknown devices get a "would use RawWheelLearn" line.
    /// </summary>
    private static void BuildFlatOutPrediction(DiagnosticSession s)
    {
        var dev = s.SelectedDevice;
        if (dev == null)
        {
            s.FlatOutPredictionLines.Add("(no device selected — skipping prediction)");
            return;
        }

        s.FlatOutPredictionLines.Add($"For device VID=0x{dev.VendorId:X4} PID=0x{dev.ProductId:X4} \"{dev.ProductName}\":");

        var rule = ResolveFlatOutRule(dev, s);
        if (rule == null)
        {
            s.FlatOutPredictionLines.Add("  No hardcoded entry in WheelDeviceDB / classifier for this VID/PID combination.");
            s.FlatOutPredictionLines.Add("  Engine path: would fall through to runtime RawWheelLearn, using whatever");
            s.FlatOutPredictionLines.Add("  axis the user manually maps in the wheel-options menu. If RawWheelLearn");
            s.FlatOutPredictionLines.Add("  hasn't run, axes will default to the engine's generic wheel layout");
            s.FlatOutPredictionLines.Add("  (STEER=lX, THROTTLE=lRz inverted, BRAKE=lY inverted, CLUTCH=slider[0] inverted).");
            return;
        }

        s.FlatOutPredictionLines.Add($"  Known-device rule: {rule.Name}");
        s.FlatOutPredictionLines.Add($"  Slot routing      : {rule.SlotRouting}");
        s.FlatOutPredictionLines.Add($"  Predicted axis map:");
        foreach (var (action, axis) in rule.AxisMap)
            s.FlatOutPredictionLines.Add($"    {action,-12} = {axis}");
        if (!string.IsNullOrEmpty(rule.Caveat))
            s.FlatOutPredictionLines.Add($"  Caveat: {rule.Caveat}");
        if (rule.DirectDrive) s.FlatOutPredictionLines.Add("  FFB profile       : Direct-drive friendly (lower-gain default, no saturation clamp)");
    }

    private sealed record FlatOutRule(
        string Name,
        string SlotRouting,
        List<(string Action, string Axis)> AxisMap,
        bool DirectDrive,
        string? Caveat);

    /// <summary>
    /// Did the user's captures show meaningful travel on a given axis?
    /// Returned by inspecting all PEDAL_* steps for a >5000-unit deflection on
    /// the named axis. Used by the dead-lZ fallback that mirrors the game's
    /// `logitech_ghub_lz_stuck_legacy_fallback` repair path.
    /// </summary>
    private static bool AxisShowedTravel(DiagnosticSession s, string axisName, int threshold = 5000)
    {
        foreach (var step in s.CaptureSteps)
        {
            if (step.Skipped) continue;
            if (!step.StepId.StartsWith("PEDAL_")) continue;
            foreach (var obs in step.Axes)
            {
                if (string.Equals(obs.AxisName, axisName, StringComparison.Ordinal)
                    && obs.MaxDeltaFromBaseline >= threshold)
                    return true;
            }
        }
        return false;
    }

    private static FlatOutRule? ResolveFlatOutRule(DiDeviceSnapshot d, DiagnosticSession s)
    {
        uint vid = d.VendorId & 0xFFFFu;
        uint pid = d.ProductId & 0xFFFFu;
        string name = d.ProductName?.ToLowerInvariant() ?? "";

        // ── Logitech (VID 0x046D) ─────────────────────────────────
        // PIDs from PlayAll/Code/Runtime/Win32/PlayAll/Drivers/Input/InputDriverPC.h
        if (vid == 0x046D)
        {
            // Correct PIDs (the previous tool revision had G923_PS and
            // G923_XBOX swapped, which is why a real G923 Xbox showed
            // "no hardcoded entry"):
            bool isG29   = pid == 0xC24F;
            bool isG27   = pid == 0xC29B;
            bool isDfgt  = pid == 0xC29A;
            bool isG923P = pid == 0xC266; // PRODUCTID_LOGITECH_G923_PS
            bool isG923X = pid == 0xC26E; // PRODUCTID_LOGITECH_G923_XBOX
            bool isG920  = pid == 0xC262;
            bool isGPro  = pid == 0xC272 || pid == 0xC279; // G PRO / G PRO Xbox (best-effort PIDs)
            bool isRs50  = pid == 0xC27B;

            if (isG29 || isG923P || isG923X)
            {
                // Dead-lZ fallback: mirrors the in-game
                // `logitech_ghub_lz_stuck_legacy_fallback` repair path. If
                // the user's PEDAL_THROTTLE / PEDAL_BRAKE / PEDAL_CLUTCH
                // captures show lY and lRz / slider[0] moving but lZ never
                // budged, predict the legacy layout (throttle=lY,
                // brake=lRz, clutch=slider[0]) instead of the G HUB layout.
                bool lzMoved      = AxisShowedTravel(s, "lZ");
                bool lyMoved      = AxisShowedTravel(s, "lY");
                bool lRzMoved     = AxisShowedTravel(s, "lRz");
                bool slider0Moved = AxisShowedTravel(s, "slider[0]");
                bool deadLzFallback = !lzMoved && lyMoved && (lRzMoved || slider0Moved);

                string ruleLabel = isG923X
                    ? "Logitech G923 (Xbox, PID 0xC26E)"
                    : isG923P
                        ? "Logitech G923 (PS, PID 0xC266)"
                        : "Logitech G29 (PID 0xC24F)";

                if (deadLzFallback)
                {
                    return new FlatOutRule(
                        Name: ruleLabel + " — DEAD-lZ legacy fallback active",
                        SlotRouting: "WHEEL slot 1 (FFB); pedals on wheel device",
                        AxisMap: new()
                        {
                            ("STEER",    "lX"),
                            ("THROTTLE", "lY  — released-high, inverted (lZ showed no travel, legacy layout)"),
                            ("BRAKE",    "lRz — released-high, inverted"),
                            ("CLUTCH",   "slider[0] — released-high, inverted"),
                        },
                        DirectDrive: false,
                        Caveat: "lZ showed zero travel during pedal capture. The engine's " +
                                "`logitech_ghub_lz_stuck_legacy_fallback` repair path would activate " +
                                "and map throttle=lY, brake=lRz, clutch=slider[0] regardless of " +
                                "whether G HUB reports the modern axis layout.");
                }

                return new FlatOutRule(
                    Name: ruleLabel,
                    SlotRouting: "WHEEL slot 1 (FFB); pedals on wheel device",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "lZ (G HUB) / lY (legacy)  — released-high, inverted"),
                        ("BRAKE",    "lRz (both modes)           — released-high, inverted"),
                        ("CLUTCH",   "lY (G HUB) / slider[0] (legacy) — released-high, inverted"),
                    },
                    DirectDrive: false,
                    Caveat: "Layout depends on G HUB axis-mode toggle. If pedal captures show lZ at " +
                            "zero travel, the engine's `logitech_ghub_lz_stuck_legacy_fallback` repair " +
                            "kicks in and uses the legacy layout (throttle=lY, brake=lRz, clutch=slider[0]).");
            }
            if (isG920)
            {
                return new FlatOutRule(
                    Name: "Logitech G920 (PID 0xC262)",
                    SlotRouting: "WHEEL slot 1; H-shifter binds via paddle buttons when G HUB-merged",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "lRz — released-high, inverted"),
                        ("BRAKE",    "lY  — released-high, inverted"),
                        ("CLUTCH",   "slider[0] — released-high, inverted"),
                    },
                    DirectDrive: false,
                    Caveat: "G920 normally exposes Xbox-mode HID. If user has the Logitech G HUB Xbox " +
                            "shifter merging enabled, gear buttons appear on the wheel device.");
            }
            if (isG27 || isDfgt)
            {
                return new FlatOutRule(
                    Name: isG27 ? "Logitech G27" : "Logitech DFGT",
                    SlotRouting: "WHEEL slot 1",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "lY — released-high, inverted"),
                        ("BRAKE",    "lRz — released-high, inverted"),
                        ("CLUTCH",   "slider[0] — released-high, inverted"),
                    },
                    DirectDrive: false,
                    Caveat: null);
            }
            if (isGPro || isRs50)
            {
                return new FlatOutRule(
                    Name: isRs50 ? "Logitech RS50 Base" : "Logitech G PRO Racing Wheel",
                    SlotRouting: "WHEEL slot 1 (DD-friendly); pedals usually on a separate device",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "external addon device, or per pedal firmware"),
                        ("BRAKE",    "external addon device, or per pedal firmware"),
                        ("CLUTCH",   "external addon device, or per pedal firmware"),
                    },
                    DirectDrive: true,
                    Caveat: "DD-class. FFB uses the safer 0.50 friction-scale profile to avoid the " +
                            "stiction issues older Logitech profiles caused on G PRO / RS50.");
            }
        }

        // ── Thrustmaster (VID 0x044F) ─────────────────────────────
        if (vid == 0x044F)
        {
            bool isT300 = pid is 0xB66D or 0xB66E or 0xB66F or 0xB677 or 0xB67A;
            bool isTx   = pid == 0xB664;
            bool isTsxw = pid == 0xB66F;
            bool isTGT  = pid == 0xB684;
            if (isT300 || isTx || isTsxw || isTGT)
            {
                return new FlatOutRule(
                    Name: $"Thrustmaster T-series (PID 0x{pid:X4})",
                    SlotRouting: "WHEEL slot 1; T3PA-style pedals appear as wheel-internal axes",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "lRz — released-high, inverted"),
                        ("BRAKE",    "lY  — released-high, inverted"),
                        ("CLUTCH",   "slider[0] — released-high, inverted"),
                    },
                    DirectDrive: isTGT,
                    Caveat: "TX/TSXW gear paddles are buttons 4/5 by default. Hub firmware mode " +
                            "(Normal vs Compatibility) changes axis claim order on some PIDs.");
            }
        }

        // ── Fanatec (VID 0x0EB7) ──────────────────────────────────
        if (vid == 0x0EB7)
        {
            return new FlatOutRule(
                Name: $"Fanatec wheelbase / ClubSport class (PID 0x{pid:X4})",
                SlotRouting: "WHEEL slot 1 (DD-friendly); ClubSport V3 pedals enumerate separately and route to ADDON slot",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "external addon device or lY (depending on hub firmware)"),
                    ("BRAKE",    "external addon device or lRz"),
                    ("CLUTCH",   "external addon device or slider[0]"),
                },
                DirectDrive: true,
                Caveat: "ClubSport V3 pedals had a known repair path: HID usage 'Rx' may report on " +
                        "DI 'lX', and the throttle vs handbrake conflict needs the manual-bind override. " +
                        "Hub firmware Normal vs Compatibility mode affects PID + duplicate endpoint behavior.");
        }

        // ── MOZA (VID 0x346E, NOT 0x16D0) ─────────────────────────
        // The previous tool revision had MOZA on 0x16D0 (Mosart Semiconductor),
        // which is actually Simucube / OpenFFBoard's reseller VID.
        if (vid == 0x346E || name.Contains("moza"))
        {
            if (pid == 0x001F || name.Contains("hbp") || name.Contains("handbrake"))
            {
                return new FlatOutRule(
                    Name: "MOZA HBP Handbrake (PID 0x001F)",
                    SlotRouting: "ADDON handbrake slot; never steals the wheel slot",
                    AxisMap: new()
                    {
                        ("HANDBRAKE", "slider[0] — handbrake axis"),
                    },
                    DirectDrive: false,
                    Caveat: "The tool polls this as a separate add-on device alongside the selected wheel. " +
                            "If the guided handbrake step says MISSED while DirectInput lists the HBP, the HBP " +
                            "was probably not included in the polled-device set.");
            }

            if (pid == 0x0001 || pid == 0x0003)
            {
                return new FlatOutRule(
                    Name: pid == 0x0001 ? "MOZA CRP Pedals" : "MOZA SR-P Pedals",
                    SlotRouting: "ADDON pedal slot; suppresses wheel-base phantom pedal axes when connected",
                    AxisMap: new()
                    {
                        ("THROTTLE", "lRx — released-low / floor-positive"),
                        ("BRAKE",    "lRy — released-low / floor-positive"),
                        ("CLUTCH",   "lRz — released-low / floor-positive"),
                    },
                    DirectDrive: false,
                    Caveat: null);
            }

            return new FlatOutRule(
                Name: "MOZA R-series wheelbase (VID 0x346E)",
                SlotRouting: "WHEEL slot 1; SR-P pedals route to ADDON slot if separately enumerated",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "lZ or addon-device axis"),
                    ("BRAKE",    "lRz or addon-device axis"),
                    ("CLUTCH",   "slider[0] or addon-device axis"),
                },
                DirectDrive: true,
                Caveat: "Multi-function stalk enumerates as a separate JOYSTICK device; the engine's " +
                        "classifier rule prevents it from being treated as a wheel.");
        }

        // ── Simucube / DIY DD (VID 0x16D0 — Mosart) ───────────────
        if (vid == 0x16D0 || name.Contains("simucube") || name.Contains("osw"))
        {
            return new FlatOutRule(
                Name: "Simucube / OSW DIY DD",
                SlotRouting: "WHEEL slot 1 (DD-friendly); separate pedal/shifter devices route to addon slots",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "external pedal device"),
                    ("BRAKE",    "external pedal device"),
                    ("CLUTCH",   "external pedal device"),
                },
                DirectDrive: true,
                Caveat: "FFB strength scales to 0.50 friction-coefficient on DD bases to prevent stiction.");
        }

        // ── SimXperience AccuForce (VID 0x1FC9 / PID 0x804C) ─────
        if (vid == 0x1FC9 || name.Contains("simxperience") || name.Contains("accuforce"))
        {
            return new FlatOutRule(
                Name: pid == 0x804C ? "SimXperience AccuForce Pro (PID 0x804C)" : $"SimXperience / AccuForce class (PID 0x{pid:X4})",
                SlotRouting: "WHEEL slot 1 (DD-friendly); separate pedals, shifters, and handbrakes route to add-on slots",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "external pedal device"),
                    ("BRAKE",    "external pedal device"),
                    ("CLUTCH",   "external pedal device"),
                    ("HANDBRAKE", "external handbrake add-on"),
                },
                DirectDrive: true,
                Caveat: "FlatOut already has an exact WheelDeviceDB row for 0x804C1FC9. Normal DirectInput " +
                        "steering is left=negative/right=positive; the report's steering-direction note tells " +
                        "whether this user's wheel is actually reversed.");
        }

        // ── Simagic ───────────────────────────────────────────────
        if (name.Contains("simagic"))
        {
            return new FlatOutRule(
                Name: "Simagic Alpha series",
                SlotRouting: "WHEEL slot 1 (DD-friendly); P1000 pedals on a separate device",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "external pedal device"),
                    ("BRAKE",    "external pedal device"),
                    ("CLUTCH",   "external pedal device"),
                },
                DirectDrive: true,
                Caveat: null);
        }

        // ── PXN (VID 0x11FF) ──────────────────────────────────────
        if (vid == 0x11FF || name.Contains("pxn"))
        {
            return new FlatOutRule(
                Name: "PXN VD-series",
                SlotRouting: "WHEEL slot 1 (no DD, no addon separation — pedals are wheel-internal)",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "lY  — center-rest, sign-detected"),
                    ("BRAKE",    "lRz — center-rest, sign-detected"),
                    ("CLUTCH",   "slider[0]"),
                },
                DirectDrive: false,
                Caveat: "PXN VD4 had a config-version migration path for an earlier wrong axis claim.");
        }

        return null;
    }
}
