using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Security;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Hosting;

namespace Ai.Tlbx.MidTerm.Services.Security;

public sealed class WindowsFirewallService
{
    public const string ManagedRuleName = "MidTerm HTTPS";

    private readonly ServerBindingInfo _bindingInfo;
    private readonly IPowerShellCommandRunner _commandRunner;

    public WindowsFirewallService(ServerBindingInfo bindingInfo, IPowerShellCommandRunner commandRunner)
    {
        _bindingInfo = bindingInfo;
        _commandRunner = commandRunner;
    }

    public FirewallRuleStatusResponse GetStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CreateBaseStatus();
        }

        var result = _commandRunner.Run(BuildStatusScript());
        if (result.ExitCode != 0)
        {
            return CreateBaseStatus();
        }

        FirewallRuleSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(result.StdOut))
        {
            snapshot = JsonSerializer.Deserialize(result.StdOut, AppJsonContext.Default.FirewallRuleSnapshot);
        }

        var status = CreateBaseStatus();
        if (snapshot is null || !snapshot.Exists)
        {
            return status;
        }

        var expectedPort = _bindingInfo.Port.ToString(CultureInfo.InvariantCulture);
        var currentProgramPath = GetCurrentProgramPath();

        return new FirewallRuleStatusResponse
        {
            Supported = status.Supported,
            CanManage = status.CanManage,
            Port = status.Port,
            BindAddress = status.BindAddress,
            LoopbackOnly = status.LoopbackOnly,
            RuleName = ManagedRuleName,
            RulePresent = true,
            RuleEnabled = string.Equals(snapshot.Enabled, "True", StringComparison.OrdinalIgnoreCase),
            MatchesCurrentPort = string.Equals(snapshot.LocalPort, expectedPort, StringComparison.OrdinalIgnoreCase),
            MatchesCurrentProgram = PathsMatch(snapshot.Program, currentProgramPath),
            RuleLocalPort = snapshot.LocalPort,
            RuleProgramPath = snapshot.Program
        };
    }

    public FirewallRuleStatusResponse EnsureRule()
    {
        EnsureManageAllowed();

        var result = _commandRunner.Run(BuildAddScript());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr)
                ? "Failed to add the Windows firewall rule."
                : result.StdErr.Trim());
        }

        return GetStatus();
    }

    public FirewallRuleStatusResponse RemoveRule()
    {
        EnsureManageAllowed();

        var result = _commandRunner.Run(BuildRemoveScript());
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr)
                ? "Failed to remove the Windows firewall rule."
                : result.StdErr.Trim());
        }

        return GetStatus();
    }

    private FirewallRuleStatusResponse CreateBaseStatus()
    {
        return new FirewallRuleStatusResponse
        {
            Supported = OperatingSystem.IsWindows(),
            CanManage = OperatingSystem.IsWindows() && IsAdministrator(),
            Port = _bindingInfo.Port,
            BindAddress = _bindingInfo.BindAddress,
            LoopbackOnly = IsLoopbackBinding(_bindingInfo.BindAddress),
            RuleName = ManagedRuleName
        };
    }

    private void EnsureManageAllowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Windows firewall management is only available on Windows.");
        }

        if (!IsAdministrator())
        {
            throw new UnauthorizedAccessException("tlbx must be running with Administrator privileges to change Windows firewall rules.");
        }
    }

    private static bool PathsMatch(string? actualPath, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath) || string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(actualPath),
            Path.GetFullPath(expectedPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsLoopbackBinding(string bindAddress)
    {
        return string.Equals(bindAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(bindAddress, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeForPowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetCurrentProgramPath() => Environment.ProcessPath ?? "mt.exe";

    private string BuildStatusScript()
    {
        var ruleName = EscapeForPowerShell(ManagedRuleName);
        return $$"""
$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop
$rule = Get-NetFirewallRule -DisplayName '{{ruleName}}' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $rule) {
  [pscustomobject]@{
    exists = $false
  } | ConvertTo-Json -Compress
  exit 0
}
$portFilter = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule | Select-Object -First 1
$appFilter = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule | Select-Object -First 1
[pscustomobject]@{
  exists = $true
  enabled = [string]$rule.Enabled
  localPort = [string]$portFilter.LocalPort
  program = $appFilter.Program
} | ConvertTo-Json -Compress
""";
    }

    private string BuildAddScript()
    {
        var ruleName = EscapeForPowerShell(ManagedRuleName);
        var programPath = EscapeForPowerShell(GetCurrentProgramPath());
        var localPort = _bindingInfo.Port.ToString(CultureInfo.InvariantCulture);
        return $$"""
$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop
Get-NetFirewallRule -DisplayName '{{ruleName}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule `
  -DisplayName '{{ruleName}}' `
  -Direction Inbound `
  -Action Allow `
  -Enabled True `
  -Profile Any `
  -Protocol TCP `
  -LocalPort {{localPort}} `
  -Program '{{programPath}}' `
  -Description 'Allows inbound HTTPS access to MidTerm.' | Out-Null
""";
    }

    private string BuildRemoveScript()
    {
        var ruleName = EscapeForPowerShell(ManagedRuleName);
        return $$"""
$ErrorActionPreference = 'Stop'
Import-Module NetSecurity -ErrorAction Stop
Get-NetFirewallRule -DisplayName '{{ruleName}}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
""";
    }
}

public sealed class FirewallRuleSnapshot
{
    public bool Exists { get; init; }
    public string? Enabled { get; init; }
    public string? LocalPort { get; init; }
    public string? Program { get; init; }
}
