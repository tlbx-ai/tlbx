using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using Ai.Tlbx.MidTerm.Models.System;
using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Security;

public static class SystemUserProvider
{
    public static List<UserInfo> GetSystemUsers()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsUsers();
        }
        else
        {
            return GetUnixUsers();
        }
    }

    private static List<UserInfo> GetWindowsUsers()
    {
        var usersByName = new Dictionary<string, UserInfo>(StringComparer.OrdinalIgnoreCase);

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Get-LocalUser | Where-Object { $_.Enabled -eq $true } | Select-Object Name, SID | ConvertTo-Json\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                AddCurrentWindowsIdentity(usersByName);
                AddSessionUsers(usersByName);
                return usersByName.Values.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (string.IsNullOrWhiteSpace(output))
            {
                AddCurrentWindowsIdentity(usersByName);
                AddSessionUsers(usersByName);
                return usersByName.Values.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).ToList();
            }

            using var doc = System.Text.Json.JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var users = doc.RootElement;
                for (var index = 0; index < users.GetArrayLength(); index++)
                {
                    var user = users[index];
                    AddWindowsUser(usersByName, user.GetProperty("Name").GetString(), ReadSid(user));
                }
            }
            else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                AddWindowsUser(usersByName, doc.RootElement.GetProperty("Name").GetString(), ReadSid(doc.RootElement));
            }
        }
        catch
        {
        }

        AddCurrentWindowsIdentity(usersByName);
        AddSessionUsers(usersByName);

        return usersByName.Values.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static void AddWindowsUser(
        IDictionary<string, UserInfo> usersByName,
        string? username,
        string? sid = null)
    {
        var accountName = NormalizeWindowsAccountName(username);
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        if (usersByName.TryGetValue(accountName, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.Sid) && !string.IsNullOrWhiteSpace(sid))
            {
                usersByName[accountName] = new UserInfo
                {
                    Username = existing.Username,
                    Sid = sid
                };
            }

            return;
        }

        usersByName[accountName] = new UserInfo
        {
            Username = accountName,
            Sid = sid
        };
    }

    internal static string? NormalizeWindowsAccountName(string? username)
    {
        var trimmed = username?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    internal static string? NormalizeWindowsUsername(string? username)
    {
        var normalizedAccountName = NormalizeWindowsAccountName(username);
        if (string.IsNullOrWhiteSpace(normalizedAccountName))
        {
            return null;
        }

        var normalized = normalizedAccountName;
        var slashIndex = normalized.LastIndexOf('\\', StringComparison.Ordinal);
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
        {
            normalized = normalized[(slashIndex + 1)..];
        }

        var atIndex = normalized.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            normalized = normalized[..atIndex];
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? ReadSid(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("SID", out var sidObj))
        {
            return null;
        }

        return sidObj.ValueKind == System.Text.Json.JsonValueKind.Object
            ? sidObj.GetProperty("Value").GetString()
            : sidObj.GetString();
    }

    [SupportedOSPlatform("windows")]
    private static void AddCurrentWindowsIdentity(IDictionary<string, UserInfo> usersByName)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            AddWindowsUser(usersByName, identity.Name, identity.User?.Value);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddSessionUsers(IDictionary<string, UserInfo> usersByName)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pSessionInfo, out var sessionCount))
        {
            return;
        }

        try
        {
            var sessionInfoSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < sessionCount; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessionInfo + i * sessionInfoSize);
                if (info.State is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSDisconnected))
                {
                    continue;
                }

                AddWindowsUser(usersByName, GetSessionAccountName(info.SessionId));
            }
        }
        finally
        {
            WTSFreeMemory(pSessionInfo);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string? BuildWindowsAccountName(string? domain, string? username)
    {
        var normalizedUsername = NormalizeWindowsAccountName(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            return null;
        }

        var normalizedDomain = NormalizeWindowsAccountName(domain);
        return string.IsNullOrWhiteSpace(normalizedDomain)
            ? normalizedUsername
            : $"{normalizedDomain}\\{normalizedUsername}";
    }

    [SupportedOSPlatform("windows")]
    private static string? GetSessionAccountName(uint sessionId)
    {
        var username = GetSessionInformationString(sessionId, WTS_INFO_CLASS.WTSUserName);
        var domain = GetSessionInformationString(sessionId, WTS_INFO_CLASS.WTSDomainName);
        return BuildWindowsAccountName(domain, username);
    }

    [SupportedOSPlatform("windows")]
    private static string? GetSessionInformationString(uint sessionId, WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytesReturned))
        {
            return null;
        }

        try
        {
            if (bytesReturned <= 2)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static List<UserInfo> GetUnixUsers()
    {
        var users = new List<UserInfo>();

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // macOS stores users in Directory Services, not /etc/passwd.
                // Use 'dscl' to enumerate local users with their UIDs.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dscl",
                    Arguments = ". -list /Users UniqueID",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is not null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        var username = parts[0];
                        if (!int.TryParse(parts[^1], CultureInfo.InvariantCulture, out var uid))
                        {
                            continue;
                        }

                        if (uid < 500 || username == "nobody" || username == "daemon" || username.StartsWith("_", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        users.Add(new UserInfo { Username = username, Uid = uid });
                    }
                }
            }
            else
            {
                // Linux: read /etc/passwd
                var passwdPath = "/etc/passwd";
                if (!File.Exists(passwdPath))
                {
                    return users;
                }

                var lines = File.ReadAllLines(passwdPath);
                foreach (var line in lines)
                {
                    var parts = line.Split(':');
                    if (parts.Length < 7)
                    {
                        continue;
                    }

                    var username = parts[0];
                    var uidStr = parts[2];
                    var gidStr = parts[3];
                    var shell = parts[6];

                    if (!int.TryParse(uidStr, CultureInfo.InvariantCulture, out var uid) || !int.TryParse(gidStr, CultureInfo.InvariantCulture, out var gid))
                    {
                        continue;
                    }

                    if (uid < 1000)
                    {
                        continue;
                    }

                    if (shell.Contains("nologin", StringComparison.Ordinal) || shell.Contains("false", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (username == "nobody" || username == "daemon" || username.StartsWith("_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    users.Add(new UserInfo
                    {
                        Username = username,
                        Uid = uid,
                        Gid = gid
                    });
                }
            }
        }
        catch
        {
            // Fall back to current user
            try
            {
                var username = Environment.UserName;
                users.Add(new UserInfo
                {
                    Username = username,
                    Uid = (int)getuid(),
                    Gid = (int)getgid()
                });
            }
            catch
            {
            }
        }

        return users;
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint getuid();

    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint getgid();

    [SupportedOSPlatform("windows")]
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [SupportedOSPlatform("windows")]
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [SupportedOSPlatform("windows")]
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [SupportedOSPlatform("windows")]
    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [SupportedOSPlatform("windows")]
    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [SupportedOSPlatform("windows")]
    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7
    }
}
