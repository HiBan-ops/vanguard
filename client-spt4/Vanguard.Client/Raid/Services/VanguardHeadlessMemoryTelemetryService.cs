using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;

// Responsibility: Produces bounded diagnostics/telemetry for Headless Memory Telemetry Service in the raid lifecycle services.
// Flow: Runtime facts are normalized, deduplicated/rate-gated where needed, then emitted according to Vanguard presentation levels.
// Authority boundary: Observation only; telemetry never changes the gameplay decision it reports.
// Invariant: Operational output stays actionable and repetitive detail remains restricted to diagnostic/trace levels.
namespace Vanguard.Client.Raid.Services;

internal static class VanguardHeadlessMemoryTelemetryService
{
    public const string StatusTag = "VANGUARD_HEADLESS_MEMORY";
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    private static DateTimeOffset nextSampleAtUtc = DateTimeOffset.MinValue;

    public static void Tick(DateTimeOffset now)
    {
#if SPT_CLIENT
        if (!VanguardFikaCompat.IsActualHeadlessProcess
            || VanguardHeadlessPostRaidQuiescenceService.IsActive
            || !VanguardClientDiagnosticsLog.IsEnabled(VanguardAuditLevel.Diagnostic)
            || now < nextSampleAtUtc)
        {
            return;
        }

        nextSampleAtUtc = now + SampleInterval;
        try
        {
            using Process process = Process.GetCurrentProcess();
            long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            WindowsMemorySnapshot systemMemory = TryReadWindowsMemory();
            VanguardClientDiagnosticsLog.Diagnostic(
                StatusTag,
                () => $"VANGUARD_HEADLESS_MEMORY workingSetMiB={ToMiB(process.WorkingSet64):0.0}; privateMiB={ToMiB(process.PrivateMemorySize64):0.0}; pagedMiB={ToMiB(process.PagedMemorySize64):0.0}; virtualMiB={ToMiB(process.VirtualMemorySize64):0.0}; managedMiB={ToMiB(managedBytes):0.0}; systemCommitMiB={systemMemory.CommitUsedMiB:0.0}; systemCommitLimitMiB={systemMemory.CommitLimitMiB:0.0}; physicalUsedMiB={systemMemory.PhysicalUsedMiB:0.0}; physicalTotalMiB={systemMemory.PhysicalTotalMiB:0.0}; systemMemoryAvailable={systemMemory.Available}; gcEnabled={MemoryControllerClass.GCEnabled}; gc0={GC.CollectionCount(0)}; gc1={GC.CollectionCount(1)}; gc2={GC.CollectionCount(2)}; intervalSeconds={SampleInterval.TotalSeconds:0}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                StatusTag,
                $"VANGUARD_HEADLESS_MEMORY_SAMPLE_FAILED type={Safe(exception.GetType().Name)}; message={Safe(exception.Message)}; gameplayUnaffected=true");
        }
#endif
    }

    public static void ResetForRaidLifecycle(string reason)
    {
        nextSampleAtUtc = DateTimeOffset.MinValue;
        VanguardClientDiagnosticsLog.Diagnostic(
            StatusTag,
            () => $"VANGUARD_HEADLESS_MEMORY_RESET reason={Safe(reason)}; intervalSeconds={SampleInterval.TotalSeconds:0}");
    }

    private static WindowsMemorySnapshot TryReadWindowsMemory()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return default;
        }

        MEMORYSTATUSEX status = new()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return default;
        }

        ulong commitUsedBytes = status.ullTotalPageFile >= status.ullAvailPageFile
            ? status.ullTotalPageFile - status.ullAvailPageFile
            : 0;
        ulong physicalUsedBytes = status.ullTotalPhys >= status.ullAvailPhys
            ? status.ullTotalPhys - status.ullAvailPhys
            : 0;

        return new WindowsMemorySnapshot(
            Available: true,
            CommitUsedMiB: ToMiB(commitUsedBytes),
            CommitLimitMiB: ToMiB(status.ullTotalPageFile),
            PhysicalUsedMiB: ToMiB(physicalUsedBytes),
            PhysicalTotalMiB: ToMiB(status.ullTotalPhys));
    }

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);
    private static double ToMiB(ulong bytes) => bytes / (1024d * 1024d);
    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(';', '_').Replace('\n', ' ').Replace('\r', ' ');

    private readonly record struct WindowsMemorySnapshot(
        bool Available,
        double CommitUsedMiB,
        double CommitLimitMiB,
        double PhysicalUsedMiB,
        double PhysicalTotalMiB);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
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
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
