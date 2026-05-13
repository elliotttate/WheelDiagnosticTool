using System;
using System.Collections.Generic;
using Vortice.DirectInput;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// DirectInput8 wrapper. Owns one IDirectInput8 instance for the process and
/// hands out per-device joystick handles. Polling lives on whatever thread
/// calls Poll() — pages drive this from a DispatcherTimer.
/// </summary>
public sealed class DirectInputService : IDisposable
{
    private IDirectInput8? _di;
    private readonly Dictionary<Guid, IDirectInputDevice8> _openDevices = new();

    public bool Initialize()
    {
        if (_di != null) return true;
        try
        {
            _di = DInput.DirectInput8Create();
            return _di != null;
        }
        catch (Exception ex)
        {
            LastInitError = ex.ToString();
            return false;
        }
    }

    public string? LastInitError { get; private set; }

    public IReadOnlyList<DiDeviceSnapshot> Enumerate()
    {
        var list = new List<DiDeviceSnapshot>();
        if (_di == null) return list;

        var instances = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        foreach (var inst in instances)
        {
            try
            {
                list.Add(BuildSnapshot(inst));
            }
            catch (Exception ex)
            {
                list.Add(new DiDeviceSnapshot
                {
                    Name = inst.InstanceName ?? "(unnamed)",
                    ProductName = inst.ProductName ?? "(unnamed)",
                    InstanceGuid = inst.InstanceGuid.ToString(),
                    Capabilities = new List<string> { "ENUMERATE_FAILED: " + ex.Message }
                });
            }
        }

        return list;
    }

    private DiDeviceSnapshot BuildSnapshot(DeviceInstance inst)
    {
        var dev = _di!.CreateDevice(inst.InstanceGuid);
        // Open without acquiring so we can read caps without fighting other apps.
        var caps = dev.Capabilities;

        var axes = new List<AxisDescriptor>();
        var buttons = new List<ButtonDescriptor>();
        var povs = new List<PovDescriptor>();

        // Set the data format so EnumObjects reports the right axis ranges.
        dev.SetDataFormat<RawJoystickState>();

        // Read axis ranges and HID usage data via the object enumeration.
        var objs = dev.GetObjects(DeviceObjectTypeFlags.All);
        int axisCount = 0, buttonCount = 0, povCount = 0;
        foreach (var obj in objs)
        {
            // Vortice exposes the DI8 object semantic type via the bit-packed
            // ObjectId flags, not via the GUID-typed ObjectType field.
            var typeFlags = obj.ObjectId.Flags;
            if ((typeFlags & DeviceObjectTypeFlags.Axis) != 0)
            {
                axisCount++;
                int min = 0, max = 0;
                try
                {
                    var range = dev.GetObjectPropertiesById(obj.ObjectId).Range;
                    min = range.Minimum;
                    max = range.Maximum;
                }
                catch
                {
                    // Some devices don't report a range until properties are set;
                    // we'll set a uniform range below.
                }

                axes.Add(new AxisDescriptor
                {
                    Name = obj.Name ?? "(axis)",
                    Min = min,
                    Max = max,
                    HasUsage = obj.UsagePage != 0 || obj.Usage != 0,
                    UsagePage = (ushort)obj.UsagePage,
                    Usage = (ushort)obj.Usage,
                    UsageMeaning = DecodeUsage((ushort)obj.UsagePage, (ushort)obj.Usage),
                    HasForceFeedback = (typeFlags & DeviceObjectTypeFlags.ForceFeedbackActuator) != 0,
                });
            }
            else if ((typeFlags & DeviceObjectTypeFlags.Button) != 0)
            {
                buttonCount++;
                buttons.Add(new ButtonDescriptor { Index = buttons.Count });
            }
            else if ((typeFlags & DeviceObjectTypeFlags.PointOfViewController) != 0)
            {
                povCount++;
                povs.Add(new PovDescriptor { Index = povs.Count });
            }
        }

        // Normalize all axes to [-32768, 32767] so observation math is uniform.
        try
        {
            dev.Properties.Range = new InputRange(-32768, 32767);
        }
        catch { /* not all devices accept */ }

        var caps2 = caps;
        bool hasFfb = (caps2.Flags & DeviceFlags.ForceFeedback) != 0;
        int devTypeRaw = (int)caps2.Type;
        uint vendor = inst.ProductGuid.GetData1() & 0xFFFFu;
        uint product = (inst.ProductGuid.GetData1() >> 16) & 0xFFFFu;

        var snap = new DiDeviceSnapshot
        {
            Name = inst.InstanceName ?? "",
            ProductName = inst.ProductName ?? "",
            ProductGuidData1 = inst.ProductGuid.GetData1(),
            InstanceGuid = inst.InstanceGuid.ToString(),
            VendorId = vendor,
            ProductId = product,
            VendorLabel = VendorLookup.Lookup(vendor) ?? "",
            DeviceType = devTypeRaw & 0xFF,
            DeviceSubType = (devTypeRaw >> 8) & 0xFF,
            DeviceTypeLabel = DecodeDeviceType(devTypeRaw & 0xFF),
            HasFfb = hasFfb,
            AxisCount = axisCount,
            ButtonCount = buttonCount,
            PovCount = povCount,
            Axes = axes,
            Buttons = buttons,
            Povs = povs,
            Capabilities = BuildCapsList(caps2),
        };

        _openDevices[inst.InstanceGuid] = dev;
        return snap;
    }

