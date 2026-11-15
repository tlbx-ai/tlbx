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
        var lastOutputAt = appServerControlHeat.IsDedicatedSession ? null : telemetry.LastOutputAt;
        var lastTextOutputAt = appServerControlHeat.IsDedicatedSession ? null : telemetry.LastTextOutputAt;
        if (appServerControlHeat.LastActivityAt is { } activityAt)
        {
            if (lastOutputAt is null || activityAt > lastOutputAt) lastOutputAt = activityAt;
            if (lastTextOutputAt is null || activityAt > lastTextOutputAt) lastTextOutputAt = activityAt;
        }

        return new SessionHeatSnapshot
        {
            TotalOutputBytes = telemetry.TotalOutputBytes,
            TotalInputBytes = telemetry.TotalInputBytes,
            TotalBellCount = telemetry.TotalBellCount,
            LastInputAt = telemetry.LastInputAt,
            LastOutputAt = lastOutputAt,
            LastTextOutputAt = lastTextOutputAt,
            LastBellAt = telemetry.LastBellAt,
            CurrentBytesPerSecond = telemetry.CurrentBytesPerSecond,
            CurrentHeat = TerminalHeat.FromTextOutput(lastTextOutputAt, _timeProvider.GetUtcNow())
        };
    }
}
