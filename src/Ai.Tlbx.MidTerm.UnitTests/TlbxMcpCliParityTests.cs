using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

/// <summary>
/// This suite protects migration inventory, aliases and option coverage. It does
/// not certify that a registered MCP tool implements its acceptance scenario.
/// </summary>
public sealed partial class TlbxMcpCliParityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-inventory", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryGeneratedBashHelperAndPowerShellAliasHasAnExplicitMigrationRequirement()
    {
        var (bash, powershell) = GenerateScripts();
        var expected = TlbxMcpCliParity.Commands.Select(entry => entry.Command).ToArray();
        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());
        AssertSetEqual(expected, BashFunctionPattern().Matches(bash).Select(match => match.Groups[1].Value));
        var aliases = AliasPattern().Matches(powershell)
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        AssertSetEqual(expected, aliases.Keys);
        foreach (var requirement in TlbxMcpCliParity.Commands)
        {
            Assert.Equal(requirement.PowerShellCommand, aliases[requirement.Command]);
            Assert.NotEmpty(requirement.McpTools);
            Assert.False(string.IsNullOrWhiteSpace(requirement.AcceptanceScenario));
            Assert.False(string.IsNullOrWhiteSpace(requirement.Category));
        }
        var publicFunctions = FunctionPattern().Matches(powershell)
            .Select(match => match.Groups[1].Value)
            .Where(name => name.StartsWith("Mt-", StringComparison.Ordinal) || name.StartsWith("Invoke-Mt", StringComparison.Ordinal));
        AssertSetEqual(TlbxMcpCliParity.Commands.Select(entry => entry.PowerShellCommand), publicFunctions);
    }

    [Fact]
    public void EveryPublicPowerShellParameterIsAccountedForIncludingOptionalFlags()
    {
        var (_, powershell) = GenerateScripts();
        var functions = FunctionPattern().Matches(powershell);
        var actual = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < functions.Count; i++)
        {
            var name = functions[i].Groups[1].Value;
            if (!name.StartsWith("Mt-", StringComparison.Ordinal) && !name.StartsWith("Invoke-Mt", StringComparison.Ordinal)) continue;
            var begin = functions[i].Index + functions[i].Length;
            var end = i + 1 < functions.Count ? functions[i + 1].Index : powershell.Length;
            actual[name] = ParameterNames(powershell[begin..end]);
        }
        AssertSetEqual(TlbxMcpCliParity.PowerShellParameters.Keys, actual.Keys);
        foreach (var contract in TlbxMcpCliParity.PowerShellParameters)
            AssertSetEqual(contract.Value.Split(',', StringSplitOptions.RemoveEmptyEntries), actual[contract.Key]);
    }

    [Fact]
    public void BrowserTextCommandsBeyondHelperAliasesHaveExplicitToolMappings()
    {
        var source = ReadBrowserEndpoints();
        var begin = source.IndexOf("private static BrowserCommandRequest? ParseCliArgs", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        var end = source.IndexOf("return request is null", begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        var parsed = BrowserSwitchCommandPattern().Matches(source[begin..end])
            .Select(match => match.Groups[1].Value);
        var special = BrowserSpecialCommandPattern().Matches(source)
            .Select(match => match.Groups[1].Value);
        AssertSetEqual(TlbxMcpCliParity.BrowserCommands.Select(entry => entry.Command), parsed.Concat(special));
    }

    [Fact]
    public void EveryBoardAndRepositorySubcommandIncludingAliasesIsAccountedFor()
    {
        var (bash, powershell) = GenerateScripts();
        foreach (var script in new[] { bash, powershell })
        {
            var usage = BoardUsagePattern().Match(script);
            Assert.True(usage.Success, "The generated board usage contract was not found.");
            var actions = usage.Groups[1].Value.Split('|').Select(action => action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            AssertSetEqual(TlbxMcpCliParity.BoardCommands.Select(entry => entry.Command), actions);
        }
        var boardBegin = bash.IndexOf("mt_board()", StringComparison.Ordinal);
        var boardEnd = bash.IndexOf("mt_sessions()", boardBegin, StringComparison.Ordinal);
        Assert.True(boardBegin >= 0 && boardEnd > boardBegin);
        var boardActions = ShellCaseActionPattern().Matches(bash[boardBegin..boardEnd])
            .SelectMany(match => match.Groups[1].Value.Split('|'));
        AssertSetEqual(TlbxMcpCliParity.BoardCommands.Select(entry => entry.Command), boardActions);
        var begin = bash.IndexOf("mt_repo()", StringComparison.Ordinal);
        var end = bash.IndexOf("mt_inspect()", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        var repoActions = ShellCaseActionPattern().Matches(bash[begin..end])
            .SelectMany(match => match.Groups[1].Value.Split('|'));
        AssertSetEqual(TlbxMcpCliParity.RepoCommands.Select(entry => entry.Command), repoActions);
    }

    [Fact]
    public void InitialFourReadToolsCannotBeMistakenForCompleteCliDiscoveryCoverage()
    {
        var missing = TlbxMcpCliParity.MissingTools(["context_get", "sessions_list", "browser_snapshot", "browser_screenshot"]);
        Assert.Contains("process_run_isolated", missing, StringComparer.Ordinal);
        Assert.Contains("terminal_prompt_now", missing, StringComparer.Ordinal);
        Assert.Contains("agent_steer", missing, StringComparer.Ordinal);
        Assert.Contains("board_item_write", missing, StringComparer.Ordinal);
        Assert.Contains("tmux_split", missing, StringComparer.Ordinal);
        Assert.DoesNotContain("context_get", missing, StringComparer.Ordinal);
        Assert.DoesNotContain("browser_screenshot", missing, StringComparer.Ordinal);
        // No result from this inventory test is evidence of runtime behavior.
    }

    private (string Bash, string PowerShell) GenerateScripts()
    {
        Directory.CreateDirectory(_directory);
        TlbxCliScriptWriter.WriteScripts(_directory, 2100, "inventory-only-token");
        return (File.ReadAllText(Path.Combine(_directory, "tlbx_cli.sh")),
            File.ReadAllText(Path.Combine(_directory, "tlbx_cli.ps1")));
    }

    private static void AssertSetEqual(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            actual.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string[] ParameterNames(string functionBody)
    {
        var declaration = ParameterStartPattern().Match(functionBody);
        if (!declaration.Success) return [];
        var begin = declaration.Index + declaration.Length;
        var position = begin;
        var depth = 1;
        while (depth > 0 && position < functionBody.Length)
        {
            if (functionBody[position] == '(') depth++;
            if (functionBody[position] == ')') depth--;
            position++;
        }
        Assert.Equal(0, depth);
        return TypedParameterPattern().Matches(functionBody[begin..(position - 1)])
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string ReadBrowserEndpoints()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "Ai.Tlbx.MidTerm", "Services", "Browser", "BrowserEndpoints.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("BrowserEndpoints.cs was not found; the CLI protocol inventory requires repository sources.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^\s*(mt_[a-z_]+)\(\)", RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BashFunctionPattern();
    [GeneratedRegex(@"^\s*Set-Alias -Scope Script -Name (mt_[a-z_]+) -Value ([A-Za-z-]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AliasPattern();
    [GeneratedRegex(@"^\s*function (?:script:)?([\w-]+)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex FunctionPattern();
    [GeneratedRegex(@"\bparam\s*\(", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ParameterStartPattern();
    [GeneratedRegex(@"\]\s*\$(\w+)\b(?!:)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TypedParameterPattern();
    [GeneratedRegex("""^\s*"([a-z][a-z-]*)"\s*=>""", RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BrowserSwitchCommandPattern();
    [GeneratedRegex("""if \(command == "([a-z][a-z-]*)"\)""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BrowserSpecialCommandPattern();
    [GeneratedRegex(@"(?:echo|throw) 'mt_board (list\|[^']+)'", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BoardUsagePattern();
    [GeneratedRegex(@"^\s*([a-z]+(?:\|[a-z]+)*)\)", RegexOptions.Multiline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ShellCaseActionPattern();
}

