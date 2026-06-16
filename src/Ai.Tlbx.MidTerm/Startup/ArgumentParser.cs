using System.Globalization;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Startup;

public static class ArgumentParser
{
    public const int DefaultPort = 2000;
    public const string DefaultBindAddress = "0.0.0.0";

    public static (int port, string bindAddress) Parse(string[] args)
    {
        var options = ParseOptions(args);
        return (options.Port, options.BindAddress);
    }

    public static MidTermRuntimeOptions ParseOptions(string[] args)
    {
        var port = DefaultPort;
        var bindAddress = DefaultBindAddress;
        string? settingsDirectory = null;
        bool? serviceMode = null;
        var serviceIdentity = MidTermServiceIdentity.FromEnvironment();

        var envPort = Environment.GetEnvironmentVariable(MidTermRuntimeOptions.PortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPort) &&
            int.TryParse(envPort, CultureInfo.InvariantCulture, out var parsedEnvPort))
        {
            port = parsedEnvPort;
        }

        var envBind = Environment.GetEnvironmentVariable(MidTermRuntimeOptions.BindAddressEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envBind))
        {
            bindAddress = envBind.Trim();
        }

        settingsDirectory = SettingsService.GetSettingsDirectoryOverride();

        var envServiceMode = Environment.GetEnvironmentVariable(MidTermRuntimeOptions.ServiceModeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envServiceMode) &&
            bool.TryParse(envServiceMode, out var parsedServiceMode))
        {
            serviceMode = parsedServiceMode;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var p))
            {
                port = p;
                i++;
            }
            else if (args[i] == "--bind" && i + 1 < args.Length)
            {
                bindAddress = args[i + 1];
                i++;
            }
            else if (args[i] == "--settings-dir" && i + 1 < args.Length)
            {
                settingsDirectory = args[i + 1];
                i++;
            }
            else if (args[i] == "--service-mode")
            {
                serviceMode = true;
            }
            else if (args[i] == "--user-mode")
            {
                serviceMode = false;
            }
            else if (args[i] == "--service-name" && i + 1 < args.Length)
            {
                serviceIdentity = serviceIdentity with { WindowsServiceName = args[i + 1] };
                i++;
            }
            else if (args[i] == "--launchd-label" && i + 1 < args.Length)
            {
                serviceIdentity = serviceIdentity with { LaunchdLabel = args[i + 1] };
                i++;
            }
            else if (args[i] == "--systemd-service" && i + 1 < args.Length)
            {
                serviceIdentity = serviceIdentity with { SystemdServiceName = args[i + 1] };
                i++;
            }
        }

        return new MidTermRuntimeOptions(
            port,
            bindAddress,
            settingsDirectory,
            serviceMode,
            serviceIdentity);
    }
}
