using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class AgentControllerInstallationService(
    SettingsService settingsService,
    AcpAgentCatalogService acpAgentCatalog)
{
    public IReadOnlyList<AgentControllerInstallationDto> GetInstalled()
    {
        var settings = settingsService.Load();
        var userProfileDirectory = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(
            settings.RunAsUser,
            settings.RunAsUserSid);
        var installed = new List<AgentControllerInstallationDto>();

        AddIfInstalled(
            installed,
            AiCliProfileService.CodexProfile,
            "Codex",
            "Codex app-server",
            "codex",
            supportsResume: true,
            userProfileDirectory);

        if (AiCliCommandLocator.FindExecutableInPath(AiCliProfileService.ClaudeProfile, userProfileDirectory) is not null &&
            AiCliCommandLocator.FindExecutableInPath("node", userProfileDirectory) is not null)
        {
            installed.Add(new AgentControllerInstallationDto
            {
                Profile = AiCliProfileService.ClaudeProfile,
                Name = "Claude Code",
                Protocol = "Claude Agent SDK",
                Command = "claude + node",
                SupportsResume = true
            });
        }

        foreach (var definition in acpAgentCatalog.GetDefinitions())
        {
            AddIfInstalled(
                installed,
                definition.Profile,
                definition.Name,
                "ACP",
                string.Join(' ', [definition.CommandName, .. definition.Arguments]),
                supportsResume: true,
                userProfileDirectory,
                definition.CommandName);
        }

        return installed;
    }

    private static void AddIfInstalled(
        List<AgentControllerInstallationDto> installed,
        string profile,
        string name,
        string protocol,
        string command,
        bool supportsResume,
        string? userProfileDirectory,
        string? commandName = null)
    {
        if (AiCliCommandLocator.FindExecutableInPath(commandName ?? profile, userProfileDirectory) is null)
        {
            return;
        }

        installed.Add(new AgentControllerInstallationDto
        {
            Profile = profile,
            Name = name,
            Protocol = protocol,
            Command = command,
            SupportsResume = supportsResume
        });
    }
}
