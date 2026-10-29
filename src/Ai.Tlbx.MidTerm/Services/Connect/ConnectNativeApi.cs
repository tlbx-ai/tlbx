using System.Runtime.InteropServices;
using System.Text;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>The pinned libdatachannel 0.24 C ABI. No assembly-wide DllImport resolver is installed.</summary>
internal sealed unsafe class ConnectNativeApi
{
    private readonly delegate* unmanaged[Cdecl]<Configuration*, int> _create;
    private readonly delegate* unmanaged[Cdecl]<int, int> _closePeer, _deletePeer, _deleteChannel, _buffered;
    private readonly delegate* unmanaged[Cdecl]<int, nint, void> _user;
    private readonly delegate* unmanaged[Cdecl]<int, nint, int> _description, _candidate, _state, _gathering, _channel;
    private readonly delegate* unmanaged[Cdecl]<int, nint, int> _opened, _closed, _error, _message, _low;
    private readonly delegate* unmanaged[Cdecl]<int, int, int> _threshold;
    private readonly delegate* unmanaged[Cdecl]<int, byte*, byte*, int> _remote, _addCandidate;
    private readonly delegate* unmanaged[Cdecl]<int, byte*, int> _local;
    private readonly delegate* unmanaged[Cdecl]<int, byte*, int, int> _label, _send;
    private readonly delegate* unmanaged[Cdecl]<int, Reliability*, int> _reliability;
    private readonly delegate* unmanaged[Cdecl]<int, byte> _isOpen;

