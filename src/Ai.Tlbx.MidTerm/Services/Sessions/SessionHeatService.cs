namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class SessionHeatService
{
    private readonly SessionTelemetryService _telemetryService;
    private readonly ISessionAppServerControlHeatSource _appServerControlHeatSource;
    private readonly TimeProvider _timeProvider;

    public SessionHeatService(
        SessionTelemetryService telemetryService,
        ISessionAppServerControlHeatSource appServerControlHeatSource,
        TimeProvider? timeProvider = null)
    {
        _telemetryService = telemetryService;
        _appServerControlHeatSource = appServerControlHeatSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SessionHeatSnapshot GetSnapshot(string sessionId)
    {
        var telemetry = _telemetryService.GetSnapshot(sessionId);
        var appServerControlHeat = _appServerControlHeatSource.GetHeatSnapshot(sessionId);
        var currentHeat = Math.Max(telemetry.CurrentHeat, appServerControlHeat.CurrentHeat);
        var lastOutputAt = telemetry.LastOutputAt;
        if (appServerControlHeat.CurrentHeat > 0)
        {
            lastOutputAt = _timeProvider.GetUtcNow();
        }

        return new SessionHeatSnapshot
        {
            TotalOutputBytes = telemetry.TotalOutputBytes,
            TotalInputBytes = telemetry.TotalInputBytes,
            TotalBellCount = telemetry.TotalBellCount,
            LastInputAt = telemetry.LastInputAt,
            LastOutputAt = lastOutputAt,
            LastTextOutputAt = appServerControlHeat.CurrentHeat > 0 ? lastOutputAt : telemetry.LastTextOutputAt,
            LastBellAt = telemetry.LastBellAt,
            CurrentBytesPerSecond = telemetry.CurrentBytesPerSecond,
            CurrentHeat = currentHeat
        };
    }
}
