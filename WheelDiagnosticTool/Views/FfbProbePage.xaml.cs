using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

public sealed partial class FfbProbePage : Page
{
    private Guid _deviceGuid;
    private CancellationTokenSource? _cts;
    private readonly List<EffectRow> _rows = new();

    public FfbProbePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var session = DiagnosticSession.Instance;
        if (session.SelectedDevice == null || !Guid.TryParse(session.SelectedDevice.InstanceGuid, out _deviceGuid))
        {
            if (App.MainAppWindow is MainWindow mw0) mw0.NavigateTo(typeof(DeviceSelectionPage));
            return;
        }
        if (App.MainAppWindow is MainWindow mw)
        {
            int idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(FfbProbePage));
            mw.SetStepLabel($"Step {idx + 1} of {WizardFlow.Steps.Count} — FFB probe");
            mw.SetStatus("");
        }

        if (!session.SelectedDevice.HasFfb)
        {
            WarnBar.Severity = InfoBarSeverity.Informational;
            WarnBar.Title = "No FFB capability";
            WarnBar.Message = "DirectInput reported this device has no force-feedback actuator. We'll still try, but expect every CreateEffect to fail.";
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        SkipButton.IsEnabled = false;

        var session = DiagnosticSession.Instance;
        session.FfbProbe.Effects.Clear();
        session.FfbProbe.Skipped = false;
        session.FfbProbe.RunningVendorProcessesAtProbeTime.Clear();
        foreach (var p in session.VendorProcesses)
            session.FfbProbe.RunningVendorProcessesAtProbeTime.Add($"{p.Vendor} - {p.ProcessName}");

        // Release the polling acquire so we can take exclusive.
        // (Polling acquire is non-exclusive, but DI sometimes won't promote
        // without an unacquire/reacquire.)
        AcquireStatus.Text = "Acquiring exclusive...";
        await Task.Delay(50);

        bool ok = AppServices.Ffb.BeginExclusive(_deviceGuid, AppServices.MainWindowHandle, session.FfbProbe);
        AcquireStatus.Text = ok
            ? $"Exclusive acquired (hr=0x{(uint)session.FfbProbe.AcquireHResult:X8})"
            : $"Exclusive acquire FAILED (hr=0x{(uint)session.FfbProbe.AcquireHResult:X8})";
        AutoCenterStatus.Text = session.FfbProbe.AutoCenterSetOk
            ? "Autocenter disabled OK"
            : $"Autocenter set failed (hr=0x{(uint)session.FfbProbe.AutoCenterHResult:X8})";

        if (!ok)
        {
            // We still let the user continue — the report wants the HRESULT.
            DoneButton.IsEnabled = true;
            return;
        }

        _cts = new CancellationTokenSource();
        try
        {
            await RunEffect("Constant force (intended LEFT, 3 sec)",  () => AppServices.Ffb.TestConstantForceAsync(-7000, 3000, _cts.Token), intendedDirection: "left");
            await RunEffect("Constant force (intended RIGHT, 3 sec)", () => AppServices.Ffb.TestConstantForceAsync(+7000, 3000, _cts.Token), intendedDirection: "right");
            await RunEffect("Spring (3 sec)",                         () => AppServices.Ffb.TestSpringAsync(3000, _cts.Token));
            await RunEffect("Damper (3 sec)",                         () => AppServices.Ffb.TestDamperAsync(3000, _cts.Token));
            await RunEffect("Sine vibration (3 sec)",                 () => AppServices.Ffb.TestSineAsync(3000, 100, _cts.Token));
        }
        finally
        {
            AppServices.Ffb.StopAndRelease();
            // Re-acquire for polling in case there are more pages after this.
            AppServices.DirectInput.AcquireForPolling(_deviceGuid, AppServices.MainWindowHandle);
            DoneButton.IsEnabled = true;
        }
    }

    private async Task RunEffect(string label, Func<Task<FfbEffectResult>> run, string? intendedDirection = null)
    {
        var row = new EffectRow(label, intendedDirection);
        _rows.Add(row);
        EffectRows.ItemsSource = null;
        EffectRows.ItemsSource = BuildRowElements();

        if (App.MainAppWindow is MainWindow mw) mw.SetStatus($"Playing: {label}");
        var result = await run();
        result.IntendedDirection = intendedDirection;
        row.Result = result;
        DiagnosticSession.Instance.FfbProbe.Effects.Add(result);

        // Ask the user.
        if (result.CreateSucceeded)
        {
            row.Awaiting = true;
            EffectRows.ItemsSource = null;
            EffectRows.ItemsSource = BuildRowElements();
            await row.FeltCompletion.Task;
            row.Awaiting = false;
            EffectRows.ItemsSource = null;
            EffectRows.ItemsSource = BuildRowElements();
        }
        else
        {
            EffectRows.ItemsSource = null;
            EffectRows.ItemsSource = BuildRowElements();
        }
    }

    private List<UIElement> BuildRowElements()
    {
        var list = new List<UIElement>();
        foreach (var r in _rows) list.Add(r.Build());
        return list;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        DiagnosticSession.Instance.FfbProbe.Skipped = true;
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(FfbProbePage));
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(WizardFlow.Steps[idx + 1].PageType);
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(FfbProbePage));
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(WizardFlow.Steps[idx + 1].PageType);
    }

    private sealed class EffectRow
    {
        public string Label { get; }
        public string? IntendedDirection { get; }
        public FfbEffectResult? Result { get; set; }
        public bool Awaiting { get; set; }
        public TaskCompletionSource<bool> FeltCompletion { get; } = new();

        public EffectRow(string label, string? intendedDirection = null)
        {
            Label = label;
            IntendedDirection = intendedDirection;
        }

        public UIElement Build()
        {
            var border = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 6, 0, 6),
            };
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            string statusLine;
            if (Result == null) statusLine = "playing...";
            else if (!Result.CreateSucceeded) statusLine = $"CreateEffect FAILED  hr=0x{(uint)Result.HResult:X8}";
            else if (IntendedDirection != null && Result.UserDirection != null)
            {
                string match = string.Equals(IntendedDirection, Result.UserDirection, StringComparison.OrdinalIgnoreCase)
                    ? "matches intended direction"
                    : Result.UserDirection == "none"
                        ? "user felt no force"
                        : $"INVERTED relative to intended direction ({IntendedDirection})";
                statusLine = $"played OK  →  user said wheel pulled {Result.UserDirection.ToUpper()}  ({match})";
            }
            else if (Result.UserFelt == true) statusLine = "played OK  →  user FELT it";
            else if (Result.UserFelt == false) statusLine = "played OK  →  user did NOT feel it";
            else statusLine = "played OK — waiting for your answer below";
            sp.Children.Add(new TextBlock { Text = statusLine, Opacity = 0.85 });

            if (Awaiting)
            {
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
                if (IntendedDirection != null)
                {
                    // Direction-meaningful effect (constant force): three buttons.
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"Which way did the wheel pull? (intended: {IntendedDirection.ToUpper()})",
                        Opacity = 0.85,
                        Margin = new Thickness(0, 4, 0, 0),
                    });

                    var bLeft = new Button { Content = "It pulled LEFT" };
                    bLeft.Click += (_, _) =>
                    {
                        Result!.UserDirection = "left";
                        Result!.UserFelt = true;
                        FeltCompletion.TrySetResult(true);
                    };
                    var bRight = new Button { Content = "It pulled RIGHT" };
                    bRight.Click += (_, _) =>
                    {
                        Result!.UserDirection = "right";
                        Result!.UserFelt = true;
                        FeltCompletion.TrySetResult(true);
                    };
                    var bNone = new Button { Content = "Nothing happened" };
                    bNone.Click += (_, _) =>
                    {
                        Result!.UserDirection = "none";
                        Result!.UserFelt = false;
                        FeltCompletion.TrySetResult(true);
                    };
                    btnRow.Children.Add(bLeft);
                    btnRow.Children.Add(bRight);
                    btnRow.Children.Add(bNone);
                }
                else
                {
                    // Non-directional (spring/damper/sine): keep the simple yes/no.
                    var yes = new Button { Content = "I felt it" };
                    yes.Click += (_, _) =>
                    {
                        Result!.UserFelt = true;
                        FeltCompletion.TrySetResult(true);
                    };
                    var no = new Button { Content = "Nothing" };
                    no.Click += (_, _) =>
                    {
                        Result!.UserFelt = false;
                        FeltCompletion.TrySetResult(true);
                    };
                    btnRow.Children.Add(yes);
                    btnRow.Children.Add(no);
                }
                sp.Children.Add(btnRow);
            }

            border.Child = sp;
            return border;
        }
    }
}
