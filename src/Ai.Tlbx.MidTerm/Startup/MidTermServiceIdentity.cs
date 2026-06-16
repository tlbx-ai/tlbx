using System.Globalization;

namespace Ai.Tlbx.MidTerm.Startup;

public sealed record MidTermServiceIdentity(
    string WindowsServiceName,
    string LaunchdLabel,
    string SystemdServiceName)
{
    public const string WindowsServiceNameEnvironmentVariable = "MIDTERM_SERVICE_NAME";
    public const string LaunchdLabelEnvironmentVariable = "MIDTERM_LAUNCHD_LABEL";
    public const string SystemdServiceEnvironmentVariable = "MIDTERM_SYSTEMD_SERVICE";

    public const string DefaultWindowsServiceName = "MidTerm";
    public const string DefaultLaunchdLabel = "ai.tlbx.midterm";
    public const string DefaultSystemdServiceName = "MidTerm";

    public static MidTermServiceIdentity Default { get; } = new(
        DefaultWindowsServiceName,
        DefaultLaunchdLabel,
        DefaultSystemdServiceName);

    public static MidTermServiceIdentity FromEnvironment() => new(
        ResolveEnvironmentValue(WindowsServiceNameEnvironmentVariable, DefaultWindowsServiceName),
        ResolveEnvironmentValue(LaunchdLabelEnvironmentVariable, DefaultLaunchdLabel),
        ResolveEnvironmentValue(SystemdServiceEnvironmentVariable, DefaultSystemdServiceName));

    public void ApplyProcessEnvironment()
    {
        Environment.SetEnvironmentVariable(WindowsServiceNameEnvironmentVariable, WindowsServiceName);
        Environment.SetEnvironmentVariable(LaunchdLabelEnvironmentVariable, LaunchdLabel);
        Environment.SetEnvironmentVariable(SystemdServiceEnvironmentVariable, SystemdServiceName);
    }

    public static string BuildInstanceWindowsServiceName(string instanceName) =>
        string.Create(CultureInfo.InvariantCulture, $"{DefaultWindowsServiceName}-{NormalizeInstanceName(instanceName)}");

    public static string BuildInstanceLaunchdLabel(string instanceName) =>
        string.Create(CultureInfo.InvariantCulture, $"{DefaultLaunchdLabel}.{NormalizeInstanceName(instanceName).ToLowerInvariant()}");

    public static string BuildInstanceSystemdServiceName(string instanceName) =>
        string.Create(CultureInfo.InvariantCulture, $"midterm-{NormalizeInstanceName(instanceName).ToLowerInvariant()}");

    private static string ResolveEnvironmentValue(string variableName, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeInstanceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "instance";
        }

        var chars = value.Trim()
            .Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "instance" : normalized;
    }
}
