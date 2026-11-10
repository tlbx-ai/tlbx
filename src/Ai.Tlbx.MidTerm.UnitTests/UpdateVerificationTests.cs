using System.Security.Cryptography;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class UpdateVerificationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ECDsa _signingKey;
    private readonly string _publicKeyBase64;

    public UpdateVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_update_verify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        _publicKeyBase64 = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());
    }

    public void Dispose()
    {
        _signingKey.Dispose();
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

    [Fact]
    public void VerifyUpdate_NoChecksums_ReturnsFalse()
    {
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal));
        manifest.Checksums = null;

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_EmptyChecksums_ReturnsFalse()
    {
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_ChecksumsWithoutMetadataSignature_ReturnsFalse()
    {
        var filePath = WriteFile("mt.exe", "unsigned payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });
        manifest.MetadataSignature = null;

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_ValidMetadataSignatureAndChecksums_ReturnsTrue()
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var agentHostPath = WriteFile("mtagenthost.exe", "signed agent host");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath),
            ["mtagenthost.exe"] = ComputeHash(agentHostPath)
        });

        Assert.True(Verify(manifest, "win-x64", "1.2.3-dev", "dev"));
    }

    [Fact]
    public void VerifyUpdate_SignedNestedRuntimePath_ReturnsTrue()
    {
        var filePath = WriteFile("x64/OpenConsole.exe", "signed ConPTY host");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x64/OpenConsole.exe"] = ComputeHash(filePath)
        });

        Assert.True(Verify(manifest));
    }

    [Theory]
    [InlineData("linux-x64", "1.2.3-dev", "dev")]
    [InlineData("win-x64", "1.2.4-dev", "dev")]
    [InlineData("win-x64", "1.2.3-dev", "stable")]
    public void VerifyUpdate_ExpectedReleaseIdentityMismatch_ReturnsFalse(
        string expectedPlatform,
        string expectedVersion,
        string expectedChannel)
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });

        Assert.False(Verify(manifest, expectedPlatform, expectedVersion, expectedChannel));
    }

    [Fact]
    public void VerifyUpdate_TamperedTopLevelMetadata_ReturnsFalse()
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });
        manifest.Web = "9.9.9-dev";

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_TamperedSignedPayload_ReturnsFalse()
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });
        var payload = Convert.FromBase64String(manifest.SignedPayload!);
        payload[^1] ^= 1;
        manifest.SignedPayload = Convert.ToBase64String(payload);

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_ChecksumMismatch_ReturnsFalse()
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });
        File.WriteAllText(filePath, "tampered after signing");

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_UnsafeChecksumPath_ReturnsFalse()
    {
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["../mt.exe"] = new string('a', 64)
        });

        Assert.False(Verify(manifest));
    }

    [Fact]
    public void VerifyUpdate_InvalidMetadataSignature_ReturnsFalse()
    {
        var filePath = WriteFile("mt.exe", "signed payload");
        var manifest = CreateManifest(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mt.exe"] = ComputeHash(filePath)
        });
        manifest.MetadataSignature = "definitely-not-base64";

        Assert.False(Verify(manifest));
    }

    private VersionManifest CreateManifest(Dictionary<string, string> checksums)
    {
        var orderedChecksums = new SortedDictionary<string, string>(checksums, StringComparer.Ordinal);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            signatureVersion = 2,
            web = "1.2.3-dev",
            pty = "1.2.3-dev",
            protocol = 1,
            minCompatiblePty = "2.0.0",
            webOnly = false,
            platform = "win-x64",
            channel = "dev",
            checksums = orderedChecksums
        });
        var signature = _signingKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return new VersionManifest
        {
            Web = "1.2.3-dev",
            Pty = "1.2.3-dev",
            Protocol = 1,
            MinCompatiblePty = "2.0.0",
            WebOnly = false,
            SignatureVersion = 2,
            Platform = "win-x64",
            Channel = "dev",
            Checksums = new Dictionary<string, string>(checksums, StringComparer.OrdinalIgnoreCase),
            SignedPayload = Convert.ToBase64String(payload),
            MetadataSignature = Convert.ToBase64String(signature)
        };
    }

    private bool Verify(
        VersionManifest manifest,
        string? expectedPlatform = null,
        string? expectedVersion = null,
        string? expectedChannel = null)
    {
        return UpdateVerification.VerifyUpdateForTesting(
            _tempDir,
            manifest,
            _publicKeyBase64,
            expectedPlatform,
            expectedVersion,
            expectedChannel);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
