using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WheelDiagnosticTool.Models;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

public sealed partial class ResultsPage : Page
{
    public ResultsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel("Final — generating report");
            mw.SetStatus("Writing...");
        }

        // Write both the .txt and the JSON sidecar off-thread so the UI stays responsive.
        string path = await Task.Run(() =>
        {
            var p = ReportWriter.WriteToFile(DiagnosticSession.Instance);
            JsonReportWriter.WriteToFile(DiagnosticSession.Instance, p);
            return p;
        });
        LocalPathText.Text = $"{path}\n{DiagnosticSession.Instance.LocalJsonPath}";
        OpenFolderButton.IsEnabled = true;
        OpenFileButton.IsEnabled = true;

        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[Math.Min(64 * 1024, f.Length)];
            int read = await f.ReadAsync(buf);
            PreviewBox.Text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        }
        catch (Exception ex)
        {
            PreviewBox.Text = $"(preview failed: {ex.Message})";
        }

        await UploadAsync(path);
    }

    private async Task UploadAsync(string path)
    {
        if (App.MainAppWindow is MainWindow mw) mw.SetStatus("Uploading to filebin.net...");
        UrlBox.PlaceholderText = "uploading...";
        UrlBox.Text = "";
        UploadError.Visibility = Visibility.Collapsed;
        RetryUploadButton.Visibility = Visibility.Collapsed;
        CopyUrlButton.IsEnabled = false;
        OpenUrlButton.IsEnabled = false;

        // Upload both files under the SAME random bin so they sit together.
        var jsonPath = DiagnosticSession.Instance.LocalJsonPath;
        var bin = FilebinUploader.GenerateBinName();

        var txtResult = await AppServices.Uploader.UploadAsync(path, bin);
        FilebinUploader.UploadResult? jsonResult = null;
        if (!string.IsNullOrEmpty(jsonPath) && File.Exists(jsonPath))
            jsonResult = await AppServices.Uploader.UploadAsync(jsonPath, bin);

        if (txtResult.Ok && !string.IsNullOrEmpty(txtResult.Url))
        {
            DiagnosticSession.Instance.FilebinUrl = txtResult.Url;
            DiagnosticSession.Instance.FilebinJsonUrl = jsonResult?.Url;
            UrlBox.Text = jsonResult?.Url is { Length: > 0 } j
                ? $"{txtResult.Url}\n{j}"
                : txtResult.Url;
            CopyUrlButton.IsEnabled = true;
            OpenUrlButton.IsEnabled = true;
            ClipboardService.TrySetText(UrlBox.Text);
            Title.Text = "Report ready ✓";
            Subtitle.Text = "The shareable link(s) have been copied to your clipboard.";
            if (App.MainAppWindow is MainWindow mwOk) mwOk.SetStatus("Done — URL copied to clipboard");
        }
        else
        {
            DiagnosticSession.Instance.FilebinError = txtResult.Error;
            UploadError.Text = $"Upload failed: {txtResult.Error}\n\nYou can still share the local file(s) or click Retry upload.";
            UploadError.Visibility = Visibility.Visible;
            RetryUploadButton.Visibility = Visibility.Visible;
            Title.Text = "Report ready (upload pending)";
            Subtitle.Text = "Reports were written locally; upload to filebin.net failed and can be retried.";
            if (App.MainAppWindow is MainWindow mwFail) mwFail.SetStatus("Upload failed");
        }
    }

    private async void OnRetryUpload(object sender, RoutedEventArgs e)
    {
        if (DiagnosticSession.Instance.LocalReportPath is { } p)
            await UploadAsync(p);
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(UrlBox.Text))
        {
            ClipboardService.TrySetText(UrlBox.Text);
            if (App.MainAppWindow is MainWindow mw) mw.SetStatus("Copied URL to clipboard");
        }
    }

    private void OnOpenUrl(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(UrlBox.Text)) return;
        // UrlBox may have two lines (txt + json) — open the first.
        var first = UrlBox.Text.Split('\n')[0].Trim();
        try { Process.Start(new ProcessStartInfo(first) { UseShellExecute = true }); } catch { }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var p = DiagnosticSession.Instance.LocalReportPath;
        if (string.IsNullOrEmpty(p)) return;
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{p}\"") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch { }
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var p = DiagnosticSession.Instance.LocalReportPath;
        if (string.IsNullOrEmpty(p)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true }); } catch { }
    }

    private void OnStartOver(object sender, RoutedEventArgs e)
    {
        var s = DiagnosticSession.Instance;
        s.CaptureSteps.Clear();
        s.ButtonIdentifications.Clear();
        s.FfbProbe.Effects.Clear();
        s.FfbProbe.Skipped = false;
        s.SelectedDevice = null;
        s.PolledDevices.Clear();
        s.IdleJitter.Clear();
        s.InferredMapping.Clear();
        s.FlatOutPredictionLines.Clear();
        s.LocalReportPath = null;
        s.LocalJsonPath = null;
        s.FilebinUrl = null;
        s.FilebinJsonUrl = null;
        s.FilebinError = null;
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(typeof(WelcomePage));
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        AppServices.Shutdown();
        Application.Current.Exit();
    }
}
