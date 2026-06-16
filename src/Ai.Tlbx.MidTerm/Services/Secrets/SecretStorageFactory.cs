using System.Diagnostics.CodeAnalysis;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Secrets;

public static class SecretStorageFactory
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Platform checks are performed via OperatingSystem.IsX() guards")]
    public static ISecretStorage Create(string settingsDirectory, bool isServiceMode)
    {
#if WINDOWS
        return new WindowsSecretStorage(settingsDirectory, isServiceMode);
#else
        // macOS: Use Keychain for user mode, file-based for service mode
        // Keychain access from launchd services is unreliable due to ACL restrictions
        if (OperatingSystem.IsMacOS() && !isServiceMode)
        {
            return new MacOsSecretStorage(settingsDirectory);
        }

        // Linux and macOS service mode use file-based storage
        if (OperatingSystem.IsMacOS() && isServiceMode)
        {
            Log.Info(() => "macOS service mode: using file-based secret storage (Keychain unavailable from launchd)");
        }

        return new UnixFileSecretStorage(settingsDirectory);
#endif
    }
}
