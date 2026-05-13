using System;
using System.Collections.Generic;
using System.Diagnostics;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Scans for running wheel-vendor software that commonly holds an exclusive
/// DInput lock on the wheel — the dominant cause of FFB CreateEffect failures
/// in user reports (FLATOUT-46/52/4N pattern from the game's Sentry).
/// </summary>
public static class VendorSoftwareScanner
{
    private record KnownProcess(string ExeName, string Vendor, string Description);

    private static readonly KnownProcess[] s_knownProcesses =
    {
        new("LGHUB",                 "Logitech",     "G HUB"),
        new("LGHUB Updater",         "Logitech",     "G HUB Updater"),
        new("lghub_agent",           "Logitech",     "G HUB Agent"),
        new("lghub_system_tray",     "Logitech",     "G HUB System Tray"),
        new("LCore",                 "Logitech",     "Logitech Gaming Software (legacy)"),
        new("LogiOptionsPlus",       "Logitech",     "Logi Options+"),
        new("Thrustmaster",          "Thrustmaster", "Thrustmaster Control Panel"),
        new("TMSDK",                 "Thrustmaster", "Thrustmaster SDK"),
        new("TmSimAgent",            "Thrustmaster", "Thrustmaster Sim Agent"),
        new("Pit House",             "Fanatec",      "Fanatec Pit House"),
        new("FanatecControlPanel",   "Fanatec",      "Fanatec Control Panel"),
        new("MOZA Pit House",        "Moza",         "MOZA Pit House"),
        new("MOZA_RACING",           "Moza",         "MOZA Racing app"),
        new("MozaTuningCenter",      "Moza",         "MOZA Tuning Center"),
        new("SimHub",                "SimHub",       "SimHub"),
        new("vJoyConf",              "vJoy",         "vJoy config"),
        new("FreeJoy",               "FreeJoy",      "FreeJoy companion"),
        new("rewasd",                "reWASD",       "reWASD remapper"),
        new("JoyToKey",              "JoyToKey",     "JoyToKey remapper"),
        new("Xpadder",               "Xpadder",      "Xpadder remapper"),
        new("Steam",                 "Valve",        "Steam (Steam Input can intercept wheels)"),
        new("DS4Windows",            "DS4Windows",   "DS4 mapper"),
        new("PXNRacing",             "PXN",          "PXN Racing"),
        new("ZeroPlus",              "Zeroplus",     "Zeroplus utility"),
        new("HoriPad",               "Hori",         "Hori utility"),
        new("Simagic Manager",       "Simagic",      "Simagic Manager"),
        new("Asetek",                "Asetek",       "Asetek Race Hub"),
    };

    public static IReadOnlyList<VendorProcess> Scan()
    {
        var list = new List<VendorProcess>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return list; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in processes)
        {
            string name;
            try { name = p.ProcessName; } catch { continue; }
            foreach (var kp in s_knownProcesses)
            {
                if (name.Equals(kp.ExeName, StringComparison.OrdinalIgnoreCase)
                 || name.StartsWith(kp.ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    string key = $"{kp.ExeName}#{p.Id}";
                    if (!seen.Add(key)) continue;
                    list.Add(new VendorProcess
                    {
                        ProcessName = name,
                        Description = kp.Description,
                        Vendor = kp.Vendor,
                        Pid = p.Id,
                    });
                    break;
                }
            }
            try { p.Dispose(); } catch { }
        }
        return list;
    }
}
