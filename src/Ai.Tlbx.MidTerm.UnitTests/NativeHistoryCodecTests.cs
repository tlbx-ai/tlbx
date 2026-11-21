using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class NativeHistoryCodecTests
{
    [Fact]
    public void WindowRoundtripPreservesOpaqueConfigUnicodeDatesAndOptionalFields()
    {
        using var config = JsonDocument.Parse("""{"name":"Grüße 🦊","enabled":true,"zero":0,"fraction":1.25,"tiny":1e-40,"large":123456789012345678901234567890123456789,"negativeZero":-0,"list":[null,false,"東京"]}""");
        var date = new DateTimeOffset(2026, 11, 15, 7, 4, 3, TimeSpan.FromHours(2)).AddTicks(1234567);
        var window = new AppServerControlHistoryWindowResponse
        {
            SessionId = "native", GeneratedAt = date, LatestSequence = long.MaxValue, HistoryCount = 2,
            HistoryWindowEnd = 2,
            History = [
                new() { EntryId = "one", Order = 1, Body = "Äöü 🦊 東京", CreatedAt = date, UpdatedAt = date.ToOffset(TimeSpan.Zero) },
                new() { EntryId = "two", Order = 2, Body = "Äöü 🦊 東京", CreatedAt = DateTimeOffset.MinValue, UpdatedAt = DateTimeOffset.MaxValue }
            ],
            Requests =
            [
                new()
                {
                    RequestId = "acp-approval",
                    PermissionOptions =
                    [
                        new() { OptionId = "session", Name = "Allow for this session", Kind = "allow_always" },
                        new() { OptionId = "once", Name = "Allow once", Kind = "allow_once" }
                    ]
                }
            ],
            QuickSettings = new() { ConfigOptions = [config.RootElement.Clone()] }
        };
        var info = AppServerControlHostJsonContext.Default.AppServerControlHistoryWindowResponse;
        var decoded = NativeHistoryCodec.Decode(NativeHistoryCodec.Encode(window, info), info);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(window, info), JsonSerializer.SerializeToNode(decoded, info)));
        Assert.Equal(date.Offset, decoded.History[0].CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, decoded.History[0].UpdatedAt.Offset);
        Assert.Same(decoded.History[0].Body, decoded.History[1].Body);
    }

    [Fact]
    public void PatchAndHostResultRetainTheirCanonicalEnvelopes()
    {
        var patch = new AppServerControlWsHistoryPatchMessage
        {
            SessionId = "s", Patch = new() { SessionId = "s", LatestSequence = 4,
                HistoryUpserts = [new() { EntryId = "e", Order = 1, Body = "live" }], HistoryRemovals = ["old"] }
        };
        var info = AppServerControlHostJsonContext.Default.AppServerControlWsHistoryPatchMessage;
        var restored = NativeHistoryCodec.Decode(NativeHistoryCodec.Encode(patch, info), info);
        Assert.Equal(JsonSerializer.Serialize(patch, info), JsonSerializer.Serialize(restored, info));
        var result = new AppServerControlHostCommandResultEnvelope { CommandId = "read", SessionId = "s", HistoryWindow = new() { SessionId = "s" } };
        var resultInfo = AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope;
        Assert.Equal(JsonSerializer.Serialize(result, resultInfo), JsonSerializer.Serialize(NativeHistoryCodec.Decode(NativeHistoryCodec.Encode(result, resultInfo), resultInfo), resultInfo));
    }

    [Fact]
    public void TruncatedCorruptAndTrailingFramesAreRejected()
    {
        var info = AppServerControlHostJsonContext.Default.AppServerControlHistoryWindowResponse;
        var packet = NativeHistoryCodec.Encode(new() { SessionId = "bounded", History = [new() { EntryId = "item" }] }, info);
        for (var length = 0; length < packet.Length; length++)
            Assert.ThrowsAny<Exception>(() => NativeHistoryCodec.Decode(packet.AsSpan(0, length), info));
        Assert.Throws<InvalidDataException>(() => NativeHistoryCodec.Decode([.. packet, 0], info));
        packet[0] ^= 255;
        Assert.Throws<InvalidDataException>(() => NativeHistoryCodec.Decode(packet, info));
        Assert.Throws<InvalidDataException>(() => NativeHistoryCodec.Decode([65, 67, 72, 49, 8, 0], info));
    }
}
