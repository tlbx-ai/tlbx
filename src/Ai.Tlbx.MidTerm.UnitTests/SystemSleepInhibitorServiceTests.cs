using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Power;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SystemSleepInhibitorServiceTests
{
    [Fact]
    public void UpdateSessionCount_ActivatesOnlyWhenEnabledAndSessionsExist()
    {
        using var backend = new FakeSystemSleepInhibitorBackend();
        using var service = new SystemSleepInhibitorService(backend);

        service.UpdateSessionCount(1);
        Assert.Equal(0, backend.ActivateCalls);

        service.UpdateEnabled(true);
        Assert.Equal(1, backend.ActivateCalls);

        service.UpdateSessionCount(3);
        Assert.Equal(1, backend.ActivateCalls);

        service.UpdateSessionCount(0);
        Assert.Equal(1, backend.DeactivateCalls);
    }

    [Fact]
    public void Dispose_DeactivatesBackendOnce()
    {
        using var backend = new FakeSystemSleepInhibitorBackend();
        SystemSleepInhibitorService? service = new(backend);

        try
        {
            service.UpdateEnabled(true);
            service.UpdateSessionCount(1);
            var ownedService = service;
            service = null;
            ownedService.Dispose();
        }
        finally
        {
            service?.Dispose();
        }

        Assert.Equal(1, backend.ActivateCalls);
        Assert.Equal(1, backend.DeactivateCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    private sealed class FakeSystemSleepInhibitorBackend : ISystemSleepInhibitorBackend
    {
        public int ActivateCalls { get; private set; }
        public int DeactivateCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        private bool _disposed;

        public bool Activate()
        {
            ActivateCalls++;
            return true;
        }

        public void Deactivate()
        {
            DeactivateCalls++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCalls++;
        }
    }

}
