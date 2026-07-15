using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Services.Certificates;

public sealed class CertificateInfoService
{
    private readonly SettingsService _settingsService;
    private X509Certificate2? _certificate;

    public string? Fingerprint { get; private set; }
    public string? FingerprintFormatted { get; private set; }
    public DateTime? NotBefore { get; private set; }
    public DateTime? NotAfter { get; private set; }
    public bool IsFallbackCertificate { get; private set; }
    public string[] DnsNames { get; private set; } = [];
    public string[] IpAddresses { get; private set; } = [];

    public CertificateInfoService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void SetCertificate(X509Certificate2 cert, bool isFallback)
    {
        _certificate = cert;
        var hashBytes = cert.GetCertHash(HashAlgorithmName.SHA256);
        Fingerprint = Convert.ToHexString(hashBytes);
        FingerprintFormatted = FormatFingerprint(hashBytes);
        NotBefore = cert.NotBefore.ToUniversalTime();
        NotAfter = cert.NotAfter.ToUniversalTime();
        IsFallbackCertificate = isFallback;

        // Extract SANs
        (DnsNames, IpAddresses) = ExtractSubjectAlternativeNames(cert);
    }

    public CertificateInfoResponse GetInfo()
    {
        return new CertificateInfoResponse
        {
            Fingerprint = Fingerprint,
            NotBefore = NotBefore,
            NotAfter = NotAfter,
            IsFallbackCertificate = IsFallbackCertificate
        };
    }

    public CertificateDownloadInfo GetDownloadInfo()
    {
        var settings = _settingsService.Load();
        return new CertificateDownloadInfo
        {
            Fingerprint = Fingerprint ?? "",
            FingerprintFormatted = FingerprintFormatted ?? "",
            NotBefore = NotBefore ?? DateTime.MinValue,
            NotAfter = NotAfter ?? DateTime.MaxValue,
            KeyProtection = settings.KeyProtection.ToString(),
            DnsNames = DnsNames,
            IpAddresses = IpAddresses,
            IsFallbackCertificate = IsFallbackCertificate
        };
    }

    public byte[]? ExportPemBytes()
    {
        if (_certificate is null)
        {
            return null;
        }

        var pem = _certificate.ExportCertificatePem();
        return Encoding.UTF8.GetBytes(pem);
    }

    public byte[]? ExportDerBytes()
    {
        if (_certificate is null)
        {
            return null;
        }

        return _certificate.Export(X509ContentType.Cert);
    }

    public byte[]? GenerateMobileConfig(string hostname)
    {
        if (_certificate is null)
        {
            return null;
        }

        var certDer = _certificate.Export(X509ContentType.Cert);
        var certBase64 = Convert.ToBase64String(certDer);
        var payloadUuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var profileUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        var mobileConfig = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>PayloadContent</key>
    <array>
        <dict>
            <key>PayloadCertificateFileName</key>
            <string>midterm.cer</string>
            <key>PayloadContent</key>
            <data>{certBase64}</data>
            <key>PayloadDescription</key>
            <string>Adds a root certificate for tlbx access</string>
            <key>PayloadDisplayName</key>
            <string>tlbx Certificate</string>
            <key>PayloadIdentifier</key>
            <string>ai.tlbx.midterm.cert.{payloadUuid}</string>
            <key>PayloadType</key>
            <string>com.apple.security.root</string>
            <key>PayloadUUID</key>
            <string>{payloadUuid}</string>
            <key>PayloadVersion</key>
            <integer>1</integer>
        </dict>
    </array>
    <key>PayloadDescription</key>
    <string>Trust certificate for tlbx at {hostname}</string>
    <key>PayloadDisplayName</key>
    <string>tlbx Certificate</string>
    <key>PayloadIdentifier</key>
    <string>ai.tlbx.midterm.profile.{profileUuid}</string>
    <key>PayloadOrganization</key>
    <string>tlbx.ai</string>
    <key>PayloadRemovalDisallowed</key>
    <false/>
    <key>PayloadType</key>
    <string>Configuration</string>
    <key>PayloadUUID</key>
    <string>{profileUuid}</string>
    <key>PayloadVersion</key>
    <integer>1</integer>
</dict>
</plist>";

        return Encoding.UTF8.GetBytes(mobileConfig);
    }

    private static string FormatFingerprint(byte[] hash)
    {
        return BitConverter.ToString(hash).Replace("-", ":", StringComparison.Ordinal);
    }

    private static (string[] dnsNames, string[] ipAddresses) ExtractSubjectAlternativeNames(X509Certificate2 cert)
    {
        var dnsNames = new List<string>();
        var ipAddresses = new List<string>();

        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value == "2.5.29.17") // Subject Alternative Name OID
            {
                var sanExtension = (X509SubjectAlternativeNameExtension)ext;
                foreach (var name in sanExtension.EnumerateDnsNames())
                {
                    dnsNames.Add(name);
                }
                foreach (var ip in sanExtension.EnumerateIPAddresses())
                {
                    ipAddresses.Add(ip.ToString());
                }
            }
        }

        return (dnsNames.ToArray(), ipAddresses.ToArray());
    }
}
