using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionCliContextServiceTests
{
    private const string Root = "01a10b97-2853-75e2-b134-d6fe7f527ae3";
    private const string Other = "01a10b97-28a0-7152-901f-e4854f489197";
    private readonly DateTime _started = new(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
    private readonly List<SessionCliContextService.Terminal> _terminals =
        [new("first001", 10, 11), new("second02", 20, 21)];
    private readonly Dictionary<int, DateTime> _processes = [];
    private readonly Clock _clock = new();
    private readonly SessionCliContextService _service;

    public SessionCliContextServiceTests()
    {
        foreach (var pid in new[] { 10, 11, 20, 21, 100, 101 }) _processes[pid] = _started;
        _service = new(() => _terminals, pid => _processes.TryGetValue(pid, out var started) ? (1, started) : null, _clock);
    }

    [Fact]
    public void ExplicitRepairImmediatelyRevokesOldLeaseAndFleetChangesRequireNewProof()
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var oldLease = _service.Resolve(Root, Root, 100);
        var repair = _service.Resolve(Root, Root, 100, fresh: true);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(oldLease[9..]), StringComparison.Ordinal);
        Feed("first001", ProofLine(repair));
        var lease = _service.Resolve(Root, Root, 100);
        Assert.Null(_service.ValidateLease(lease[9..]));
        _terminals.RemoveAt(1);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentThreadsRecoverTheirExactTerminalWithoutAncestryOrTitles()
    {
        var first = _service.Resolve(Root, Root, 100);
        var second = _service.Resolve(Other, Other, 101);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", first, StringComparison.Ordinal);
        Assert.Contains("No session operation was executed", first, StringComparison.Ordinal);
        Feed("first001", ProofLine(first));
        Feed("second02", ProofLine(second));
        var firstLease = _service.Resolve(Root, Root, 100);
        var secondLease = _service.Resolve(Other, Other, 101);
        Assert.StartsWith("first001:", firstLease, StringComparison.Ordinal);
        Assert.StartsWith("second02:", secondLease, StringComparison.Ordinal);
        Assert.Null(_service.ValidateLease(firstLease[9..]));
        Assert.Null(_service.ValidateLease(secondLease[9..]));
        Assert.Equal(firstLease, _service.Resolve(Root, Root, 100));
    }

    [Fact]
    public void FragmentedAnsiColoredAndWrappedProofWorksButOscTitleDoesNot()
    {
        var error = _service.Resolve(Root, Root, 100);
        var marker = ProofLine(error);
        var title = "\x1b]0;" + marker + "\x07";
        foreach (var b in Encoding.UTF8.GetBytes(title)) _service.ObserveOutput("second02", [b]);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        var colored = "\x1b[31m" + marker[..20] + "\x1b[0m\r\n  " + marker[20..];
        foreach (var b in Encoding.UTF8.GetBytes(colored)) _service.ObserveOutput("first001", [b]);
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateProofRevokesAnAlreadyIssuedLeaseAndRequiresFreshCorrection()
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var lease = _service.Resolve(Root, Root, 100);
        Feed("second02", proof);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_AMBIGUOUS", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        var repair = _service.Resolve(Root, Root, 100, fresh: true);
        Assert.NotEqual(proof, ProofLine(repair));
        Feed("second02", proof); // Replaying the old proof cannot claim the new context.
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        Feed("first001", ProofLine(repair));
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("first001")]
    [InlineData("second02")]
    public void AnyCodexTerminalInputInvalidatesProofBeforeMutation(string changedTerminal)
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var lease = _service.Resolve(Root, Root, 100);
        _service.InvalidateInput(changedTerminal);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Feed("first001", proof); // A screen redraw after /new or /resume is stale output.
        var retry = _service.Resolve(Root, Root, 100);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", retry, StringComparison.Ordinal);
        Assert.NotEqual(proof, ProofLine(retry));
    }

    [Fact]
    public void ClientReplacementOrPidReuseCannotKeepTheOldBinding()
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var lease = _service.Resolve(Root, Root, 100);
        _processes[11] = _started.AddSeconds(10);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void NewAndResumedThreadsNeedTheirOwnFreshProof()
    {
        Feed("first001", ProofLine(_service.Resolve(Root, Root, 100)));
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        var child = _service.Resolve(Root, Other, 100);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", child, StringComparison.Ordinal);
        Assert.Contains("ask the parent agent", child, StringComparison.Ordinal);
        _service.InvalidateInput("first001");
        var newThread = _service.Resolve(Other, Other, 100);
        Feed("first001", ProofLine(newThread));
        Assert.StartsWith("first001:", _service.Resolve(Other, Other, 100), StringComparison.Ordinal);
        _service.InvalidateInput("second02"); // Existing TUI selects the old conversation.
        var resumed = _service.Resolve(Root, Root, 101);
        Feed("second02", ProofLine(resumed));
        Assert.StartsWith("second02:", _service.Resolve(Root, Root, 101), StringComparison.Ordinal);
    }

    [Fact]
    public void CallerExitPidReuseAndTimeoutRejectLeases()
    {
        Feed("first001", ProofLine(_service.Resolve(Root, Root, 100)));
        var lease = _service.Resolve(Root, Root, 100);
        _processes.Remove(100);
        Assert.Contains("TLBX_CONTEXT_INVALID_PROCESS", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        _processes[100] = _started.AddSeconds(1);
        Assert.Contains("TLBX_CONTEXT_INVALID_PROCESS", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        var replacement = _service.Resolve(Root, Root, 100);
        _clock.UtcNow += TimeSpan.FromMinutes(3);
        Assert.Contains("TLBX_CONTEXT_EXPIRED", _service.ValidateLease(replacement[9..]), StringComparison.Ordinal);
        _clock.UtcNow += TimeSpan.FromMinutes(3);
        var expiredProof = _service.Resolve(Root, Root, 100);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", expiredProof, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalOutputAndUnknownMarkersNeverCreateOwnership()
    {
        Feed("first001", "TLBXCTX:00000000000000000000000000000000");
        var error = _service.Resolve(Root, Root, 100);
        Feed("first001", ProofLine(error)[..30]);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    private void Feed(string session, string text) => _service.ObserveOutput(session, Encoding.UTF8.GetBytes(text));
    private static string ProofLine(string error) => error.Split('\n')[0];
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
