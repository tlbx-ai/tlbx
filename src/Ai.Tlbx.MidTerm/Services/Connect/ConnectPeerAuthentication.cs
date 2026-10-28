using System.Security.Cryptography;
using System.Text.Json;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Proof of the pinned access key, bound to both negotiated DTLS identities.</summary>
internal sealed class ConnectPeerAuthentication(ConnectDeviceConfiguration configuration, string connectionId, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string? _browserFingerprint;
    private string? _hostFingerprint;
    private byte[]? _challenge;
    private long _expiresAt;
    private bool _consumed;
    public bool Authenticated { get; private set; }

    public void SetOffer(string sdp)
    {
        if (_browserFingerprint is not null) throw new InvalidDataException("Connect offer cannot be replaced.");
        _browserFingerprint = ConnectKeyProtocol.Fingerprint(sdp);
    }

    public void SetAnswer(string sdp)
    {
        if (_hostFingerprint is not null) throw new InvalidDataException("Connect answer cannot be replaced.");
        _hostFingerprint = ConnectKeyProtocol.Fingerprint(sdp);
    }

    public ConnectEnvelope CreateChallenge()
    {
        if (_challenge is not null || _browserFingerprint is null || _hostFingerprint is null)
            throw new InvalidDataException("Connect peer authentication is not ready.");
        _expiresAt = _time.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds();
        _challenge = JsonSerializer.SerializeToUtf8Bytes(new ConnectPeerPayload("tlbx-connect-peer", 1, connectionId,
            configuration.DeviceId, ConnectKeyProtocol.Encode(RandomNumberGenerator.GetBytes(32)), _expiresAt,
            _browserFingerprint, _hostFingerprint), ConnectJsonContext.Default.ConnectPeerPayload);
        using var host = ConnectKeyProtocol.PrivateKey(configuration.HostPrivateKey);
        return new ConnectEnvelope { Type = "authChallenge", Data = ConnectKeyProtocol.Encode(_challenge), Signature = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(host, _challenge)) };
    }

    public void AcceptProof(ConnectEnvelope envelope)
    {
        if (_consumed || _challenge is null || envelope.Type != "authProof" || envelope.Data is null
            || _time.GetUtcNow().ToUnixTimeSeconds() >= _expiresAt)
            throw new InvalidDataException("Connect access proof is missing, expired or already used.");
        _consumed = true;
        using var access = ConnectKeyProtocol.PublicKey(configuration.AccessPublicKey);
        if (!ConnectKeyProtocol.Verify(access, _challenge, envelope.Data)) throw new InvalidDataException("Connect access proof was rejected.");
        Authenticated = true;
    }
}
