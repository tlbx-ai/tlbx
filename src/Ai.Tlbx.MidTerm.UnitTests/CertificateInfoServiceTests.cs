using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ai.Tlbx.MidTerm.Services.Certificates;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CertificateInfoServiceTests
{
    [Fact]
    public void GeneratedCertificate_IsAdvertisedAsAndroidCa()
    {
        using var certificate = CertificateGenerator.GenerateSelfSigned(["localhost"], ["127.0.0.1"]);
        var service = CreateService();

        service.SetCertificate(certificate, isFallback: false);

        Assert.True(service.GetInfo().IsCertificateAuthority);
    }

    [Fact]
    public void LegacyServerCertificate_WithoutBasicConstraints_IsNotAdvertisedAsAndroidCa()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Legacy", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var service = CreateService();

        service.SetCertificate(certificate, isFallback: false);

        Assert.False(service.GetInfo().IsCertificateAuthority);
    }

    private static CertificateInfoService CreateService() =>
        new(new SettingsService(Path.Combine(Path.GetTempPath(), $"tlbx-ca-test-{Guid.NewGuid():N}")));
}
