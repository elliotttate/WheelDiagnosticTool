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
        else if (action == "STEER") baseDetail += $" — center-rest, dir {source.DominantPressDirection}";
        else if (centeredIdle) baseDetail += " — center-rest pedal (Hall-effect or load cell zero-baseline)";

        entry.Detail = baseDetail;
        if (!string.IsNullOrEmpty(source.ConfidenceReason)) entry.Note = source.ConfidenceReason;

        s.InferredMapping.Add(entry);
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

        var rule = ResolveFlatOutRule(dev);
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

    private static FlatOutRule? ResolveFlatOutRule(DiDeviceSnapshot d)
    {
        uint vid = d.VendorId & 0xFFFFu;
        uint pid = d.ProductId & 0xFFFFu;
        string name = d.ProductName?.ToLowerInvariant() ?? "";

        // ── Logitech ──────────────────────────────────────────────
        if (vid == 0x046D)
        {
            // G29 / G923 PS / G923 Xbox / DFGT / G27 / DFPro all share the
            // same released-high layout in legacy mode; G HUB rewrites the
            // axes to a different layout.
            bool isG29   = pid == 0xC24F;
            bool isG27   = pid == 0xC29B;
            bool isDfgt  = pid == 0xC29A;
            bool isG923P = pid == 0xC267;
            bool isG923X = pid == 0xC266;
            bool isG920  = pid == 0xC262;

            if (isG29 || isG923P || isG923X)
            {
                return new FlatOutRule(
                    Name: $"Logitech G29/G923 class (PID 0x{pid:X4})",
                    SlotRouting: "WHEEL slot 1 (FFB), pedals routed to wheel slot",
                    AxisMap: new()
                    {
                        ("STEER",    "lX"),
                        ("THROTTLE", "lY (G HUB) or lRz (legacy)  — released-high, inverted"),
                        ("BRAKE",    "lRz (G HUB) or slider[0] (legacy) — released-high, inverted"),
                        ("CLUTCH",   "slider[0] (G HUB) or lY (legacy) — released-high, inverted"),
                    },
                    DirectDrive: false,
                    Caveat: "Layout depends on G HUB axis-mode toggle. Repair path " +
                            "`logitech_ghub_lz_stuck_legacy_fallback` exists for users where lZ is " +
                            "frozen and we fall back to the legacy layout.");
            }
            if (isG920)
            {
                return new FlatOutRule(
                    Name: "Logitech G920 (Xbox-class)",
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
        }

        // ── Thrustmaster ──────────────────────────────────────────
        if (vid == 0x044F)
        {
            // T300RS B66E, T300 Ferrari, TX, TS-XW, T128, T248, etc.
            bool isT300 = pid is 0xB66D or 0xB66E or 0xB66F or 0xB677 or 0xB67A;
            bool isTx   = pid == 0xB664;
            bool isTsxw = pid == 0xB66F;
            if (isT300 || isTx || isTsxw)
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
                    DirectDrive: false,
                    Caveat: "TX/TSXW gear paddles are buttons 4/5 by default. Hub firmware mode " +
                            "(Normal vs Compatibility) changes axis claim order on some PIDs.");
            }
        }

        // ── Fanatec ──────────────────────────────────────────────
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

        // ── MOZA ────────────────────────────────────────────────
        if (vid == 0x16D0 || name.Contains("moza"))
        {
            return new FlatOutRule(
                Name: $"MOZA R-series wheelbase",
                SlotRouting: "WHEEL slot 1; SR-P pedals route to ADDON slot if separately enumerated",
                AxisMap: new()
                {
                    ("STEER",    "lX"),
                    ("THROTTLE", "lY or addon-device axis"),
                    ("BRAKE",    "lRz or addon-device axis"),
                    ("CLUTCH",   "slider[0] or addon-device axis"),
                },
                DirectDrive: true,
                Caveat: "Multi-function stalk enumerates as a separate JOYSTICK device; classifier " +
                        "rule prevents the engine from treating it as a wheel.");
        }

        // ── PXN ─────────────────────────────────────────────────
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
