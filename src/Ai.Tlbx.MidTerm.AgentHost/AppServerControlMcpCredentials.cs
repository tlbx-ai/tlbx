using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

/// <summary>Ephemeral owner-only handoff; provider configuration contains only its path.</summary>
internal sealed class AppServerControlMcpCredentials : IDisposable
{
    private readonly string _directory;
    internal AppServerControlMcpServer Server { get; private set; } = new();
    internal string FilePath { get; }

    internal AppServerControlMcpCredentials(AppServerControlMcpServer source)
    {
        _directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-" + Guid.NewGuid().ToString("N"));
        SecurityIdentifier? owner = null;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Cannot identify the MCP credential file owner.");
            owner = user;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
        }
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FilePath = Path.Combine(_directory, "credentials.json");
        try
        {
            Write(source, owner);
        }
        catch
        {
            Directory.Delete(_directory, recursive: true);
            throw;
        }
    }

    internal bool CanRefresh(AppServerControlMcpServer source) =>
        !string.IsNullOrEmpty(source.ScopeIdentity) && string.Equals(Server.ScopeIdentity, source.ScopeIdentity, StringComparison.Ordinal);

    internal void Refresh(AppServerControlMcpServer source)
    {
        if (!CanRefresh(source)) throw new InvalidOperationException("Cannot transfer MCP credentials across runtime scopes.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            Write(source, identity.User);
        }
        else Write(source, null);
    }

    private void Write(AppServerControlMcpServer source, SecurityIdentifier? owner)
    {
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = CreateCredentialFile(temporary, owner))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var (name, value) in source.EnvironmentVariables) writer.WriteString(name, value);
                writer.WriteEndObject();
                writer.Flush();
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, FilePath, overwrite: true); break; }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 16 && (error.HResult & 0xffff) is 5 or 32)
                { Thread.Sleep(25); }
                catch (UnauthorizedAccessException error) when (OperatingSystem.IsWindows() && attempt < 16 && (error.HResult & 0xffff) is 5 or 32)
                { Thread.Sleep(25); }
            }
        }
        finally { File.Delete(temporary); }
        Server = new AppServerControlMcpServer
        {
            ScopeIdentity = source.ScopeIdentity,
            Command = source.Command,
            Arguments = [.. source.Arguments, "--credentials-file", FilePath]
        };
    }

    private static FileStream CreateCredentialFile(string path, SecurityIdentifier? owner)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(owner!);
            security.AddAccessRule(new FileSystemAccessRule(owner!, FileSystemRights.FullControl, AccessControlType.Allow));
            // Apply the protected ACL atomically at creation. An ordinary write
            // handle does not carry WRITE_DAC for a later SetAccessControl call.
            return new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions,
                FileShare.Read, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    public void Dispose()
    {
        try { File.Delete(FilePath); Directory.Delete(_directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
