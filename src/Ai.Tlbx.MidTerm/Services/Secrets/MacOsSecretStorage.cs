using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Security.Cryptography;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Secrets;

/// <summary>
/// Stores secrets in the macOS Data Protection Keychain using modern SecItem* APIs.
/// Unlike the legacy SecKeychain* APIs, these do not trigger
/// "would like to access data from other apps" TCC dialogs.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsSecretStorage : ISecretStorage
{
    private const string ServiceNamePrefix = "ai.tlbx.midterm";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    // macOS uses Keychain which is queried on-demand, no "load" phase
    // Individual operation failures are logged in each method
    private readonly string _serviceName;

    public bool LoadFailed => false;
    public string? LoadError => null;

    public MacOsSecretStorage(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        var normalized = Path.GetFullPath(settingsDirectory);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        _serviceName = $"{ServiceNamePrefix}.{suffix}";
    }

    public string? GetSecret(string key)
    {
        var query = IntPtr.Zero;
        var result = IntPtr.Zero;

        try
        {
            query = CreateQuery(key);
            CFDictionarySetValue(query, kSecReturnData, kCFBooleanTrue);
            CFDictionarySetValue(query, kSecMatchLimit, kSecMatchLimitOne);

            var status = SecItemCopyMatching(query, out result);

            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            if (status != ErrSecSuccess)
            {
                Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"Keychain read failed for '{key}' with status {status}"));
                return null;
            }

            var length = CFDataGetLength(result);
            if (length == 0)
            {
                return string.Empty;
            }

            var dataPtr = CFDataGetBytePtr(result);
            var bytes = new byte[length];
            Marshal.Copy(dataPtr, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (query != IntPtr.Zero) CFRelease(query);
            if (result != IntPtr.Zero) CFRelease(result);
        }
    }

    public void SetSecret(string key, string value)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(value);
        var passwordData = IntPtr.Zero;
        var addDict = IntPtr.Zero;
        var updateDict = IntPtr.Zero;
        var query = IntPtr.Zero;

        try
        {
            passwordData = CFDataCreate(IntPtr.Zero, passwordBytes, passwordBytes.Length);
            addDict = CreateQuery(key);
            CFDictionarySetValue(addDict, kSecValueData, passwordData);
            CFDictionarySetValue(addDict, kSecUseDataProtectionKeychain, kCFBooleanTrue);

            var status = SecItemAdd(addDict, IntPtr.Zero);

            if (status == ErrSecDuplicateItem)
            {
                // Item exists — update it
                query = CreateQuery(key);
                updateDict = CFDictionaryCreateMutable(
                    IntPtr.Zero, 1,
                    ref kCFTypeDictionaryKeyCallBacks,
                    ref kCFTypeDictionaryValueCallBacks);
                CFDictionarySetValue(updateDict, kSecValueData, passwordData);

                status = SecItemUpdate(query, updateDict);
            }

            if (status != ErrSecSuccess)
            {
                Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"Keychain write failed for '{key}' with status {status}"));
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Failed to store secret in Keychain: status {status}"));
            }
        }
        finally
        {
            if (passwordData != IntPtr.Zero) CFRelease(passwordData);
            if (addDict != IntPtr.Zero) CFRelease(addDict);
            if (updateDict != IntPtr.Zero) CFRelease(updateDict);
            if (query != IntPtr.Zero) CFRelease(query);
        }
    }

    public void DeleteSecret(string key)
    {
        var query = IntPtr.Zero;

        try
        {
            query = CreateQuery(key);
            var status = SecItemDelete(query);

            if (status == ErrSecItemNotFound)
            {
                return;
            }

            if (status != ErrSecSuccess)
            {
                Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"Keychain delete failed for '{key}' with status {status}"));
            }
        }
        finally
        {
            if (query != IntPtr.Zero) CFRelease(query);
        }
    }

    /// <summary>
    /// Builds a base query dictionary with kSecClass, kSecAttrService, kSecAttrAccount,
    /// and kSecUseDataProtectionKeychain set.
    /// </summary>
    private IntPtr CreateQuery(string account)
    {
        var dict = CFDictionaryCreateMutable(
            IntPtr.Zero, 4,
            ref kCFTypeDictionaryKeyCallBacks,
            ref kCFTypeDictionaryValueCallBacks);

        var cfService = CFStringCreateWithCString(IntPtr.Zero, _serviceName, kCFStringEncodingUTF8);
        var cfAccount = CFStringCreateWithCString(IntPtr.Zero, account, kCFStringEncodingUTF8);

        try
        {
            CFDictionarySetValue(dict, kSecClass, kSecClassGenericPassword);
            CFDictionarySetValue(dict, kSecAttrService, cfService);
            CFDictionarySetValue(dict, kSecAttrAccount, cfAccount);
            CFDictionarySetValue(dict, kSecUseDataProtectionKeychain, kCFBooleanTrue);
        }
        finally
        {
            CFRelease(cfService);
            CFRelease(cfAccount);
        }

        return dict;
    }

    #region Security framework P/Invoke

    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";

    [DllImport(SecurityLib)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SecurityLib)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityLib)]
    private static extern int SecItemDelete(IntPtr query);

    // Security constants (loaded as extern symbols)
    private static readonly IntPtr kSecClass = GetIndirectConstant(SecurityLib, "kSecClass");
    private static readonly IntPtr kSecClassGenericPassword = GetIndirectConstant(SecurityLib, "kSecClassGenericPassword");
    private static readonly IntPtr kSecAttrService = GetIndirectConstant(SecurityLib, "kSecAttrService");
    private static readonly IntPtr kSecAttrAccount = GetIndirectConstant(SecurityLib, "kSecAttrAccount");
    private static readonly IntPtr kSecValueData = GetIndirectConstant(SecurityLib, "kSecValueData");
    private static readonly IntPtr kSecReturnData = GetIndirectConstant(SecurityLib, "kSecReturnData");
    private static readonly IntPtr kSecMatchLimit = GetIndirectConstant(SecurityLib, "kSecMatchLimit");
    private static readonly IntPtr kSecMatchLimitOne = GetIndirectConstant(SecurityLib, "kSecMatchLimitOne");
    private static readonly IntPtr kSecUseDataProtectionKeychain = GetIndirectConstant(SecurityLib, "kSecUseDataProtectionKeychain");

    #endregion

    #region CoreFoundation P/Invoke

    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundationLib)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDictionaryCreateMutable(
        IntPtr allocator,
        nint capacity,
        ref CFDictionaryKeyCallBacks keyCallBacks,
        ref CFDictionaryValueCallBacks valueCallBacks);

    [DllImport(CoreFoundationLib)]
    private static extern void CFDictionarySetValue(IntPtr theDict, IntPtr key, IntPtr value);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFDataGetLength(IntPtr theData);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr theData);

    private const uint kCFStringEncodingUTF8 = 0x08000100;

    // kCFBooleanTrue
    private static readonly IntPtr kCFBooleanTrue = GetIndirectConstant(CoreFoundationLib, "kCFBooleanTrue");

    // Dictionary callback structs
    [DllImport(CoreFoundationLib)]
    private static extern ref CFDictionaryKeyCallBacks _kCFTypeDictionaryKeyCallBacks();

    [DllImport(CoreFoundationLib)]
    private static extern ref CFDictionaryValueCallBacks _kCFTypeDictionaryValueCallBacks();

    private static CFDictionaryKeyCallBacks kCFTypeDictionaryKeyCallBacks = GetKeyCallBacks();
    private static CFDictionaryValueCallBacks kCFTypeDictionaryValueCallBacks = GetValueCallBacks();

    private static CFDictionaryKeyCallBacks GetKeyCallBacks()
    {
        var ptr = GetSymbolAddress(CoreFoundationLib, "kCFTypeDictionaryKeyCallBacks");
        return Marshal.PtrToStructure<CFDictionaryKeyCallBacks>(ptr);
    }

    private static CFDictionaryValueCallBacks GetValueCallBacks()
    {
        var ptr = GetSymbolAddress(CoreFoundationLib, "kCFTypeDictionaryValueCallBacks");
        return Marshal.PtrToStructure<CFDictionaryValueCallBacks>(ptr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFDictionaryKeyCallBacks
    {
        public nint version;
        public IntPtr retain;
        public IntPtr release;
        public IntPtr copyDescription;
        public IntPtr equal;
        public IntPtr hash;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFDictionaryValueCallBacks
    {
        public nint version;
        public IntPtr retain;
        public IntPtr release;
        public IntPtr copyDescription;
        public IntPtr equal;
    }

    #endregion

    #region Symbol loader

    private static IntPtr GetSymbolAddress(string library, string name)
    {
        var lib = NativeLibrary.Load(library);
        if (!NativeLibrary.TryGetExport(lib, name, out var ptr))
        {
            throw new EntryPointNotFoundException($"Symbol '{name}' not found in {library}");
        }

        return ptr;
    }

    private static IntPtr GetIndirectConstant(string library, string name)
    {
        var ptr = GetSymbolAddress(library, name);

        // Security constants are pointers-to-CFStringRef; dereference to get the actual CFStringRef
        return Marshal.ReadIntPtr(ptr);
    }

    #endregion
}
