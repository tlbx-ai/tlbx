using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

/// <summary>Host-owned MCP epoch, retained while Web is disconnected.</summary>
internal sealed class AppServerControlMcpRuntimeScope
{
    private readonly Lock _gate = new();
    private string _identity = Guid.NewGuid().ToString("N");
    private string? _threadId;
    private bool _started;
    private string? _backendIdentity;

    internal string Identity { get { lock (_gate) return _identity; } }
    internal string? ThreadId { get { lock (_gate) return _threadId; } }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _identity = Guid.NewGuid().ToString("N");
            _started = false;
            _threadId = null;
        }
    }

    internal void Prepare(string? threadId, bool startsNewThread, string? backendIdentity = null)
    {
        lock (_gate)
        {
            if (_started && (startsNewThread || (_backendIdentity is not null && backendIdentity is not null &&
                !string.Equals(_backendIdentity, backendIdentity, StringComparison.Ordinal)) || (!string.IsNullOrEmpty(threadId) &&
                !string.Equals(_threadId, threadId, StringComparison.Ordinal))))
            {
                _identity = Guid.NewGuid().ToString("N");
                _started = false;
                _threadId = threadId;
            }
            if (backendIdentity is not null) _backendIdentity = backendIdentity;
        }
    }

    internal void Observe(AppServerControlProviderEvent value)
    {
        lock (_gate)
        {
            var state = value.SessionState?.State;
            if (state is "error" or "stopped" || value.Type == "session.exited")
            {
                if (_started) _identity = Guid.NewGuid().ToString("N");
                _started = false;
                _threadId = null;
            }
            else if (state == "starting")
            {
                _started = true;
            }
            else if (state is "ready" or "idle") _started = true;

            var thread = value.ThreadState?.ProviderThreadId;
            if (!string.IsNullOrEmpty(thread))
            {
                if (_threadId is not null && !string.Equals(_threadId, thread, StringComparison.Ordinal))
                    _identity = Guid.NewGuid().ToString("N");
                _threadId = thread;
            }
        }
    }
}
