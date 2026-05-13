using Windows.ApplicationModel.DataTransfer;

namespace WheelDiagnosticTool.Services;

public static class ClipboardService
{
    public static bool TrySetText(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
            // Flush so the text survives the app exiting.
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
