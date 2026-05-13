using System;
using System.Collections.Generic;
using WheelDiagnosticTool.Views;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// The fixed wizard sequence. Each entry is the Page type to navigate to
/// and a human-readable label that goes in the header so users can see
/// "Step 4 of 30" while they work.
///
/// Capture-step pages share a Page type (CaptureStepPage) and read their
/// prompt from the StepId in the navigation parameter.
/// </summary>
public static class WizardFlow
{
    public sealed record Step(Type PageType, string Label, string? Param = null);

    public static readonly List<Step> Steps = new()
    {
        new(typeof(WelcomePage),         "Welcome"),
        new(typeof(EnumerationPage),     "Detecting hardware"),
        new(typeof(DeviceSelectionPage), "Pick device"),

        // Idle jitter (hands-off baseline)
        new(typeof(IdleJitterPage),  "Idle jitter capture"),

        // Steering
        new(typeof(CaptureStepPage), "Center the wheel",              "STEER_CENTER"),
        new(typeof(CaptureStepPage), "Turn wheel fully LEFT",         "STEER_LEFT"),
        new(typeof(CaptureStepPage), "Turn wheel fully RIGHT",        "STEER_RIGHT"),

        // Pedals
        new(typeof(CaptureStepPage), "Press THROTTLE fully, release", "PEDAL_THROTTLE"),
        new(typeof(CaptureStepPage), "Press BRAKE fully, release",    "PEDAL_BRAKE"),
        new(typeof(CaptureStepPage), "Press CLUTCH fully, release (skip if no clutch)", "PEDAL_CLUTCH"),
        new(typeof(CaptureStepPage), "Pull HANDBRAKE fully, release (skip if none)",    "PEDAL_HANDBRAKE"),

        // Pedal crosstalk — done after each pedal is identified individually
        new(typeof(CaptureStepPage), "Press THROTTLE and BRAKE TOGETHER (then release)", "CROSSTALK_T_AND_B"),
        new(typeof(CaptureStepPage), "Press BRAKE HARD (do not touch throttle or clutch)", "CROSSTALK_BRAKE_ONLY"),
        new(typeof(CaptureStepPage), "Press THROTTLE HARD (do not touch brake or clutch)", "CROSSTALK_THROTTLE_ONLY"),
        new(typeof(CaptureStepPage), "Press CLUTCH while pressing THROTTLE (skip if no clutch)", "CROSSTALK_CLUTCH_AND_THROTTLE"),

        // Shifter / paddles
        new(typeof(CaptureStepPage), "Press UPSHIFT paddle / button",   "PADDLE_UP"),
        new(typeof(CaptureStepPage), "Press DOWNSHIFT paddle / button", "PADDLE_DOWN"),

        // Each H-shifter gear individually
        new(typeof(CaptureStepPage), "Shift to gear 1 (skip if no H-shifter)", "GEAR_1"),
        new(typeof(CaptureStepPage), "Shift to gear 2",                         "GEAR_2"),
        new(typeof(CaptureStepPage), "Shift to gear 3",                         "GEAR_3"),
        new(typeof(CaptureStepPage), "Shift to gear 4",                         "GEAR_4"),
        new(typeof(CaptureStepPage), "Shift to gear 5",                         "GEAR_5"),
        new(typeof(CaptureStepPage), "Shift to gear 6",                         "GEAR_6"),
        new(typeof(CaptureStepPage), "Shift to gear 7 (skip if N/A)",           "GEAR_7"),
        new(typeof(CaptureStepPage), "Shift to REVERSE",                        "GEAR_R"),

        // POV (hat)
        new(typeof(CaptureStepPage), "Press each POV/HAT direction",  "POV_ALL"),

        // Wheel-rim button discovery
        new(typeof(ButtonDiscoveryPage), "Identify wheel-rim buttons"),

        // FFB
        new(typeof(FfbProbePage), "Force-feedback probe"),

        // Final
        new(typeof(ResultsPage), "Generating report"),
    };

    public static int IndexOf(Type pageType, string? param)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].PageType == pageType && Steps[i].Param == param) return i;
        }
        return -1;
    }

    public static Step? Next(Type currentPageType, string? currentParam)
    {
        var i = IndexOf(currentPageType, currentParam);
        if (i < 0 || i + 1 >= Steps.Count) return null;
        return Steps[i + 1];
    }
}
