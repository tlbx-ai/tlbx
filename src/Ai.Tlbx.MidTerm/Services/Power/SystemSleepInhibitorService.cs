using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Power;

internal sealed class SystemSleepInhibitorService : IDisposable
{
    private readonly ISystemSleepInhibitorBackend? _ownedBackend;
    private readonly ISystemSleepInhibitorBackend? _injectedBackend;
    private readonly object _lock = new();
    private bool _enabled;
    private int _sessionCount;
    private bool _desiredInhibiting;
    private bool _inhibiting;
    private bool _disposed;

    public SystemSleepInhibitorService()
    {
        _ownedBackend = SystemSleepInhibitorBackendFactory.Create();
    }

    internal SystemSleepInhibitorService(ISystemSleepInhibitorBackend backend)
    {
        _injectedBackend = backend;
    }

    private ISystemSleepInhibitorBackend Backend => _ownedBackend ?? _injectedBackend!;

    public void UpdateEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _enabled = enabled;
            ReconcileStateLocked();
        }
    }

    public void UpdateSessionCount(int sessionCount)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _sessionCount = Math.Max(0, sessionCount);
            ReconcileStateLocked();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_inhibiting)
                {
                    Backend.Deactivate();
                    _inhibiting = false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "SystemSleepInhibitorService.Dispose");
            }
            finally
            {
                (_ownedBackend ?? _injectedBackend)?.Dispose();
            }
        }
    }

    private void ReconcileStateLocked()
    {
        var shouldInhibit = _enabled && _sessionCount > 0;
        if (shouldInhibit == _desiredInhibiting)
        {
            return;
        }

        _desiredInhibiting = shouldInhibit;

        if (shouldInhibit)
        {
            _inhibiting = Backend.Activate();
            if (_inhibiting)
            {
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Sleep inhibitor enabled for {_sessionCount} active session(s)"));
            }
            else
            {
                Log.Warn(() => "Failed to enable sleep inhibitor for active tlbx sessions");
            }

            return;
        }

        try
        {
            if (_inhibiting)
            {
                Backend.Deactivate();
                Log.Info(() => "Sleep inhibitor disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "SystemSleepInhibitorService.Deactivate");
        }
        finally
        {
            _inhibiting = false;
        }
    }
}
