using System.Diagnostics;
using System.Text.Json;
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
        Assert.Contains("Unknown tlbx CLI command: $cmd", powershell, StringComparison.Ordinal);
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
        Assert.Contains("Unknown tlbx CLI command: %s", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_WritesRemoteSessionControlHelpers()
    {
        Directory.CreateDirectory(_tempDir);

        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var shell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "mtcli.ps1"));

        Assert.Contains("mt_redraw()", shell, StringComparison.Ordinal);
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
        Assert.Contains("mt_control_plane()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_agent_capabilities()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_events()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_dispatch()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_work_add()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_work_update()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_publish_status()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_checkpoint()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_input_history()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_input_history_replay()", shell, StringComparison.Ordinal);
        Assert.Contains("api/input-history?sessionId=", shell, StringComparison.Ordinal);
        Assert.Contains("[ -n \"$sid\" ] || { echo \"Session id required.\"", shell, StringComparison.Ordinal);
        Assert.Contains("mt_bootstrap()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_supervise()", shell, StringComparison.Ordinal);
        Assert.Contains("mt_ctrlc()", shell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Redraw", powershell, StringComparison.Ordinal);
        Assert.Contains("Get-Command nohup -CommandType Application", powershell, StringComparison.Ordinal);
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
        Assert.Contains("function Mt-ControlPlane", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-AgentCapabilities", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Events", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Dispatch", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-WorkAdd", powershell, StringComparison.Ordinal);
        Assert.Contains("if ($RepositoryPath) { $body.repositoryPath = $RepositoryPath }", powershell, StringComparison.Ordinal);
        Assert.Contains("if ($Url) { $body.url = $Url }", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-WorkUpdate", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-PublishStatus", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Checkpoint", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-InputHistory", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-InputHistoryReplay", powershell, StringComparison.Ordinal);
        Assert.Contains("if (-not $SessionId) { Write-Error \"Session id required.\"", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Bootstrap", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Supervise", powershell, StringComparison.Ordinal);
        Assert.Contains("function Mt-Ctrlc", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_open -Value Mt-Open", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_redraw -Value Mt-Redraw", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_paste -Value Mt-Paste", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_prompt -Value Mt-Prompt", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_wake -Value Mt-Wake", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_control_plane -Value Mt-ControlPlane", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_agent_capabilities -Value Mt-AgentCapabilities", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_dispatch -Value Mt-Dispatch", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_publish_status -Value Mt-PublishStatus", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_supervise -Value Mt-Supervise", powershell, StringComparison.Ordinal);
        Assert.Contains("Set-Alias -Name mt_status -Value Mt-Status", powershell, StringComparison.Ordinal);
        Assert.Contains("ValueFromRemainingArguments", powershell, StringComparison.Ordinal);
        Assert.Contains("/buffer/tail?lines=", shell, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/$sid/redraw", shell, StringComparison.Ordinal);
        Assert.Contains("/input/keys", powershell, StringComparison.Ordinal);
        Assert.Contains("/input/text", shell, StringComparison.Ordinal);
        Assert.Contains("/input/paste", shell, StringComparison.Ordinal);
        Assert.Contains("/input/prompt", shell, StringComparison.Ordinal);
        Assert.Contains("/api/command-bay/queue", shell, StringComparison.Ordinal);
        Assert.Contains("/inject-guidance", shell, StringComparison.Ordinal);
        Assert.Contains("/api/sessions/attention", shell, StringComparison.Ordinal);
        Assert.Contains("/api/input-history", shell, StringComparison.Ordinal);
        Assert.Contains("/api/workers/bootstrap", powershell, StringComparison.Ordinal);
        Assert.Contains("/activity?seconds=", powershell, StringComparison.Ordinal);
        Assert.Contains("midterm supervisor snapshot", shell, StringComparison.Ordinal);
        Assert.Contains("fleet attention:", powershell, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_BashBootstrapProducesValidJsonWithSlashCommands()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var scriptPath = ToBashPath(Path.Combine(_tempDir, "mtcli.sh"));
        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "source \"$1\"; _MJ() { printf '%s' \"$2\"; }; " +
            "mt_bootstrap 'review \"worker\"' 'Q:/repo with space' codex status compact");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("review \"worker\"", root.GetProperty("name").GetString());
        Assert.Equal("Q:/repo with space", root.GetProperty("workingDirectory").GetString());
        Assert.Equal("codex", root.GetProperty("profile").GetString());
        var slashCommands = root.GetProperty("slashCommands");
        Assert.Equal(2, slashCommands.GetArrayLength());
        Assert.Equal("status", slashCommands[0].GetString());
        Assert.Equal("compact", slashCommands[1].GetString());
    }

    [Fact]
    public void WriteScripts_BashRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.sh");
        File.WriteAllText(childPath,
            "sleep 0.15\n" +
            "printf 'stdout-sentinel\\n'\n" +
            "printf 'arg1=<%s>\\n' \"$1\"\n" +
            "printf 'arg2=<%s>\\n' \"$2\"\n" +
            "printf 'arg3=<%s>\\n' \"$3\"\n" +
            "if IFS= read -r line; then printf 'stdin=<%s>\\n' \"$line\"; else printf 'stdin=<eof>\\n'; fi\n" +
            "printf 'stderr-sentinel\\n' >&2\n");

        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("source \"$1\"; mt_run_isolated \"$2\" \"$3\" \"$4\" \"$5\" \"$6\"");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(ToBashPath(Path.Combine(_tempDir, "mtcli.sh")));
        startInfo.ArgumentList.Add(ToBashPath(bashPath));
        startInfo.ArgumentList.Add(ToBashPath(childPath));
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = Path.Combine(_tempDir, "runs", runId, "stdout.log");
        var stderrPath = Path.Combine(_tempDir, "runs", runId, "stderr.log");

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin=<eof>", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
        Assert.EndsWith($"/runs/{runId}/stdout.log", root.GetProperty("stdoutPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.EndsWith($"/runs/{runId}/stderr.log", root.GetProperty("stderrPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_PowerShellRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var powershellPath = ResolvePowerShellPath();
        if (powershellPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        MtcliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.ps1");
        File.WriteAllText(childPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$InputArgs)\n" +
            "Start-Sleep -Milliseconds 150\n" +
            "[Console]::Out.WriteLine('stdout-sentinel')\n" +
            "$InputArgs | ForEach-Object -Begin { $index = 0 } -Process { [Console]::Out.WriteLine(('arg{0}=<{1}>' -f (++$index), $_)) }\n" +
            "[Console]::Out.WriteLine(('stdin-length={0}' -f ([Console]::In.ReadToEnd().Length)))\n" +
            "[Console]::Error.WriteLine('stderr-sentinel')\n");

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_tempDir, "mtcli.ps1"));
        startInfo.ArgumentList.Add("mt_run_isolated");
        startInfo.ArgumentList.Add(powershellPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(childPath);
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = root.GetProperty("stdoutPath").GetString()!;
        var stderrPath = root.GetProperty("stderrPath").GetString()!;
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stdout.log"), stdoutPath);
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stderr.log"), stderrPath);

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin-length=0", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
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
        var claudePath = Path.Combine(_tempDir, MidtermDirectory.DirectoryName, "CLAUDE.md");
        var claude = File.ReadAllText(claudePath);

        Assert.Contains("guidance-version:", agents, StringComparison.Ordinal);
        Assert.Contains("mt_apply_update", agents, StringComparison.Ordinal);
        Assert.Contains("continue with the new build", agents, StringComparison.Ordinal);
        Assert.Contains("mt_open` both sets the target", agents, StringComparison.Ordinal);
        Assert.Contains("mt_open is the CLI command that opens/docks the preview", agents, StringComparison.Ordinal);
        Assert.Contains("state: ready", agents, StringComparison.Ordinal);
        Assert.Contains("Every C# change in the local source loop restarts the source `mt`", agents, StringComparison.Ordinal);
        Assert.Contains("the outer tlbx browser tab owns `/ws/state`", agents, StringComparison.Ordinal);
        Assert.Contains("ui clients: 0", agents, StringComparison.Ordinal);
        Assert.Contains("mt_session prints the current tlbx terminal session ID", agents, StringComparison.Ordinal);
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
        Assert.Contains("mt_input_history", agents, StringComparison.Ordinal);
        Assert.Contains("does not infer prompts from PTY output", agents, StringComparison.Ordinal);
        Assert.Contains("mt_control_plane", agents, StringComparison.Ordinal);
        Assert.Contains("mt_publish_status", agents, StringComparison.Ordinal);
        Assert.Contains("mt_agent_capabilities", agents, StringComparison.Ordinal);
        Assert.Contains("mt_dispatch", agents, StringComparison.Ordinal);
        Assert.Contains("mt_events", agents, StringComparison.Ordinal);
        Assert.Contains("outlet for agents, not another agent", agents, StringComparison.Ordinal);
        Assert.Contains("mt_bootstrap", agents, StringComparison.Ordinal);
        Assert.Contains("mt_supervise", agents, StringComparison.Ordinal);
        Assert.Contains("mt_run_isolated", agents, StringComparison.Ordinal);
        Assert.Contains(".midterm/runs/<run-id>/", agents, StringComparison.Ordinal);
        Assert.Contains("mt_run_isolated", claude, StringComparison.Ordinal);
        Assert.Contains("separate argv tokens", claude, StringComparison.Ordinal);
        Assert.Contains(".midterm/runs/<run-id>/", claude, StringComparison.Ordinal);
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

    private static string? ResolveBashPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolvePowerShellPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\PowerShell\7\pwsh.exe"]
            : ["/usr/bin/pwsh", "/usr/local/bin/pwsh"];

        var fixedPath = candidates.FirstOrDefault(File.Exists);
        if (fixedPath is not null)
        {
            return fixedPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, executableName))
            .FirstOrDefault(File.Exists);
    }

    private static string WaitForFileContent(string path, Func<string, bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        string content = string.Empty;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    if (completed(content))
                    {
                        return content;
                    }
                }
                catch (IOException)
                {
                }
            }

            Thread.Sleep(25);
        }

        Assert.Fail($"Timed out waiting for isolated run artifact '{path}'. Last content: {content}");
        return content;
    }

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Length >= 3 && normalized[1] == ':'
            ? $"/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}"
            : normalized;
    }
}
