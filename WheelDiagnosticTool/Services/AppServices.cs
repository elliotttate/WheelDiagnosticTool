using System;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Process-global service singletons. Pages reach for these via AppServices.X
/// rather than fighting with DI containers in WinUI 3's awkward host-builder
/// model.
/// </summary>
public static class AppServices
{
    private static DirectInputService? s_di;
    public static DirectInputService DirectInput
    {
        get
        {
            if (s_di == null)
            {
                s_di = new DirectInputService();
                s_di.Initialize();
            }
            return s_di;
        }
    }

    private static FfbProbeService? s_ffb;
    public static FfbProbeService Ffb => s_ffb ??= new FfbProbeService(DirectInput);

    public static FilebinUploader Uploader { get; } = new();

    public static IntPtr MainWindowHandle { get; set; }

    public static void Shutdown()
    {
        try { s_ffb?.StopAndRelease(); } catch { }
        try { s_di?.Dispose(); } catch { }
        s_di = null;
    }
}
