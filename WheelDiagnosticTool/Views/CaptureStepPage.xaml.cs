using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using Vortice.DirectInput;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

/// <summary>
/// Shared page for all "do the thing, we'll watch the wheel" steps. Reads its
/// prompt + StepId from WizardFlow based on its position in the sequence.
/// Captures every axis observation throughout the step and picks the
/// dominant one (largest delta from baseline) when the user clicks Next.
/// </summary>
public sealed partial class CaptureStepPage : Page
{
    private DispatcherTimer? _timer;
    private string _stepId = "";
    private string _prompt = "";
    private Guid _deviceGuid;
    private readonly Dictionary<string, AxisObservation> _axes = new();
    private readonly Dictionary<int, int> _povs = new();
    private readonly HashSet<int> _buttonsPressed = new();
    private bool _baselineCaptured;
    private int _baselineFramesRemaining;
    private const int BaselineFrames = 30; // ~0.5s at 60Hz

    public CaptureStepPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var session = DiagnosticSession.Instance;
        if (session.SelectedDevice == null)
        {
            // No device — kick back to selection.
            if (App.MainAppWindow is MainWindow mw0) mw0.NavigateTo(typeof(DeviceSelectionPage));
            return;
        }
        if (!Guid.TryParse(session.SelectedDevice.InstanceGuid, out _deviceGuid))
        {
            if (App.MainAppWindow is MainWindow mw1) mw1.NavigateTo(typeof(DeviceSelectionPage));
            return;
        }

        // Figure out which step we are based on the current frame's BackStack
        // depth — but it's simpler to look up by what page we are *and* try
        // each step's param in order to find the first one that hasn't been
        // captured yet.
        (_stepId, _prompt) = ResolveStep(session);

