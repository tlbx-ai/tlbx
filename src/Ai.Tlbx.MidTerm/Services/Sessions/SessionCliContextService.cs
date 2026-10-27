using System.Security.Cryptography;
using static System.FormattableString;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// Associates Codex tool threads with terminals using fresh output, never titles or cwd.
/// Input and client changes invalidate the association before another scoped request.
/// </summary>
public sealed class SessionCliContextService : IDisposable
{
    public const string LeaseHeader = "X-Tlbx-Cli-Context";
    internal const string MarkerPrefix = "TLBXCTX:";
    private const int MaximumEntries = 2048;
    private readonly object _gate = new();
    private readonly Dictionary<string, Handshake> _handshakes = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<Terminal>> _terminals;
    private readonly Func<int, (int Parent, DateTime Started)?> _process;
    private readonly TimeProvider _clock;
    private readonly Func<int, bool?> _sharedBackend;
    private readonly Dictionary<string, long> _inputVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutputScanner> _scanners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Proof> _proofs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Proof> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public SessionCliContextService(TtyHostSessionManager manager)
        : this(() => manager.GetSessionList(includeHidden: true).Sessions
            // Reclassify preserved host snapshots just like the session API. Raw GetInfo
            // metadata may omit Codex identity after a web update without a client change.
            .Select(s => s.ForegroundPid is int pid && pid > 0 && s.IsRunning && s.ForegroundProcessIdentity == "codex"
                ? new Terminal(s.Id, s.Pid, pid) : null).OfType<Terminal>().ToArray(),
            SessionProcessContext.ReadProcess, TimeProvider.System)
    {
        manager.OnOutput += (id, _, _, _, data) => ObserveOutput(id, data.Span);
        manager.OnInput += ObserveInput;
        manager.OnSessionClosed += id => { lock (_gate) { _scanners.Remove(id); _inputVersions.Remove(id); } };
    }

    internal SessionCliContextService(Func<IReadOnlyList<Terminal>> terminals,
        Func<int, (int Parent, DateTime Started)?> process, TimeProvider clock,
        Func<int, bool?>? sharedBackend = null)
    {
        _terminals = terminals;
        _process = process;
        _clock = clock;
        _sharedBackend = sharedBackend ?? SessionProcessContext.IsSharedCodexBackend;
    }

