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
            0x2F24u => "PXN",
            0x36E6u => "PXN VD",
            0x0F0Du => "Hori",
            0x346Eu => "MOZA",
            0x16D0u => "Mosart (Simagic / Simucube / OpenFFBoard)",
            0x0483u => "SIMAGIC (STM32)",
            0x3670u => "SIMAGIC",
            0x0738u => "Mad Catz",
            0x0E6Fu => "Logic3 / PDP",
            0x28DEu => "Valve (Steam Input virtual device)",
            0x045Eu => "Microsoft (Xbox controller)",
            0x1532u => "Razer",
            0x1A86u => "QinHeng (DIY/open-source rigs)",
            0x16C0u => "Voti (vJoy / FreeJoy / OpenFFBoard)",
            0x2341u => "Arduino (DIY rig)",
            0x1FC9u => "SimXperience / NXP",
            0x1209u => "InterBiometrics (FreeJoy / open hardware)",
            0xDDFDu => "SIMSONN",
            0x3416u => "CAMMUS",
            0x30B7u => "Heusinkveld",
            0x10F5u => "Turtle Beach",
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