        StepTitle.Text = _prompt;
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(CaptureStepPage) && s.Param == _stepId);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel($"Step {idx + 1} of {WizardFlow.Steps.Count} — {_stepId}");
            mw.SetStatus("Sampling axes...");
        }

        _axes.Clear();
        _povs.Clear();
        _buttonsPressed.Clear();
        _baselineCaptured = false;
        _baselineFramesRemaining = BaselineFrames;

        // Pre-seed an entry per axis so the table doesn't pop in once the user
        // touches each one — they appear with their baseline immediately.
        foreach (var a in session.SelectedDevice.Axes)
            _axes[a.Name] = new AxisObservation { AxisName = a.Name };

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
        // Walk the wizard sequence and pick the first capture step we haven't
        // already recorded a result for.
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
        if (!AppServices.DirectInput.Poll(_deviceGuid, out var state) || state == null) return;

        // Snapshot the eight standard joystick axes.
        UpdateAxis("lX",  state.X);
        UpdateAxis("lY",  state.Y);
        UpdateAxis("lZ",  state.Z);
        UpdateAxis("lRx", state.RotationX);
        UpdateAxis("lRy", state.RotationY);
        UpdateAxis("lRz", state.RotationZ);
        var sliders = state.Sliders;
        if (sliders != null && sliders.Length > 0) UpdateAxis("slider[0]", sliders[0]);
        if (sliders != null && sliders.Length > 1) UpdateAxis("slider[1]", sliders[1]);

        // Buttons
        var btns = state.Buttons;
        if (btns != null)
        {
            for (int i = 0; i < btns.Length; i++)
            {
                if (btns[i]) _buttonsPressed.Add(i);
            }
        }

        // POVs
        var povs = state.PointOfViewControllers;
        if (povs != null)
        {
            for (int i = 0; i < povs.Length; i++)
            {
                _povs[i] = povs[i];
            }
        }

        if (!_baselineCaptured)
        {
            _baselineFramesRemaining--;
            if (_baselineFramesRemaining <= 0)
            {
                // Lock baselines to the median/current value of each axis as the
                // settle point. (Mean would be fine too — for a stationary axis
                // they're equal.)
                foreach (var kv in _axes)
                    kv.Value.Baseline = kv.Value.LastValue;
                _baselineCaptured = true;
                if (App.MainAppWindow is MainWindow mw) mw.SetStatus("Baselines captured — do the motion now");
            }
        }

        RenderLive();
    }

    private void UpdateAxis(string name, int value)
    {
        if (!_axes.TryGetValue(name, out var obs))
        {
            obs = new AxisObservation { AxisName = name };
            _axes[name] = obs;
        }
        obs.LastValue = value;
        if (value < obs.MinObserved) obs.MinObserved = value;
        if (value > obs.MaxObserved) obs.MaxObserved = value;

        if (_baselineCaptured)
        {
            int delta = Math.Abs(value - obs.Baseline);
            if (delta > obs.MaxDeltaFromBaseline) obs.MaxDeltaFromBaseline = delta;
            if (delta > 1500) obs.SeenMotion = true;

            // press direction: sign of the largest signed deviation from baseline
            int signed = value - obs.Baseline;
            if (Math.Abs(signed) > Math.Abs(obs.PressDirection * obs.MaxDeltaFromBaseline))
                obs.PressDirection = signed > 0 ? +1 : -1;
        }
    }

    private void RenderLive()
    {
        // Pick the dominant axis = largest MaxDeltaFromBaseline.
        AxisObservation? top = null;
        foreach (var a in _axes.Values)
        {
            if (top == null || a.MaxDeltaFromBaseline > top.MaxDeltaFromBaseline) top = a;
        }
        if (top != null && top.MaxDeltaFromBaseline > 1500)
        {
            DominantText.Text = $"Detected dominant axis: {top.AxisName}  (motion seen: {top.SeenMotion})";
            DominantRange.Text = $"baseline={top.Baseline}  range observed: {top.MinObserved}..{top.MaxObserved}   travel={top.Range}   press direction: {(top.PressDirection > 0 ? "+ (axis grows when pressed)" : top.PressDirection < 0 ? "- (axis shrinks when pressed)" : "0")}";
        }
        else
        {
            DominantText.Text = _baselineCaptured
                ? "Detected dominant axis: (waiting for >1500 deviation from baseline)"
                : "Capturing idle baseline...";
            DominantRange.Text = "";
        }

        ButtonsText.Text = _buttonsPressed.Count == 0
            ? "Buttons pressed during step: (none)"
            : $"Buttons pressed during step: {string.Join(", ", _buttonsPressed)}";

        if (_povs.Count == 0)
            PovText.Text = "";
        else
        {
            var bits = new List<string>();
            foreach (var kv in _povs) bits.Add($"POV{kv.Key}={kv.Value}");
            PovText.Text = string.Join("   ", bits);
        }

        RenderAxisTable();
    }

    private void RenderAxisTable()
    {
        var rows = new List<TextBlock>();
        rows.Add(new TextBlock
        {
            Text = "  axis        baseline    current      min       max      delta   motion?",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.7,
        });
        foreach (var a in _axes.Values)
        {
            string text = $"  {a.AxisName,-10}  {a.Baseline,8}  {a.LastValue,8}  {(a.MinObserved == int.MaxValue ? 0 : a.MinObserved),8}  {(a.MaxObserved == int.MinValue ? 0 : a.MaxObserved),8}  {a.MaxDeltaFromBaseline,8}  {(a.SeenMotion ? "yes" : "no")}";
            rows.Add(new TextBlock
            {
                Text = text,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            });
        }
        AxisList.ItemsSource = rows;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _axes.Clear();
        _buttonsPressed.Clear();
        _povs.Clear();
        _baselineCaptured = false;
        _baselineFramesRemaining = BaselineFrames;
        if (DiagnosticSession.Instance.SelectedDevice is { } dev)
            foreach (var a in dev.Axes)
                _axes[a.Name] = new AxisObservation { AxisName = a.Name };
        DominantText.Text = "Re-sampling idle baseline...";
        DominantRange.Text = "";
        if (App.MainAppWindow is MainWindow mw) mw.SetStatus("Baseline reset");
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
        StopTimer();

        var result = new CaptureStepResult { StepId = _stepId, Prompt = _prompt };
        foreach (var a in _axes.Values) result.Axes.Add(a);

        AxisObservation? top = null;
        foreach (var a in result.Axes)
        {
            if (a.MaxDeltaFromBaseline < 1500) continue;
            if (top == null || a.MaxDeltaFromBaseline > top.MaxDeltaFromBaseline) top = a;
        }
        if (top != null)
        {
            result.DominantAxisName = top.AxisName;
            result.DominantBaseline = top.Baseline;
            result.DominantMin = top.MinObserved == int.MaxValue ? 0 : top.MinObserved;
            result.DominantMax = top.MaxObserved == int.MinValue ? 0 : top.MaxObserved;
            result.DominantPressDirection = top.PressDirection;
        }

        foreach (var b in _buttonsPressed) result.ButtonsPressed.Add(b);
        result.ButtonsPressed.Sort();
        foreach (var kv in _povs) result.PovValues[kv.Key] = kv.Value;

        DiagnosticSession.Instance.CaptureSteps.Add(result);
        Advance();
    }

    private void Advance()
    {
        // Walk to the next page in the flow (which may be another capture step
        // with a new StepId, or a different page type entirely).
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(CaptureStepPage) && s.Param == _stepId);
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        var next = WizardFlow.Steps[idx + 1];
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(next.PageType);
    }
}