    public ConnectNativeApi(nint library)
    {
        _create = (delegate* unmanaged[Cdecl]<Configuration*, int>)NativeLibrary.GetExport(library, "rtcCreatePeerConnection");
        _closePeer = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "rtcClosePeerConnection");
        _deletePeer = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "rtcDeletePeerConnection");
        _deleteChannel = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "rtcDeleteDataChannel");
        _buffered = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "rtcGetBufferedAmount");
        _user = (delegate* unmanaged[Cdecl]<int, nint, void>)NativeLibrary.GetExport(library, "rtcSetUserPointer");
        _description = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetLocalDescriptionCallback");
        _candidate = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetLocalCandidateCallback");
        _state = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetStateChangeCallback");
        _gathering = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetGatheringStateChangeCallback");
        _channel = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetDataChannelCallback");
        _opened = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetOpenCallback");
        _closed = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetClosedCallback");
        _error = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetErrorCallback");
        _message = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetMessageCallback");
        _low = (delegate* unmanaged[Cdecl]<int, nint, int>)NativeLibrary.GetExport(library, "rtcSetBufferedAmountLowCallback");
        _threshold = (delegate* unmanaged[Cdecl]<int, int, int>)NativeLibrary.GetExport(library, "rtcSetBufferedAmountLowThreshold");
        _remote = (delegate* unmanaged[Cdecl]<int, byte*, byte*, int>)NativeLibrary.GetExport(library, "rtcSetRemoteDescription");
        _addCandidate = (delegate* unmanaged[Cdecl]<int, byte*, byte*, int>)NativeLibrary.GetExport(library, "rtcAddRemoteCandidate");
        _local = (delegate* unmanaged[Cdecl]<int, byte*, int>)NativeLibrary.GetExport(library, "rtcSetLocalDescription");
        _label = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)NativeLibrary.GetExport(library, "rtcGetDataChannelLabel");
        _send = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)NativeLibrary.GetExport(library, "rtcSendMessage");
        _reliability = (delegate* unmanaged[Cdecl]<int, Reliability*, int>)NativeLibrary.GetExport(library, "rtcGetDataChannelReliability");
        _isOpen = (delegate* unmanaged[Cdecl]<int, byte>)NativeLibrary.GetExport(library, "rtcIsOpen");
        var logger = (delegate* unmanaged[Cdecl]<int, nint, void>)NativeLibrary.GetExport(library, "rtcInitLogger");
        logger(0, 0); // Never print native SDP, TURN URLs or credentials.
        var settings = new SctpSettings { ReceiveBuffer = 2 * 1024 * 1024, SendBuffer = 1024 * 1024, MaximumChunks = 1024 };
        var configure = (delegate* unmanaged[Cdecl]<SctpSettings*, int>)NativeLibrary.GetExport(library, "rtcSetSctpSettings");
        Check(configure(&settings));
    }

    public int Create(string[] servers, string? proxy, bool relayOnly, nint token)
    {
        var strings = new nint[servers.Length]; nint proxyString = 0;
        try
        {
            for (var index = 0; index < servers.Length; index++) strings[index] = Marshal.StringToCoTaskMemUTF8(servers[index]);
            if (proxy is not null) proxyString = Marshal.StringToCoTaskMemUTF8(proxy);
            fixed (nint* values = strings)
            {
                var configuration = new Configuration
                {
                    IceServers = (nint)values, IceServersCount = strings.Length, ProxyServer = proxyString,
                    CertificateType = 1, TransportPolicy = relayOnly ? 1 : 0, DisableAutoNegotiation = 1, MaximumMessageSize = 65536
                };
                var peer = _create(&configuration); Check(peer);
                _user(peer, token);
                try
                {
                    Check(_description(peer, (nint)(delegate* unmanaged[Cdecl]<int, byte*, byte*, nint, void>)&ConnectWebRtcTransport.NativeDescription));
                    Check(_candidate(peer, (nint)(delegate* unmanaged[Cdecl]<int, byte*, byte*, nint, void>)&ConnectWebRtcTransport.NativeCandidate));
                    Check(_state(peer, (nint)(delegate* unmanaged[Cdecl]<int, int, nint, void>)&ConnectWebRtcTransport.NativeState));
                    Check(_gathering(peer, (nint)(delegate* unmanaged[Cdecl]<int, int, nint, void>)&ConnectWebRtcTransport.NativeGathering));
                    Check(_channel(peer, (nint)(delegate* unmanaged[Cdecl]<int, int, nint, void>)&ConnectWebRtcTransport.NativeChannel));
                    return peer;
                }
                catch { DeletePeer(peer); throw; }
            }
        }
        finally
        {
            foreach (var value in strings) if (value != 0) Marshal.FreeCoTaskMem(value);
            if (proxyString != 0) Marshal.FreeCoTaskMem(proxyString);
        }
    }

    public void ApplyOffer(int peer, string sdp)
    {
        var bytes = Encoding.UTF8.GetBytes(sdp + '\0');
        fixed (byte* offer = bytes)
        fixed (byte* type = "offer\0"u8) Check(_remote(peer, offer, type));
        fixed (byte* type = "answer\0"u8) Check(_local(peer, type));
    }

    public void AddCandidate(int peer, string candidate, string? mid)
    {
        var bytes = Encoding.UTF8.GetBytes(candidate + '\0'); var media = Encoding.UTF8.GetBytes((mid ?? "0") + '\0');
        fixed (byte* value = bytes)
        fixed (byte* mediaId = media) Check(_addCandidate(peer, value, mediaId));
    }

    public bool ReliableTlbxChannel(int id)
    {
        var bytes = new byte[32]; int size;
        fixed (byte* buffer = bytes) size = _label(id, buffer, bytes.Length);
        if (size < 0 || !bytes.AsSpan().StartsWith("tlbx\0"u8)) return false;
        var reliability = new Reliability();
        return _reliability(id, &reliability) >= 0 && reliability.Unordered == 0 && reliability.Unreliable == 0;
    }

    public void AttachChannel(int id, nint token)
    {
        _user(id, token);
        Check(_threshold(id, 512 * 1024));
        Check(_message(id, (nint)(delegate* unmanaged[Cdecl]<int, byte*, int, nint, void>)&ConnectWebRtcTransport.NativeMessage));
        Check(_closed(id, (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&ConnectWebRtcTransport.NativeClosed));
        Check(_error(id, (nint)(delegate* unmanaged[Cdecl]<int, byte*, nint, void>)&ConnectWebRtcTransport.NativeError));
        Check(_low(id, (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&ConnectWebRtcTransport.NativeBufferedLow));
        Check(_opened(id, (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&ConnectWebRtcTransport.NativeOpen));
    }

    public bool IsOpen(int id) => _isOpen(id) != 0;
    public int BufferedAmount(int id) { var amount = _buffered(id); Check(amount); return amount; }
    public void Send(int id, ReadOnlySpan<byte> bytes) { fixed (byte* data = bytes) Check(_send(id, data, bytes.Length)); }
    public void DeleteChannel(int id)
    {
        _user(id, 0); _opened(id, 0); _closed(id, 0); _error(id, 0); _message(id, 0); _low(id, 0); _deleteChannel(id);
    }
    public void DeletePeer(int id)
    {
        _description(id, 0); _candidate(id, 0); _state(id, 0); _gathering(id, 0); _channel(id, 0); _user(id, 0);
        _closePeer(id); _deletePeer(id);
    }
    private static void Check(int result)
    {
        if (result < 0) throw new IOException("The native WebRTC operation failed (code " + result.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Configuration
    {
        public nint IceServers;
        public int IceServersCount;
        public nint ProxyServer, BindAddress;
        public int CertificateType, TransportPolicy;
        public byte EnableIceTcp, EnableIceUdpMux, DisableAutoNegotiation, ForceMediaTransport;
        public ushort PortRangeBegin, PortRangeEnd;
        public int Mtu, MaximumMessageSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Reliability { public byte Unordered, Unreliable; public uint MaximumPacketLifeTime, MaximumRetransmits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SctpSettings
    {
        public int ReceiveBuffer, SendBuffer, MaximumChunks, InitialCongestionWindow, MaximumBurst, CongestionControl,
            DelayedSack, MinimumRetransmitTimeout, MaximumRetransmitTimeout, InitialRetransmitTimeout, MaximumRetransmits, Heartbeat;
    }
}
