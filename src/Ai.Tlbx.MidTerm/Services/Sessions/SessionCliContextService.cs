using System.Globalization;
using System.Security.Cryptography;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// Associates Codex tool threads with terminals using fresh output, never titles or cwd.
/// Input and client changes invalidate the association before another scoped request.
/// </summary>
public sealed class SessionCliContextService
{
    public const string LeaseHeader = "X-Tlbx-Cli-Context";
    internal const string MarkerPrefix = "TLBXCTX:";
    private const int MaximumEntries = 2048;
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<Terminal>> _terminals;
    private readonly Func<int, (int Parent, DateTime Started)?> _process;
    private readonly TimeProvider _clock;
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
        Func<int, (int Parent, DateTime Started)?> process, TimeProvider clock)
    {
        _terminals = terminals;
        _process = process;
        _clock = clock;
    }

    internal sealed record Terminal(string Id, int ShellPid, int ClientPid);
    private sealed record Owner(Terminal Terminal, DateTime Started, long InputVersion);
    private sealed class Proof(string root, string thread, string nonce, DateTimeOffset expires,
        string topology, Dictionary<string, Owner> candidates)
    {
        public string Root { get; } = root;
        public string Thread { get; } = thread;
        public string Nonce { get; } = nonce;
        public DateTimeOffset Expires { get; } = expires;
        public string Topology { get; } = topology;
        public Dictionary<string, Owner> Candidates { get; } = candidates;
        public HashSet<string> Observers { get; } = new(StringComparer.Ordinal);
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
                if (!proof.Candidates.TryGetValue(sessionId, out var candidate)) return;
                if (candidate.InputVersion != _inputVersions.GetValueOrDefault(sessionId)) return;
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
            if (caller is null) return Error("INVALID_PROCESS", "The calling tool process no longer exists.");
            var owners = GetOwners();
            var topology = Topology(owners);
            var key = root + "/" + thread;
            _contexts.TryGetValue(key, out var proof);
            if (fresh || proof is null || proof.Expires <= _clock.GetUtcNow() || proof.Topology != topology ||
                proof.Observers.Any(id => !owners.TryGetValue(id, out var owner) || !SameOwner(proof.Candidates[id], owner)))
            {
                proof = NewProof(root, thread, topology, owners);
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
                : "Retry the exact original command in a separate shell tool call. The proof above must be displayed in your terminal first. If tool output is hidden, print ONLY the proof line in a separate tool call, then retry.";
            if (root != thread)
                instructions += " If this is a subagent without its own terminal output, ask the parent agent to perform the scoped operation; do not overwrite CODEX_SESSION_ID or CODEX_THREAD_ID.";
            return MarkerPrefix + proof.Nonce + "\n" + Error(status, instructions) +
                $"\nCodex root={root}; thread={thread}; matched terminals={proof.Observers.Count}.";
        }
    }

    internal string CreateHostLease(string sessionId, int callerPid)
    {
        lock (_gate)
        {
            Prune();
            var caller = _process(callerPid);
            if (caller is null) return Error("INVALID_PROCESS", "The calling tool process no longer exists.");
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
            if (!_leases.TryGetValue(token, out var lease) || lease.Expires <= _clock.GetUtcNow())
                return Error("EXPIRED", "Run mt_context, then retry the original command.");
            var caller = _process(lease.CallerPid);
            if (caller is null || caller.Value.Started != lease.CallerStarted)
                return Error("INVALID_PROCESS", "The original tool process ended or its PID was reused. Run the original command in the current tool shell.");
            if (lease.Proof is not { } proof) return null;
            var owners = GetOwners();
            if (!_contexts.TryGetValue(proof.Root + "/" + proof.Thread, out var currentProof) || currentProof != proof ||
                proof.Expires <= _clock.GetUtcNow() || proof.Topology != Topology(owners) ||
                proof.Observers.Count != 1 || !owners.TryGetValue(lease.SessionId, out var owner) ||
                lease.Owner is null || !SameOwner(lease.Owner, owner))
                return Error("CHANGED", "The terminal input, client, or proof changed. Run mt_context repair, then retry the original command. Do not guess a session ID.");
            return null;
        }
    }

    internal static bool IsResolution(string value) => value.Length == 41 && value[8] == ':' &&
        value[..8].All(char.IsAsciiLetterOrDigit) && value[9..].All(char.IsAsciiHexDigit);

    internal static string Error(string status, string instruction) =>
        $"TLBX_CONTEXT_{status}: No session operation was executed.\n{instruction}\nNever infer ownership from cwd, terminal title, or inherited MT_SESSION_ID.";

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

    private static bool SameOwner(Owner left, Owner right) => left.Terminal == right.Terminal &&
        left.Started == right.Started && left.InputVersion == right.InputVersion;

    private static string Topology(Dictionary<string, Owner> owners) => string.Join(';', owners.OrderBy(pair => pair.Key,
        StringComparer.Ordinal).Select(pair => string.Create(CultureInfo.InvariantCulture,
            $"{pair.Key}:{pair.Value.Terminal.ShellPid}:{pair.Value.Terminal.ClientPid}:{pair.Value.Started.Ticks}:{pair.Value.InputVersion}")));

    private Proof NewProof(string root, string thread, string topology, Dictionary<string, Owner> owners)
    {
        var nonce = RandomToken();
        var proof = new Proof(root, thread, nonce, _clock.GetUtcNow().AddMinutes(5), topology, owners);
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
        private readonly char[] _tail = new char[40];
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
                _tail[_next] = (char)b;
                _next = (_next + 1) % 40;
                _count = Math.Min(_count + 1, 40);
                if (_count == 40 && HasPrefix())
                {
                    var valid = true;
                    for (var i = 0; i < nonce.Length; i++)
                    {
                        nonce[i] = _tail[(_next + 8 + i) % 40];
                        if (!char.IsAsciiHexDigit(nonce[i])) { valid = false; break; }
                    }
                    if (valid) found(new string(nonce).ToLowerInvariant());
                }
            }
        }

        private bool HasPrefix()
        {
            for (var i = 0; i < MarkerPrefix.Length; i++)
                if (_tail[(_next + i) % 40] != MarkerPrefix[i]) return false;
            return true;
        }
    }
}
