using System.Globalization;
using System.Runtime.InteropServices;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Power;

internal sealed class WindowsSystemSleepInhibitorBackend : ISystemSleepInhibitorBackend
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly object _lock = new();
    private Thread? _workerThread;
    private ManualResetEventSlim? _releaseSignal;
    private bool _disposed;

    public bool Activate()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            if (_workerThread is not null && _workerThread.IsAlive)
            {
                return true;
            }

            DisposeStaleWorkerStateLocked();

            ManualResetEventSlim? readySignal = new(false);
            ManualResetEventSlim? releaseSignal = new(false);
            var state = new WindowsSleepInhibitorThreadState(readySignal, releaseSignal);
            var workerThread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "tlbx Sleep Inhibitor"
            };

            try
            {
                workerThread.Start(state);

                if (!readySignal.Wait(TimeSpan.FromSeconds(2)))
                {
                    releaseSignal.Set();
                    workerThread.Join(TimeSpan.FromSeconds(1));
                    Log.Warn(() => "Timed out enabling Windows sleep inhibitor");
                    return false;
                }

                readySignal.Dispose();
                readySignal = null;

                if (!state.Succeeded)
                {
                    workerThread.Join(TimeSpan.FromSeconds(1));
                    Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"Windows sleep inhibitor failed with Win32 error {state.Win32Error}"));
                    return false;
                }

                _workerThread = workerThread;
                var previousReleaseSignal = Interlocked.Exchange(ref _releaseSignal, releaseSignal);
                previousReleaseSignal?.Dispose();
                releaseSignal = null;
                return true;
            }
            finally
            {
                readySignal?.Dispose();
                releaseSignal?.Dispose();
            }
        }
    }

    public void Deactivate()
    {
        Thread? workerThread;
        ManualResetEventSlim? releaseSignal;

        lock (_lock)
        {
            workerThread = Interlocked.Exchange(ref _workerThread, null);
            releaseSignal = Interlocked.Exchange(ref _releaseSignal, null);
        }

        if (releaseSignal is null)
        {
            return;
        }

        try
        {
            releaseSignal.Set();
            workerThread?.Join(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseSignal.Dispose();
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
        }

        Deactivate();
    }

    private static void ThreadMain(object? stateObj)
    {
        var state = (WindowsSleepInhibitorThreadState)stateObj!;
        try
        {
            state.Succeeded = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
            if (!state.Succeeded)
            {
                state.Win32Error = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            state.ReadySignal.Set();
        }

        if (!state.Succeeded)
        {
            return;
        }

        state.ReleaseSignal.Wait();
        _ = SetThreadExecutionState(EsContinuous);
    }

    private void DisposeStaleWorkerStateLocked()
    {
        if (_workerThread is not null && _workerThread.IsAlive)
        {
            return;
        }

        _workerThread = null;

        var staleReleaseSignal = Interlocked.Exchange(ref _releaseSignal, null);
        staleReleaseSignal?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private sealed class WindowsSleepInhibitorThreadState
    {
        public WindowsSleepInhibitorThreadState(
            ManualResetEventSlim readySignal,
            ManualResetEventSlim releaseSignal)
        {
            ReadySignal = readySignal;
            ReleaseSignal = releaseSignal;
        }

        public ManualResetEventSlim ReadySignal { get; }
        public ManualResetEventSlim ReleaseSignal { get; }
        public bool Succeeded { get; set; }
        public int Win32Error { get; set; }
    }
}
