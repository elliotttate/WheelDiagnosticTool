using System;
using System.IO;
using System.Linq;
using System.Text;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Turns the captured DiagnosticSession into the final .txt that ships
/// to filebin / Sentry. Section ordering is deliberate:
///
///   1. Inferred mapping summary  ← the triager reads this first
///   2. FlatOut runtime prediction (does the engine agree?)
///   3. HID-usage → DI-field map  (the "what is the throttle, really?" table)
///   4. System / vendor software / virtual layers
///   5. DirectInput / HID / XInput enumeration
///   6. Selected device
///   7. Idle jitter
///   8. Per-step capture details (with button events + crosstalk bleed)
///   9. Button identifications
///  10. FFB probe results
/// </summary>
public static class ReportWriter
{
    public static string WriteToFile(DiagnosticSession s, string? overridePath = null)
    {
        // Run the analysis layer first so the summary at the top is populated.
        MappingAnalysis.Run(s);

        var sb = new StringBuilder(128 * 1024);
        WriteTo(s, sb);
        var path = overridePath ?? BuildDefaultPath(s);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        s.LocalReportPath = path;
        return path;
    }

    public static string BuildDefaultPath(DiagnosticSession s)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WheelDiagnosticTool");
        var safeName = s.SelectedDevice?.ProductName ?? "Device";
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        var stamp = s.StartedUtc.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(folder, $"WheelDiagnostics-{safeName}-{stamp}.txt");
    }

    public static void WriteTo(DiagnosticSession s, StringBuilder sb)
    {
        var hr = new string('=', 78);
        sb.AppendLine(hr);
        sb.AppendLine("Wheel Diagnostic Tool report");
        sb.AppendLine(hr);
        sb.AppendLine($"  tool version          = {s.ToolVersion}");
        sb.AppendLine($"  schema version        = {s.SchemaVersion}");
        sb.AppendLine($"  captured (UTC)        = {s.StartedUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  captured (local)      = {s.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        WriteInferredMapping(s, sb);
        WriteFlatOutPrediction(s, sb);
        WriteHidToDiMap(s, sb);

        sb.AppendLine("System");
        sb.AppendLine("------");
        sb.AppendLine($"  os description        = {s.OsDescription}");
        sb.AppendLine($"  os version            = {s.OsVersion}");
        sb.AppendLine($"  machine name          = {s.MachineName}");
        sb.AppendLine($"  architecture          = {s.ComputerArchitecture}");
        sb.AppendLine($"  logical cpus          = {s.LogicalProcessors}");
        sb.AppendLine($"  total physical memory = {(s.PhysicalMemoryBytes / (1024L * 1024L * 1024L)):F1} GB");
        sb.AppendLine($"  .NET runtime          = {s.DotNetRuntime}");
        sb.AppendLine();

        WriteVendorProcesses(s, sb);
        WriteVirtualLayers(s, sb);
        WriteDirectInputDevices(s, sb);
        WriteHidDevices(s, sb);
        WriteXInputSlots(s, sb);
        WriteSelectedDevice(s, sb);
        WriteIdleJitter(s, sb);
        WriteCaptures(s, sb);
        WriteButtonIdentifications(s, sb);
        WriteFfbProbe(s, sb);

        sb.AppendLine();
        sb.AppendLine(hr);
        sb.AppendLine("End of report");
        sb.AppendLine(hr);
    }

    private static void WriteInferredMapping(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Inferred mapping (what the captures say the user's hardware is doing)");
        sb.AppendLine("---------------------------------------------------------------------");
        if (s.InferredMapping.Count == 0)
        {
            sb.AppendLine("  (no captures yet — wizard not completed)");
            sb.AppendLine();
            return;
        }
        foreach (var m in s.InferredMapping)
        {
            string conf = m.Confidence switch
            {
                CaptureConfidence.High         => "high  ",
                CaptureConfidence.Low          => "low   ",
                CaptureConfidence.Missed       => "MISSED",
                CaptureConfidence.Ambiguous    => "AMBIG ",
                CaptureConfidence.NotApplicable => "n/a   ",
                _ => "?     "
            };
            string src = string.IsNullOrEmpty(m.SourceDeviceName) ? "" : $" @ {m.SourceDeviceName}";
            sb.AppendLine($"  [{conf}] {m.Action,-12} = {m.Detail}{src}");
            if (!string.IsNullOrEmpty(m.Note))
                sb.AppendLine($"             {m.Note}");
        }
        sb.AppendLine();
    }

    private static void WriteFlatOutPrediction(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("FlatOut/PlayAll engine prediction");
        sb.AppendLine("---------------------------------");
        sb.AppendLine("What the in-game wheel system would do for this device based on known-device");
        sb.AppendLine("rules. Compare against 'Inferred mapping' above — disagreements between the");
        sb.AppendLine("two are the fast path for spotting game-vs-hardware misroute bugs.");
        if (s.FlatOutPredictionLines.Count == 0)
        {
            sb.AppendLine("  (no prediction available)");
        }
        else
        {
            foreach (var line in s.FlatOutPredictionLines) sb.AppendLine(line);
        }
        sb.AppendLine();
    }

    private static void WriteHidToDiMap(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("HID usage → DirectInput field → inferred action");
        sb.AppendLine("-----------------------------------------------");
        sb.AppendLine("Bridges the gap between 'the HID descriptor says this axis is Throttle' and");
        sb.AppendLine("'the engine reads it as lRz'. The DI-field column comes from the DIJOYSTATE2");
        sb.AppendLine("byte offset of the axis, NOT the HID descriptor name (those can disagree —");
        sb.AppendLine("e.g. Logitech wheels often label an axis 'Y-Axis' that lands on the lRz offset).");
        sb.AppendLine("The inferred-action column is from the user's PEDAL_/STEER_ captures.");
        var dev = s.SelectedDevice;
        if (dev == null || dev.Axes.Count == 0)
        {
            sb.AppendLine("  (no device selected, or device exposes no axes)");
            sb.AppendLine();
            return;
        }
        sb.AppendLine();
        sb.AppendLine("  HID page:usage  HID meaning  HID descriptor name        ->  DI offset (field)  ->  inferred action");

        // Build a lookup from DI-axis-name (lX/lY/etc.) to inferred action
        // for the selected device. The captures key on DI offset names, NOT
        // HID descriptor names, so the match has to be on Axes[i].Name —
        // which after the v0.1.3 fix is also DI-offset-derived.
        var axisToAction = new System.Collections.Generic.Dictionary<string, (string action, CaptureConfidence conf)>();
        foreach (var m in s.InferredMapping)
        {
            if (m.SourceDeviceProductGuid != dev.ProductGuidData1) continue;
            var token = (m.Detail ?? "").Split(' ', 2)[0];
            if (!string.IsNullOrEmpty(token)) axisToAction[token] = (m.Action, m.Confidence);
        }

        foreach (var a in dev.Axes.OrderBy(a => a.DIByteOffset < 0 ? int.MaxValue : a.DIByteOffset))
        {
            string hid = a.HasUsage ? $"0x{a.UsagePage:X2}:0x{a.Usage:X2}" : "    -";
            string meaning = string.IsNullOrEmpty(a.UsageMeaning) ? "?" : a.UsageMeaning;
            string hidName = string.IsNullOrEmpty(a.HidName) ? "(no name)" : a.HidName;
            string diField = a.DIByteOffset >= 0 ? $"+{a.DIByteOffset,2} ({a.Name})" : a.Name;
            string action = "(no capture matched this axis)";
            if (axisToAction.TryGetValue(a.Name, out var entry))
                action = $"{entry.action} (conf={entry.conf})";
            sb.AppendLine($"  {hid,-14}  {meaning,-11}  {Trim(hidName, 26),-26}  ->  {diField,-18}  ->  {action}");
        }
        sb.AppendLine();
    }

    private static void WriteVendorProcesses(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Running wheel-vendor software (any process here can hold an exclusive");
        sb.AppendLine("lock on the wheel and block FFB CreateEffect — close them before racing");
        sb.AppendLine("if FFB is missing):");
        if (s.VendorProcesses.Count == 0)
        {
            sb.AppendLine("  (none detected)");
        }
        else
        {
            foreach (var p in s.VendorProcesses)
            {
                sb.AppendLine($"  pid={p.Pid,-6} {p.Vendor,-14} {p.ProcessName,-30} {p.Description}");
            }
        }
        sb.AppendLine();
    }

    private static void WriteVirtualLayers(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Virtual / hiding layers (HidHide, ViGEm, vJoy, Interception)");
        sb.AppendLine("--------------------------------------------------------------");
        sb.AppendLine("When a HID device is visible to Windows but missing from DirectInput, the");
        sb.AppendLine("most common cause is one of these layers filtering or virtualizing it.");
        if (s.VirtualLayers.Count == 0)
        {
            sb.AppendLine("  (no known virtualization/hiding layers detected)");
            sb.AppendLine();
            return;
        }
        foreach (var v in s.VirtualLayers)
        {
            sb.AppendLine($"  {v.Name,-14} {v.Kind,-14} running={(v.IsRunning ? "YES" : "no ")}  {v.Detail}");
        }
        sb.AppendLine();
    }

    private static void WriteDirectInputDevices(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine($"DirectInput attached game controllers ({s.DirectInputDevices.Count} total)");
        sb.AppendLine("---------------------------------------------------------------------");
        if (s.DirectInputDevices.Count == 0)
        {
            sb.AppendLine("  (none — DI8DEVCLASS_GAMECTRL returned 0 devices. If you have a wheel");
            sb.AppendLine("   plugged in, it may be in XInput-only mode or hidden by Steam Input.)");
            sb.AppendLine();
            return;
        }
        int i = 0;
        foreach (var d in s.DirectInputDevices)
        {
            sb.AppendLine($"  [{i++}] \"{d.ProductName}\"");
            sb.AppendLine($"       instance        = {d.InstanceGuid}");
            sb.AppendLine($"       VID/PID         = 0x{d.VendorId:X4} / 0x{d.ProductId:X4}{(string.IsNullOrEmpty(d.VendorLabel) ? "" : "  (" + d.VendorLabel + ")")}");
            sb.AppendLine($"       productGuidData1= 0x{d.ProductGuidData1:X8}");
            sb.AppendLine($"       devType         = {d.DeviceTypeLabel} (sub=0x{d.DeviceSubType:X2})");
            sb.AppendLine($"       caps            = {string.Join(", ", d.Capabilities)}");
            sb.AppendLine($"       FFB capable     = {(d.HasFfb ? "YES" : "no")}");
            sb.AppendLine($"       axes/btns/povs  = {d.AxisCount} / {d.ButtonCount} / {d.PovCount}");
            if (d.Axes.Count > 0)
            {
                sb.AppendLine("       DI field    DI offset  HID page:usage  HID meaning   HID name                   min       max       FFB");
                foreach (var a in d.Axes.OrderBy(a => a.DIByteOffset < 0 ? int.MaxValue : a.DIByteOffset))
                {
                    var hidStr = a.HasUsage ? $"0x{a.UsagePage:X2}:0x{a.Usage:X2}" : "    -";
                    string ofs = a.DIByteOffset >= 0 ? $"+{a.DIByteOffset,2}" : "  ?";
                    sb.AppendLine($"         {a.Name,-10}  {ofs,-7}  {hidStr,-14}  {a.UsageMeaning,-12}  {Trim(a.HidName, 24),-24}  {a.Min,8}  {a.Max,8}  {(a.HasForceFeedback ? "yes" : "")}");
                }
            }
            sb.AppendLine();
        }
    }

    private static void WriteHidDevices(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine($"Windows HID-level enumeration (vendor-filtered to wheel/pedal/shifter VIDs; {s.HidDevices.Count} total)");
        sb.AppendLine("---------------------------------------------------------------------");
        sb.AppendLine("Devices listed here that are NOT in the DirectInput list above are being");
        sb.AppendLine("hidden — usually by HidHide / vJoy / Steam Input / G HUB.");
        sb.AppendLine();
        if (s.HidDevices.Count == 0)
        {
            sb.AppendLine("  (no HID devices matched any known wheel-vendor VID)");
            sb.AppendLine();
            return;
        }
        foreach (var h in s.HidDevices)
        {
            sb.AppendLine($"  VID/PID 0x{h.VendorId:X4}/0x{h.ProductId:X4} rev=0x{h.ReleaseNumber:X4} ({h.VendorLabel})  usage=0x{h.UsagePage:X2}:0x{h.Usage:X2}");
            sb.AppendLine($"    name         = {h.FriendlyName}");
            if (!string.IsNullOrEmpty(h.ManufacturerString)) sb.AppendLine($"    manufacturer = {h.ManufacturerString}");
            if (!string.IsNullOrEmpty(h.ProductString))      sb.AppendLine($"    product      = {h.ProductString}");
            if (!string.IsNullOrEmpty(h.SerialNumberString)) sb.AppendLine($"    serial       = {h.SerialNumberString}");
            sb.AppendLine($"    path         = {h.DevicePath}");
            sb.AppendLine($"    visible in DI list above = {(h.MatchesInDirectInput ? "YES" : "NO  ← hidden from DI")}");
        }
        sb.AppendLine();
    }

    private static void WriteXInputSlots(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("XInput slots (0..3) — devices that appear here are running in XInput mode.");
        sb.AppendLine("A wheel reported as subtype WHEEL is fine. A wheel reported as GAMEPAD");
        sb.AppendLine("means the hardware toggle is in XInput/Xbox mode and DInput won't see it");
        sb.AppendLine("as a wheel — flip the toggle to PC/DirectInput mode.");
        sb.AppendLine();
        foreach (var x in s.XInputSlots)
        {
            if (!x.Connected)
            {
                sb.AppendLine($"  [{x.UserIndex}] (empty)");
                continue;
            }
            sb.AppendLine($"  [{x.UserIndex}] subType={x.SubTypeLabel}");
            sb.AppendLine($"       LStick=({x.LeftStickX},{x.LeftStickY}) RStick=({x.RightStickX},{x.RightStickY}) trigL={x.LeftTrigger} trigR={x.RightTrigger} buttons=0x{x.Buttons:X4}");
        }
        sb.AppendLine();
    }

    private static void WriteSelectedDevice(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Device under test");
        sb.AppendLine("-----------------");
        if (s.SelectedDevice == null)
        {
            sb.AppendLine("  (none selected)");
            sb.AppendLine();
            return;
        }
        sb.AppendLine($"  name              = \"{s.SelectedDevice.ProductName}\"");
        sb.AppendLine($"  VID / PID         = 0x{s.SelectedDevice.VendorId:X4} / 0x{s.SelectedDevice.ProductId:X4}");
        sb.AppendLine($"  vendor label      = {s.SelectedDevice.VendorLabel}");
        sb.AppendLine($"  productGuidData1  = 0x{s.SelectedDevice.ProductGuidData1:X8}");
        sb.AppendLine($"  axes/btns/povs    = {s.SelectedDevice.AxisCount} / {s.SelectedDevice.ButtonCount} / {s.SelectedDevice.PovCount}");
        sb.AppendLine($"  FFB capable       = {(s.SelectedDevice.HasFfb ? "YES" : "no")}");

        if (s.PolledDevices.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"  Multi-device polling — captures listened to {s.PolledDevices.Count} devices simultaneously:");
            foreach (var d in s.PolledDevices)
                sb.AppendLine($"    - \"{d.ProductName}\"  VID=0x{d.VendorId:X4} PID=0x{d.ProductId:X4} ({d.VendorLabel})");
        }
        sb.AppendLine();
    }

    private static void WriteIdleJitter(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Idle jitter (8-second hands-off capture)");
        sb.AppendLine("----------------------------------------");
        if (s.IdleJitter.Count == 0)
        {
            sb.AppendLine("  (skipped or not captured)");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("  device                          axis        mean       min       max     range    stddev   verdict");
        foreach (var r in s.IdleJitter.OrderByDescending(r => r.Max - r.Min))
        {
            sb.AppendLine($"  {Trim(r.DeviceProductName, 30),-30}  {r.AxisName,-10}  {r.Mean,7}  {r.Min,8}  {r.Max,8}  {(r.Max - r.Min),6}  {r.StandardDeviation,8:0.0}   {r.Verdict}");
        }
        sb.AppendLine();
    }

    private static void WriteCaptures(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine($"Guided captures ({s.CaptureSteps.Count} steps)");
        sb.AppendLine("---------------------------------------------------------------------");
        if (s.CaptureSteps.Count == 0)
        {
            sb.AppendLine("  (none)");
            sb.AppendLine();
            return;
        }

        foreach (var step in s.CaptureSteps)
        {
            sb.AppendLine($"* {step.StepId} — {step.Prompt}");
            if (step.Skipped)
            {
                sb.AppendLine("    SKIPPED by user");
                if (!string.IsNullOrEmpty(step.UserNote)) sb.AppendLine($"    note: {step.UserNote}");
                sb.AppendLine();
                continue;
            }

            string conf = step.Confidence switch
            {
                CaptureConfidence.High         => "high",
                CaptureConfidence.Low          => "low",
                CaptureConfidence.Missed       => "MISSED",
                CaptureConfidence.Ambiguous    => "AMBIGUOUS",
                CaptureConfidence.NotApplicable => "n/a",
                _ => "?"
            };
            sb.AppendLine($"    confidence     : {conf}  ({step.ConfidenceReason ?? ""})");
            if (string.IsNullOrEmpty(step.DominantAxisName))
            {
                sb.AppendLine("    dominant axis  : (none detected — no axis moved enough)");
            }
            else
            {
                sb.AppendLine($"    dominant axis  : {step.DominantDeviceProductName}::{step.DominantAxisName}");
                sb.AppendLine($"    baseline       : {step.DominantBaseline}");
                sb.AppendLine($"    observed range : {step.DominantMin} .. {step.DominantMax}  (travel = {step.DominantMax - step.DominantMin})");
                sb.AppendLine($"    press direction: {step.DominantPressDirection}  ({(step.DominantPressDirection > 0 ? "axis increases when pressed" : step.DominantPressDirection < 0 ? "axis decreases when pressed" : "no net motion")})");
            }

            if (step.Axes.Count > 0)
            {
                sb.AppendLine("    every axis observed during this step:");
                sb.AppendLine("      device                          axis      baseline    min       max    range  motion?  maxDelta");
                foreach (var a in step.Axes.OrderByDescending(a => a.MaxDeltaFromBaseline))
                {
                    sb.AppendLine($"      {Trim(a.DeviceProductName, 30),-30}  {a.AxisName,-8}  {a.Baseline,8}  {(a.MinObserved == int.MaxValue ? 0 : a.MinObserved),8}  {(a.MaxObserved == int.MinValue ? 0 : a.MaxObserved),8}  {a.Range,6}   {(a.SeenMotion ? "yes" : "no "),-3}    {a.MaxDeltaFromBaseline}");
                }
            }

            // Per-event button trail — order matters for paddle/gear steps.
            if (step.ButtonEvents.Count > 0)
            {
                sb.AppendLine("    button events (in order, press/release with timing):");
                foreach (var ev in step.ButtonEvents)
                {
                    sb.AppendLine($"      t={ev.TimeOffsetSec:0.000}s  {(ev.Pressed ? "PRESS  " : "release")}  {Trim(ev.DeviceProductName, 30),-30}  btn {ev.ButtonIndex}");
                }
            }
            else
            {
                sb.AppendLine("    button events  : (none)");
            }

            if (step.PovValues.Count > 0)
            {
                foreach (var kv in step.PovValues)
                    sb.AppendLine($"    POV {kv.Key} ended at value {kv.Value}");
            }

            if (step.CrosstalkBleed.Count > 0)
            {
                sb.AppendLine("    cross-axis bleed (other axes that moved while the actuated one was pressed):");
                foreach (var a in step.CrosstalkBleed.OrderByDescending(a => a.MaxDeltaFromBaseline))
                {
                    double pct = 65535.0 < 1 ? 0 : (a.MaxDeltaFromBaseline * 100.0) / 65535.0;
                    sb.AppendLine($"      {Trim(a.DeviceProductName, 30),-30}  {a.AxisName,-8}  bled {a.MaxDeltaFromBaseline} units (~{pct:0.0}% of full scale)");
                }
            }

            if (!string.IsNullOrEmpty(step.UserNote))
                sb.AppendLine($"    user note: {step.UserNote}");

            sb.AppendLine();
        }
    }

    private static void WriteButtonIdentifications(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine($"Button identifications ({s.ButtonIdentifications.Count} mapped)");
        sb.AppendLine("---------------------------------------------------------------------");
        if (s.ButtonIdentifications.Count == 0)
        {
            sb.AppendLine("  (none — user skipped or no buttons were identified)");
            sb.AppendLine();
            return;
        }
        foreach (var b in s.ButtonIdentifications)
        {
            string detail = b.Skipped ? "SKIPPED" : $"button {b.ButtonIndex}@{b.DeviceProductName}";
            sb.AppendLine($"  {b.Label,-30} -> {detail}");
        }
        sb.AppendLine();
    }

    private static void WriteFfbProbe(DiagnosticSession s, StringBuilder sb)
    {
        sb.AppendLine("Force-feedback probe");
        sb.AppendLine("--------------------");
        var f = s.FfbProbe;
        if (f.Skipped)
        {
            sb.AppendLine("  SKIPPED by user.");
            sb.AppendLine();
            return;
        }
        sb.AppendLine($"  exclusive acquire   : {(f.ExclusiveAcquireOk ? "OK" : "FAILED")}  hr=0x{(uint)f.AcquireHResult:X8}");
        sb.AppendLine($"  set autocenter off  : {(f.AutoCenterSetOk ? "OK" : "FAILED")}  hr=0x{(uint)f.AutoCenterHResult:X8}");
        if ((uint)f.AcquireHResult == 0x800700AAu)
        {
            sb.AppendLine("    ↑ ERROR_BUSY: another process holds exclusive (G HUB / Pit House / vJoy / etc.)");
        }
        sb.AppendLine();
        sb.AppendLine("  per-effect results (CreateEffect HRESULT + user-confirmed sensation):");
        if (f.Effects.Count == 0)
        {
            sb.AppendLine("    (no effects tested)");
        }
        else
        {
            foreach (var e in f.Effects)
            {
                string felt = e.UserFelt switch
                {
                    true => "FELT",
                    false => "NOT FELT",
                    null => "(not asked)"
                };
                sb.AppendLine($"    {e.EffectName,-22}  hr=0x{(uint)e.HResult:X8}  create={(e.CreateSucceeded ? "OK" : "FAILED")}  user={felt}");
                if (!string.IsNullOrEmpty(e.Note)) sb.AppendLine($"        note: {e.Note}");
            }
        }
        sb.AppendLine();
        if (f.RunningVendorProcessesAtProbeTime.Count > 0)
        {
            sb.AppendLine("  vendor processes running when FFB was probed (these compete for the");
            sb.AppendLine("  exclusive lock):");
            foreach (var p in f.RunningVendorProcessesAtProbeTime)
                sb.AppendLine($"    - {p}");
        }
        sb.AppendLine();
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");
}
