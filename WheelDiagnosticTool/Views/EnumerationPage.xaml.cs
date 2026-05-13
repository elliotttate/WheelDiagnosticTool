using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

public sealed partial class EnumerationPage : Page
{
    public EnumerationPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel("Step 2 — detecting hardware");
            mw.SetStatus("Scanning...");
        }
        NextButton.IsEnabled = false;
        await RunScanAsync();
        if (App.MainAppWindow is MainWindow mw2)
            mw2.SetStatus("Scan complete");
        NextButton.IsEnabled = true;
    }

    private async Task RunScanAsync()
    {
        var session = DiagnosticSession.Instance;
        session.DirectInputDevices.Clear();
        session.HidDevices.Clear();
        session.XInputSlots.Clear();
        session.VendorProcesses.Clear();
        session.EnumerationNotes.Clear();

        SystemInfoService.Populate(session);

        // Run the heavy enumeration off the UI thread so the page stays responsive.
        await Task.Run(() =>
        {
            var di = AppServices.DirectInput;
            if (!di.Initialize())
                session.EnumerationNotes.Add($"DirectInput init failed: {di.LastInitError ?? "(unknown)"}");

            foreach (var d in di.Enumerate())
                session.DirectInputDevices.Add(d);

            foreach (var h in HidEnumerationService.Enumerate())
                session.HidDevices.Add(h);

            // Cross-reference HID list against DI list so the report can tell
            // the triager which HID-visible devices DI hid.
            foreach (var h in session.HidDevices)
            {
                bool matched = false;
                foreach (var d in session.DirectInputDevices)
                {
                    if (d.VendorId == h.VendorId && d.ProductId == h.ProductId)
                    {
                        matched = true;
                        break;
                    }
                }
                h.MatchesInDirectInput = matched;
            }

            foreach (var x in XInputService.EnumerateAll())
                session.XInputSlots.Add(x);

            foreach (var p in VendorSoftwareScanner.Scan())
                session.VendorProcesses.Add(p);
        });

        Render();
    }

    private void Render()
    {
        var s = DiagnosticSession.Instance;
        DiList.ItemsSource = BuildDiRows(s);
        HidList.ItemsSource = BuildHidRows(s);
        XInputList.ItemsSource = BuildXInputRows(s);
        VendorList.ItemsSource = BuildVendorRows(s);

        SubText.Text = $"{s.DirectInputDevices.Count} DirectInput · {s.HidDevices.Count} HID · {CountConnected(s)} XInput · {s.VendorProcesses.Count} vendor processes";
    }

    private static int CountConnected(DiagnosticSession s)
    {
        int n = 0;
        foreach (var x in s.XInputSlots) if (x.Connected) n++;
        return n;
    }

    private static System.Collections.Generic.List<TextBlock> BuildDiRows(DiagnosticSession s)
    {
        var rows = new System.Collections.Generic.List<TextBlock>();
        if (s.DirectInputDevices.Count == 0)
        {
            rows.Add(new TextBlock { Text = "  (no DirectInput game controllers — wheel may be in XInput-only mode or hidden by Steam Input)", Opacity = 0.7 });
            return rows;
        }
        int i = 0;
        foreach (var d in s.DirectInputDevices)
        {
            rows.Add(new TextBlock
            {
                Text = $"  [{i++}] \"{d.ProductName}\"  VID=0x{d.VendorId:X4} PID=0x{d.ProductId:X4} {d.VendorLabel}  type={d.DeviceTypeLabel}  axes={d.AxisCount} btns={d.ButtonCount} povs={d.PovCount} ffb={(d.HasFfb ? "yes" : "no")}"
            });
        }
        return rows;
    }

    private static System.Collections.Generic.List<TextBlock> BuildHidRows(DiagnosticSession s)
    {
        var rows = new System.Collections.Generic.List<TextBlock>();
        if (s.HidDevices.Count == 0)
        {
            rows.Add(new TextBlock { Text = "  (no HID devices matched a known wheel-vendor VID)", Opacity = 0.7 });
            return rows;
        }
        foreach (var h in s.HidDevices)
        {
            string suffix = h.MatchesInDirectInput ? "" : "  ← visible to Windows but hidden from DI";
            rows.Add(new TextBlock { Text = $"  {h.VendorLabel,-15}  VID=0x{h.VendorId:X4} PID=0x{h.ProductId:X4}  {h.FriendlyName}{suffix}" });
        }
        return rows;
    }

    private static System.Collections.Generic.List<TextBlock> BuildXInputRows(DiagnosticSession s)
    {
        var rows = new System.Collections.Generic.List<TextBlock>();
        foreach (var x in s.XInputSlots)
        {
            if (!x.Connected)
                rows.Add(new TextBlock { Text = $"  [{x.UserIndex}] (empty)", Opacity = 0.5 });
            else
                rows.Add(new TextBlock { Text = $"  [{x.UserIndex}] subType={x.SubTypeLabel}" });
        }
        return rows;
    }

    private static System.Collections.Generic.List<TextBlock> BuildVendorRows(DiagnosticSession s)
    {
        var rows = new System.Collections.Generic.List<TextBlock>();
        if (s.VendorProcesses.Count == 0)
        {
            rows.Add(new TextBlock { Text = "  (no vendor software detected)", Opacity = 0.6 });
            return rows;
        }
        foreach (var p in s.VendorProcesses)
            rows.Add(new TextBlock { Text = $"  pid={p.Pid,-6} {p.Vendor,-14} {p.ProcessName,-30} {p.Description}" });
        return rows;
    }

    private async void OnRescanClicked(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        await RunScanAsync();
        RescanButton.IsEnabled = true;
        NextButton.IsEnabled = true;
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        var next = WizardFlow.Next(typeof(EnumerationPage), null);
        if (next != null && App.MainAppWindow is MainWindow mw)
            mw.NavigateTo(next.PageType);
    }
}
