using System.Diagnostics;
using System.Globalization;

namespace Ai.Tlbx.MidTerm.Common.Process;

public static class MidTermProcessPriority
{
    public const string DefaultPriorityClass = "aboveNormal";

    private static readonly Lock Sync = new();
    private static bool _enabled = true;
    private static ProcessPriorityClass _priorityClass = ProcessPriorityClass.AboveNormal;

    public static void Configure(bool enabled, string? priorityClassName)
    {
        lock (Sync)
        {
            _enabled = enabled;
            _priorityClass = ResolvePriorityClass(priorityClassName);
        }
    }

    public static ProcessPriorityClass ResolvePriorityClass(string? priorityClassName)
    {
        if (string.IsNullOrWhiteSpace(priorityClassName))
        {
            return ProcessPriorityClass.AboveNormal;
        }

        return priorityClassName.Trim().ToLowerInvariant() switch
        {
            "normal" => ProcessPriorityClass.Normal,
            "abovenormal" or "above-normal" or "above_normal" => ProcessPriorityClass.AboveNormal,
            "high" => ProcessPriorityClass.High,
            _ => ProcessPriorityClass.AboveNormal
        };
    }

    public static bool TryApplyToCurrentProcess(
        string role,
        Action<string>? info = null,
        Action<string>? warn = null)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return TryApply(process, role, info, warn);
    }

    public static bool TryApplyToProcessId(
        int processId,
        string role,
        Action<string>? info = null,
        Action<string>? warn = null)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return TryApply(process, role, info, warn);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            warn?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Failed to apply MidTerm process priority to {role} PID {processId}: {ex.Message}"));
            return false;
        }
    }

    internal static bool ShouldSetPriority(ProcessPriorityClass current, ProcessPriorityClass target)
    {
        if (current == target)
        {
            return false;
        }

        return current is not ProcessPriorityClass.High and not ProcessPriorityClass.RealTime;
    }

    private static bool TryApply(
        System.Diagnostics.Process process,
        string role,
        Action<string>? info,
        Action<string>? warn)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        bool enabled;
        ProcessPriorityClass target;
        lock (Sync)
        {
            enabled = _enabled;
            target = _priorityClass;
        }

        if (!enabled)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            var current = process.PriorityClass;
            if (!ShouldSetPriority(current, target))
            {
                return false;
            }

            process.PriorityClass = target;
            info?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Applied MidTerm process priority {target} to {role} PID {process.Id}"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            warn?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Failed to apply MidTerm process priority to {role} PID {SafeProcessId(process)}: {ex.Message}"));
            return false;
        }
    }

    private static int SafeProcessId(System.Diagnostics.Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }
}
