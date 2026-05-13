using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

/// <summary>
/// Shared page for all "do the thing, we'll watch the wheel" steps. Reads
/// its prompt from WizardFlow based on its position in the sequence.
/// Captures every axis observation across every polled device throughout
/// the step and picks the dominant one (largest delta from baseline) when
/// the user clicks Next.
/// </summary>
public sealed partial class CaptureStepPage : Page
{
    private DispatcherTimer? _timer;
    private string _stepId = "";
    private string _prompt = "";
    private MultiDevicePoller? _poller;
    private bool _confirmedLowConfidence;
    private const int BaselineFrames = 30; // ~0.5s at 60Hz

    public CaptureStepPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var session = DiagnosticSession.Instance;
        if (session.SelectedDevice == null || session.PolledDevices.Count == 0)
        {
            if (App.MainAppWindow is MainWindow mw0) mw0.NavigateTo(typeof(DeviceSelectionPage));
            return;
        }

        (_stepId, _prompt) = ResolveStep(session);
        StepTitle.Text = _prompt;
        ConfirmBar.IsOpen = false;
        _confirmedLowConfidence = false;

        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(CaptureStepPage) && s.Param == _stepId);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel($"Step {idx + 1} of {WizardFlow.Steps.Count} — {_stepId}");
            mw.SetStatus("Sampling axes...");
        }

        _poller = new MultiDevicePoller(AppServices.DirectInput, session.PolledDevices, BaselineFrames);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnPoll;
        _timer.Start();

        RenderAxisTable();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopTimer();
    }

    private void StopTimer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnPoll;
            _timer = null;
        }
    }

    private (string id, string prompt) ResolveStep(DiagnosticSession session)
    {
        foreach (var s in WizardFlow.Steps)
        {
            if (s.PageType != typeof(CaptureStepPage)) continue;
            bool alreadyCaptured = session.CaptureSteps.Exists(c => c.StepId == s.Param);
            if (!alreadyCaptured) return (s.Param ?? "", s.Label);
        }
        return ("DONE", "All capture steps complete");
    }

    private void OnPoll(object? sender, object e)
    {
        if (_poller == null) return;
        _poller.Tick(DiagnosticSession.Instance.SelectedDevice);
        if (_poller.BaselineCaptured && App.MainAppWindow is MainWindow mw && mw.GetStatus() == "Sampling axes...")
            mw.SetStatus("Baselines captured — do the motion now");
        RenderLive();
    }

    private void RenderLive()
    {
        if (_poller == null) return;
        var (top, conf, reason) = _poller.PickDominant();
        if (top != null && top.MaxDeltaFromBaseline > 1500)
        {
            DominantText.Text = $"Detected dominant axis: {top.DeviceProductName}::{top.AxisName}  (confidence: {conf})";
            DominantRange.Text = $"baseline={top.Baseline}  range observed: {top.MinObserved}..{top.MaxObserved}   travel={top.Range}   press direction: {(top.PressDirection > 0 ? "+ (axis grows when pressed)" : top.PressDirection < 0 ? "- (axis shrinks when pressed)" : "0")}";
        }
        else
        {
            DominantText.Text = _poller.BaselineCaptured
                ? "Detected dominant axis: (waiting for >1500 deviation from baseline)"
                : "Capturing idle baseline...";
            DominantRange.Text = "";
        }

        // Show the most recent press events with device tag
        var presses = _poller.ButtonEvents
            .Where(ev => ev.Pressed)
            .GroupBy(ev => (ev.DeviceProductGuid, ev.ButtonIndex))
            .Select(g => $"{g.First().DeviceProductName}::btn{g.Key.ButtonIndex}")
            .Distinct()
            .ToList();
        ButtonsText.Text = presses.Count == 0
            ? "Buttons pressed during step: (none)"
            : $"Buttons pressed during step: {string.Join(", ", presses)}";

        if (_poller.PrimaryPovs.Count == 0)
        {
            PovText.Text = "";
        }
        else
        {
            var bits = new List<string>();
            foreach (var kv in _poller.PrimaryPovs) bits.Add($"POV{kv.Key}={kv.Value}");
            PovText.Text = string.Join("   ", bits);
        }

        RenderAxisTable();
    }

    private void RenderAxisTable()
    {
        if (_poller == null) return;
        var rows = new List<TextBlock>();
        rows.Add(new TextBlock
        {
            Text = "  device                          axis        baseline    current      min       max      delta   motion?",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.7,
        });
        foreach (var a in _poller.Axes.Values.OrderByDescending(a => a.MaxDeltaFromBaseline))
        {
            string text = $"  {Trim(a.DeviceProductName, 30),-30}  {a.AxisName,-10}  {a.Baseline,8}  {a.LastValue,8}  {(a.MinObserved == int.MaxValue ? 0 : a.MinObserved),8}  {(a.MaxObserved == int.MinValue ? 0 : a.MaxObserved),8}  {a.MaxDeltaFromBaseline,8}  {(a.SeenMotion ? "yes" : "no")}";
            rows.Add(new TextBlock
            {
                Text = text,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            });
        }
        AxisList.ItemsSource = rows;
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _poller?.ResetBaseline(BaselineFrames);
        DominantText.Text = "Re-sampling idle baseline...";
        DominantRange.Text = "";
        ConfirmBar.IsOpen = false;
        _confirmedLowConfidence = false;
        if (App.MainAppWindow is MainWindow mw) mw.SetStatus("Sampling axes...");
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        StopTimer();
        var result = new CaptureStepResult { StepId = _stepId, Prompt = _prompt, Skipped = true };
        DiagnosticSession.Instance.CaptureSteps.Add(result);
        Advance();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_poller == null) return;

        var (top, conf, reason) = _poller.PickDominant();
        bool isButtonOriented = StepIsButtonOriented(_stepId);
        bool anyButtonPressed = _poller.ButtonEvents.Any(ev => ev.Pressed);

        // Validation prompt — once per step. If the step looks like it failed,
        // ask the user to confirm before locking the result in.
        if (!_confirmedLowConfidence)
        {
            string? warn = null;
            if (isButtonOriented && !anyButtonPressed)
                warn = "No buttons were pressed during this step. If your shifter/paddle isn't firing, retry; otherwise click Next again to record this as 'no button fired'.";
            else if (!isButtonOriented && conf == CaptureConfidence.Missed)
                warn = $"We only saw {(top?.MaxDeltaFromBaseline ?? 0)} units of motion on any axis. Did you complete the action? Retry, or click Next again to record this anyway.";
            else if (conf == CaptureConfidence.Ambiguous)
                warn = $"Two axes moved by similar amounts ({reason}). Retry while only moving the intended control, or click Next again to record this anyway.";

            if (warn != null)
            {
                ConfirmBar.Message = warn;
                ConfirmBar.IsOpen = true;
                _confirmedLowConfidence = true;
                return;
            }
        }

        StopTimer();

        var result = new CaptureStepResult { StepId = _stepId, Prompt = _prompt };
        foreach (var a in _poller.Axes.Values) result.Axes.Add(a);

        if (top != null)
        {
            result.DominantAxisName = top.AxisName;
            result.DominantDeviceProductName = top.DeviceProductName;
            result.DominantDeviceProductGuid = top.DeviceProductGuid;
            result.DominantBaseline = top.Baseline;
            result.DominantMin = top.MinObserved == int.MaxValue ? 0 : top.MinObserved;
            result.DominantMax = top.MaxObserved == int.MinValue ? 0 : top.MaxObserved;
            result.DominantPressDirection = top.PressDirection;
        }
        result.Confidence = conf;
        result.ConfidenceReason = reason;

        foreach (var ev in _poller.ButtonEvents) result.ButtonEvents.Add(ev);
        foreach (var kv in _poller.PrimaryPovs) result.PovValues[kv.Key] = kv.Value;

        // Crosstalk bleed: for crosstalk-specific steps, snapshot every
        // non-dominant axis that moved more than the noise floor so we can
        // report "throttle bled while only brake was pressed".
        if (IsCrosstalkStep(_stepId))
        {
            foreach (var a in _poller.Axes.Values)
            {
                if (a == top) continue;
                if (a.MaxDeltaFromBaseline < 500) continue;
                result.CrosstalkBleed.Add(a);
            }
        }

        DiagnosticSession.Instance.CaptureSteps.Add(result);
        Advance();
    }

    private static bool StepIsButtonOriented(string stepId) =>
        stepId is "PADDLE_UP" or "PADDLE_DOWN"
            or "GEAR_1" or "GEAR_2" or "GEAR_3" or "GEAR_4"
            or "GEAR_5" or "GEAR_6" or "GEAR_7" or "GEAR_R";

    private static bool IsCrosstalkStep(string stepId) => stepId.StartsWith("CROSSTALK_");

    private void Advance()
    {
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(CaptureStepPage) && s.Param == _stepId);
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        var next = WizardFlow.Steps[idx + 1];
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(next.PageType);
    }
}
