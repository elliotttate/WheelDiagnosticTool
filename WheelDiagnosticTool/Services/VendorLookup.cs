namespace WheelDiagnosticTool.Services;

/// <summary>
/// USB vendor IDs we care about for steering wheels, pedals, shifters,
/// handbrakes, and button boxes. Same set the game's diagnostic uses
/// (FODeviceFilteringManager.cpp:2675-2705) so reports correlate.
/// </summary>
public static class VendorLookup
{
    public static string? Lookup(uint vid)
    {
        return (vid & 0xFFFFu) switch
        {
            0x046Du => "Logitech",
            0x044Fu => "Thrustmaster",
            0x0EB7u => "Fanatec",
            0x11FFu => "PXN/Zeroplus",
            0x0F0Du => "Hori",
            0x16D0u => "Moza",
            0x0738u => "Mad Catz",
            0x0E6Fu => "Logic3 / PDP",
            0x28DEu => "Valve (Steam Input virtual device)",
            0x045Eu => "Microsoft (Xbox controller)",
            0x1532u => "Razer",
            0x1A86u => "QinHeng (DIY/open-source rigs)",
            0x16C0u => "Voti (vJoy / FreeJoy / OpenFFBoard)",
            0x2341u => "Arduino (DIY rig)",
            0x1FC9u => "NXP (DIY rig)",
            0x1209u => "InterBiometrics (FreeJoy / open hardware)",
            _ => null
        };
    }

    public static bool IsLikelyWheelVid(ushort vid)
    {
        return Lookup(vid) is { } v
            && !v.StartsWith("Valve", System.StringComparison.OrdinalIgnoreCase)
            && !v.StartsWith("Microsoft", System.StringComparison.OrdinalIgnoreCase)
            && !v.StartsWith("Razer", System.StringComparison.OrdinalIgnoreCase);
    }
}
