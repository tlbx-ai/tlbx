using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Services.Connect;

public sealed record ConnectUnlockRequest(string? Password);
public sealed record ConnectUnlockResponse(string UnlockToken, int ExpiresIn);
public sealed record ConnectGenerateRequest(string? UnlockToken, string? ServerUrl = null, string? Name = null);
public sealed record ConnectDisconnectRequest(string? UnlockToken);
public sealed record ConnectStatus(bool Configured, bool Online, string? ServerUrl, string? Name,
    string? DeviceId, string? LastError, int ActiveConnections);
public sealed record ConnectRegistration(int Version, string Payload, string Signature);
public sealed record ConnectAccessKey(int Version, string Kind, ConnectRegistration Registration, string PrivateKey);
public sealed record ConnectGenerateResponse(ConnectRegistration Registration, ConnectAccessKey AccessKey);
internal sealed record ConnectRegistrationPayload(string Kind, int Version, string ServerUrl, string DeviceId,
    string Name, string HostPublicKey, string AccessPublicKey);
internal sealed record ConnectDeviceConfiguration(string ServerUrl, string Name, string DeviceId,
    string HostPrivateKey, string HostPublicKey, string AccessPublicKey);
internal sealed record ConnectChallengeRequest(string DeviceId, string Role);
internal sealed record ConnectChallengeResponse(string ChallengeId, string Payload);
internal sealed record ConnectAuthenticationRequest(string ChallengeId, string Signature);
internal sealed record ConnectAuthenticationPayload(string Kind, int Version, string Role, string Origin,
    string DeviceId, string Nonce, long ExpiresAt);
internal sealed record ConnectPeerPayload(string Kind, int Version, string ConnectionId, string DeviceId,
    string Nonce, long ExpiresAt, string BrowserFingerprint, string HostFingerprint);
internal sealed record ConnectTokenResponse(string DeviceId, string DeviceToken);
internal sealed record ConnectIceServer(string[] Urls, string? Username = null, string? Credential = null);

internal sealed class ConnectSignal
{
    public string Type { get; set; } = "";
    public int? Version { get; set; }
    public string? ConnectionId { get; set; }
    public ConnectIceServer[]? IceServers { get; set; }
    public bool? RelayOnly { get; set; }
    public string? Sdp { get; set; }
    public string? Candidate { get; set; }
    public string? SdpMid { get; set; }
    public int? SdpMLineIndex { get; set; }
    public string? State { get; set; }
    public string? Data { get; set; }
    public string? Message { get; set; }
    public string? Code { get; set; }
    public bool? Fatal { get; set; }
    public string? LocalType { get; set; }
    public string? RemoteType { get; set; }
    public string? Protocol { get; set; }
}

internal sealed class ConnectEnvelope
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Method { get; set; }
    public string? Surface { get; set; }
    public string? PreviewRouteKey { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    public int? Status { get; set; }
    public string[]? Protocols { get; set; }
    public string? Protocol { get; set; }
    public bool? Binary { get; set; }
    public string? Data { get; set; }
    public string? Signature { get; set; }
    public int? Code { get; set; }
    public string? Reason { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectUnlockRequest))]
[JsonSerializable(typeof(ConnectUnlockResponse))]
[JsonSerializable(typeof(ConnectGenerateRequest))]
[JsonSerializable(typeof(ConnectDisconnectRequest))]
[JsonSerializable(typeof(ConnectRegistration))]
[JsonSerializable(typeof(ConnectAccessKey))]
[JsonSerializable(typeof(ConnectGenerateResponse))]
[JsonSerializable(typeof(ConnectRegistrationPayload))]
[JsonSerializable(typeof(ConnectChallengeRequest))]
[JsonSerializable(typeof(ConnectChallengeResponse))]
[JsonSerializable(typeof(ConnectAuthenticationRequest))]
[JsonSerializable(typeof(ConnectAuthenticationPayload))]
[JsonSerializable(typeof(ConnectPeerPayload))]
[JsonSerializable(typeof(ConnectStatus))]
[JsonSerializable(typeof(ConnectDeviceConfiguration))]
[JsonSerializable(typeof(ConnectTokenResponse))]
[JsonSerializable(typeof(ConnectSignal))]
[JsonSerializable(typeof(ConnectEnvelope))]
internal sealed partial class ConnectJsonContext : JsonSerializerContext { }
