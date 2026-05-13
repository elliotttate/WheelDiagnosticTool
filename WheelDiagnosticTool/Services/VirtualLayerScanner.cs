using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Detects virtualization / hiding layers that sit between physical HID
/// devices and DirectInput. The standard "I plugged in my shifter but the
/// game doesn't see it" call is almost always one of:
///   - HidHide: kernel filter hiding the device from non-allow-listed apps.
///   - ViGEmBus: virtual XInput/DS4 controllers (reWASD/DS4Windows uses it).
///   - vJoy / vXBox: virtual joystick drivers.
///   - Interception: keyboard/mouse driver used by some rebind tools.
///   - Steam Input: not a driver per se, but Steam intercepts HID and
///     presents a virtual XInput device.
///
/// We scan two surfaces:
///   1. Running processes (already done by VendorSoftwareScanner) — re-scan
///      here so the report has a dedicated layer section.
///   2. Kernel services via the registry under HKLM\SYSTEM\CurrentControlSet\
///      Services\NAME — checking ImagePath/Start so we know the driver is
///      installed even if the user app isn't running.
/// </summary>
public static class VirtualLayerScanner
{
    private record KnownLayer(string Name, string[] ServiceNames, string[] ProcessNames, string Description);

    private static readonly KnownLayer[] s_layers =
    {
        new("HidHide",     new string[]{ "HidHide" },           new string[]{ "HidHideClient", "HidHideCLI" }, "Kernel HID filter that can hide devices from DirectInput"),
        new("ViGEmBus",    new string[]{ "ViGEmBus" },          new string[]{ "ViGEmBus" },                     "Virtual XInput/DS4 bus (used by reWASD/DS4Windows)"),
        new("vJoy",        new string[]{ "vjoy", "vJoy" },      new string[]{ "vJoyConf", "vJoyMonitor" },      "Virtual joystick driver"),
        new("vXBox",       new string[]{ "vXboxBus", "ScpBus"}, System.Array.Empty<string>(),                   "Virtual Xbox / ScpToolkit bus"),
        new("Interception",new string[]{ "interception" },      System.Array.Empty<string>(),                   "Keyboard/mouse driver used by some remappers"),
        new("FlydigiVHID", new string[]{ "FlydigiVirtual" },    System.Array.Empty<string>(),                   "Flydigi virtual HID driver"),
    };

    public static IReadOnlyList<VirtualLayer> Scan()
    {
        var list = new List<VirtualLayer>();

        // Running-process side
        Process[] processes;
        try { processes = Process.GetProcesses(); } catch { processes = Array.Empty<Process>(); }
        var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            try { runningNames.Add(p.ProcessName); } catch { }
            try { p.Dispose(); } catch { }
        }

        foreach (var layer in s_layers)
        {
            foreach (var pn in layer.ProcessNames)
            {
                if (runningNames.Contains(pn))
                {
                    list.Add(new VirtualLayer
                    {
                        Name = layer.Name,
                        Kind = "Process",
                        Detail = pn + ".exe",
                        IsRunning = true,
                    });
                }
            }

            // Kernel-service side — read HKLM\SYSTEM\CurrentControlSet\Services\NAME
            foreach (var sn in layer.ServiceNames)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sn}");
                    if (key == null) continue;
                    var start = key.GetValue("Start") as int? ?? -1;
                    var imagePath = (key.GetValue("ImagePath") as string) ?? "";
                    string startLabel = start switch
                    {
                        0 => "Boot",
                        1 => "System",
                        2 => "Auto",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => $"?({start})"
                    };
                    list.Add(new VirtualLayer
                    {
                        Name = layer.Name,
                        Kind = "KernelService",
                        Detail = $"start={startLabel} image={imagePath}",
                        IsRunning = start <= 2, // Boot/System/Auto = effectively running
                    });
                }
                catch
                {
                    // registry access denied or key shape unexpected — skip
                }
            }
        }

        return list;
    }
}
