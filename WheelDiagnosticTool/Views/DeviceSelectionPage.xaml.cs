using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

public sealed partial class DeviceSelectionPage : Page
{
    private List<DiDeviceSnapshot> _devices = new();

    public DeviceSelectionPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel("Step 3 — pick device");
            mw.SetStatus("");
        }

        _devices = new List<DiDeviceSnapshot>(DiagnosticSession.Instance.DirectInputDevices);
        DeviceList.Items.Clear();
        foreach (var d in _devices)
        {
            var item = new ListViewItem
            {
                Content = BuildItemContent(d),
                Tag = d,
            };
            DeviceList.Items.Add(item);
        }

        // Auto-select the most wheel-like device (HasFfb first, then steering vendor)
        DiDeviceSnapshot? autoPick = null;
        foreach (var d in _devices)
        {
            if (d.HasFfb) { autoPick = d; break; }
        }
        if (autoPick == null)
        {
            foreach (var d in _devices)
            {
                if (!string.IsNullOrEmpty(d.VendorLabel)) { autoPick = d; break; }
            }
        }
        if (autoPick != null)
        {
            int idx = _devices.IndexOf(autoPick);
            if (idx >= 0) DeviceList.SelectedIndex = idx;
        }
    }

    private static UIElement BuildItemContent(DiDeviceSnapshot d)
    {
        var sp = new StackPanel { Spacing = 2, Padding = new Thickness(8, 4, 8, 4) };
        sp.Children.Add(new TextBlock { Text = d.ProductName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });
        sp.Children.Add(new TextBlock
        {
            Text = $"VID 0x{d.VendorId:X4}  PID 0x{d.ProductId:X4}   {d.VendorLabel}   {d.DeviceTypeLabel}   axes={d.AxisCount} btns={d.ButtonCount} povs={d.PovCount}   FFB={(d.HasFfb ? "yes" : "no")}",
            Opacity = 0.8, FontSize = 13,
        });
        return sp;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NextButton.IsEnabled = DeviceList.SelectedIndex >= 0;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(typeof(EnumerationPage));
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        var idx = DeviceList.SelectedIndex;
        if (idx < 0 || idx >= _devices.Count) return;
        var chosen = _devices[idx];
        DiagnosticSession.Instance.SelectedDevice = chosen;

        // Acquire the chosen device for polling now so subsequent capture pages
        // can read state without re-acquiring each time.
        if (!Guid.TryParse(chosen.InstanceGuid, out var guid))
        {
            // shouldn't happen, but skip acquisition rather than blocking the wizard
        }
        else
        {
            AppServices.DirectInput.AcquireForPolling(guid, AppServices.MainWindowHandle);
        }

        var next = WizardFlow.Next(typeof(DeviceSelectionPage), null);
        if (next != null && App.MainAppWindow is MainWindow mw) mw.NavigateTo(next.PageType);
    }
}