    private static List<string> BuildCapsList(Capabilities c)
    {
        var list = new List<string>();
        if ((c.Flags & DeviceFlags.Attached) != 0) list.Add("ATTACHED");
        if ((c.Flags & DeviceFlags.PolledDevice) != 0) list.Add("POLLED");
        if ((c.Flags & DeviceFlags.PolledDataFormat) != 0) list.Add("POLLED_DATAFORMAT");
        if ((c.Flags & DeviceFlags.ForceFeedback) != 0) list.Add("FORCE_FEEDBACK");
        if ((c.Flags & DeviceFlags.Emulated) != 0) list.Add("EMULATED");
        if ((c.Flags & DeviceFlags.Hidden) != 0) list.Add("HIDDEN");
        return list;
    }

    private static string DecodeUsage(ushort page, ushort usage)
    {
        return page switch
        {
            0x01 => usage switch
            {
                0x30 => "X", 0x31 => "Y", 0x32 => "Z",
                0x33 => "Rx", 0x34 => "Ry", 0x35 => "Rz",
                0x36 => "Slider", 0x37 => "Dial", 0x38 => "Wheel",
                _ => $"Desktop:0x{usage:X2}"
            },
            0x02 => usage switch
            {
                0xC4 => "Throttle", 0xC5 => "Brake", 0xC6 => "Clutch",
                0xC7 => "Shifter",  0xC8 => "Steering",
                _ => $"Simulation:0x{usage:X2}"
            },
            _ => $"Page:0x{page:X2}/0x{usage:X2}"
        };
    }

    private static string DecodeDeviceType(int t)
    {
        return t switch
        {
            (int)DeviceType.Device => "DEVICE",
            (int)DeviceType.Mouse => "MOUSE",
            (int)DeviceType.Keyboard => "KEYBOARD",
            (int)DeviceType.Joystick => "JOYSTICK",
            (int)DeviceType.Gamepad => "GAMEPAD",
            (int)DeviceType.Driving => "DRIVING",
            (int)DeviceType.Flight => "FLIGHT",
            (int)DeviceType.FirstPerson => "1STPERSON",
            (int)DeviceType.ControlDevice => "DEVICECTRL",
            (int)DeviceType.ScreenPointer => "SCREENPOINTER",
            (int)DeviceType.Remote => "REMOTE",
            (int)DeviceType.Supplemental => "SUPPLEMENTAL",
            _ => $"0x{t:X2}"
        };
    }

    /// <summary>
    /// Acquire the device in non-exclusive/background mode for read-only polling.
    /// </summary>
    public bool AcquireForPolling(Guid instanceGuid, IntPtr windowHandle)
    {
        if (!_openDevices.TryGetValue(instanceGuid, out var dev)) return false;
        try
        {
            dev.Unacquire();
            dev.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
            var hr = dev.Acquire();
            return hr.Success;
        }
        catch { return false; }
    }

    public bool Poll(Guid instanceGuid, out JoystickState? state)
    {
        state = null;
        if (!_openDevices.TryGetValue(instanceGuid, out var dev)) return false;
        try
        {
            dev.Poll();
            state = dev.GetCurrentJoystickState();
            return true;
        }
        catch
        {
            try { dev.Acquire(); } catch { }
            return false;
        }
    }

    public IDirectInputDevice8? GetDevice(Guid instanceGuid)
        => _openDevices.TryGetValue(instanceGuid, out var dev) ? dev : null;

    public void Dispose()
    {
        foreach (var d in _openDevices.Values)
        {
            try { d.Unacquire(); } catch { }
            try { d.Dispose(); } catch { }
        }
        _openDevices.Clear();
        _di?.Dispose();
        _di = null;
    }
}

internal static class GuidExtensions
{
    public static uint GetData1(this Guid g)
    {
        // First 4 bytes (little-endian) of the GUID, which matches Windows' guidProduct.Data1
        var bytes = g.ToByteArray();
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }
}
