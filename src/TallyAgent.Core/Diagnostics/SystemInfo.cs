using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TallyAgent.Core.Diagnostics;

public static class SystemInfo
{
    public static long DiskFreeMb()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AgentInfo.DataDir)!);
            return drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch { return -1; }
    }

    public static long ProcessMemoryMb()
    {
        try { return Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); }
        catch { return -1; }
    }

    public static string WindowsVersion() => Environment.OSVersion.VersionString;

    /// <summary>Cheap connectivity signal: any non-loopback interface up.
    /// The authoritative check is whether the API call itself succeeds.</summary>
    public static bool NetworkAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }
}
