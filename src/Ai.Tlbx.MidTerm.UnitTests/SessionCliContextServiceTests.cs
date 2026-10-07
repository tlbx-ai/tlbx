using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionCliContextServiceTests : IDisposable
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
    public void NonceOnlyRedrawRecognizesFreshProofWithoutReemittingVisiblePrefix()
    {
        var old = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", old);
        var fresh = ProofLine(_service.Resolve(Root, Root, 100, fresh: true));
        Assert.NotEqual(old[8], fresh[8]);
        Assert.NotEqual(old[^1], fresh[^1]);
        Feed("first001", "\x1b[1;9H" + fresh[8..]);
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        Feed("second02", fresh[24..]); // A partial nonce is never sufficient.
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        Feed("second02", "\x1b]0;" + fresh[8..] + "\x07");
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateToolAncestryResolvesImmediatelyButSharedBackendRequiresProof()
    {
        var parents = new Dictionary<int, int> { [100] = 12, [12] = 11, [11] = 10, [10] = 1 };
        (int, DateTime)? Process(int pid) => parents.TryGetValue(pid, out var parent) ? (parent, _started) : null;
        using var privateService = new SessionCliContextService(() => _terminals, Process, _clock, _ => false);
        var direct = privateService.Resolve(Root, Root, 100);
        Assert.StartsWith("first001:", direct, StringComparison.Ordinal);
        Assert.Null(privateService.ValidateLease(direct[9..]));
        privateService.ObserveInput("first001", true);
        Assert.Contains("TLBX_CONTEXT_CHANGED", privateService.ValidateLease(direct[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_CHANGED", privateService.Resolve(Root, Root, 100), StringComparison.Ordinal);
        using var daemonService = new SessionCliContextService(() => _terminals, Process, _clock, pid => pid == 12);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", daemonService.Resolve(Root, Root, 100), StringComparison.Ordinal);
        using var unknownService = new SessionCliContextService(() => _terminals, Process, _clock, _ => null);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", unknownService.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex --yolo", false)]
    [InlineData("codex app-server --listen unix://", true)]
    [InlineData("codex \"app-server\" --listen unix://", true)]
    [InlineData("codex 'app-server' --managed-daemon", true)]
    public void SharedBackendDetectionPreservesQuotedArguments(string command, bool shared)
        => Assert.Equal(shared, SessionProcessContext.IsSharedCodexCommand(command));

    [Fact]
    public void AutomaticTerminalRepliesKeepBindingButUserInputRevokesIt()
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var lease = _service.Resolve(Root, Root, 100);
        _service.ObserveInput("first001", userInput: false);
        Assert.Null(_service.ValidateLease(lease[9..]));
        _service.ObserveInput("first001", userInput: true);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitRepairImmediatelyRevokesOldLeaseAndOwnerClosureRequiresNewProof()
    {
        var proof = ProofLine(_service.Resolve(Root, Root, 100));
        Feed("first001", proof);
        var oldLease = _service.Resolve(Root, Root, 100);
        var repair = _service.Resolve(Root, Root, 100, fresh: true);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(oldLease[9..]), StringComparison.Ordinal);
        Feed("first001", ProofLine(repair));
        var lease = _service.Resolve(Root, Root, 100);
        Assert.Null(_service.ValidateLease(lease[9..]));
        _terminals.RemoveAt(0);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingStartupKeepsPendingProofAndLeaseButDuplicateOutputStillBlocks()
    {
        _terminals.RemoveAt(1);
        var marker = ProofLine(_service.Resolve(Root, Root, 100));
        _terminals.Add(new("second02", 20, 21));
        Assert.Equal(marker, ProofLine(_service.Resolve(Root, Root, 100)));
        Feed("first001", marker);
        var lease = _service.Resolve(Root, Root, 100);
        Assert.StartsWith("first001:", lease, StringComparison.Ordinal);
        _terminals.RemoveAt(1);
        Assert.Null(_service.ValidateLease(lease[9..]));
        _terminals.Add(new("second02", 20, 21));
        Assert.Null(_service.ValidateLease(lease[9..]));
        Feed("second02", marker);
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(lease[9..]), StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_AMBIGUOUS", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
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
        foreach (var b in Encoding.UTF8.GetBytes("\u009d0;" + marker + "\u009c")) _service.ObserveOutput("second02", [b]);
        _service.ObserveOutput("second02", [0x9d, .. Encoding.ASCII.GetBytes("0;" + marker), 0x9c]);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        var colored = "\x1b[31m" + marker[..20] + "\x1b[0m\r\n  " + marker[20..];
        foreach (var b in Encoding.UTF8.GetBytes(colored)) _service.ObserveOutput("first001", [b]);
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8ContinuationBytesAreNotTerminalControls()
    {
        var marker = ProofLine(_service.Resolve(Root, Root, 100));
        // These glyphs contain 90/9B/9C/9D/9E/9F continuation bytes, not C1 controls.
        // Split every byte to exercise the streaming decoder across output chunks.
        foreach (var b in Encoding.UTF8.GetBytes("┐┛├┝┞┟▐\r\n" + marker))
            _service.ObserveOutput("first001", [b]);
        Assert.StartsWith("first001:", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
        // Unicode inside a real OSC must never terminate it and expose a hidden proof.
        _service.ObserveOutput("second02", Encoding.UTF8.GetBytes("\x1b]0;├" + marker + "\x07"));
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
        Assert.NotEqual(proof, ProofLine(repair), StringComparer.Ordinal);
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
        Assert.NotEqual(proof, ProofLine(retry), StringComparer.Ordinal);
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

    [Fact]
    public async Task AutomaticFirstCallBindsFromIndependentOutputAndRechecksLease()
    {
        var result = await _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            Feed("first001", marker);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.StartsWith("first001:", result, StringComparison.Ordinal);
        Assert.Null(_service.ValidateLease(result[9..]));
        _service.InvalidateInput("first001");
        Assert.Contains("TLBX_CONTEXT_CHANGED", _service.ValidateLease(result[9..]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticDiscoveryDoesNotBindWithoutTerminalOutput()
    {
        var result = await _service.ResolveAsync(Root, Root, 100, false,
            (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticDiscoveryUnavailableReturnsWithoutGuessing()
    {
        var result = await _service.ResolveAsync(Root, Root, 100, false,
            (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.Contains("TLBX_CONTEXT_UNBOUND", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticDiscoveryDetectsDuplicateDuringSettleWindow()
    {
        Task? duplicate = null;
        var result = await _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            Feed("first001", marker);
            duplicate = Task.Run(async () =>
            {
                await Task.Delay(50, CancellationToken.None);
                Feed("second02", marker);
            }, CancellationToken.None);
            return Task.FromResult(true);
        }, CancellationToken.None);
        await duplicate!;
        Assert.Contains("TLBX_CONTEXT_AMBIGUOUS", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticDiscoveryCancellationReleasesSerialization()
    {
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.ResolveAsync(Root, Root, 100, false, (_, token) =>
            {
                cancelled.Cancel();
                return Task.FromCanceled<bool>(token);
            }, cancelled.Token));
        var result = await _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            Feed("first001", marker);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.StartsWith("first001:", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("client")]
    [InlineData("caller")]
    public async Task AutomaticDiscoveryRejectsMutationDuringHandshake(string change)
    {
        var result = await _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            Feed("first001", marker);
            if (change == "input") _service.InvalidateInput("first001");
            else if (change == "client") _processes[11] = _started.AddSeconds(1);
            else _processes[100] = _started.AddSeconds(1);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.Contains(change == "caller" ? "TLBX_CONTEXT_INVALID_PROCESS" : "TLBX_CONTEXT_CHANGED", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringSettleRequiresNewProofAndNewObservationWindow()
    {
        using var cancelled = new CancellationTokenSource();
        string? abortedMarker = null;
        var first = _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            abortedMarker = marker;
            Feed("first001", marker);
            return Task.FromResult(true);
        }, cancelled.Token);
        // The synchronous emitter already returned; the only remaining await
        // is the duplicate settle window. Cancellation aborts that observation.
        Assert.NotNull(abortedMarker);
        Assert.False(first.IsCompleted);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var emitted = false;
        var second = _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            emitted = true;
            Assert.NotEqual(abortedMarker, marker, StringComparer.Ordinal);
            Feed("first001", abortedMarker); // Late output of the aborted proof is inert.
            Assert.Contains("TLBX_CONTEXT_UNBOUND", _service.Resolve(Root, Root, 100), StringComparison.Ordinal);
            Feed("first001", marker);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(emitted);
        Assert.False(second.IsCompleted);
        Assert.StartsWith("first001:", await second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentFirstCallsShareOneProofEmission()
    {
        var emitting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<bool> Emit(string marker, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            emitting.TrySetResult();
            await release.Task.WaitAsync(token);
            Feed("first001", marker);
            return true;
        }
        var first = _service.ResolveAsync(Root, Root, 100, false, Emit, CancellationToken.None);
        await emitting.Task;
        var second = _service.ResolveAsync(Root, Root, 101, false, Emit, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.StartsWith("first001:", result, StringComparison.Ordinal));
        Assert.All(results, result => Assert.Null(_service.ValidateLease(result[9..])));
        Assert.NotEqual(results[0], results[1], StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConcurrentCallCannotBypassDuplicateSettleWindow()
    {
        string? pendingMarker = null;
        var first = _service.ResolveAsync(Root, Root, 100, false, (marker, _) =>
        {
            pendingMarker = marker;
            Feed("first001", marker);
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.NotNull(pendingMarker);
        var second = _service.ResolveAsync(Root, Root, 101, false,
            (_, _) => throw new InvalidOperationException("Must reuse the discovery"), CancellationToken.None);
        Assert.False(second.IsCompleted, second.IsCompleted ? second.Result : null);
        Feed("second02", pendingMarker);
        Assert.All(await Task.WhenAll(first, second), result =>
            Assert.Contains("TLBX_CONTEXT_AMBIGUOUS", result, StringComparison.Ordinal));
    }

    public void Dispose() => _service.Dispose();

    private void Feed(string session, string text) => _service.ObserveOutput(session, Encoding.UTF8.GetBytes(text));
    private static string ProofLine(string error) => error.Split('\n')[0];
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
