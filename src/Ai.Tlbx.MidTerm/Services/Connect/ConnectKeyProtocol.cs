using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectKeyProtocol
{
    internal static (ConnectDeviceConfiguration Configuration, ConnectGenerateResponse Downloads) Generate(string serverUrl, string name)
    {
        using var host = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var access = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicBytes = host.ExportSubjectPublicKeyInfo();
        var hostPublic = Encode(publicBytes);
        var accessPublic = Encode(access.ExportSubjectPublicKeyInfo());
        var deviceId = Convert.ToHexStringLower(SHA256.HashData(publicBytes))[..32];
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ConnectRegistrationPayload("tlbx-connect-registration", 1,
            serverUrl, deviceId, name, hostPublic, accessPublic), ConnectJsonContext.Default.ConnectRegistrationPayload);
        var registration = new ConnectRegistration(1, Encode(payload), Encode(Sign(host, payload)));
        return (new ConnectDeviceConfiguration(serverUrl, name, deviceId, Encode(host.ExportPkcs8PrivateKey()), hostPublic, accessPublic),
            new ConnectGenerateResponse(registration, new ConnectAccessKey(1, "tlbx-connect-access", registration, Encode(access.ExportPkcs8PrivateKey()))));
    }

    internal static string Encode(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);
    internal static byte[] Decode(string value, int maximum = 16384)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException("Invalid Connect key encoding.");
        return WebEncoders.Base64UrlDecode(value);
    }

    internal static ECDsa PublicKey(string value)
    {
        var key = ECDsa.Create();
        try
        {
            var bytes = Decode(value, 512);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Connect requires a P-256 key.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    internal static ECDsa PrivateKey(string value)
    {
        var key = ECDsa.Create();
        try
        {
            var bytes = Decode(value, 1024);
            try
            {
                key.ImportPkcs8PrivateKey(bytes, out var read);
                if (read != bytes.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                    throw new InvalidDataException("Connect requires a P-256 key.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    internal static byte[] Sign(ECDsa key, byte[] payload) => key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    internal static bool Verify(ECDsa key, byte[] payload, string signature)
    {
        var bytes = Decode(signature, 128);
        return bytes.Length == 64 && key.VerifyData(payload, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal static byte[] ValidateHostChallenge(ConnectDeviceConfiguration configuration, string encoded)
    {
        var bytes = Decode(encoded);
        var challenge = JsonSerializer.Deserialize(bytes, ConnectJsonContext.Default.ConnectAuthenticationPayload)
            ?? throw new InvalidDataException("Missing Connect challenge.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (challenge.Kind != "tlbx-connect-auth" || challenge.Version != 1 || challenge.Role != "host"
            || challenge.DeviceId != configuration.DeviceId || challenge.Origin != new Uri(configuration.ServerUrl).GetLeftPart(UriPartial.Authority)
            || challenge.ExpiresAt <= now || challenge.ExpiresAt > now + 300 || Decode(challenge.Nonce, 128).Length < 16)
            throw new InvalidDataException("Connect challenge does not match this host.");
        return bytes;
    }

    internal static string Fingerprint(string sdp)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in sdp.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase)) continue;
            const string prefix = "a=fingerprint:sha-256 ";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Connect requires DTLS SHA-256 fingerprints.");
            var fingerprint = line[prefix.Length..].Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Invalid DTLS fingerprint.");
            fingerprints.Add(fingerprint);
        }
        return fingerprints.Count == 1 ? fingerprints.Single() : throw new InvalidDataException("Exactly one DTLS fingerprint is required.");
    }
}
