using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WheelDiagnosticToolLauncher;

internal static class Program
{
    private const string ResourceName = "WheelDiagnosticTool.zip";
    private const string AppFolderName = "WheelDiagnosticTool";
    private const string AppExeName = "WheelDiagnosticTool.exe";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);
            string appDir = Path.Combine(root, "app");
            string appExe = Path.Combine(appDir, AppExeName);
            string versionMarker = Path.Combine(appDir, ".launcher-version");

            Directory.CreateDirectory(root);

            // Re-extract if the marker doesn't match the launcher's build stamp,
            // or if the inner exe is missing. Saves the ~3s zip extraction on
            // every launch once the user has run us once.
            string thisStamp = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
            bool needExtract = !File.Exists(appExe);
            if (!needExtract && File.Exists(versionMarker))
            {
                try { needExtract = File.ReadAllText(versionMarker).Trim() != thisStamp; }
                catch { needExtract = true; }
            }
            else needExtract = true;

            if (needExtract)
            {
                if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                Directory.CreateDirectory(appDir);

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    return Fail($"Embedded archive '{ResourceName}' missing from setup binary. " +
                                "This is a packaging bug — please report the build that produced this file.");
                }

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    string dest = Path.Combine(appDir,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var src = entry.Open();
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                }

                File.WriteAllText(versionMarker, thisStamp);
            }

            if (!File.Exists(appExe))
            {
                return Fail($"Setup ran but the expected app at {appExe} was not produced.");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = appDir,
                UseShellExecute = true,
            });

            return 0;
        }
        catch (Exception ex)
        {
            return Fail($"Setup failed: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }
    }

    private static int Fail(string message)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "setup-error.log"),
                $"{DateTime.UtcNow:O}\n{message}\n");
        }
        catch { /* best effort */ }

        MessageBoxW(IntPtr.Zero,
            message + "\n\nDetails saved to %LOCALAPPDATA%\\" + AppFolderName + "\\setup-error.log",
            "Wheel Diagnostic Tool Setup",
            0x00000010 /* MB_ICONERROR */);
        return 1;
    }
}
