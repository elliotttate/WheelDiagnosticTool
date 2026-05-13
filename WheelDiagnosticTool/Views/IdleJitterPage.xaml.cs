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
/// 8-second hands-off capture. We record every poll sample per axis and
/// compute min/max/stddev. Quiet axes are silent in the report; noisy or
/// drifting axes get a verdict line.
/// </summary>
public sealed partial class IdleJitterPage : Page
{
    private DispatcherTimer? _timer;
    private DateTime _captureStartUtc;
    private const int DurationMs = 8000;
    private readonly Dictionary<string, List<int>> _samples = new();
    private readonly Dictionary<string, (string device, uint guid, string axis)> _meta = new();

    public IdleJitterPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var session = DiagnosticSession.Instance;
        if (session.SelectedDevice == null || session.PolledDevices.Count == 0)
        {
            if (App.MainAppWindow is MainWindow mw0) mw0.NavigateTo(typeof(DeviceSelectionPage));
            return;
        }
        if (App.MainAppWindow is MainWindow mw)
        {
            int idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(IdleJitterPage));
            mw.SetStepLabel($"Step {idx + 1} of {WizardFlow.Steps.Count} — idle jitter");
            mw.SetStatus("");
        }
        StartCapture();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopTimer();
    }

    private void StartCapture()
    {
        _samples.Clear();
        _meta.Clear();
        _captureStartUtc = DateTime.UtcNow;
        ProgressBarCtl.Value = 0;
        NextButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        StatusLabel.Text = "Capturing... DON'T touch the wheel";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
    }

    private void OnTick(object? sender, object e)
    {
        var session = DiagnosticSession.Instance;
        foreach (var d in session.PolledDevices)
        {
            if (!Guid.TryParse(d.InstanceGuid, out var guid)) continue;
            if (!AppServices.DirectInput.Poll(guid, out var s) || s == null) continue;
            Record(d, "lX",  s.X);
            Record(d, "lY",  s.Y);
            Record(d, "lZ",  s.Z);
            Record(d, "lRx", s.RotationX);
            Record(d, "lRy", s.RotationY);
            Record(d, "lRz", s.RotationZ);
            var sliders = s.Sliders;
            if (sliders != null && sliders.Length > 0) Record(d, "slider[0]", sliders[0]);
            if (sliders != null && sliders.Length > 1) Record(d, "slider[1]", sliders[1]);
        }

        var elapsed = (DateTime.UtcNow - _captureStartUtc).TotalMilliseconds;
        ProgressBarCtl.Value = Math.Min(DurationMs, elapsed);
        LiveLine.Text = $"{(int)elapsed} / {DurationMs} ms";
        if (elapsed >= DurationMs)
        {
            StopTimer();
            Finish();
        }
    }

    private void Record(DiDeviceSnapshot d, string axisName, int value)
    {
        var key = $"{d.ProductGuidData1:X8}::{axisName}";
        if (!_samples.TryGetValue(key, out var list))
        {
            list = new List<int>();
            _samples[key] = list;
            _meta[key] = (d.ProductName, d.ProductGuidData1, axisName);
        }
        list.Add(value);
    }

    private void Finish()
    {
        StatusLabel.Text = "Capture complete";
        var session = DiagnosticSession.Instance;
        session.IdleJitter.Clear();

        var rows = new List<TextBlock>();
        rows.Add(new TextBlock
        {
            Text = "  device                          axis        mean       min       max     range    stddev   verdict",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Opacity = 0.7,
        });

        foreach (var kv in _samples.OrderByDescending(s => s.Value.Count == 0 ? 0 : s.Value.Max() - s.Value.Min()))
        {
            var (deviceName, guid, axis) = _meta[kv.Key];
            var samples = kv.Value;
            if (samples.Count == 0) continue;
            int min = samples.Min(), max = samples.Max();
            double mean = samples.Average();
            double variance = samples.Sum(v => (v - mean) * (v - mean)) / samples.Count;
            double stddev = Math.Sqrt(variance);
            int range = max - min;

            string verdict = range switch
            {
                < 100  => "quiet",
                < 500  => "minor noise",
                < 2000 => "noisy",
                _      => "very noisy / drifting"
            };

            var row = new IdleJitterRow
            {
                DeviceProductName = deviceName,
                DeviceProductGuid = guid,
                AxisName = axis,
                Mean = (int)mean,
                Min = min,
                Max = max,
                StandardDeviation = stddev,
                SampleCount = samples.Count,
                Verdict = verdict,
            };
            session.IdleJitter.Add(row);

            rows.Add(new TextBlock
            {
                Text = $"  {Trim(deviceName, 30),-30}  {axis,-10}  {(int)mean,7}  {min,8}  {max,8}  {range,6}  {stddev,8:0.0}   {verdict}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            });
        }
        JitterTable.ItemsSource = rows;
        NextButton.IsEnabled = true;
        RetryButton.IsEnabled = true;
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private void OnRetry(object sender, RoutedEventArgs e) => StartCapture();

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        StopTimer();
        DiagnosticSession.Instance.IdleJitter.Clear();
        Advance();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        StopTimer();
        Advance();
    }

    private void Advance()
    {
        var idx = WizardFlow.Steps.FindIndex(s => s.PageType == typeof(IdleJitterPage));
        if (idx < 0 || idx + 1 >= WizardFlow.Steps.Count) return;
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(WizardFlow.Steps[idx + 1].PageType);
    }
}
