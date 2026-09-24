namespace Ai.Tlbx.MidTerm.Models.Certificates;

public sealed class CertificateInfoResponse
{
    public string? Fingerprint { get; init; }
    public DateTime? NotBefore { get; init; }
    public DateTime? NotAfter { get; init; }
    public bool IsFallbackCertificate { get; init; }
    public bool IsCertificateAuthority { get; init; }
}
