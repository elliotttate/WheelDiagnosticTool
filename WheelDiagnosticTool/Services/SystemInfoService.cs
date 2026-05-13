using System;
using System.Runtime.InteropServices;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

public static class SystemInfoService
{
    public static void Populate(DiagnosticSession session)
    {
        try { session.OsDescription = RuntimeInformation.OSDescription; } catch { }
        try { session.OsVersion = Environment.OSVersion.VersionString; } catch { }
        try { session.MachineName = Environment.MachineName; } catch { }
        try { session.ComputerArchitecture = RuntimeInformation.OSArchitecture.ToString(); } catch { }
        try { session.LogicalProcessors = Environment.ProcessorCount; } catch { }
        try { session.DotNetRuntime = RuntimeInformation.FrameworkDescription; } catch { }

        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
                session.PhysicalMemoryBytes = (long)memStatus.ullTotalPhys;
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
