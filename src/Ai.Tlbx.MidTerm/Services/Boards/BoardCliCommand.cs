using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.WebPreview;

namespace Ai.Tlbx.MidTerm.Services.Boards;

/// <summary>Native entry point using the same authenticated, ancestry-verified transport as mt_board.</summary>
internal static class BoardCliCommand
{
    internal static int Run(string[] arguments, string workingDirectory)
    {
        string? bodyFile = null;
        try
        {
            if (arguments.Length > 0 && arguments[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine("mt board list|get ID|item ID ITEM|create JSON|update ID JSON|viewport ID JSON|delete ID REV|add ID JSON|write ID ITEM JSON|remove ID ITEM REV|browser ID ITEM JSON|launch ID ITEM JSON|launches ID");
                Console.WriteLine("mt board serve BOARD_ID DIRECTORY — add a local HTML/JS program (index.html) as a browser tile");
                Console.WriteLine("JSON may be inline or @file.json. Updates require expectedRevision from the latest read.");
                return 0;
            }
            if (arguments.Length > 0 && arguments[0] == "serve")
            {
                if (arguments.Length != 3) throw new ArgumentException("Usage: mt board serve BOARD_ID DIRECTORY");
                var directory = Path.GetFullPath(arguments[2], workingDirectory);
                if (WebPreviewLocalDirectory.ResolveFile(directory, "index.html") is null)
                    throw new ArgumentException("The local program directory must contain an index.html and cannot traverse symbolic links.");
                var write = new BoardItemWrite { Item = new BoardItem
                {
                    Kind = "browser", Title = Path.GetFileName(directory), LocalDirectory = directory,
                    Color = "plain", Width = 640, Height = 440
                }};
                bodyFile = Path.Combine(Path.GetTempPath(), $"tlbx-board-{Guid.NewGuid():N}.json");
                File.WriteAllText(bodyFile, JsonSerializer.Serialize(write, BoardJsonContext.Default.BoardItemWrite));
                arguments = ["add", arguments[1], "@" + bodyFile];
            }
            var windows = OperatingSystem.IsWindows();
            var helper = FindHelper(workingDirectory, windows ? "tlbx_cli.ps1" : "tlbx_cli.sh");
            if (helper is null) throw new InvalidOperationException("No tlbx connection helper found. Run this command in a tlbx session working directory containing .tlbx/tlbx_cli.");
            var start = new ProcessStartInfo(windows ? "pwsh" : "/bin/bash")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (windows)
            {
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-File");
            }
            start.ArgumentList.Add(helper);
            start.ArgumentList.Add("mt_board");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the tlbx CLI transport.");
            // A native caller may itself be piped by an agent. Preserve both channels and drain concurrently.
            using var output = Console.OpenStandardOutput();
            using var error = Console.OpenStandardError();
            Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(output),
                process.StandardError.BaseStream.CopyToAsync(error)).GetAwaiter().GetResult();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (bodyFile is not null) File.Delete(bodyFile);
        }
    }

    private static string? FindHelper(string directory, string name)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, ".tlbx", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
