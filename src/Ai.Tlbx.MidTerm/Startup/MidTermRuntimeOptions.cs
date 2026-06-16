using System.Globalization;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Startup;

public sealed record MidTermRuntimeOptions(
    int Port,
    string BindAddress,
    string? SettingsDirectory,
    bool? ServiceMode,
    MidTermServiceIdentity ServiceIdentity)
{
    public const string PortEnvironmentVariable = "MIDTERM_PORT";
    public const string BindAddressEnvironmentVariable = "MIDTERM_BIND";
    public const string ServiceModeEnvironmentVariable = "MIDTERM_SERVICE_MODE";

    public void ApplyProcessEnvironment()
    {
        Environment.SetEnvironmentVariable(PortEnvironmentVariable, Port.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(BindAddressEnvironmentVariable, BindAddress);

        if (!string.IsNullOrWhiteSpace(SettingsDirectory))
        {
            Environment.SetEnvironmentVariable(
                SettingsService.SettingsDirectoryEnvironmentVariable,
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(SettingsDirectory)));
        }

        if (ServiceMode is not null)
        {
            Environment.SetEnvironmentVariable(ServiceModeEnvironmentVariable, ServiceMode.Value ? "true" : "false");
        }

        ServiceIdentity.ApplyProcessEnvironment();
    }
}
