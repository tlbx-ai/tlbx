using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Update;

namespace Ai.Tlbx.MidTerm.Services.Updates;

/// <summary>
/// Verifies update integrity using SHA256 checksums and ECDSA P-384 signatures.
/// </summary>
public static class UpdateVerification
{
    // ECDSA P-384 public key for verifying release signatures (base64 encoded SPKI format)
    private const string PublicKeyBase64 = "MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE9txOtWhrtgO7q8Hlpe7tzv8ARMHaLYpO1JFm9psIc6LyBMLgwgz0GXfL+kU7iDVK0GyE6q2nsz7AEhKfwfbQY7d+k/WKPDEvV6OzYIYStxW4v2mAKNY1XHyuOntapcb/";

    /// <summary>Authenticates release metadata before using its runtime compatibility claims.</summary>
    public static bool VerifyManifest(
        VersionManifest manifest,
        string? expectedPlatform = null,
        string? expectedVersion = null,
        string? expectedChannel = null) =>
        VerifyMetadataSignature(manifest, PublicKeyBase64, expectedPlatform, expectedVersion, expectedChannel);

    /// <summary>
    /// Verifies the integrity of extracted update files.
    /// </summary>
    /// <param name="extractDir">Directory containing extracted update files</param>
    /// <param name="manifest">Version manifest with checksums and signature</param>
    /// <returns>True if verification passes, false otherwise</returns>
    public static bool VerifyUpdate(
        string extractDir,
        VersionManifest manifest,
        string? expectedPlatform = null,
        string? expectedVersion = null,
        string? expectedChannel = null)
    {
        return VerifyUpdateCore(
            extractDir,
            manifest,
            PublicKeyBase64,
            expectedPlatform,
            expectedVersion,
            expectedChannel);
    }

    internal static bool VerifyUpdateForTesting(
        string extractDir,
        VersionManifest manifest,
        string publicKeyBase64,
        string? expectedPlatform = null,
        string? expectedVersion = null,
        string? expectedChannel = null)
    {
        return VerifyUpdateCore(
            extractDir,
            manifest,
            publicKeyBase64,
            expectedPlatform,
            expectedVersion,
            expectedChannel);
    }

    private static bool VerifyUpdateCore(
        string extractDir,
        VersionManifest manifest,
        string publicKeyBase64,
        string? expectedPlatform,
        string? expectedVersion,
        string? expectedChannel)
    {
        var checksums = manifest.Checksums;
        if (checksums is null || checksums.Count == 0)
        {
            Log.Warn(() => "UpdateVerification: Signed checksums are required - update rejected");
            return false;
        }

        if (!VerifyMetadataSignature(
                manifest,
                publicKeyBase64,
                expectedPlatform,
                expectedVersion,
                expectedChannel))
        {
            Log.Warn(() => "UpdateVerification: Metadata signature verification failed - update rejected");
            return false;
        }

        Log.Info(() => "UpdateVerification: Metadata signature verified successfully");

        // Verify each file's checksum
        foreach (var (filename, expectedHash) in checksums)
        {
            if (!TryResolveChecksumPath(extractDir, filename, out var filePath))
            {
                Log.Warn(() => $"UpdateVerification: Unsafe checksum entry rejected: {filename}");
                return false;
            }

            if (!File.Exists(filePath))
            {
                Log.Warn(() => $"UpdateVerification: Expected file not found: {filename}");
                return false;
            }

            var actualHash = ComputeFileHash(filePath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn(() => $"UpdateVerification: Checksum mismatch for {filename}: expected {expectedHash}, got {actualHash}");
                return false;
            }

            Log.Info(() => $"UpdateVerification: Checksum verified: {filename}");
        }

        return true;
    }

    private static bool TryResolveChecksumPath(string extractDir, string filename, out string filePath)
    {
        filePath = "";
        if (string.IsNullOrWhiteSpace(filename) || Path.IsPathRooted(filename))
        {
            return false;
        }

        var segments = filename.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return false;
        }

        var extractRoot = Path.GetFullPath(extractDir);
        var candidate = Path.GetFullPath(Path.Combine([extractRoot, .. segments]));
        if (!candidate.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        filePath = candidate;
        return true;
    }

    /// <summary>
    /// Verifies the ECDSA signature and the exact metadata/checksum binding.
    /// </summary>
    private static bool VerifyMetadataSignature(
        VersionManifest manifest,
        string publicKeyBase64,
        string? expectedPlatform,
        string? expectedVersion,
        string? expectedChannel)
    {
        if (manifest.SignatureVersion != 2 ||
            string.IsNullOrWhiteSpace(manifest.Platform) ||
            string.IsNullOrWhiteSpace(manifest.Channel) ||
            string.IsNullOrWhiteSpace(manifest.SignedPayload) ||
            string.IsNullOrWhiteSpace(manifest.MetadataSignature))
        {
            return false;
        }

        try
        {
            var publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            var signatureBytes = Convert.FromBase64String(manifest.MetadataSignature);
            var payloadBytes = Convert.FromBase64String(manifest.SignedPayload);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
            if (!ecdsa.VerifyData(
                    payloadBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                return false;
            }

            using var payloadDocument = JsonDocument.Parse(payloadBytes);
            var root = payloadDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("signatureVersion").GetInt32() != 2 ||
                !Matches(root, "web", manifest.Web) ||
                !Matches(root, "pty", manifest.Pty) ||
                root.GetProperty("protocol").GetInt32() != manifest.Protocol ||
                !Matches(root, "minCompatiblePty", manifest.MinCompatiblePty) ||
                root.GetProperty("webOnly").GetBoolean() != manifest.WebOnly ||
                !Matches(root, "platform", manifest.Platform) ||
                !Matches(root, "channel", manifest.Channel))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedPlatform) &&
                !string.Equals(manifest.Platform, expectedPlatform, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedVersion) &&
                !string.Equals(
                    NormalizeVersion(manifest.Web),
                    NormalizeVersion(expectedVersion),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedChannel) &&
                !string.Equals(manifest.Channel, expectedChannel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var checksums = manifest.Checksums;
            var signedChecksums = root.GetProperty("checksums");
            if (checksums is null || signedChecksums.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var signedChecksumCount = 0;
            using var checksumEnumerator = signedChecksums.EnumerateObject();
            while (checksumEnumerator.MoveNext())
            {
                signedChecksumCount++;
                var checksum = checksumEnumerator.Current;
                if (!checksums.TryGetValue(checksum.Name, out var expectedHash) ||
                    !string.Equals(checksum.Value.GetString(), expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (signedChecksumCount != checksums.Count)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "UpdateVerification.VerifyMetadataSignature");
            return false;
        }
    }

    private static bool Matches(JsonElement root, string propertyName, string expected)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string NormalizeVersion(string version)
    {
        return version.Trim().TrimStart('v', 'V');
    }

    /// <summary>
    /// Computes SHA256 hash of a file.
    /// </summary>
    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Serializes checksums to a deterministic JSON string for signature verification.
    /// Uses manual JSON building to avoid AOT serialization issues.
    /// </summary>
    internal static string SerializeChecksumsForSigning(Dictionary<string, string> checksums)
    {
        // Sort by key for deterministic output
        var sorted = checksums.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        // Build JSON manually to avoid AOT issues
        var sb = new StringBuilder("{");
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append('"');
            sb.Append(JsonEncodedText.Encode(sorted[i].Key));
            sb.Append("\":\"");
            sb.Append(JsonEncodedText.Encode(sorted[i].Value));
            sb.Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }
}
