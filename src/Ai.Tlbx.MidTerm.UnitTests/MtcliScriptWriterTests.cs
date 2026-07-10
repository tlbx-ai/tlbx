using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MtcliScriptWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "midterm-mtcli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteScripts_WritesApplyUpdateHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("mt_apply_update()", shell, StringComparison.Ordinal);
        Assert.Contains("$_MT/api/update/apply", shell, StringComparison.Ordinal);
        Assert.Contains("Current version:", shell, StringComparison.Ordinal);

        Assert.Contains("function Mt-ApplyUpdate", powershell, StringComparison.Ordinal);
        Assert.Contains("$script:_MT/api/update/apply", powershell, StringComparison.Ordinal);
        Assert.Contains("Current version:", powershell, StringComparison.Ordinal);
        Assert.Contains("_MBR", shell, StringComparison.Ordinal);
        Assert.Contains("_MJR", shell, StringComparison.Ordinal);
        Assert.Contains("_MCURL --fail-with-body -sSk -b", shell, StringComparison.Ordinal);
        Assert.Contains("command -v curl.exe >/dev/null 2>&1", shell, StringComparison.Ordinal);
        Assert.Contains("function script:_MBR", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MJR", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_WritesOptionalApiKeyAuthHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("set MT_API_KEY", shell, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $MT_API_KEY", shell, StringComparison.Ordinal);
        Assert.Contains("_MCURL --fail-with-body -sSk -H", shell, StringComparison.Ordinal);
        Assert.Contains("_MCURL --fail-with-body -sSk -b", shell, StringComparison.Ordinal);
        Assert.Contains("Treat it like a local session secret", shell, StringComparison.Ordinal);

        Assert.Contains("set MT_API_KEY", powershell, StringComparison.Ordinal);
        Assert.Contains("$env:MT_API_KEY", powershell, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $($env:MT_API_KEY)", powershell, StringComparison.Ordinal);
        Assert.Contains("& curl.exe --fail-with-body -sSk -H", powershell, StringComparison.Ordinal);
        Assert.Contains("& curl.exe --fail-with-body -sSk -b", powershell, StringComparison.Ordinal);
        Assert.Contains("Treat it like a local session secret", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_WritesSessionScopedPreviewHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("MT_SESSION_ID", shell, StringComparison.Ordinal);
        Assert.Contains("MT_PREVIEW_NAME", shell, StringComparison.Ordinal);
        Assert.Contains("mt_context()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_session()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_preview()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_preview_reset()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_previews()", shell, StringComparison.Ordinal);
        Assert.Contains("_MCTXERR()", shell, StringComparison.Ordinal);
        Assert.Contains("_MREQUIRECTX()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_context --bash or mt_context --pwsh", shell, StringComparison.Ordinal);
        Assert.Contains("export MT_SESSION_ID=%q; export MT_PREVIEW_NAME=%q", shell, StringComparison.Ordinal);
        Assert.Contains("Usage: mt_context [text|bash|pwsh|json]", shell, StringComparison.Ordinal);
        Assert.Contains("_MSTATUS_URL()", shell, StringComparison.Ordinal);
        Assert.Contains("_MSTATUS()", shell, StringComparison.Ordinal);
        Assert.Contains("_MWAITCONTROLLABLE()", shell, StringComparison.Ordinal);
        Assert.Contains("_MURLENC()", shell, StringComparison.Ordinal);
        Assert.Contains("/api/browser/status-text", shell, StringComparison.Ordinal);
        Assert.Contains("mt_navigate() {", shell, StringComparison.Ordinal);
        Assert.Contains("mt_navigate failed: preview did not become controllable.", shell, StringComparison.Ordinal);
        Assert.Contains("\"$_MT/api/browser/open\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("mt_navigate()   { _MREQUIRECTX \"mt_navigate\" || return $?; _MJ", shell, StringComparison.Ordinal);
        Assert.Contains("mt_open() {", shell, StringComparison.Ordinal);
        Assert.Contains("mt_mobile() {", shell, StringComparison.Ordinal);
        Assert.Contains("/api/browser/mobile-device", shell, StringComparison.Ordinal);
        Assert.Contains("local claim=0 url=\"\" open_out status", shell, StringComparison.Ordinal);
        Assert.Contains("--claim) claim=1", shell, StringComparison.Ordinal);
        Assert.Contains("mt_claim_preview >/dev/null", shell, StringComparison.Ordinal);
        Assert.Contains("mt_claim_main_browser()", shell, StringComparison.Ordinal);
        Assert.Contains("_MBB claim-main --browser \"$1\"", shell, StringComparison.Ordinal);
        Assert.Contains("status=$(_MWAITCONTROLLABLE 25)", shell, StringComparison.Ordinal);
        Assert.Contains("controllable: yes", shell, StringComparison.Ordinal);
        Assert.Contains("selected visible: yes", shell, StringComparison.Ordinal);
        Assert.Contains("sessionId", shell, StringComparison.Ordinal);
        Assert.Contains("$(_MSID)", shell, StringComparison.Ordinal);
        Assert.Contains("previewName", shell, StringComparison.Ordinal);
        Assert.Contains("$(_MPREVIEW)", shell, StringComparison.Ordinal);
        Assert.Contains("\\\"activateSession\\\":true", shell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Context", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Session", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Preview", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-PreviewReset", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Previews", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-ClaimPreview", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-ClaimMainBrowser", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Capabilities", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Inspect", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MContextMissingMessage", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MRequireSessionContext", powershell, StringComparison.Ordinal);
        Assert.Contains("mt_context --bash or mt_context --pwsh", powershell, StringComparison.Ordinal);
        Assert.Contains("Usage: mt_context [text|bash|pwsh|json]", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MStatusUrl", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MStatus", powershell, StringComparison.Ordinal);
        Assert.Contains("function script:_MWaitForControllableStatus", powershell, StringComparison.Ordinal);
        Assert.Contains("/api/browser/status-text", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Navigate {", powershell, StringComparison.Ordinal);
        Assert.Contains("usage: mt_navigate URL", powershell, StringComparison.Ordinal);
        Assert.Contains("mt_navigate failed: preview did not become controllable.", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("-X PUT \"$script:_MT/api/webpreview/target\"", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Open {", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Mobile {", powershell, StringComparison.Ordinal);
        Assert.Contains("param([string]$Url, [switch]$Claim)", powershell, StringComparison.Ordinal);
        Assert.Contains("Mt-ClaimPreview | Out-Null", powershell, StringComparison.Ordinal);
        Assert.Contains("$openResponse = _MJR -d", powershell, StringComparison.Ordinal);
        Assert.Contains("$status = _MWaitForControllableStatus", powershell, StringComparison.Ordinal);
        Assert.Contains("controllable: yes", powershell, StringComparison.Ordinal);
        Assert.Contains("selected visible: yes", powershell, StringComparison.Ordinal);
        Assert.Contains("Get-Command $candidate -ErrorAction SilentlyContinue", powershell, StringComparison.Ordinal);
        Assert.Contains("Unknown MidTerm CLI command: $cmd", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_context -Value Mt-Context", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_session -Value Mt-Session", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_preview -Value Mt-Preview", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_claim_preview -Value Mt-ClaimPreview", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_claim_main_browser -Value Mt-ClaimMainBrowser", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_capabilities -Value Mt-Capabilities", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_inspect -Value Mt-Inspect", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_mobile -Value Mt-Mobile", powershell, StringComparison.Ordinal);
        Assert.Contains("$env:MT_SESSION_ID", powershell, StringComparison.Ordinal);
        Assert.Contains("previewName=(_MPreview)", powershell, StringComparison.Ordinal);
        Assert.Contains("activateSession=$true", powershell, StringComparison.Ordinal);
        Assert.Contains("_normalized_cmd=\"${_cmd#mt_}\"", shell, StringComparison.Ordinal);
        Assert.Contains("_normalized_cmd=\"${_cmd#mt-}\"", shell, StringComparison.Ordinal);
        Assert.Contains("command -v \"mt_$_normalized_cmd\"", shell, StringComparison.Ordinal);
        Assert.Contains("Unknown MidTerm CLI command: %s", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_WritesRemoteSessionControlHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("mt_tail()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_sendtext()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_paste()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_prompt()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_prompt_now()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_slash()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_wake()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_wake_cancel()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_sendkeys()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_inject()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_activity()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_attention()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_bootstrap()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_supervise()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_ctrlc()", shell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Tail", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-SendText", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Paste", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Prompt", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-PromptNow", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Slash", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Wake", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-WakeCancel", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-SendKeys", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Inject", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Activity", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Attention", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Bootstrap", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Supervise", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Ctrlc", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_open -Value Mt-Open", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_paste -Value Mt-Paste", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_prompt -Value Mt-Prompt", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_wake -Value Mt-Wake", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_supervise -Value Mt-Supervise", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_status -Value Mt-Status", powershell, StringComparison.Ordinal);
        Assert.Contains("ValueFromRemainingArguments", powershell, StringComparison.Ordinal);
        Assert.Contains("/buffer/tail?lines=", shell, StringComparison.Ordinal);
        Assert.Contains("/input/keys", powershell, StringComparison.Ordinal);
        Assert.Contains("/input/text", shell, StringComparison.Ordinal);
        Assert.Contains("/input/paste", shell, StringComparison.Ordinal);
        Assert.Contains("/input/prompt", shell, StringComparison.Ordinal);
        Assert.Contains("/api/command-bay/queue", shell, StringComparison.Ordinal);
        Assert.Contains("/inject-guidance", shell, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/attention", shell, StringComparison.Ordinal);
        Assert.Contains("/api/workers/bootstrap", powershell, StringComparison.Ordinal);
        Assert.Contains("/activity?seconds=", powershell, StringComparison.Ordinal);
        Assert.Contains("midterm supervisor snapshot", shell, StringComparison.Ordinal);
        Assert.Contains("fleet attention:", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_WritesNormalizedDirectExecutionHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("_normalized_cmd=\"${_cmd#mt_}\"", shell, StringComparison.Ordinal);
        Assert.Contains("_normalized_cmd=\"${_cmd#mt-}\"", shell, StringComparison.Ordinal);
        Assert.Contains("command -v \"mt_$_normalized_cmd\"", shell, StringComparison.Ordinal);

        Assert.Contains("$normalizedCmd = if ($cmd -match '^(?i)mt[_-](.+)$') { $Matches[1] } else { $cmd }", powershell, StringComparison.Ordinal);
        Assert.Contains("$pascalCmd = if ($normalizedCmd.Length -gt 0)", powershell, StringComparison.Ordinal);
        Assert.Contains("\"mt_$normalizedCmd\"", powershell, StringComparison.Ordinal);
        Assert.Contains("\"Mt-$pascalCmd\"", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_OpenWaitsForControllableStatusWithoutAnonymousRetry()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("mt_status()     { _MREQUIRECTX \"mt_status\" || return $?; _MSTATUS", shell, StringComparison.Ordinal);
        Assert.Contains("open_out=$(_MJR -d", shell, StringComparison.Ordinal);
        Assert.Contains("mt_proxylog_summary()", shell, StringComparison.Ordinal);
        Assert.Contains("_MBB proxylog-summary --limit", shell, StringComparison.Ordinal);
        Assert.Contains("status=$(_MWAITCONTROLLABLE 25)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("_MNOSESSION()", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("original=(", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("output=$(_MB \"${original[@]}\")", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("if [ -n \"$(_MPREVIEW)\" ] && ! _MHAS \"--preview\" \"${args[@]}\"; then", shell, StringComparison.Ordinal);

        Assert.Contains("function Mt-Status     { _MRequireSessionContext \"mt_status\"; try { _MStatus }", powershell, StringComparison.Ordinal);
        Assert.Contains("$openResponse = _MJR -d", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-ProxyLogSummary", powershell, StringComparison.Ordinal);
        Assert.Contains("$status = _MWaitForControllableStatus", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("function script:_MShouldRetryAnonymous", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("$originalArgs = @($args)", powershell, StringComparison.Ordinal);
        Assert.DoesNotContain("$output = _MB @originalArgs", powershell, StringComparison.Ordinal);
        Assert.Contains("elseif ($env:MT_PREVIEW_NAME -and -not ($allArgs -contains \"--preview\"))", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_WritesAgentsGuidanceWithSessionScopedPreviewWorkflow()
    {
        Directory.CreateDirectory(_tempDir);

        MidtermDirectory.Ensure(_tempDir);

        var agentsPath = Path.Combine(_tempDir, MidtermDirectory.DirectoryName, "AGENTS.md");
        var agents = File.ReadAllText(agentsPath);

        Assert.Contains("guidance-version:", agents, StringComparison.Ordinal);
        Assert.Contains("mt_apply_update", agents, StringComparison.Ordinal);
        Assert.Contains("continue with the new build", agents, StringComparison.Ordinal);
        Assert.Contains("mt_open` both sets the target", agents, StringComparison.Ordinal);
        Assert.Contains("mt_open is the CLI command that opens/docks the preview", agents, StringComparison.Ordinal);
        Assert.Contains("state: ready", agents, StringComparison.Ordinal);
        Assert.Contains("Every C# change in the local source loop restarts the source `mt`", agents, StringComparison.Ordinal);
        Assert.Contains("the outer MidTerm browser tab owns `/ws/state`", agents, StringComparison.Ordinal);
        Assert.Contains("ui clients: 0", agents, StringComparison.Ordinal);
        Assert.Contains("mt_session prints the current MidTerm terminal session ID", agents, StringComparison.Ordinal);
        Assert.Contains("mt_context --bash / mt_context --pwsh", agents, StringComparison.Ordinal);
        Assert.Contains("mt_preview user1", agents, StringComparison.Ordinal);
        Assert.Contains("mt_tail", agents, StringComparison.Ordinal);
        Assert.Contains("mt_prompt", agents, StringComparison.Ordinal);
        Assert.Contains("mt_prompt_now", agents, StringComparison.Ordinal);
        Assert.Contains("mt_slash", agents, StringComparison.Ordinal);
        Assert.Contains("mt_wake", agents, StringComparison.Ordinal);
        Assert.Contains("mt_sendkeys", agents, StringComparison.Ordinal);
        Assert.Contains("mt_activity", agents, StringComparison.Ordinal);
        Assert.Contains("mt_attention", agents, StringComparison.Ordinal);
        Assert.Contains("mt_bootstrap", agents, StringComparison.Ordinal);
        Assert.Contains("mt_supervise", agents, StringComparison.Ordinal);
        Assert.Contains("mt_preview_reset", agents, StringComparison.Ordinal);
        Assert.Contains("status` and `mt_status` both resolve", agents, StringComparison.Ordinal);
        Assert.Contains("atomically", agents, StringComparison.Ordinal);
        Assert.Contains("recreates and refreshes it automatically", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_DoesNotCreateRootAgentDocs()
    {
        Directory.CreateDirectory(_tempDir);

        MidtermDirectory.Ensure(_tempDir);

        Assert.False(File.Exists(Path.Combine(_tempDir, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "CLAUDE.md")));
    }

    [Fact]
    public void TryEnsureForCwd_ReturnsNullWhenDirectoryIsMissing()
    {
        var missingPath = Path.Combine(_tempDir, "missing");

        var result = MidtermDirectory.TryEnsureForCwd(missingPath);

        Assert.Null(result);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
