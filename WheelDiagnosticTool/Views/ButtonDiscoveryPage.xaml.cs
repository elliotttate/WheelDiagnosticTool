using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

/// <summary>
/// Iterates a fixed list of common wheel-rim button labels (Horn, Headlights,
/// Pause, View, Boost, Respawn, Wipers, ...). For each label, polls the
/// device, shows whichever button is currently pressed, and lets the user
/// either Capture that button or Skip.
/// </summary>
public sealed partial class ButtonDiscoveryPage : Page
{
    private DispatcherTimer? _timer;
    private Guid _deviceGuid;
    private int _lastPressedIndex = -1;
    private int _currentTargetIdx;

    private static readonly string[] s_buttonTargets =
    {
        "Horn",
        "Headlights / lights toggle",
        "Pause / menu",
        "Look back / rear view",
        "Toggle camera view",
        "Boost / nitro",
        "Respawn / reset car",
        "Wipers (front)",
        "Pit limiter",
        "Engine start / ignition",
        "Map / minimap toggle",
        "Music skip",
        "Trap launch A",
        "Trap launch B",
        "Trap launch C",
        "Trap launch D",
    };

    public ButtonDiscoveryPage() => InitializeComponent();

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
            int idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(ButtonDiscoveryPage));
            mw.SetStepLabel($"Step {idx + 1} of {WizardFlow.Steps.Count} — buttons");
            mw.SetStatus("Press a button on the device, then click Capture.");
        }

        _currentTargetIdx = 0;
        UpdateTargetLabel();
        RenderResults();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnPoll;
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnPoll;
            _timer = null;
        }
    }

    private void OnPoll(object? sender, object e)
    {
        if (!AppServices.DirectInput.Poll(_deviceGuid, out var state) || state == null) return;

        _lastPressedIndex = -1;
        var btns = state.Buttons;
        if (btns != null)
        {
            for (int i = 0; i < btns.Length; i++)
            {
                if (btns[i]) { _lastPressedIndex = i; break; }
            }
        }

        if (_lastPressedIndex >= 0)
        {
            LastPressed.Text = $"Currently pressed: button {_lastPressedIndex}";
            CaptureButton.IsEnabled = true;
        }
        else
        {
            LastPressed.Text = "No button pressed.";
            CaptureButton.IsEnabled = false;
        }
    }

    private void UpdateTargetLabel()
    {
        if (_currentTargetIdx >= s_buttonTargets.Length)
        {
            CurrentTarget.Text = "All targets covered — click Done.";
            CaptureButton.IsEnabled = false;
            SkipButton.IsEnabled = false;
            return;
        }
        CurrentTarget.Text = s_buttonTargets[_currentTargetIdx];
        CaptureButton.IsEnabled = false;
        SkipButton.IsEnabled = true;
    }

    private void RenderResults()
    {
        var rows = new List<TextBlock>();
        foreach (var b in DiagnosticSession.Instance.ButtonIdentifications)
        {
            rows.Add(new TextBlock { Text = $"  {b.Label,-30} -> {(b.Skipped ? "SKIPPED" : "button " + b.ButtonIndex)}" });
        }
        ResultList.ItemsSource = rows;
    }

    private void OnCaptureCurrent(object sender, RoutedEventArgs e)
    {
        if (_currentTargetIdx >= s_buttonTargets.Length || _lastPressedIndex < 0) return;
        var dev = DiagnosticSession.Instance.SelectedDevice;
        DiagnosticSession.Instance.ButtonIdentifications.Add(new ButtonIdentification
        {
            Label = s_buttonTargets[_currentTargetIdx],
            ButtonIndex = _lastPressedIndex,
            DeviceProductName = dev?.ProductName ?? "",
            DeviceProductGuid = dev?.ProductGuidData1 ?? 0,
        });
        _currentTargetIdx++;
        UpdateTargetLabel();
        RenderResults();
    }

    private void OnSkipCurrent(object sender, RoutedEventArgs e)
    {
        if (_currentTargetIdx >= s_buttonTargets.Length) return;
        DiagnosticSession.Instance.ButtonIdentifications.Add(new ButtonIdentification
        {
            Label = s_buttonTargets[_currentTargetIdx],
            Skipped = true,
        });
        _currentTargetIdx++;
        UpdateTargetLabel();
        RenderResults();
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(ButtonDiscoveryPage));
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        var next = WizardFlow.Steps[idx + 1];
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(next.PageType);
    }
}
