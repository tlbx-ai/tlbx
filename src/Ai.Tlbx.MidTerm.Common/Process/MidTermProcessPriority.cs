using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ai.Tlbx.MidTerm.Common.Process;

public static class MidTermProcessPriority
{
    public const string DefaultPriorityClass = "aboveNormal";

    private static readonly Lock Sync = new();
    private static bool _enabled = true;
    private static ProcessPriorityClass _priorityClass = ProcessPriorityClass.AboveNormal;
    // Process-lifetime handle; closing it never terminates hosted sessions.
    private static readonly Dictionary<int, WindowsProcessPriorityJob> Jobs = [];
    private static string? _instanceKey;

    public static void Configure(bool enabled, string? priorityClassName, string? instanceKey = null)
    {
        lock (Sync)
        {
            _enabled = enabled;
            _priorityClass = ResolvePriorityClass(priorityClassName);
            // Reopen the same group after web-only updates with preserved hosts.
            // Hash the private instance key so another user cannot precreate it.
            _instanceKey = instanceKey;
        }
    }

    internal static string GetJobName(string instanceKey, int sessionId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Global\\tlbx-priority-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey)))}-session-{sessionId}");

    public static SafeFileHandle? HoldInheritedJob(string? instanceId, string? ownerToken, Action<string>? warn = null)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(ownerToken))
            return null;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return WindowsProcessPriorityJob.OpenLifetimeHandle(GetJobName($"{instanceId}:{ownerToken}", process.SessionId));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Standalone hosts and old web servers have no priority group.
            if (ex.NativeErrorCode != 2) warn?.Invoke($"Unable to retain runtime priority group: {ex.Message}");
            return null;
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
                $"Failed to apply tlbx process priority to {role} PID {processId}: {ex.Message}"));
            return false;
        }
    }

    internal static bool ShouldSetPriority(ProcessPriorityClass current, ProcessPriorityClass target)
    {
        if (current == target)
        {
            return false;
        }

        return current is not ProcessPriorityClass.RealTime;
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

        ProcessPriorityClass target;

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            var current = process.PriorityClass;
            if (current == ProcessPriorityClass.RealTime) return false;
            lock (Sync)
            {
                target = _enabled ? _priorityClass : ProcessPriorityClass.Normal;
                // A job cannot span Windows logon sessions. In particular, mt
                // runs in session 0 as LocalSystem and CreateProcessAsUser must
                // not inherit its job when launching an interactive user's host.
                // Keep mt outside the jobs and group hosted runtimes per session.
                if (role != "mt" && !Jobs.ContainsKey(process.SessionId) && (_enabled || _instanceKey is not null))
                {
                    Jobs.Add(process.SessionId, new WindowsProcessPriorityJob(
                        _instanceKey is null ? null : GetJobName(_instanceKey, process.SessionId)));
                }
                foreach (var job in Jobs.Values) job.SetPriority(target);
                // Retain the job when disabled so existing children also return
                // to Normal and future descendants follow the changed setting.
                if (role != "mt" && Jobs.TryGetValue(process.SessionId, out var runtimeJob)) runtimeJob.Assign(process);
                if (ShouldSetPriority(process.PriorityClass, target)) process.PriorityClass = target;
            }
            if (!ShouldSetPriority(current, target))
            {
                return false;
            }

            info?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Applied tlbx process priority {target} to {role} PID {process.Id}"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            warn?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"Failed to apply tlbx process priority to {role} PID {SafeProcessId(process)}: {ex.Message}"));
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
