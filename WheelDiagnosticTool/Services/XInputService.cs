using System.Collections.Generic;
using System.Runtime.InteropServices;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// XInput probe via xinput1_4.dll. Catches wheels currently in XInput / Xbox
/// mode (Logitech G920 / G923 Xbox especially) so the diagnostic can say
/// "your wheel is showing up here as an XInput pad — switch the toggle."
/// </summary>
public static class XInputService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputGamepad Vibration;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities caps);

    private const uint ERROR_SUCCESS = 0;

    public static IReadOnlyList<XInputSnapshot> EnumerateAll()
    {
        var list = new List<XInputSnapshot>();
        for (uint i = 0; i < 4; i++)
        {
            uint hr = XInputGetState(i, out var state);
            if (hr != ERROR_SUCCESS)
            {
                list.Add(new XInputSnapshot { UserIndex = (int)i, Connected = false });
                continue;
            }

            string subType = "(unknown)";
            if (XInputGetCapabilities(i, 0, out var caps) == ERROR_SUCCESS)
            {
                subType = caps.SubType switch
                {
                    0 => "UNKNOWN",
                    1 => "GAMEPAD",
                    2 => "WHEEL",
                    3 => "ARCADE_STICK",
                    4 => "FLIGHT_STICK",
                    5 => "DANCE_PAD",
                    6 => "GUITAR",
                    7 => "GUITAR_ALT",
                    8 => "DRUM_KIT",
                    11 => "GUITAR_BASS",
                    19 => "ARCADE_PAD",
                    _ => $"0x{caps.SubType:X2}"
                };
            }

            list.Add(new XInputSnapshot
            {
                UserIndex = (int)i,
                Connected = true,
                SubTypeLabel = subType,
                LeftStickX = state.Gamepad.sThumbLX,
                LeftStickY = state.Gamepad.sThumbLY,
                RightStickX = state.Gamepad.sThumbRX,
                RightStickY = state.Gamepad.sThumbRY,
                LeftTrigger = state.Gamepad.bLeftTrigger,
                RightTrigger = state.Gamepad.bRightTrigger,
                Buttons = state.Gamepad.wButtons,
            });
        }
        return list;
    }
}
