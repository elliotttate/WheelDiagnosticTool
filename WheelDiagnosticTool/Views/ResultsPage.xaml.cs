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

        // Write the file off-thread to keep the UI responsive even for huge dumps.
        string path = await Task.Run(() => ReportWriter.WriteToFile(DiagnosticSession.Instance));
        LocalPathText.Text = path;
        OpenFolderButton.IsEnabled = true;
        OpenFileButton.IsEnabled = true;

        // Preview the head of the file.
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

        var result = await AppServices.Uploader.UploadAsync(path);
        if (result.Ok && !string.IsNullOrEmpty(result.Url))
        {
            DiagnosticSession.Instance.FilebinUrl = result.Url;
            UrlBox.Text = result.Url;
            CopyUrlButton.IsEnabled = true;
            OpenUrlButton.IsEnabled = true;
            ClipboardService.TrySetText(result.Url);
            Title.Text = "Report ready ✓";
            Subtitle.Text = "The shareable link has been copied to your clipboard.";
            if (App.MainAppWindow is MainWindow mwOk) mwOk.SetStatus("Done — URL copied to clipboard");
        }
        else
        {
            DiagnosticSession.Instance.FilebinError = result.Error;
            UploadError.Text = $"Upload failed: {result.Error}\n\nYou can still share the local file or click Retry upload.";
            UploadError.Visibility = Visibility.Visible;
            RetryUploadButton.Visibility = Visibility.Visible;
            Title.Text = "Report ready (upload pending)";
            Subtitle.Text = "The .txt was written locally; upload to filebin.net failed and can be retried.";
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
        try { Process.Start(new ProcessStartInfo(UrlBox.Text) { UseShellExecute = true }); } catch { }
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
        // Reset session state and walk back to the welcome page.
        var s = DiagnosticSession.Instance;
        s.CaptureSteps.Clear();
        s.ButtonIdentifications.Clear();
        s.FfbProbe.Effects.Clear();
        s.FfbProbe.Skipped = false;
        s.SelectedDevice = null;
        s.LocalReportPath = null;
        s.FilebinUrl = null;
        s.FilebinError = null;
        if (App.MainAppWindow is MainWindow mw) mw.NavigateTo(typeof(WelcomePage));
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        AppServices.Shutdown();
        Application.Current.Exit();
    }
}
