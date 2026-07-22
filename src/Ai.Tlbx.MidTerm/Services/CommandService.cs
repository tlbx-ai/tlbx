using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Services;

public sealed class CommandService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".sh", ".cmd", ".bat", ".zsh"
    };

    private static string GetScriptsDir(string basePath)
    {
        return TlbxDirectory.Ensure(basePath);
    }

    public static string MapExtensionToShellType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".ps1" => "Pwsh",
            ".cmd" or ".bat" => "Cmd",
            ".sh" => "Bash",
            ".zsh" => "Zsh",
            _ => "Bash"
        };
    }

    public ScriptListResponse ListScripts(string workingDirectory)
    {
        var dir = GetScriptsDir(workingDirectory);
        var response = new ScriptListResponse { ScriptsDirectory = dir };

        if (!Directory.Exists(dir))
        {
            return response;
        }

        var scripts = new List<ScriptDefinition>();
        foreach (var file in Directory.EnumerateFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var script = ParseScriptFile(file);
            if (script is not null)
            {
                scripts.Add(script);
            }
        }

        response.Scripts = scripts.ToArray();
        return response;
    }

    private static ScriptDefinition? ParseScriptFile(string filePath)
    {
        try
        {
            var filename = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            var name = Path.GetFileNameWithoutExtension(filePath);
            var content = File.ReadAllText(filePath);

            return new ScriptDefinition
            {
                Filename = filename,
                Name = name,
                Extension = extension,
                ShellType = MapExtensionToShellType(extension),
                Content = content
            };
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to parse script file {filePath}: {ex.Message}");
            return null;
        }
    }

    public ScriptDefinition CreateScript(string workingDirectory, string name, string extension, string content)
    {
        var dir = TlbxDirectory.Ensure(workingDirectory);

        var filename = $"{name}{extension}";
        var filePath = Path.Combine(dir, filename);

        File.WriteAllText(filePath, content);

        return new ScriptDefinition
        {
            Filename = filename,
            Name = name,
            Extension = extension,
            ShellType = MapExtensionToShellType(extension),
            Content = content
        };
    }

    public ScriptDefinition? UpdateScript(string workingDirectory, string filename, string content)
    {
        var filePath = Path.Combine(GetScriptsDir(workingDirectory), filename);
        if (!File.Exists(filePath))
        {
            return null;
        }

        File.WriteAllText(filePath, content);

        var extension = Path.GetExtension(filePath);
        return new ScriptDefinition
        {
            Filename = filename,
            Name = Path.GetFileNameWithoutExtension(filePath),
            Extension = extension,
            ShellType = MapExtensionToShellType(extension),
            Content = content
        };
    }

    public bool DeleteScript(string workingDirectory, string filename)
    {
        var filePath = Path.Combine(GetScriptsDir(workingDirectory), filename);
        if (!File.Exists(filePath))
        {
            return false;
        }

        File.Delete(filePath);
        return true;
    }

    public async Task<RunScriptResponse> RunScriptAsync(
        string workingDirectory,
        string filename,
        TtyHostSessionManager sessionManager,
        CancellationToken ct = default)
    {
        var filePath = Path.Combine(GetScriptsDir(workingDirectory), filename);
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException("Script file not found");
        }

        var extension = Path.GetExtension(filePath);
        var shellType = MapExtensionToShellType(extension);

        var session = await sessionManager.CreateSessionAsync(shellType, 120, 30, workingDirectory, ct);
        if (session is null)
        {
            throw new InvalidOperationException("Failed to create hidden session");
        }

        sessionManager.MarkHidden(session.Id);

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(string sid, ulong _, int __, int ___, ReadOnlyMemory<byte> ____)
        {
            if (sid == session.Id)
            {
                readyTcs.TrySetResult();
            }
        }

        sessionManager.OnOutput += OnOutput;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await readyTcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn(() => $"Shell readiness timeout for session {session.Id}, sending command anyway");
            }
        }
        finally
        {
            sessionManager.OnOutput -= OnOutput;
        }

        var command = BuildExecutionCommand(filename, extension);
        var commandBytes = Encoding.UTF8.GetBytes(command + "\n");
        await sessionManager.SendInputAsync(session.Id, commandBytes, ct);

        return new RunScriptResponse { HiddenSessionId = session.Id };
    }

    private static string BuildExecutionCommand(string filename, string extension)
    {
        var scriptPath = $"{TlbxDirectory.DirectoryName}/{filename}";
        return extension.ToLowerInvariant() switch
        {
            ".ps1" => $"& './{scriptPath}'",
            ".cmd" or ".bat" => scriptPath,
            ".sh" => $"bash '{scriptPath}'",
            ".zsh" => $"zsh '{scriptPath}'",
            _ => $"bash '{scriptPath}'"
        };
    }
}