    internal sealed record Terminal(string Id, int ShellPid, int ClientPid);
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var handshake in _handshakes.Values) handshake.Gate.Dispose();
            _handshakes.Clear();
        }
    }

    private sealed class Handshake
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed record Owner(Terminal Terminal, DateTime Started, long InputVersion);
    private sealed class Proof(string root, string thread, string nonce, DateTimeOffset expires,
        Dictionary<string, Owner> candidates)
    {
        public string Root { get; } = root;
        public string Thread { get; } = thread;
        public string Nonce { get; } = nonce;
        public DateTimeOffset Expires { get; } = expires;
        public Dictionary<string, Owner> Candidates { get; } = candidates;
        public HashSet<string> Observers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> UnstableCandidates { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Lease(Proof? Proof, Owner? Owner, string SessionId, int CallerPid,
        DateTime CallerStarted, DateTimeOffset Expires);

    internal void InvalidateInput(string sessionId)
    {
        lock (_gate) { _inputVersions[sessionId] = _inputVersions.GetValueOrDefault(sessionId) + 1; }
    }

    internal void ObserveInput(string sessionId, bool userInput)
    {
        // Cursor positions, colors and other xterm replies cannot change the active thread.
        if (userInput) InvalidateInput(sessionId);
    }

    internal void ObserveOutput(string sessionId, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_proofs.Count == 0) return;
            if (!_scanners.TryGetValue(sessionId, out var scanner))
                _scanners[sessionId] = scanner = new OutputScanner();
            scanner.Feed(bytes, nonce =>
            {
                if (!_proofs.TryGetValue(nonce, out var proof) || proof.Expires <= _clock.GetUtcNow()) return;
                if (!proof.Candidates.TryGetValue(sessionId, out var candidate))
                {
                    if (!GetOwners().TryGetValue(sessionId, out candidate)) return;
                    proof.Candidates[sessionId] = candidate;
                }
                // Changed input can never turn stale output into sole ownership. Still
                // count it as duplicate evidence instead of hiding a second client.
                if (candidate.InputVersion != _inputVersions.GetValueOrDefault(sessionId))
                    proof.UnstableCandidates.Add(sessionId);
                proof.Observers.Add(sessionId);
            });
        }
    }

    internal string Resolve(string root, string thread, int callerPid, bool fresh = false)
    {
        lock (_gate)
        {
            Prune();
            var caller = _process(callerPid);
            if (caller is null) return ContextError("INVALID_PROCESS", "caller-process-missing: the calling tool process no longer exists.",
                "resolve", root, thread, callerPid);
            var owners = GetOwners();
            // Private TUI tools can be attributed immediately. Stop at a shared
            // app-server boundary: its ancestry identifies the first launcher only.
            var direct = FindDirectOwner(callerPid, owners);
            if (direct is not null)
            {
                var existingDirect = _leases.FirstOrDefault(pair => pair.Value.Proof is null && pair.Value.Owner is not null &&
                    pair.Value.CallerPid == callerPid && pair.Value.CallerStarted == caller.Value.Started);
                if (existingDirect.Key is not null)
                    return SameOwner(existingDirect.Value.Owner!, direct)
                        ? direct.Terminal.Id + ":" + existingDirect.Key
                        : ContextError("CHANGED", OwnerChange(existingDirect.Value.Owner!, direct)!,
                            "direct-ancestry", root, thread, callerPid);
                var directToken = RandomToken();
                _leases[directToken] = new Lease(null, direct, direct.Terminal.Id, callerPid, caller.Value.Started, _clock.GetUtcNow().AddMinutes(2));
                return direct.Terminal.Id + ":" + directToken;
            }
            var key = root + "/" + thread;
            _contexts.TryGetValue(key, out var proof);
            if (fresh || proof is null || proof.Expires <= _clock.GetUtcNow() || !RefreshCandidates(proof, owners))
            {
                proof = NewProof(root, thread, owners);
                _contexts[key] = proof;
            }
            if (proof.Observers.Count == 1 && proof.UnstableCandidates.Contains(proof.Observers.Single()))
            {
                proof = NewProof(root, thread, owners);
                _contexts[key] = proof;
            }
            if (proof.Observers.Count == 1)
            {
                var owner = owners[proof.Observers.Single()];
                var existing = _leases.FirstOrDefault(pair => pair.Value.Proof == proof &&
                    pair.Value.CallerPid == callerPid && pair.Value.CallerStarted == caller.Value.Started);
                if (existing.Key is not null) return owner.Terminal.Id + ":" + existing.Key;
                var token = RandomToken();
                _leases[token] = new Lease(proof, owner, owner.Terminal.Id, callerPid, caller.Value.Started,
                    _clock.GetUtcNow().AddMinutes(2));
                return owner.Terminal.Id + ":" + token;
            }
            var status = proof.Observers.Count > 1 ? "AMBIGUOUS" : "UNBOUND";
            var instructions = status == "AMBIGUOUS"
                ? "Run mt_context repair as a separate shell tool call, then retry the original command. Do not copy the proof to other terminals. If repair remains ambiguous, stop: the same Codex thread may be displayed in multiple terminals. Ask to close the duplicate client before retrying; never guess a session ID."
                : "Required next action: retry the exact original command once in a separate shell tool call. The proof above must be displayed in your terminal first. If tool output is hidden, print ONLY the proof line in a separate tool call, then retry. If the same reason repeats, stop retrying and report this complete diagnostic. The failed operation is still pending.";
            if (root != thread)
                instructions += " If this is a subagent without its own terminal output, ask the parent agent to perform the scoped operation; do not overwrite CODEX_SESSION_ID or CODEX_THREAD_ID.";
            return MarkerPrefix + proof.Nonce + "\n" + ContextError(status,
                proof.Observers.Count > 1 ? "duplicate-proof: the same fresh proof appeared in multiple terminals."
                    : owners.Count == 0 ? "no-candidates: no running Codex terminal with a live client process was found."
                    : "proof-not-observed: no candidate terminal has displayed the fresh proof.",
                "terminal-proof", root, thread, callerPid, proof, owners, instructions);
        }
    }

    internal async Task<string> ResolveAsync(string root, string thread, int callerPid, bool fresh,
        Func<string, CancellationToken, Task<bool>> emitProof, CancellationToken ct)
    {
        var key = root + "/" + thread;
        Handshake handshake;
        lock (_gate)
        {
            if (!_handshakes.TryGetValue(key, out handshake!))
            {
                if (_handshakes.Count >= MaximumEntries)
                    return ContextError("BUSY", "discovery-capacity: too many concurrent thread handshakes.", "automatic-discovery", root, thread, callerPid);
                _handshakes[key] = handshake = new Handshake();
            }
            handshake.Users++;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var acquired = false;
        try
        {
            await handshake.Gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            for (var attempt = 0; ; attempt++)
            {
                var result = await ResolveAttemptAsync(root, thread, callerPid, fresh || attempt > 0, emitProof, deadline.Token).ConfigureAwait(false);
                // Only renew the identity preflight. No application request has been sent.
                // Ambiguity, caller replacement, and authorization failures are never retried.
                if (!result.StartsWith("TLBX_CONTEXT_CHANGED:", StringComparison.Ordinal) ||
                    !result.Contains("stage=automatic-discovery;", StringComparison.Ordinal)) return result;
                if (attempt > 0) return result + "\nRecovery: a fresh proof was attempted once; no session operation was executed.";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ContextError("UNBOUND", "discovery-deadline: automatic binding exceeded its 10-second total budget.",
                "automatic-discovery", root, thread, callerPid);
        }
        finally
        {
            if (acquired) handshake.Gate.Release();
            lock (_gate)
            {
                if (--handshake.Users == 0)
                {
                    _handshakes.Remove(key);
                    handshake.Gate.Dispose();
                }
            }
        }
    }

    private async Task<string> ResolveAttemptAsync(string root, string thread, int callerPid, bool fresh,
        Func<string, CancellationToken, Task<bool>> emitProof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? initial = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Proof? pendingProof = null;
        try
        {
            // Recheck after serialization: another caller may already have established
            // this thread's proof, so concurrent first calls emit only once.
            initial = Resolve(root, thread, callerPid, fresh);
            if (!initial.Contains("TLBX_CONTEXT_UNBOUND", StringComparison.Ordinal)) return initial;
            var marker = initial.Split('\n')[0];
            Proof proof;
            DateTime callerStarted;
            lock (_gate)
            {
                proof = _contexts[root + "/" + thread];
                var caller = _process(callerPid);
                if (caller is null) return ContextError("INVALID_PROCESS", "caller-process-missing: the calling tool process no longer exists.",
                    "automatic-discovery", root, thread, callerPid, proof);
                callerStarted = caller.Value.Started;
            }
            pendingProof = proof;
            if (!await emitProof(marker, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false))
                return initial + "\nAutomatic discovery: proof submission unavailable; the daemon did not accept the echo request.";
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var resolution = ReadResolution(settled: false);
                if (resolution.Length == 0)
                {
                    // Duplicate clients can render the same RPC output slightly apart.
                    // The lease guard continues checking after this short observation window.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
                    return ReadResolution(settled: true);
                }
                if (!resolution.Contains("TLBX_CONTEXT_UNBOUND", StringComparison.Ordinal)) return resolution;
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(false);
            }

            string ReadResolution(bool settled)
            {
                lock (_gate)
                {
                    var caller = _process(callerPid);
                    if (caller is null || caller.Value.Started != callerStarted)
                        return ContextError("INVALID_PROCESS", Invariant($"caller-process-changed: expected start={callerStarted:O}; actual start={caller?.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}."),
                            "automatic-discovery", root, thread, callerPid, proof);
                    if (!_contexts.TryGetValue(root + "/" + thread, out var current) || current != proof)
                        return ContextError("CHANGED", "proof-replaced: another fresh/repair request replaced this thread's proof.",
                            "automatic-discovery", root, thread, callerPid, proof);
                    var owners = GetOwners();
                    if (proof.Observers.Count(id => owners.TryGetValue(id, out var live) &&
                        proof.Candidates.TryGetValue(id, out var previous) && SameClient(previous, live)) > 1)
                        return ContextError("AMBIGUOUS", "duplicate-proof: the same fresh proof appeared in multiple live terminals.",
                            "automatic-discovery", root, thread, callerPid, proof, owners,
                            "Close the duplicate client, then retry the original command. Do not choose a session ID or replay the mutation manually.");
                    if (!RefreshCandidates(proof, owners, out var change, allowUnobservedInput: true))
                        return ContextError("CHANGED", change!, "automatic-discovery", root, thread, callerPid, proof, owners);
                    if (proof.Observers.Count == 1 && proof.UnstableCandidates.Contains(proof.Observers.Single()))
                        return ContextError("CHANGED", "input-before-observation: the sole observer received input after this proof was issued; request a fresh proof.",
                            "automatic-discovery", root, thread, callerPid, proof, owners);
                    // Do not mint a lease until duplicate observation has settled.
                    if (!settled && proof.Observers.Count == 1) return string.Empty;
                    var result = Resolve(root, thread, callerPid);
                    if (_contexts[root + "/" + thread] != proof)
                        return ContextError("CHANGED", "proof-replaced: the proof changed while resolving the observed terminal.",
                            "automatic-discovery", root, thread, callerPid, proof, owners);
                    return result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Keep an unobserved challenge after our timeout: the returned error
            // prints it in the caller's terminal, allowing the requested retry.
            // Explicit cancellation or an aborted duplicate-settle window must
            // invalidate it instead. Never revoke a newer proof.
            lock (_gate)
            {
                var key = root + "/" + thread;
                if (pendingProof is not null && (ct.IsCancellationRequested || pendingProof.Observers.Count > 0) &&
                    _contexts.TryGetValue(key, out var current) && current == pendingProof)
                {
                    _contexts.Remove(key);
                    _proofs.Remove(pendingProof.Nonce);
                }
            }
            ct.ThrowIfCancellationRequested();
            return (initial ?? ContextError("UNBOUND", "discovery-queue-timeout: another discovery occupied the handshake slot.",
                "automatic-discovery", root, thread, callerPid)) +
                Invariant($"\nAutomatic discovery: timeout after 5 seconds; phase={(pendingProof is null ? "waiting-for-slot" : pendingProof.Observers.Count > 0 ? "duplicate-settle" : "waiting-for-terminal-output")}; observed={pendingProof?.Observers.Count ?? 0}.");
        }
    }

    internal string CreateHostLease(string sessionId, int callerPid)
    {
        lock (_gate)
        {
            Prune();
            var caller = _process(callerPid);
            if (caller is null) return ContextError("INVALID_PROCESS", "caller-process-missing: the calling tool process no longer exists.",
                "host-lease", "hosted", "hosted", callerPid);
            var token = RandomToken();
            _leases[token] = new Lease(null, null, sessionId, callerPid, caller.Value.Started,
                _clock.GetUtcNow().AddMinutes(2));
            return sessionId + ":" + token;
        }
    }

    internal string? ValidateLease(string token)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(token, out var lease))
                return Error("EXPIRED", "Reason: unknown-lease: this server has no matching lease (expired, pruned, or issued before a server restart).\nNext: Run the original command in a new shell tool call to obtain a fresh lease from the owning server.");
            if (lease.Expires <= _clock.GetUtcNow())
                return ContextError("EXPIRED", Invariant($"lease-expired: expiredAt={lease.Expires:O}."), "lease-validation",
                    lease.Proof?.Root ?? "direct", lease.Proof?.Thread ?? "direct", lease.CallerPid);
            var caller = _process(lease.CallerPid);
            if (caller is null || caller.Value.Started != lease.CallerStarted)
                return ContextError("INVALID_PROCESS", Invariant($"caller-process-changed: expected start={lease.CallerStarted:O}; actual start={caller?.Started.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}."),
                    "lease-validation", lease.Proof?.Root ?? "direct", lease.Proof?.Thread ?? "direct", lease.CallerPid);
            if (lease.Proof is not { } proof)
            {
                if (lease.Owner is null) return null;
                var direct = FindDirectOwner(lease.CallerPid, GetOwners());
                return direct is not null && SameOwner(lease.Owner, direct) ? null :
                    ContextError("CHANGED", direct is null ? $"ancestry-lost: caller no longer resolves to session={lease.SessionId}."
                        : OwnerChange(lease.Owner, direct)!, "lease-validation", "direct", "direct", lease.CallerPid);
            }
            var owners = GetOwners();
            string? reason;
            if (!_contexts.TryGetValue(proof.Root + "/" + proof.Thread, out var currentProof) || currentProof != proof)
                reason = "proof-replaced: another fresh/repair request replaced this thread's proof.";
            else if (proof.Expires <= _clock.GetUtcNow()) reason = Invariant($"proof-expired: expiredAt={proof.Expires:O}.");
            else if (RefreshCandidates(proof, owners, out reason))
            {
                if (proof.Observers.Count != 1) reason = Invariant($"duplicate-proof: expected one observer; actual={proof.Observers.Count}.");
                else if (!owners.TryGetValue(lease.SessionId, out var owner)) reason = $"owner-missing: session={lease.SessionId} is no longer a live Codex candidate.";
                else reason = lease.Owner is null ? "owner-missing: lease has no terminal snapshot." : OwnerChange(lease.Owner, owner);
            }
            return reason is null ? null : ContextError("CHANGED", reason, "lease-validation", proof.Root, proof.Thread, lease.CallerPid, proof, owners);
        }
    }

    internal static bool IsResolution(string value) => value.Length == 41 && value[8] == ':' &&
        value[..8].All(char.IsAsciiLetterOrDigit) && value[9..].All(char.IsAsciiHexDigit);

    internal static string Error(string status, string instruction) =>
        $"TLBX_CONTEXT_{status}: No session operation was executed.\n{instruction}\nNever infer ownership from cwd, terminal title, or inherited MT_SESSION_ID.";

    private static string ContextError(string status, string reason, string stage, string root, string thread,
        int callerPid, Proof? proof = null, Dictionary<string, Owner>? owners = null, string? next = null) =>
        Error(status, Invariant($"Reason: {reason}\nContext: stage={stage}; callerPid={callerPid}; root={root}; thread={thread}.") +
            (proof is null ? "" : Invariant($"\nEvidence: candidates={proof.Candidates.Count} [{string.Join(",", proof.Candidates.Keys.Order(StringComparer.Ordinal).Take(8))}]; observed={proof.Observers.Count} [{string.Join(",", proof.Observers.Order(StringComparer.Ordinal).Take(8))}].")) +
            (owners is null ? "" : Invariant($"\nLive candidates (first 8 of {owners.Count}): {string.Join("; ", owners.Values.OrderBy(o => o.Terminal.Id, StringComparer.Ordinal).Take(8).Select(o => Invariant($"session={o.Terminal.Id}, shellPid={o.Terminal.ShellPid}, clientPid={o.Terminal.ClientPid}, clientStarted={o.Started:O}, inputVersion={o.InputVersion}")))}.")) +
            "\nNext: " + (next ?? "Retry the original command once in a new shell tool call. If the same reason repeats, stop retrying and report this complete diagnostic; run mt_context repair separately only to request a fresh proof."));

    private Dictionary<string, Owner> GetOwners()
    {
        var owners = new Dictionary<string, Owner>(StringComparer.Ordinal);
        foreach (var terminal in _terminals())
        {
            var process = _process(terminal.ClientPid);
            if (process is not null)
                owners[terminal.Id] = new Owner(terminal, process.Value.Started, _inputVersions.GetValueOrDefault(terminal.Id));
        }
        return owners;
    }

    private Owner? FindDirectOwner(int pid, Dictionary<string, Owner> owners)
    {
        var visited = new HashSet<int>();
        DateTime? childStarted = null;
        while (pid > 0 && visited.Count < 128 && visited.Add(pid))
        {
            var process = _process(pid);
            if (process is null || (childStarted is not null && process.Value.Started > childStarted) || _sharedBackend(pid) != false) return null;
            var candidates = owners.Values.Where(owner => owner.Terminal.ClientPid == pid).ToArray();
            if (candidates.Length != 0) return candidates.Length == 1 ? candidates[0] : null;
            childStarted = process.Value.Started;
            pid = process.Value.Parent;
        }
        return null;
    }

    private static bool SameClient(Owner left, Owner right) => left.Terminal == right.Terminal && left.Started == right.Started;

    private static bool SameOwner(Owner left, Owner right) => SameClient(left, right) && left.InputVersion == right.InputVersion;

    private static string? OwnerChange(Owner previous, Owner current)
    {
        if (SameOwner(previous, current)) return null;
        var changes = new List<string>();
        if (previous.Terminal != current.Terminal)
            changes.Add(Invariant($"client-changed: session={previous.Terminal.Id}; shellPid={previous.Terminal.ShellPid}->{current.Terminal.ShellPid}; clientPid={previous.Terminal.ClientPid}->{current.Terminal.ClientPid}"));
        if (previous.Started != current.Started)
            changes.Add(Invariant($"client-process-replaced: session={previous.Terminal.Id}; clientPid={current.Terminal.ClientPid}; started={previous.Started:O}->{current.Started:O}"));
        if (previous.InputVersion != current.InputVersion)
            changes.Add(Invariant($"terminal-input-changed: session={previous.Terminal.Id}; inputVersion={previous.InputVersion}->{current.InputVersion} (user input observed; terminal protocol replies do not increment this counter)"));
        return changes.Count == 0 ? null : string.Join("; ", changes) + ".";
    }

    private static bool RefreshCandidates(Proof proof, Dictionary<string, Owner> owners) => RefreshCandidates(proof, owners, out _);

    private static bool RefreshCandidates(Proof proof, Dictionary<string, Owner> owners, out string? change, bool allowUnobservedInput = false)
    {
        change = null;
        List<Owner>? updates = null;
        foreach (var (id, previous) in proof.Candidates)
        {
            if (owners.TryGetValue(id, out var current))
            {
                change = OwnerChange(previous, current);
                if (change is not null)
                {
                    // Only a currently running fresh handshake may absorb ordinary input
                    // in an unobserved sibling. Cached leases remain invalidated by ANY
                    // candidate input. A sibling showing this nonce still makes it ambiguous.
                    if (!allowUnobservedInput || proof.Observers.Contains(id) || !SameClient(previous, current))
                        return false;
                    proof.UnstableCandidates.Add(id);
                    (updates ??= []).Add(current);
                    change = null;
                }
            }
            else if (proof.Observers.Contains(id))
            {
                change = Invariant($"owner-missing: observed session={id}, clientPid={previous.Terminal.ClientPid} is no longer a live Codex candidate.");
                return false;
            }
        }
        if (updates is not null)
            foreach (var owner in updates) proof.Candidates[owner.Terminal.Id] = owner;
        foreach (var id in proof.Candidates.Keys.Where(id => !owners.ContainsKey(id)).ToArray())
        {
            proof.Candidates.Remove(id);
            proof.UnstableCandidates.Remove(id);
        }
        foreach (var (id, owner) in owners) proof.Candidates.TryAdd(id, owner);
        return true;
    }

    private Proof NewProof(string root, string thread, Dictionary<string, Owner> owners)
    {
        var nonce = RandomToken();
        // A TUI may reuse the visible prefix and rewrite only changed nonce cells.
        // Change both ends on rotation so a contiguous diff emits the full nonce.
        if (_contexts.TryGetValue(root + "/" + thread, out var previous))
            while (nonce[0] == previous.Nonce[0] || nonce[^1] == previous.Nonce[^1]) nonce = RandomToken();
        var proof = new Proof(root, thread, nonce, _clock.GetUtcNow().AddMinutes(5), owners);
        _proofs[nonce] = proof;
        return proof;
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _leases.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _leases.Remove(key);
        foreach (var key in _proofs.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _proofs.Remove(key);
        foreach (var key in _contexts.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _contexts.Remove(key);
        // Authenticated local callers still cannot grow the registry without bound.
        if (_proofs.Count >= MaximumEntries) { _proofs.Clear(); _contexts.Clear(); _leases.Clear(); _scanners.Clear(); }
        if (_leases.Count >= MaximumEntries) _leases.Clear();
    }

    private static string RandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Incremental ASCII proof matching across UTF-8/VT chunks and line wrapping; OSC is never evidence.</summary>
    private sealed class OutputScanner
    {
        private readonly char[] _tail = new char[32];
        private int _next;
        private int _count;
        private int _escapeState; // 0 text, 1 ESC, 2 CSI, 3 OSC/string, 4 string ESC
        private int _utf8Remaining;
        private byte _utf8Lead;

        public void Feed(ReadOnlySpan<byte> bytes, Action<string> found)
        {
            Span<char> nonce = stackalloc char[32];
            foreach (var b in bytes)
            {
                // Continuation bytes in ordinary Unicode text are not C1 controls.
                // Preserve decoder state across output chunks. Only C2 80..9F encodes
                // an actual UTF-8 C1 control; raw single-byte controls also remain valid.
                if (_utf8Remaining > 0)
                {
                    if (b is >= 0x80 and <= 0xbf)
                    {
                        _utf8Remaining--;
                        if (_utf8Remaining != 0 || _utf8Lead != 0xc2 || b > 0x9f)
                        {
                            _count = 0;
                            continue;
                        }
                    }
                    else { _utf8Remaining = 0; _count = 0; }
                }
                if (b is >= 0xc2 and <= 0xf4)
                {
                    _utf8Lead = b;
                    _utf8Remaining = b < 0xe0 ? 1 : b < 0xf0 ? 2 : 3;
                    if (b != 0xc2) _count = 0;
                    continue;
                }
                if (b == 0x9c) { _escapeState = 0; continue; }
                if (_escapeState == 3) { if (b == 7) _escapeState = 0; else if (b == 27) _escapeState = 4; continue; }
                if (_escapeState == 4) { _escapeState = b == (byte)'\\' ? 0 : 3; continue; }
                if (_escapeState == 2) { if (b is >= 0x40 and <= 0x7e) _escapeState = 0; continue; }
                if (_escapeState == 1)
                {
                    _escapeState = b switch { (byte)'[' => 2, (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' => 3, _ => 0 };
                    continue;
                }
                if (b == 27) { _escapeState = 1; continue; }
                if (b is 0x90 or 0x9d or 0x9e or 0x9f) { _escapeState = 3; continue; }
                if (b == 0x9b) { _escapeState = 2; continue; }
                if (b is 9 or 10 or 13 or 32) continue;
                if (b is < 0x21 or > 0x7e) { _count = 0; continue; }
                if (!char.IsAsciiHexDigit((char)b)) { _count = 0; continue; }
                _tail[_next] = (char)b;
                _next = (_next + 1) % 32;
                _count = Math.Min(_count + 1, 32);
                if (_count == 32)
                {
                    var valid = true;
                    for (var i = 0; i < nonce.Length; i++)
                    {
                        nonce[i] = _tail[(_next + i) % 32];
                        if (!char.IsAsciiHexDigit(nonce[i])) { valid = false; break; }
                    }
                    if (valid) found(new string(nonce).ToLowerInvariant());
                }
            }
        }

    }
}
