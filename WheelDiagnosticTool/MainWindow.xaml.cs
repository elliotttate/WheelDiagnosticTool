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
            // Sized for the densest pages — per-axis capture table on a wheel
            // with a separate pedal addon (Fanatec ClubSport + H-shifter) takes
            // ~24 axis rows + the live monitor card + buttons. 1500x950 keeps
            // the layout from clipping on a 1080p display while still leaving
            // room around it.
            appWindow.Resize(new Windows.Graphics.SizeInt32(1500, 950));
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
