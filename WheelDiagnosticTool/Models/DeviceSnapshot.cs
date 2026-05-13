using System.Collections.Generic;

namespace WheelDiagnosticTool.Models;

/// <summary>
/// Per-axis raw HID info captured at enumeration time. Mirrors what the game
/// surfaces in WheelDiagnostics.txt under the per-slot idle-cal table.
/// </summary>
public sealed class AxisDescriptor
{
    public string Name { get; init; } = ""; // lX, lY, lZ, lRx, lRy, lRz, slider[0], slider[1]
    public int Min { get; init; }
    public int Max { get; init; }
    public bool HasUsage { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public string UsageMeaning { get; init; } = "";
    public bool HasForceFeedback { get; init; }
}

public sealed class ButtonDescriptor
{
    public int Index { get; init; }
}

public sealed class PovDescriptor
{
    public int Index { get; init; }
}

/// <summary>
/// One discovered input device (DirectInput-side). The Windows HID side is
/// captured separately because some devices the OS sees never reach DI.
/// </summary>
public sealed class DiDeviceSnapshot
{
    public string Name { get; init; } = "";
    public string ProductName { get; init; } = "";
    public uint ProductGuidData1 { get; init; } // = (PID << 16) | VID for HID devices
    public string InstanceGuid { get; init; } = "";
    public uint VendorId { get; init; }
    public uint ProductId { get; init; }
    public string VendorLabel { get; init; } = "";
    public int DeviceType { get; init; } // dwDevType low byte
    public int DeviceSubType { get; init; } // dwDevType high byte
    public string DeviceTypeLabel { get; init; } = "";
    public bool HasFfb { get; init; }
    public int AxisCount { get; init; }
    public int ButtonCount { get; init; }
    public int PovCount { get; init; }
    public List<AxisDescriptor> Axes { get; init; } = new();
    public List<ButtonDescriptor> Buttons { get; init; } = new();
    public List<PovDescriptor> Povs { get; init; } = new();
    public List<string> Capabilities { get; init; } = new();
}

/// <summary>
/// HID-level enumeration entry (system-wide, regardless of DInput visibility).
/// </summary>
public sealed class HidDeviceSnapshot
{
    public string DevicePath { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string ManufacturerString { get; init; } = "";
    public string ProductString { get; init; } = "";
    public string SerialNumberString { get; init; } = "";
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort ReleaseNumber { get; init; } // firmware version reported by HID descriptor
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public string VendorLabel { get; init; } = "";
    public bool MatchesInDirectInput { get; set; }
}

/// <summary>
/// XInput slot 0..3 state snapshot.
/// </summary>
public sealed class XInputSnapshot
{
    public int UserIndex { get; init; }
    public bool Connected { get; init; }
    public string SubTypeLabel { get; init; } = "";
    public int LeftStickX { get; init; }
    public int LeftStickY { get; init; }
    public int RightStickX { get; init; }
    public int RightStickY { get; init; }
    public int LeftTrigger { get; init; }
    public int RightTrigger { get; init; }
    public ushort Buttons { get; init; }
}

public sealed class VendorProcess
{
    public string ProcessName { get; init; } = "";
    public string Description { get; init; } = "";
    public int Pid { get; init; }
    public string Vendor { get; init; } = "";
}
