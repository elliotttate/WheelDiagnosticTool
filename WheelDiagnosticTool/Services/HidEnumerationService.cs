using System.Collections.Generic;
using HidSharp;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// HID-level enumeration of every device the OS sees, filtered to wheel/
/// pedal/shifter vendors. Catches devices DirectInput hides (G HUB
/// virtualization, Steam Input, vJoy) so we can correlate.
/// </summary>
public static class HidEnumerationService
{
    public static IReadOnlyList<HidDeviceSnapshot> Enumerate()
    {
        var list = new List<HidDeviceSnapshot>();
        var loader = DeviceList.Local;

        foreach (var dev in loader.GetHidDevices())
        {
            ushort vid;
            ushort pid;
            ushort releaseNumber = 0;
            ushort usagePage = 0;
            ushort usage = 0;
            string friendly = "";
            string manufacturer = "";
            string product = "";
            string serial = "";

            try
            {
                vid = (ushort)dev.VendorID;
                pid = (ushort)dev.ProductID;
            }
            catch
            {
                continue;
            }

            // Filter early: we only care about wheel/sim/peripheral vendors plus
            // generic-desktop usage pages. Random HID barcode scanners, security
            // keys, and bluetooth dongles flood this list otherwise.
            var vendorLabel = VendorLookup.Lookup(vid);
            if (vendorLabel == null) continue;

            try { releaseNumber = (ushort)dev.ReleaseNumberBcd; } catch { }
            try { friendly = dev.GetFriendlyName() ?? dev.GetProductName() ?? ""; } catch { }
            try { manufacturer = dev.GetManufacturer() ?? ""; } catch { }
            try { product = dev.GetProductName() ?? ""; } catch { }
            try { serial = dev.GetSerialNumber() ?? ""; } catch { }

            try
            {
                var reportDescriptor = dev.GetReportDescriptor();
                foreach (var di in reportDescriptor.DeviceItems)
                {
                    foreach (var u in di.Usages.GetAllValues())
                    {
                        usagePage = (ushort)((u >> 16) & 0xFFFF);
                        usage = (ushort)(u & 0xFFFF);
                        break;
                    }
                    break;
                }
            }
            catch
            {
                // Report-descriptor parsing fails on a fair number of devices —
                // that's OK; we still record the VID/PID/friendly name.
            }

            list.Add(new HidDeviceSnapshot
            {
                DevicePath = dev.DevicePath ?? "",
                FriendlyName = friendly,
                ManufacturerString = manufacturer,
                ProductString = product,
                SerialNumberString = serial,
                VendorId = vid,
                ProductId = pid,
                ReleaseNumber = releaseNumber,
                UsagePage = usagePage,
                Usage = usage,
                VendorLabel = vendorLabel,
            });
        }

        return list;
    }
}
