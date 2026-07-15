using System.Diagnostics;
using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Power;

internal static class SystemSleepInhibitorBackendFactory
{
    public static ISystemSleepInhibitorBackend Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSystemSleepInhibitorBackend();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ProcessSystemSleepInhibitorBackend(CreateMacOsStartInfo, "caffeinate");
        }

        if (OperatingSystem.IsLinux())
        {
            return new ProcessSystemSleepInhibitorBackend(CreateLinuxStartInfo, "systemd-inhibit");
        }

        return new NoOpSystemSleepInhibitorBackend();
    }

    private static ProcessStartInfo CreateMacOsStartInfo()
    {
        var startInfo = new ProcessStartInfo("caffeinate")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static ProcessStartInfo CreateLinuxStartInfo()
    {
        var startInfo = new ProcessStartInfo("systemd-inhibit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--what=sleep");
        startInfo.ArgumentList.Add("--why=tlbx active sessions");
        startInfo.ArgumentList.Add("--mode=block");
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 60; done"));
        return startInfo;
    }
}
