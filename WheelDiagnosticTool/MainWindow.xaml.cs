using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using WheelDiagnosticTool.Services;
using WheelDiagnosticTool.Views;

namespace WheelDiagnosticTool;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Size the window before navigating so the first page lays out at the right size.
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppServices.MainWindowHandle = hwnd;
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        }
        catch
        {
            // best-effort resize
        }

        RootFrame.Navigate(typeof(WelcomePage));
    }

    public void NavigateTo(Type pageType)
    {
        RootFrame.Navigate(pageType);
    }

    public void SetStepLabel(string text)
    {
        StepText.Text = text;
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    public string GetStatus() => StatusText.Text;
}
