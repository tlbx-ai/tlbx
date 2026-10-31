using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class AcpAppServerControlAgentRuntime : IAppServerControlAgentRuntime
{
    private const int MaxInlineImageBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan AttachRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(15);

    private readonly Action<AppServerControlProviderEvent> _emit;
    private readonly string _provider;
    private readonly string _agentName;
    private readonly IReadOnlyList<string> _executableArguments;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _pendingPromptTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPermissionRequest> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AcpToolState> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _configOptionIds = new(StringComparer.Ordinal);
    private Process? _process;
    private StreamReader? _output;
    private StreamReader? _error;
    private StreamWriter? _input;
    private Task? _readerTask;
    private Task? _errorTask;
    private string? _sessionId;
    private string? _workingDirectory;
    private AppServerControlMcpServer? _mcpServer;
    private AppServerControlMcpCredentials? _mcpCredentials;
    private bool _disposingProcess;
    private string? _binaryPath;
    private string? _userProfileDirectory;
    private Dictionary<string, string?> _environmentVariables = new(StringComparer.Ordinal);
    private string? _authenticationMethodId;
    private string? _providerThreadId;
    private string? _activeTurnId;
    private string? _cancellingTurnId;
    private TaskCompletionSource? _activeTurnCompletion;
    private string? _activeTurnModel;
    private string? _activeTurnEffort;
    private string? _availableCommandsSignature;
    private string? _lastSessionNotificationSignature;
    private string? _currentModeId;
    private string? _planModeId;
    private string? _defaultModeId;
    private bool _supportsLegacyModels;
    private bool _supportsImagePrompts;
    private AppServerControlQuickSettingsSummary _quickSettings = new();
    private List<AppServerControlCommand> _availableCommands = [];
    private int _nextRequestId;
    private long _sequence;
    private bool _disposed;

    public AcpAppServerControlAgentRuntime(
        string provider,
        string? agentName,
        IReadOnlyList<string>? executableArguments,
        Action<AppServerControlProviderEvent> emit)
    {
        _provider = string.IsNullOrWhiteSpace(provider)
            ? throw new ArgumentException("ACP provider is required.", nameof(provider))
            : provider.Trim().ToLowerInvariant();
        _agentName = string.IsNullOrWhiteSpace(agentName) ? _provider : agentName.Trim();
        _executableArguments = executableArguments?.ToArray() ?? [];
        _emit = providerEvent =>
        {
            lock (_replayGate)
            {
                if (_replayEvents is not null)
                {
                    _replayEvents.Add(providerEvent);
                    return;
                }
            }

            emit(providerEvent);
        };
    }

    public string Provider => _provider;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        await DisposeProcessAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _writeGate.Dispose();
        _gate.Dispose();
    }

    public async Task<HostCommandOutcome> ExecuteAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return command.Type switch
            {
                "runtime.attach" => await AttachAsync(command, ct).ConfigureAwait(false),
                "turn.start" => await StartTurnAsync(command, ct).ConfigureAwait(false),
                "turn.steer" => await SteerTurnAsync(command, ct).ConfigureAwait(false),
                "turn.interrupt" => await InterruptTurnAsync(command, ct).ConfigureAwait(false),
                "thread.compact" => await CompactViaNativeCommandAsync(command, ct).ConfigureAwait(false),
                "request.resolve" => await ResolvePermissionRequestAsync(command, ct).ConfigureAwait(false),
                "user-input.resolve" => await ResolveUserInputAsync(command, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported ACP command '{command.Type}' for {_agentName}.")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostCommandOutcome> AttachAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var attach = command.AttachRuntime ?? throw new InvalidOperationException("runtime.attach payload is required.");
        if (!string.Equals(attach.Provider, Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ACP runtime for '{Provider}' cannot attach provider '{attach.Provider}'.");
        }

        if (string.IsNullOrWhiteSpace(attach.WorkingDirectory) || !Directory.Exists(attach.WorkingDirectory))
        {
            throw new InvalidOperationException($"{_agentName} ACP working directory is required.");
        }

        var binaryPath = attach.ExecutablePath;
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            throw new InvalidOperationException($"{_agentName} ACP executable was not found.");
        }

        if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(_providerThreadId))
        {
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(command.SessionId, _sessionId, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(attach.WorkingDirectory), Path.GetFullPath(_workingDirectory!), pathComparison) ||
                !string.Equals(Path.GetFullPath(binaryPath), Path.GetFullPath(_binaryPath!), pathComparison) ||
                !(attach.ExecutableArguments ?? []).SequenceEqual(_executableArguments, StringComparer.Ordinal) ||
                !string.Equals(attach.AuthenticationMethodId, _authenticationMethodId, StringComparison.Ordinal) ||
                !attach.EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual(_environmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(attach.ResumeThreadId) &&
                 !string.Equals(attach.ResumeThreadId, _providerThreadId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"{_agentName} already owns a different ACP attachment; its live conversation was preserved.");
            }

            // mt reconnects to this persistent host after a web-server restart.
            // Its ownership handshake must not create another provider conversation.
            return Accepted(command.CommandId, command.SessionId);
        }

        await DisposeProcessAsync().ConfigureAwait(false);

        _sessionId = command.SessionId;
        _workingDirectory = attach.WorkingDirectory;
        if (attach.McpServer is not null)
        {
            _mcpCredentials?.Dispose();
            _mcpCredentials = new AppServerControlMcpCredentials(attach.McpServer);
            _mcpServer = _mcpCredentials.Server;
        }
        else _mcpServer = null;
        _binaryPath = binaryPath;
        _userProfileDirectory = attach.UserProfileDirectory;
        _environmentVariables = new Dictionary<string, string?>(attach.EnvironmentVariables, StringComparer.Ordinal);
        _authenticationMethodId = attach.AuthenticationMethodId;
        _providerThreadId = null;
        _quickSettings = CreateDefaultQuickSettings();
        ResetProtocolState();

        try
        {
            await StartAcpProcessAsync(ct).ConfigureAwait(false);

            var initializeResult = await SendRequestAsync(
                    "initialize",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("protocolVersion", 2);
                        writer.WritePropertyName("capabilities");
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                        writer.WritePropertyName("info");
                        writer.WriteStartObject();
                        writer.WriteString("name", "tlbx");
                        writer.WriteString("version", "dev");
                        writer.WriteEndObject();
                        writer.WritePropertyName("clientCapabilities");
                        writer.WriteStartObject();
                        writer.WritePropertyName("session");
                        writer.WriteStartObject();
                        writer.WritePropertyName("configOptions");
                        writer.WriteStartObject();
                        writer.WriteBoolean("boolean", true);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                        writer.WritePropertyName("fs");
                        writer.WriteStartObject();
                        writer.WriteBoolean("readTextFile", false);
                        writer.WriteBoolean("writeTextFile", false);
                        writer.WriteEndObject();
                        writer.WriteBoolean("terminal", false);
                        writer.WriteEndObject();
                        writer.WritePropertyName("clientInfo");
                        writer.WriteStartObject();
                        writer.WriteString("name", "tlbx");
                        writer.WriteString("title", "tlbx");
                        writer.WriteString("version", "dev");
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    },
                    AttachRequestTimeout,
                    ct)
                .ConfigureAwait(false);
            _protocolGeneration = ResolveProtocolGeneration(initializeResult);
            if (_mcpServer is not null && _protocolGeneration == 2 &&
                Traverse(initializeResult, "capabilities", "session", "mcp", "stdio") is not { ValueKind: JsonValueKind.Object })
                throw new InvalidOperationException($"{_agentName} does not advertise ACP session.mcp.stdio; tlbx MCP cannot be configured for this runtime.");
            await AuthenticateAcpAsync(initializeResult, ct).ConfigureAwait(false);
            _supportsImagePrompts = _protocolGeneration == 2
                ? Traverse(initializeResult, "capabilities", "session", "prompt", "image") is { ValueKind: JsonValueKind.Object }
                : Traverse(initializeResult, "agentCapabilities", "promptCapabilities", "image") is
                {
                    ValueKind: JsonValueKind.True
                };

            var resumeThreadId = string.IsNullOrWhiteSpace(attach.ResumeThreadId) ? null : attach.ResumeThreadId.Trim();
            var sessionMethod = ResolveSessionOpenMethod(initializeResult, resumeThreadId);
            _providerThreadId = resumeThreadId;
            var newSessionResult = await OpenSessionAsync(sessionMethod, resumeThreadId, ct).ConfigureAwait(false);

            _providerThreadId = resumeThreadId ?? GetString(newSessionResult, "sessionId")
                ?? throw new InvalidOperationException($"{_agentName} did not return an ACP session id.");
            _quickSettings = CreateQuickSettingsFromState(initializeResult, newSessionResult);

            var events = new List<AppServerControlProviderEvent>
        {
                CreateEvent("agent.metadata.updated", null, null, null, "acp", sessionMethod, newSessionResult, e =>
                {
                    _availableCommands = ReadAvailableCommands(newSessionResult);
                    e.AgentMetadataUpdated = new AppServerControlProviderAgentMetadataPayload
                    {
                        AgentName = _agentName,
                        AvailableCommands = AppServerControlCommand.CloneMany(_availableCommands)
                    };
                }),
                CreateEvent("session.started", null, null, null, "mtagenthost.acp", "runtime.attach", attach, appServerControlEvent =>
                {
                    appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                    {
                        State = "starting",
                        StateLabel = "Starting",
                        Reason = $"{_agentName} ACP runtime attached."
                    };
                }),
                CreateEvent("thread.started", null, null, null, "acp", sessionMethod, newSessionResult, appServerControlEvent =>
                {
                    appServerControlEvent.ThreadState = new AppServerControlProviderThreadStatePayload
                    {
                        State = "active",
                        StateLabel = "Active",
                        ProviderThreadId = _providerThreadId
                    };
                })
        };
            AppendLoadedHistory(events);
            await AppendLoadedRequestsAsync(events, ct).ConfigureAwait(false);
            lock (_replayGate)
            {
                if (_protocolGeneration == 1 || _foregroundState == "idle")
                    events.Add(CreateEvent("session.ready", null, null, null, "acp", sessionMethod, newSessionResult, appServerControlEvent =>
                        {
                            appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                            {
                                State = "ready",
                                StateLabel = "Ready",
                                Reason = $"{_agentName} ACP runtime is ready for the next turn."
                            };
                        }));
                events.Add(CreateQuickSettingsUpdatedEvent(_quickSettings, "acp", sessionMethod, newSessionResult));
                _replayEvents = null;
                foreach (var providerEvent in events) _emit(providerEvent);
            }
            return Accepted(command.CommandId, command.SessionId);
        }
        catch
        {
            lock (_replayGate)
            {
                _loadedUpdates = null;
                _replayEvents = null;
            }
            await DisposeProcessAsync().ConfigureAwait(false);
            _providerThreadId = null;
            throw;
        }
    }

    private async Task<HostCommandOutcome> StartTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!string.IsNullOrWhiteSpace(_activeTurnId))
        {
            throw new InvalidOperationException($"{_agentName} already has an active ACP turn.");
        }

        var request = command.StartTurn ?? throw new InvalidOperationException("turn.start payload is required.");
        var quickSettings = ResolveRequestedQuickSettings(request);
        ValidateRequestedConfigValues(request.ConfigValues);
        await ApplyRequestedQuickSettingsAsync(quickSettings, ct).ConfigureAwait(false);
        await ApplyRequestedConfigValuesAsync(request.ConfigValues, ct).ConfigureAwait(false);
        var promptBlocks = BuildPromptBlocks(
            request,
            _planModeId is null ? quickSettings.PlanMode : AppServerControlQuickSettings.PlanModeOff);
        if (promptBlocks.Count == 0)
        {
            throw new InvalidOperationException("App Server Controller turn input must include text or attachments.");
        }

        var turnId = "turn-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _activeTurnId = turnId;
        _activeTurnCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _quickSettings = MergeRequestedQuickSettings(quickSettings);
        _activeTurnModel = _quickSettings.Model;
        _activeTurnEffort = _quickSettings.Effort;
        if (_protocolGeneration == 1) _tools.Clear();

        var requestId = NextRequestId();
        _pendingPromptTurns[requestId] = turnId;
        foreach (var appServerControlEvent in CreateTurnStartEvents(request))
        {
            _emit(appServerControlEvent);
        }

        try
        {
            await WriteJsonRpcRequestAsync(
                    requestId,
                    "session/prompt",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("sessionId", _providerThreadId);
                        writer.WritePropertyName("prompt");
                        WritePromptBlocks(writer, promptBlocks);
                        writer.WriteEndObject();
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            _pendingPromptTurns.TryRemove(requestId, out _);
            CompleteTurn(
                turnId,
                "failed",
                "Failed",
                "send_failed",
                $"Failed to send {_agentName} ACP prompt.",
                string.Empty);
            ResetTurnState();
            throw;
        }

        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Status = "accepted",
                Accepted = new AppServerControlCommandAcceptedResponse
                {
                    SessionId = command.SessionId,
                    Status = "accepted",
                    TurnId = turnId
                },
                TurnStarted = new AppServerControlTurnStartResponse
                {
                    SessionId = command.SessionId,
                    Provider = Provider,
                    ThreadId = _providerThreadId ?? _sessionId ?? command.SessionId,
                    TurnId = turnId,
                    Status = "accepted",
                    QuickSettings = new AppServerControlQuickSettingsSummary
                    {
                        Model = _quickSettings.Model,
                        Effort = _quickSettings.Effort,
                        PlanMode = _quickSettings.PlanMode,
                        PermissionMode = _quickSettings.PermissionMode,
                        ModelOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.ModelOptions),
                        EffortOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.EffortOptions),
                        ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(_quickSettings.ConfigOptions)
                    }
                }
            },
            Events = []
        };
    }

    private AppServerControlProviderEvent[] CreateTurnStartEvents(AppServerControlTurnRequest request)
    {
        return
        [
            CreateQuickSettingsUpdatedEvent(_quickSettings, "midterm.appServerControl", "turn.start", request),
            CreateEvent("session.state.changed", _activeTurnId, null, null, "acp", "session/prompt", request, appServerControlEvent =>
            {
                appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "running",
                    StateLabel = "Running",
                    Reason = $"{_agentName} ACP turn started."
                };
            }),
            CreateEvent("turn.started", _activeTurnId, null, null, "acp", "session/prompt", request, appServerControlEvent =>
            {
                appServerControlEvent.TurnStarted = new AppServerControlProviderTurnStartedPayload
                {
                    Model = _activeTurnModel,
                    Effort = _activeTurnEffort
                };
            })
        ];
    }

    private Task<HostCommandOutcome> CompactViaNativeCommandAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        if (!_availableCommands.Any(native => native.Name.TrimStart('/').Equals("compact", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{_agentName} does not advertise a native /compact command.");
        return StartTurnAsync(new AppServerControlHostCommandEnvelope
        {
            CommandId = command.CommandId, SessionId = command.SessionId, Type = "turn.start",
            StartTurn = new AppServerControlTurnRequest { Text = "/compact" }
        }, ct);
    }

    private async Task<HostCommandOutcome> SteerTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var request = command.SteerTurn ?? throw new InvalidOperationException("turn.steer payload is required.");
        if (string.IsNullOrWhiteSpace(_activeTurnId) ||
            !string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{_agentName} steer target no longer matches the active turn.");
        }

        var replacement = new AppServerControlTurnRequest
        {
            Text = request.Text,
            Attachments = request.Attachments
        };
        // Validate input before stopping useful work. ACP v1 steering is an explicit
        // cancel-and-reprompt within the same provider conversation, not a native steer RPC.
        if (BuildPromptBlocks(replacement, _quickSettings.PlanMode).Count == 0)
        {
            throw new InvalidOperationException("ACP steer input must include text or attachments.");
        }

        await InterruptTurnAsync(new AppServerControlHostCommandEnvelope
        {
            CommandId = command.CommandId,
            SessionId = command.SessionId,
            Type = "turn.interrupt",
            InterruptTurn = new AppServerControlInterruptRequest { TurnId = request.ExpectedTurnId }
        }, ct).ConfigureAwait(false);

        return await StartTurnAsync(new AppServerControlHostCommandEnvelope
        {
            CommandId = command.CommandId,
            SessionId = command.SessionId,
            Type = "turn.start",
            StartTurn = replacement
        }, ct).ConfigureAwait(false);
    }

    private async Task<HostCommandOutcome> InterruptTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        var turnId = string.IsNullOrWhiteSpace(command.InterruptTurn?.TurnId)
            ? _activeTurnId
            : command.InterruptTurn!.TurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException($"{_agentName} does not have an active turn to interrupt.");
        }

        if (!string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{_agentName} interrupt target no longer matches the active turn.");
        }

        var completion = _activeTurnCompletion?.Task
            ?? throw new InvalidOperationException($"{_agentName} does not have an active prompt to cancel.");
        _cancellingTurnId = turnId;

        await WriteJsonRpcNotificationAsync(
                "session/cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", _providerThreadId);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);

        foreach (var pair in _pendingPermissions.Where(pair => string.Equals(pair.Value.TurnId, turnId, StringComparison.Ordinal)).ToList())
        {
            if (_pendingPermissions.TryRemove(pair.Key, out var pending))
            {
                await WritePermissionResponseAsync(pending.RpcId, null, cancelled: true, ct).ConfigureAwait(false);
                EmitCancelledRequest(pair.Key, pending.TurnId, "command_execution_approval");
            }
        }

        await CancelUserInputRequestsAsync(turnId, ct).ConfigureAwait(false);

        // ACP permits final updates until the original prompt response arrives.
        // Keep its turn identity alive and do not admit another prompt before that boundary.
        try
        {
            await completion.WaitAsync(CancelTimeout, ct).ConfigureAwait(false);
            EnsureAttached();
        }
        catch (TimeoutException)
        {
            await DisposeProcessAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"{_agentName} did not confirm ACP cancellation; its runtime was stopped to prevent overlapping prompts.");
        }

        return Accepted(
            command.CommandId,
            command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                TurnId = turnId
            });
    }

    private async Task<HostCommandOutcome> ResolvePermissionRequestAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveRequest ?? throw new InvalidOperationException("request.resolve payload is required.");
        if (!_pendingPermissions.TryRemove(request.RequestId, out var pending))
        {
            throw new InvalidOperationException($"{_agentName} ACP permission request was not found: {request.RequestId}");
        }

        var selectedOption = SelectPermissionOption(pending.Options, request.Decision);
        if (string.IsNullOrWhiteSpace(selectedOption))
        {
            await WritePermissionResponseAsync(pending.RpcId, null, cancelled: true, ct).ConfigureAwait(false);
        }
        else
        {
            await WritePermissionResponseAsync(pending.RpcId, selectedOption, cancelled: false, ct).ConfigureAwait(false);
        }

        return Accepted(
            command.CommandId,
            command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = request.RequestId
            },
            events:
            [
                CreateEvent("request.resolved", pending.TurnId, null, request.RequestId, "acp", "session/request_permission", request, appServerControlEvent =>
                {
                    appServerControlEvent.RequestResolved = new AppServerControlProviderRequestResolvedPayload
                    {
                        RequestType = "command_execution_approval",
                        Decision = request.Decision
                    };
                })
            ]);
    }

    private async Task StartAcpProcessAsync(CancellationToken ct)
    {
        var startInfo = CreateProcessStartInfo(
            _binaryPath!,
            BuildArguments(),
            _workingDirectory!);

        Process? process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            AppServerControlProviderRuntimeConfiguration.ApplyUserProfileEnvironment(process.StartInfo, _userProfileDirectory);
            AppServerControlProviderRuntimeConfiguration.ApplyEnvironmentVariables(process.StartInfo, Provider);
            foreach (var pair in _environmentVariables)
            {
                if (pair.Value is null) process.StartInfo.Environment.Remove(pair.Key);
                else process.StartInfo.Environment[pair.Key] = pair.Value;
            }
            process.StartInfo.Environment.Remove("FORCE_COLOR");
            process.StartInfo.Environment["NO_COLOR"] = "1";
            if (!process.Start())
            {
                throw new InvalidOperationException($"{_agentName} ACP process could not be started.");
            }

            var startedProcess = process;
#pragma warning disable IDISP003
            process = null;
            _process = startedProcess;
#pragma warning restore IDISP003
            _output = startedProcess.StandardOutput;
            _error = startedProcess.StandardError;
            _input = startedProcess.StandardInput;
            _readerTask = Task.Run(() => ReadLoopAsync(startedProcess, CancellationToken.None), CancellationToken.None);
            _errorTask = Task.Run(() => ReadErrorLoopAsync(CancellationToken.None), CancellationToken.None);
        }
        finally
        {
            process?.Dispose();
        }

        await Task.CompletedTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _output is not null)
            {
                var line = await _output.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    await HandleAcpLineAsync(line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EmitRuntimeMessage("runtime.error", $"{_agentName} ACP stream failed.", ex.Message);
        }
        finally
        {
            await FinalizeExitAsync(process).ConfigureAwait(false);
        }
    }

    internal static bool IsInformationalAcpDiagnostic(string line) =>
        line.Length > 5 && line[0] == 'I' && line[5] == ' ' &&
        int.TryParse(line.AsSpan(1, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
        !line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) &&
        !line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) &&
        !line.Contains("Traceback", StringComparison.OrdinalIgnoreCase);

    private async Task ReadErrorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _error is not null)
            {
                var line = await _error.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    var diagnostic = line.Trim();
                    var informational = IsInformationalAcpDiagnostic(diagnostic);
                    EmitRuntimeMessage(informational ? "runtime.notice" : "runtime.warning", diagnostic, diagnostic,
                        timelineVisible: !informational);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task HandleAcpLineAsync(string line, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && !root.TryGetProperty("method", out _))
        {
            HandleJsonRpcResponse(idElement.ToString(), root, line);
            return;
        }

        var method = GetString(root, "method");
        lock (_replayGate)
        {
            if (_loadedUpdates is not null && root.TryGetProperty("id", out _))
            {
                _loadedRequests.Add(line);
                return;
            }
        }
        switch (method)
        {
            case "session/update":
            case "x.ai/session/update":
            case "_x.ai/session/update":
                HandleSessionUpdate(root, line);
                break;
            case "x.ai/session_notification":
            case "_x.ai/session_notification":
                HandleSessionNotification(root, line, method);
                break;
            case "session/request_permission":
                await HandlePermissionRequestAsync(root, line, ct).ConfigureAwait(false);
                break;
            case "x.ai/ask_user_question":
            case "_x.ai/ask_user_question":
                await HandleGrokUserInputRequestAsync(root, line, ct).ConfigureAwait(false);
                break;
            case "x.ai/exit_plan_mode":
            case "_x.ai/exit_plan_mode":
                await HandleGrokExitPlanModeAsync(root, line, ct).ConfigureAwait(false);
                break;
            default:
                if (root.TryGetProperty("id", out var requestId))
                {
                    await WriteJsonRpcErrorAsync(requestId.Clone(), -32601, $"Unsupported ACP method '{method ?? "(null)"}'.", ct).ConfigureAwait(false);
                }
                else
                {
                    EmitRuntimeMessage("runtime.notice", $"{_agentName} ACP notification ignored.", method ?? line, timelineVisible: false);
                }

                break;
        }
    }

    private void HandleJsonRpcResponse(string id, JsonElement root, string rawLine)
    {
        if (_pendingRequests.TryRemove(id, out var pending))
        {
            if (root.TryGetProperty("error", out var error))
            {
                pending.TrySetException(new InvalidOperationException(ReadErrorMessage(error)));
                return;
            }

            pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }

        if (_pendingPromptTurns.TryRemove(id, out var turnId))
        {
            if (_protocolGeneration == 2)
            {
                HandleV2PromptResponse(turnId, root, rawLine);
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                CompleteTurn(turnId, "failed", "Failed", "error", ReadErrorMessage(error), rawLine);
                return;
            }

            var stopReason = GetString(root, "result", "stopReason") ?? "end_turn";
            var state = string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ? "interrupted" : "completed";
            var stateLabel = state == "interrupted" ? "Interrupted" : "Completed";
            CompleteTurn(turnId, state, stateLabel, stopReason, null, rawLine);
        }
    }

    private void HandleSessionUpdate(JsonElement root, string rawLine)
    {
        var nativeSessionId = GetString(root, "params", "sessionId");
        if (!string.IsNullOrWhiteSpace(_providerThreadId) && !string.IsNullOrWhiteSpace(nativeSessionId) &&
            !string.Equals(nativeSessionId, _providerThreadId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_replayGate)
        {
            if (_loadedUpdates is not null)
            {
                _loadedUpdates.Add((root.Clone(), rawLine));
                return;
            }
        }

        ApplySessionUpdate(root, rawLine, _activeTurnId);
    }

    private void ApplySessionUpdate(JsonElement root, string rawLine, string? turnId)
    {
        var update = Traverse(root, "params", "update");
        if (update is not { ValueKind: JsonValueKind.Object } updateElement)
        {
            return;
        }

        var updateType = GetString(updateElement, "sessionUpdate");
        if (_protocolGeneration == 2 && HandleV2Update(updateType, updateElement, root, rawLine, turnId))
        {
            return;
        }
        switch (updateType)
        {
            case "available_commands_update":
                HandleAvailableCommandsUpdate(updateElement, root, rawLine);
                return;
            case "config_option_update":
                HandleConfigOptionUpdate(updateElement, root, rawLine);
                return;
            case "current_mode_update":
                _currentModeId = GetString(updateElement, "currentModeId");
                return;
            case "usage_update":
                HandleUsageUpdate(updateElement, root, rawLine);
                return;
        }

        if (string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        switch (updateType)
        {
            case "agent_message_chunk":
                EmitContentDelta(turnId, "assistant_text", ExtractContentText(updateElement), "acp", updateType, root, rawLine);
                break;
            case "agent_thought_chunk":
                EmitContentDelta(turnId, "reasoning_text", ExtractContentText(updateElement), "acp", updateType, root, rawLine);
                break;
            case "plan":
                EmitPlan(turnId, updateElement, root, rawLine);
                break;
            case "tool_call":
                HandleToolCall(turnId, updateElement, root, rawLine);
                break;
            case "tool_call_update":
                HandleToolCallUpdate(turnId, updateElement, root, rawLine);
                break;
            default:
                if (!string.IsNullOrWhiteSpace(updateType))
                {
                    EmitRuntimeMessage("runtime.notice", $"{_agentName} ACP update ignored: {updateType}", rawLine, timelineVisible: false);
                }

                break;
        }
    }

    private void HandleAvailableCommandsUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        var catalogue = Traverse(update, "availableCommands");
        var hasCatalogue = catalogue is { ValueKind: JsonValueKind.Array };
        var commands = hasCatalogue ? ReadAvailableCommands(update) : _availableCommands;
        var tools = ReadMetaTools(update);
        if (!hasCatalogue && tools.Count == 0) return;

        var signature = Traverse(update, "availableCommands")?.GetRawText() + "\u001f" + string.Join('\n', tools);
        if (string.Equals(signature, _availableCommandsSignature, StringComparison.Ordinal))
        {
            return;
        }

        _availableCommandsSignature = signature;
        if (hasCatalogue)
        {
            _availableCommands = commands;
            _emit(CreateEvent("agent.metadata.updated", null, null, null, "acp", "available_commands_update", root,
                e => e.AgentMetadataUpdated = new AppServerControlProviderAgentMetadataPayload
                {
                    AvailableCommands = AppServerControlCommand.CloneMany(commands)
                }, rawLine));
        }
        var names = commands.Select(static command => command.Name).ToList();
        var message = commands.Count == 0
            ? $"{_agentName} ACP tools updated."
            : $"{_agentName} commands available: {FormatNamePreview(names)}.";
        var detail = BuildAvailableCommandsDetail(names, tools);
        EmitRuntimeMessage("agent.state", message, detail, "acp", "available_commands_update", root, rawLine, timelineVisible: false);
    }

    private void HandleSessionNotification(JsonElement root, string rawLine, string? method)
    {
        var message = BuildSessionNotificationMessage(root);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var signature = method + "\u001f" + message + "\u001f" + rawLine;
        if (string.Equals(signature, _lastSessionNotificationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastSessionNotificationSignature = signature;
        EmitRuntimeMessage("agent.state", message, rawLine, "acp", method, root, rawLine);
    }

    private async Task HandlePermissionRequestAsync(JsonElement root, string rawLine, CancellationToken ct)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var turnId = _activeTurnId;
        var nativeSessionId = GetString(root, "params", "sessionId");
        if (!string.Equals(nativeSessionId, _providerThreadId, StringComparison.Ordinal) ||
            (string.IsNullOrWhiteSpace(turnId) && _protocolGeneration == 1) ||
            (!string.IsNullOrWhiteSpace(turnId) && string.Equals(turnId, _cancellingTurnId, StringComparison.Ordinal)))
        {
            await WritePermissionResponseAsync(idElement.Clone(), null, cancelled: true, ct).ConfigureAwait(false);
            return;
        }

        var requestId = "acp-approval-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        turnId ??= $"acp-background:{requestId}";
        var toolCall = Traverse(root, "params", "toolCall");
        var options = ReadPermissionOptions(root);
        if (Provider == "antigravity" && GetString(toolCall, "toolCallId")?.StartsWith("interaction_", StringComparison.Ordinal) == true)
        {
            await HandleAcpChoiceQuestionAsync(root, idElement, turnId, options, rawLine, ct).ConfigureAwait(false);
            return;
        }
        var selectedOption = string.Equals(_quickSettings.PermissionMode, AppServerControlQuickSettings.PermissionModeAuto, StringComparison.Ordinal)
            ? SelectPermissionOption(options, "accept")
            : null;
        if (!string.IsNullOrWhiteSpace(selectedOption))
        {
            await WritePermissionResponseAsync(idElement.Clone(), selectedOption, cancelled: false, ct).ConfigureAwait(false);
            return;
        }

        var title = toolCall is { ValueKind: JsonValueKind.Object } toolElement
            ? GetString(toolElement, "title") ?? GetString(toolElement, "toolCallId") ?? $"{_agentName} tool call"
            : $"{_agentName} tool call";
        var detail = toolCall is { ValueKind: JsonValueKind.Object } detailElement
            ? detailElement.GetRawText()
            : rawLine;
        _pendingPermissions[requestId] = new PendingPermissionRequest(idElement.Clone(), turnId, options);
        _emit(CreateEvent("request.opened", turnId, null, requestId, "acp", "session/request_permission", root, appServerControlEvent =>
        {
            appServerControlEvent.RequestOpened = new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = "command_execution_approval",
                RequestTypeLabel = "Approval",
                Detail = title + Environment.NewLine + detail
            };
        }, rawLine));
    }

    private void HandleToolCall(string turnId, JsonElement update, JsonElement root, string rawLine)
    {
        var toolCallId = GetString(update, "toolCallId") ?? $"{Provider}-tool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var title = GetString(update, "title") ?? toolCallId;
        var kind = GetString(update, "kind");
        var status = NormalizeToolStatus(GetString(update, "status"));
        var itemType = NormalizeToolItemType(kind, title);
        var presentation = AppServerControlToolPresentationProjector.FromAcp(
            itemType,
            status,
            title,
            kind,
            Traverse(update, "rawInput"));
        var state = new AcpToolState
        {
            ItemId = toolCallId,
            ItemType = itemType,
            Title = title,
            Presentation = presentation
        };
        AppServerControlToolPresentationProjector.AppendAcpContent(Traverse(update, "content"), state.Output);
        AppServerControlToolPresentationProjector.ApplyOutput(presentation, state.Output, status);
        _tools[toolCallId] = state;

        _emit(CreateEvent(status == "completed" ? "item.completed" : "item.started", turnId, toolCallId, null, "acp", "tool_call", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                ItemType = state.ItemType,
                Status = status,
                Title = title,
                Detail = presentation.Subject,
                ToolPresentation = AppServerControlToolPresentationProjector.Clone(presentation)
            };
        }, rawLine));
        if (IsTerminalToolStatus(status))
        {
            _tools.Remove(toolCallId);
        }
    }

    private void HandleToolCallUpdate(string turnId, JsonElement update, JsonElement root, string rawLine, bool appendContent = false)
    {
        var toolCallId = GetString(update, "toolCallId") ?? $"{Provider}-tool-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (!_tools.TryGetValue(toolCallId, out var state))
        {
            state = new AcpToolState
            {
                ItemId = toolCallId,
                ItemType = NormalizeToolItemType(GetString(update, "kind"), GetString(update, "title")),
                Title = GetString(update, "title") ?? toolCallId,
                Presentation = AppServerControlToolPresentationProjector.FromAcp(
                    NormalizeToolItemType(GetString(update, "kind"), GetString(update, "title")),
                    "in_progress",
                    GetString(update, "title") ?? toolCallId,
                    GetString(update, "kind"),
                    Traverse(update, "rawInput"))
            };
            _tools[toolCallId] = state;
        }

        if (_protocolGeneration == 2)
        {
            if (update.TryGetProperty("title", out _)) state.Title = GetString(update, "title") ?? toolCallId;
            if (update.TryGetProperty("kind", out _)) state.ItemType = NormalizeToolItemType(GetString(update, "kind"), state.Title);
            if (!appendContent && update.TryGetProperty("content", out _)) state.Output = new();
            turnId = state.TurnId ??= turnId;
        }
        var status = _protocolGeneration == 2 && !update.TryGetProperty("status", out _)
            ? state.Status : NormalizeToolStatus(GetString(update, "status"));
        state.Status = status;
        var updatePresentation = AppServerControlToolPresentationProjector.FromAcp(
            state.ItemType,
            status,
            GetString(update, "title") ?? state.Title,
            GetString(update, "kind"),
            Traverse(update, "rawInput"));
        AppServerControlToolPresentationProjector.Merge(state.Presentation, updatePresentation);
        AppServerControlToolPresentationProjector.AppendAcpContent(Traverse(update, "content"), state.Output);
        AppServerControlToolPresentationProjector.ApplyOutput(state.Presentation, state.Output, status);
        var eventType = status is "completed" or "failed" or "cancelled" ? "item.completed" : "item.started";
        _emit(CreateEvent(eventType, turnId, state.ItemId, null, "acp", "tool_call_update", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                Replace = _protocolGeneration == 2,
                ItemType = state.ItemType,
                Status = status,
                Title = state.Title,
                Detail = state.Presentation.Subject,
                ToolPresentation = AppServerControlToolPresentationProjector.Clone(state.Presentation)
            };
        }, rawLine));

        var diff = ExtractDiff(update);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            _emit(CreateEvent("diff.updated", turnId, state.ItemId, null, "acp", "tool_call_update", root, appServerControlEvent =>
            {
                appServerControlEvent.DiffUpdated = new AppServerControlProviderDiffUpdatedPayload
                {
                    UnifiedDiff = diff
                };
            }, rawLine));
        }
        if (IsTerminalToolStatus(status) && _protocolGeneration == 1)
        {
            _tools.Remove(toolCallId);
        }
    }

    private void HandleConfigOptionUpdate(JsonElement update, JsonElement root, string rawLine)
    {
        var updated = ApplyConfigOptions(_quickSettings, Traverse(update, "configOptions"));
        if (updated is null)
        {
            return;
        }

        _quickSettings = updated;
        CaptureConfigOptions(Traverse(update, "configOptions"));
        _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "acp", "config_option_update", root, rawLine));
    }

    private void EmitContentDelta(
        string turnId,
        string streamKind,
        string delta,
        string source,
        string method,
        JsonElement root,
        string rawLine)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        _emit(CreateEvent("content.delta", turnId, null, null, source, method, root, appServerControlEvent =>
        {
            appServerControlEvent.ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = streamKind,
                Delta = delta
            };
        }, rawLine));
    }

    private void EmitPlan(string turnId, JsonElement update, JsonElement root, string rawLine)
    {
        var plan = BuildPlanMarkdown(update);
        if (string.IsNullOrWhiteSpace(plan))
        {
            return;
        }

        _lastPlanMarkdown = plan;

        _emit(CreateEvent("plan.completed", turnId, null, null, "acp", "plan", root, appServerControlEvent =>
        {
            appServerControlEvent.PlanCompleted = new AppServerControlProviderPlanCompletedPayload
            {
                PlanMarkdown = plan
            };
        }, rawLine));
    }

    private void CompleteTurn(string turnId, string state, string stateLabel, string? stopReason, string? errorMessage, string rawLine)
    {
        TaskCompletionSource? completion = null;
        if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
        {
            completion = _activeTurnCompletion;
            _activeTurnCompletion = null;
            ResetTurnState();
        }
        _emit(CreateEvent("turn.completed", turnId, null, null, "acp", "session/prompt", null, appServerControlEvent =>
        {
            appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = state,
                StateLabel = stateLabel,
                StopReason = stopReason,
                ErrorMessage = errorMessage
            };
        }, rawLine));
        _emit(CreateEvent("session.state.changed", turnId, null, null, "acp", "session/prompt", null, appServerControlEvent =>
        {
            appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
            {
                State = "ready",
                StateLabel = "Ready",
                Reason = state == "failed" ? $"{_agentName} turn failed." : $"{_agentName} turn completed."
            };
        }, rawLine));

        completion?.TrySetResult();
    }

    private async Task<JsonElement> SendRequestAsync(string method, Action<Utf8JsonWriter> writeParameters, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = NextRequestId();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = pending;
        try
        {
            await WriteJsonRpcRequestAsync(requestId, method, writeParameters, ct).ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task WriteJsonRpcRequestAsync(string id, string method, Action<Utf8JsonWriter> writeParameters, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("id", id);
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    writeParameters(writer);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonRpcNotificationAsync(string method, Action<Utf8JsonWriter> writeParameters, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    writeParameters(writer);
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WritePermissionResponseAsync(JsonElement id, string? optionId, bool cancelled, CancellationToken ct)
    {
        var outcome = cancelled || string.IsNullOrWhiteSpace(optionId)
            ? "cancelled"
            : "selected";
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("result");
                    writer.WriteStartObject();
                    writer.WritePropertyName("outcome");
                    writer.WriteStartObject();
                    writer.WriteString("outcome", outcome);
                    if (!cancelled && !string.IsNullOrWhiteSpace(optionId))
                    {
                        writer.WriteString("optionId", optionId);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonRpcErrorAsync(JsonElement id, int code, string message, CancellationToken ct)
    {
        await WriteJsonLineAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("error");
                    writer.WriteStartObject();
                    writer.WriteNumber("code", code);
                    writer.WriteString("message", message);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync(Action<Utf8JsonWriter> writePayload, CancellationToken ct)
    {
        var input = _input ?? throw new InvalidOperationException($"{_agentName} ACP process input stream is unavailable.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writePayload(writer);
            }

            await input.WriteLineAsync(Utf8NoBom.GetString(buffer.ToArray()).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task DisposeProcessAsync()
    {
        _disposingProcess = true;
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(new InvalidOperationException($"{_agentName} ACP runtime is shutting down."));
        }

        _pendingRequests.Clear();
        _pendingPromptTurns.Clear();
        _pendingPermissions.Clear();
        _pendingUserInputs.Clear();
        _pendingChoiceQuestions.Clear();

        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _error?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
        _error = null;

        if (_readerTask is not null)
        {
            await Task.WhenAny(_readerTask, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
        }

        if (_errorTask is not null)
        {
            await Task.WhenAny(_errorTask, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
        }

        _readerTask = null;
        _errorTask = null;
        _mcpCredentials?.Dispose();
        _mcpCredentials = null;
        _mcpServer = null;
        ResetTurnState();
        _disposingProcess = false;
    }

    private async Task FinalizeExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        if (!ReferenceEquals(_process, process))
        {
            return;
        }

        if (!_shutdown.IsCancellationRequested && !string.IsNullOrWhiteSpace(_activeTurnId))
        {
            var turnId = _activeTurnId;
            _emit(CreateEvent("turn.completed", turnId, null, null, "acp", "process.exit", new { process.ExitCode }, appServerControlEvent =>
            {
                appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                {
                    State = "failed",
                    StateLabel = "Failed",
                    StopReason = "process_exit",
                    ErrorMessage = $"{_agentName} exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                };
            }));
            EmitRuntimeMessage("runtime.error", $"{_agentName} ACP process exited unexpectedly.", $"Exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
            ResetTurnState();
        }

        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(new InvalidOperationException($"{_agentName} ACP connection closed."));
        }
        if (!_shutdown.IsCancellationRequested && !_disposingProcess)
            _emit(CreateEvent("session.stopped", null, null, null, "acp", "process.exit", null, value =>
            {
                value.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "stopped", StateLabel = "Stopped", Reason = $"{_agentName} ACP connection closed."
                };
            }));
    }

    private void EnsureAttached()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) ||
            string.IsNullOrWhiteSpace(_workingDirectory) ||
            string.IsNullOrWhiteSpace(_binaryPath) ||
            string.IsNullOrWhiteSpace(_providerThreadId) ||
            _process is null ||
            _process.HasExited)
        {
            throw new InvalidOperationException($"{_agentName} ACP runtime is not attached.");
        }
    }

    private string NextRequestId()
    {
        return "midterm-" + Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
    }

    private AppServerControlQuickSettingsSummary CreateDefaultQuickSettings()
    {
        const string defaultPermissionMode = AppServerControlQuickSettings.PermissionModeManual;
        return AppServerControlQuickSettings.CreateSummary(
            null,
            null,
            AppServerControlQuickSettings.PlanModeOff,
            defaultPermissionMode,
            defaultPermissionMode);
    }

    private AppServerControlQuickSettingsSummary ResolveRequestedQuickSettings(AppServerControlTurnRequest request)
    {
        var requestedModel = NormalizeOptional(request.Model);
        return AppServerControlQuickSettings.CreateSummary(
            requestedModel ?? _quickSettings.Model,
            request.Effort ?? _quickSettings.Effort,
            request.PlanMode ?? _quickSettings.PlanMode,
            request.PermissionMode ?? _quickSettings.PermissionMode,
            _quickSettings.PermissionMode);
    }

    private AppServerControlQuickSettingsSummary MergeRequestedQuickSettings(AppServerControlQuickSettingsSummary requested)
    {
        return new AppServerControlQuickSettingsSummary
        {
            Model = _quickSettings.Model,
            Effort = _quickSettings.Effort,
            PlanMode = requested.PlanMode,
            PermissionMode = requested.PermissionMode,
            ModelOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.ModelOptions),
            EffortOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.EffortOptions),
            ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(_quickSettings.ConfigOptions)
        };
    }

    private async Task ApplyRequestedQuickSettingsAsync(
        AppServerControlQuickSettingsSummary requested,
        CancellationToken ct)
    {
        // Validate all requested native changes before making the first RPC.
        // A local setting must never claim a provider accepted an unsupported value.
        var changesModel = !string.Equals(requested.Model, _quickSettings.Model, StringComparison.Ordinal) &&
                           !string.IsNullOrWhiteSpace(requested.Model);
        var changesEffort = !string.Equals(requested.Effort, _quickSettings.Effort, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(requested.Effort);
        if (changesModel)
        {
            if (!_configOptionIds.ContainsKey("model") && !_supportsLegacyModels)
                throw new InvalidOperationException($"{_agentName} does not support changing the ACP model.");
            ValidateNativeOption("model", requested.Model!, _quickSettings.ModelOptions);
        }
        if (changesEffort)
        {
            if (!_configOptionIds.ContainsKey("effort"))
                throw new InvalidOperationException($"{_agentName} does not expose an ACP reasoning effort setting.");
            ValidateNativeOption("reasoning effort", requested.Effort!, _quickSettings.EffortOptions);
        }

        if (changesModel)
        {
            if (_configOptionIds.TryGetValue("model", out var configId))
            {
                await SetConfigOptionAsync(configId, requested.Model!, ct).ConfigureAwait(false);
            }
            else if (_supportsLegacyModels)
            {
                await SetLegacyModelAsync(requested.Model!, ct).ConfigureAwait(false);
            }
        }

        if (changesEffort)
        {
            if (!_configOptionIds.TryGetValue("effort", out var effortConfigId))
                throw new InvalidOperationException($"{_agentName} no longer exposes ACP reasoning effort for the selected model.");
            ValidateNativeOption("reasoning effort", requested.Effort!, _quickSettings.EffortOptions);
            await SetConfigOptionAsync(effortConfigId, requested.Effort!, ct).ConfigureAwait(false);
        }

        var requestedModeId = string.Equals(
            requested.PlanMode,
            AppServerControlQuickSettings.PlanModeOn,
            StringComparison.Ordinal)
            ? _planModeId
            : _defaultModeId;
        if (!string.IsNullOrWhiteSpace(requestedModeId) &&
            !string.Equals(requestedModeId, _currentModeId, StringComparison.Ordinal))
        {
            await SetLegacyModeAsync(requestedModeId, ct).ConfigureAwait(false);
        }
    }

    private void ValidateNativeOption(string name, string value, IReadOnlyCollection<AppServerControlQuickSettingsOption> options)
    {
        if (options.Count > 0 && !options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{_agentName} does not advertise ACP {name} '{value}'.");
    }

    private Task SetConfigOptionAsync(string configId, string value, CancellationToken ct) =>
        SetConfigOptionAsync(configId, JsonSerializer.SerializeToElement(value, AppServerControlHostJsonContext.Default.String), ct);

    private async Task SetConfigOptionAsync(string configId, JsonElement value, CancellationToken ct)
    {
        var result = await SendRequestAsync(
                "session/set_config_option",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", _providerThreadId);
                    writer.WriteString("configId", configId);
                    if (_protocolGeneration == 2) writer.WriteString("type", value.ValueKind is JsonValueKind.True or JsonValueKind.False ? "boolean" : "id");
                    writer.WritePropertyName("value");
                    value.WriteTo(writer);
                    writer.WriteEndObject();
                },
                AttachRequestTimeout,
                ct)
            .ConfigureAwait(false);
        var updated = ApplyConfigOptions(_quickSettings, Traverse(result, "configOptions"));
        if (updated is not null)
        {
            _quickSettings = updated;
            CaptureConfigOptions(Traverse(result, "configOptions"));
            _emit(CreateQuickSettingsUpdatedEvent(_quickSettings, "acp", "session/set_config_option", result, result.GetRawText()));
        }
        else
        {
            throw new InvalidOperationException($"{_agentName} returned no ACP configuration after changing '{configId}'.");
        }
    }

    private async Task SetLegacyModelAsync(string modelId, CancellationToken ct)
    {
        await SendRequestAsync(
                "session/set_model",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", _providerThreadId);
                    writer.WriteString("modelId", modelId);
                    writer.WriteEndObject();
                },
                AttachRequestTimeout,
                ct)
            .ConfigureAwait(false);
        _quickSettings.Model = modelId;
    }

    private async Task SetLegacyModeAsync(string modeId, CancellationToken ct)
    {
        await SendRequestAsync(
                "session/set_mode",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", _providerThreadId);
                    writer.WriteString("modeId", modeId);
                    writer.WriteEndObject();
                },
                AttachRequestTimeout,
                ct)
            .ConfigureAwait(false);
        _currentModeId = modeId;
    }

    private AppServerControlQuickSettingsSummary CreateQuickSettingsFromState(JsonElement initializeResult, JsonElement sessionResult)
    {
        var modelState = Traverse(sessionResult, "models") ?? Traverse(initializeResult, "_meta", "modelState");
        _supportsLegacyModels = modelState is { ValueKind: JsonValueKind.Object };
        CaptureConfigOptions(Traverse(sessionResult, "configOptions"));
        CaptureModes(Traverse(sessionResult, "modes"));
        var model = NormalizeOptional(GetString(modelState, "currentModelId")) ?? _quickSettings.Model;
        var summary = AppServerControlQuickSettings.CreateSummary(
            model,
            null,
            AppServerControlQuickSettings.PlanModeOff,
            _quickSettings.PermissionMode,
            _quickSettings.PermissionMode);
        summary.ModelOptions = ReadModelOptions(modelState, model);
        return ApplyConfigOptions(summary, Traverse(sessionResult, "configOptions")) ?? summary;
    }

    private void CaptureConfigOptions(JsonElement? configOptions)
    {
        if (configOptions is not { ValueKind: JsonValueKind.Array } options)
        {
            return;
        }

        _configOptionIds.Clear();
        using var optionEnumerator = options.EnumerateArray();
        while (optionEnumerator.MoveNext())
        {
            var option = optionEnumerator.Current;
            var id = GetString(option, "id") ?? GetString(option, "configId");
            var category = GetString(option, "category");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (string.Equals(category, "model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "model", StringComparison.OrdinalIgnoreCase))
            {
                _configOptionIds.TryAdd("model", id);
            }
            else if (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(id, "effort", StringComparison.OrdinalIgnoreCase))
            {
                _configOptionIds.TryAdd("effort", id);
            }
        }
    }

    private void CaptureModes(JsonElement? modeState)
    {
        if (modeState is not { ValueKind: JsonValueKind.Object } modes)
        {
            return;
        }

        _currentModeId = GetString(modes, "currentModeId");
        var availableModes = Traverse(modes, "availableModes");
        if (availableModes is not { ValueKind: JsonValueKind.Array } values)
        {
            return;
        }

        using var modeEnumerator = values.EnumerateArray();
        while (modeEnumerator.MoveNext())
        {
            var mode = modeEnumerator.Current;
            var id = GetString(mode, "id") ?? GetString(mode, "modeId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (id.Contains("plan", StringComparison.OrdinalIgnoreCase))
            {
                _planModeId ??= id;
            }
            else
            {
                _defaultModeId ??= id;
            }
        }
    }

    private AppServerControlQuickSettingsSummary? ApplyConfigOptions(AppServerControlQuickSettingsSummary current, JsonElement? configOptions)
    {
        if (configOptions is not { ValueKind: JsonValueKind.Array } options)
        {
            return null;
        }

        var next = new AppServerControlQuickSettingsSummary
        {
            Model = current.Model,
            Effort = null,
            PlanMode = current.PlanMode,
            PermissionMode = current.PermissionMode,
            ModelOptions = _supportsLegacyModels ? AppServerControlQuickSettings.CloneOptions(current.ModelOptions) : [],
            EffortOptions = [],
            ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(current.ConfigOptions)
        };

        next.ConfigOptions.Clear();

        var appliedModel = false;
        var appliedEffort = false;
        using var optionEnumerator = options.EnumerateArray();
        while (optionEnumerator.MoveNext())
        {
            var option = optionEnumerator.Current;
            next.ConfigOptions.Add(option.Clone());
            var category = GetString(option, "category");
            var id = GetString(option, "id") ?? GetString(option, "configId");
            if (GetString(option, "type") != "select") continue;
            var currentValue = GetString(option, "currentValue");
            if (!appliedModel && (string.Equals(category, "model", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "model", StringComparison.OrdinalIgnoreCase)))
            {
                appliedModel = true;
                next.Model = NormalizeOptional(currentValue);
                next.ModelOptions = ReadConfigValueOptions(option);
            }
            else if (!appliedEffort && (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(id, "effort", StringComparison.OrdinalIgnoreCase)))
            {
                appliedEffort = true;
                next.Effort = currentValue;
                next.EffortOptions = ReadConfigValueOptions(option);
            }
        }

        return next;
    }

    private static List<AppServerControlQuickSettingsOption> ReadModelOptions(JsonElement? modelState, string? currentModel)
    {
        var availableModels = Traverse(modelState, "availableModels");
        if (availableModels is not { ValueKind: JsonValueKind.Array } models)
        {
            return string.IsNullOrWhiteSpace(currentModel)
                ? []
                : [new AppServerControlQuickSettingsOption { Value = currentModel, Label = currentModel, IsDefault = true }];
        }

        var result = new List<AppServerControlQuickSettingsOption>();
        using var modelEnumerator = models.EnumerateArray();
        while (modelEnumerator.MoveNext())
        {
            var model = modelEnumerator.Current;
            var value = GetString(model, "modelId") ?? GetString(model, "id");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result.Add(new AppServerControlQuickSettingsOption
            {
                Value = value,
                Label = GetString(model, "name") ?? value,
                IsDefault = string.Equals(value, currentModel, StringComparison.Ordinal)
            });
        }

        return result;
    }

    private static List<AppServerControlQuickSettingsOption> ReadConfigValueOptions(JsonElement option)
    {
        if (!option.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AppServerControlQuickSettingsOption>();
        using var itemEnumerator = options.EnumerateArray();
        while (itemEnumerator.MoveNext())
        {
            var item = itemEnumerator.Current;
            if (item.TryGetProperty("options", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(ReadConfigValueOptions(item));
                continue;
            }
            var value = GetString(item, "value");
            if (value is null)
            {
                continue;
            }

            result.Add(new AppServerControlQuickSettingsOption
            {
                Value = value,
                Label = GetString(item, "name") ?? value,
                Description = GetString(item, "description"),
                IsDefault = string.Equals(value, GetString(option, "currentValue"), StringComparison.Ordinal)
            });
        }

        return result;
    }

    private static int ResolveProtocolGeneration(JsonElement initializeResult)
    {
        if (!initializeResult.TryGetProperty("protocolVersion", out var protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.Number ||
            !protocolVersion.TryGetInt32(out var value) ||
            value is not (1 or 2))
        {
            throw new InvalidOperationException("ACP agent returned an unsupported protocol version; tlbx supports ACP v1 and v2.");
        }

        // Some v1 agents report version 2 with the legacy response shape.
        // The v2 info/capabilities envelope determines lifecycle semantics.
        return value == 2 && initializeResult.TryGetProperty("info", out _) ? 2 : 1;
    }

    private List<AcpPromptBlock> BuildPromptBlocks(AppServerControlTurnRequest request, string? planMode)
    {
        var blocks = new List<AcpPromptBlock>();
        var input = request.Text?.TrimStart() ?? string.Empty;
        var commandName = input.StartsWith('/') ? input[1..].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        var isNativeCommand = commandName is not null && _availableCommands.Any(command => command.Name == commandName);
        var useNativePlanCommand = Provider == "antigravity" && planMode == AppServerControlQuickSettings.PlanModeOn &&
            _availableCommands.Any(command => command.Name == "plan");
        var prompt = isNativeCommand ? request.Text : useNativePlanCommand ? "/plan " + request.Text :
            AppServerControlQuickSettings.ApplyPlanModePrompt(request.Text, planMode);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            blocks.Add(AcpPromptBlock.CreateText(prompt));
        }

        if (request.Attachments.Count == 0)
        {
            return blocks;
        }

        var fileReferences = new List<string>();
        foreach (var attachment in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.Path))
            {
                continue;
            }

            if (!File.Exists(attachment.Path))
            {
                throw new InvalidOperationException($"App Server Controller attachment does not exist: {attachment.Path}");
            }

            if (_supportsImagePrompts && string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                var fileInfo = new FileInfo(attachment.Path);
                if (fileInfo.Length > MaxInlineImageBytes)
                {
                    throw new InvalidOperationException($"ACP image attachment exceeds {MaxInlineImageBytes.ToString(CultureInfo.InvariantCulture)} bytes: {attachment.Path}");
                }

                var bytes = File.ReadAllBytes(attachment.Path);
                blocks.Add(AcpPromptBlock.CreateImage(
                    Convert.ToBase64String(bytes),
                    ResolveAttachmentMimeType(attachment),
                    new Uri(attachment.Path).AbsoluteUri));
                continue;
            }

            var label = string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ? "[image] " : "[file] ";
            var mime = string.IsNullOrWhiteSpace(attachment.MimeType) ? string.Empty : $" ({attachment.MimeType})";
            fileReferences.Add($"- {label}{attachment.Path}{mime}");
        }

        if (fileReferences.Count > 0)
        {
            var heading = fileReferences.Count == 1
                ? "Attached resource:"
                : $"Attached resources ({fileReferences.Count.ToString(CultureInfo.InvariantCulture)}):";
            blocks.Add(AcpPromptBlock.CreateText($"{heading}{Environment.NewLine}{string.Join(Environment.NewLine, fileReferences)}"));
        }

        return blocks;
    }

    private static void WritePromptBlocks(Utf8JsonWriter writer, IReadOnlyList<AcpPromptBlock> promptBlocks)
    {
        writer.WriteStartArray();
        foreach (var block in promptBlocks)
        {
            writer.WriteStartObject();
            writer.WriteString("type", block.Type);
            if (string.Equals(block.Type, "image", StringComparison.Ordinal))
            {
                writer.WriteString("data", block.Data);
                writer.WriteString("mimeType", block.MimeType);
                if (!string.IsNullOrWhiteSpace(block.Uri)) writer.WriteString("uri", block.Uri);
            }
            else
            {
                writer.WriteString("text", block.Text);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static List<PermissionOption> ReadPermissionOptions(JsonElement root)
    {
        var options = Traverse(root, "params", "options");
        if (options is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var result = new List<PermissionOption>();
        using var optionEnumerator = array.EnumerateArray();
        while (optionEnumerator.MoveNext())
        {
            var option = optionEnumerator.Current;
            var optionId = GetString(option, "optionId");
            if (string.IsNullOrWhiteSpace(optionId))
            {
                continue;
            }

            result.Add(new PermissionOption(optionId, GetString(option, "name"), GetString(option, "kind")));
        }

        return result;
    }

    private static string? SelectPermissionOption(IReadOnlyList<PermissionOption> options, string? decision)
    {
        var wantsAccept = string.Equals(decision, "accept", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase);
        var preferredKind = wantsAccept ? "allow" : "reject";
        var selected = options.FirstOrDefault(option => option.Kind?.StartsWith(preferredKind, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(selected.OptionId))
        {
            return selected.OptionId;
        }

        selected = options.FirstOrDefault(option => option.Name?.Contains(wantsAccept ? "allow" : "reject", StringComparison.OrdinalIgnoreCase) == true);
        return string.IsNullOrWhiteSpace(selected.OptionId) ? null : selected.OptionId;
    }

    private AppServerControlProviderEvent CreateQuickSettingsUpdatedEvent(
        AppServerControlQuickSettingsSummary quickSettings,
        string source,
        string? method,
        object? payload,
        string? rawLine = null)
    {
        return CreateEvent("quick-settings.updated", null, null, null, source, method, payload, appServerControlEvent =>
        {
            appServerControlEvent.QuickSettingsUpdated = AppServerControlQuickSettings.ToPayload(quickSettings);
        }, rawLine);
    }

    private AppServerControlProviderEvent CreateEvent(
        string type,
        string? turnId,
        string? itemId,
        string? requestId,
        string source,
        string? method,
        object? payload,
        Action<AppServerControlProviderEvent> configure,
        string? rawLine = null)
    {
        var appServerControlEvent = new AppServerControlProviderEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            EventId = $"evt-{Provider}-{Guid.NewGuid():N}",
            SessionId = _sessionId ?? string.Empty,
            Provider = Provider,
            ThreadId = _providerThreadId ?? _sessionId ?? string.Empty,
            TurnId = turnId,
            ItemId = itemId,
            RequestId = requestId,
            CreatedAt = DateTimeOffset.UtcNow,
            Type = type,
            Raw = new AppServerControlProviderEventRaw
            {
                Source = source,
                Method = method,
                PayloadOmitted = payload is not null || !string.IsNullOrEmpty(rawLine)
            }
        };
        configure(appServerControlEvent);
        return appServerControlEvent;
    }

    private static HostCommandOutcome Accepted(
        string commandId,
        string sessionId,
        AppServerControlCommandAcceptedResponse? accepted = null,
        IReadOnlyList<AppServerControlProviderEvent>? events = null)
    {
        return new HostCommandOutcome
        {
            Result = new AppServerControlHostCommandResultEnvelope
            {
                CommandId = commandId,
                SessionId = sessionId,
                Status = "accepted",
                Accepted = accepted
            },
            Events = events ?? []
        };
    }

    private void EmitRuntimeMessage(string type, string message, string? detail, bool timelineVisible = true)
    {
        EmitRuntimeMessage(type, message, detail, "acp", type, new { message, detail }, null, timelineVisible);
    }

    private void EmitRuntimeMessage(
        string type,
        string message,
        string? detail,
        string source,
        string? method,
        object? payload,
        string? rawLine,
        bool timelineVisible = true)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        _emit(CreateEvent(type, _activeTurnId, null, null, source, method, payload, appServerControlEvent =>
        {
            var runtimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = message,
                Detail = detail
            };
            if (timelineVisible)
            {
                appServerControlEvent.RuntimeMessage = runtimeMessage;
            }
            else
            {
                appServerControlEvent.RuntimeNoticeOnly = runtimeMessage;
            }
        }, rawLine));
    }

    private string BuildArguments()
    {
        return string.Join(" ", _executableArguments.Select(QuoteArgument));
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ProcessStartInfo CreateProcessStartInfo(string binaryPath, string arguments, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(binaryPath);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                return new ProcessStartInfo
                {
                    FileName = comspec,
                    Arguments = $"/d /c \"\"{binaryPath}\" {arguments}\"",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    StandardInputEncoding = Utf8NoBom
                };
            }
        }

        return new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom
        };
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(static ch => char.IsWhiteSpace(ch) || ch is '"' or '\\')
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string? FindExecutableInPath(string commandName, string? userProfileDirectory)
    {
        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        var candidateNames = OperatingSystem.IsWindows() ? GetWindowsExecutableNames(commandName) : [commandName];
        foreach (var directory in EnumerateSearchDirectories(userProfileDirectory))
        {
            foreach (var candidateName in candidateNames)
            {
                var fullPath = Path.Combine(directory, candidateName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchDirectories(string? userProfileDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(userProfileDirectory, ".grok", "bin"),
                         Path.Combine(userProfileDirectory, ".local", "bin"),
                         Path.Combine(userProfileDirectory, "AppData", "Roaming", "npm"),
                         Path.Combine(userProfileDirectory, "AppData", "Local", "Programs", "nodejs")
                     })
            {
                if (Directory.Exists(directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        var currentProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentProfile))
        {
            var grokBin = Path.Combine(currentProfile, ".grok", "bin");
            if (Directory.Exists(grokBin) && seen.Add(grokBin))
            {
                yield return grokBin;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            foreach (var rawDirectory in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = rawDirectory.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(directory) && seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }
    }

    private static string[] GetWindowsExecutableNames(string commandName)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(commandName)))
        {
            return [commandName];
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathext)
            ? [".exe", ".cmd", ".bat"]
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return extensions.Select(ext => commandName + ext.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ExtractContentText(JsonElement update)
    {
        var content = Traverse(update, "content");
        return ExtractText(content);
    }

    private static List<AppServerControlCommand> ReadAvailableCommands(JsonElement update)
    {
        var commands = Traverse(update, "availableCommands");
        if (commands is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var result = new List<AppServerControlCommand>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var commandEnumerator = array.EnumerateArray();
        while (commandEnumerator.MoveNext())
        {
            var command = commandEnumerator.Current;
            var name = GetString(command, "name");
            var description = GetString(command, "description");
            if (!string.IsNullOrWhiteSpace(name) && description is not null && names.Add(name))
            {
                result.Add(new AppServerControlCommand
                {
                    Name = name,
                    Description = description,
                    Input = Traverse(command, "input")?.Clone(),
                    InputHint = GetString(command, "input", "type") is null or "text" ? GetString(command, "input", "hint") : null
                });
            }
        }

        return result;
    }

    private static List<string> ReadMetaTools(JsonElement update)
    {
        var tools = Traverse(update, "_meta", "tools");
        if (tools is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var result = new List<string>();
        using var toolEnumerator = array.EnumerateArray();
        while (toolEnumerator.MoveNext())
        {
            var tool = toolEnumerator.Current;
            if (tool.ValueKind == JsonValueKind.String)
            {
                var name = tool.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string FormatNamePreview(IReadOnlyList<string> names)
    {
        const int previewLimit = 8;
        var preview = string.Join(", ", names.Take(previewLimit));
        return names.Count <= previewLimit
            ? preview
            : $"{preview}, +{(names.Count - previewLimit).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildAvailableCommandsDetail(IReadOnlyList<string> commands, IReadOnlyList<string> tools)
    {
        var builder = new StringBuilder();
        if (commands.Count > 0)
        {
            builder.AppendLine($"Commands ({commands.Count.ToString(CultureInfo.InvariantCulture)}): {string.Join(", ", commands)}");
        }

        if (tools.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"Tools ({tools.Count.ToString(CultureInfo.InvariantCulture)}): {string.Join(", ", tools)}");
        }

        return builder.ToString().Trim();
    }

    private static string? BuildSessionNotificationMessage(JsonElement root)
    {
        var parameters = Traverse(root, "params");
        if (parameters is not { ValueKind: JsonValueKind.Object } parameterElement)
        {
            return null;
        }

        var notification = Traverse(parameterElement, "notification");
        var source = notification is { ValueKind: JsonValueKind.Object } notificationElement
            ? notificationElement
            : parameterElement;
        var title = GetString(source, "title");
        var message = GetString(source, "message") ??
                      GetString(source, "description") ??
                      GetString(source, "status") ??
                      GetString(source, "state") ??
                      GetString(source, "type") ??
                      GetString(source, "event");
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }

        return string.IsNullOrWhiteSpace(message) ||
               string.Equals(title, message, StringComparison.Ordinal)
            ? title
            : $"{title}: {message}";
    }

    private static string ExtractText(JsonElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var value = element.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Object when string.Equals(GetString(value, "type"), "text", StringComparison.OrdinalIgnoreCase):
                return GetString(value, "text") ?? string.Empty;
            case JsonValueKind.Object when value.TryGetProperty("content", out var content):
                return ExtractText(content);
            case JsonValueKind.Array:
                var parts = new List<string>();
                using (var itemEnumerator = value.EnumerateArray())
                {
                    while (itemEnumerator.MoveNext())
                    {
                        var text = ExtractText(itemEnumerator.Current);
                        if (!string.IsNullOrEmpty(text))
                        {
                            parts.Add(text);
                        }
                    }
                }

                return string.Join(Environment.NewLine, parts);
            default:
                return string.Empty;
        }
    }

    private static string? ExtractDiff(JsonElement update)
    {
        var content = Traverse(update, "content");
        if (content is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        var builder = new StringBuilder();
        using var itemEnumerator = array.EnumerateArray();
        while (itemEnumerator.MoveNext())
        {
            var item = itemEnumerator.Current;
            if (!string.Equals(GetString(item, "type"), "diff", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = GetString(item, "path") ?? "file";
            var oldText = GetString(item, "oldText") ?? string.Empty;
            var newText = GetString(item, "newText") ?? string.Empty;
            builder.AppendLine($"--- a/{path}");
            builder.AppendLine($"+++ b/{path}");
            builder.AppendLine("@@");
            foreach (var line in oldText.Split('\n'))
            {
                builder.Append('-');
                builder.AppendLine(line.TrimEnd('\r'));
            }

            foreach (var line in newText.Split('\n'))
            {
                builder.Append('+');
                builder.AppendLine(line.TrimEnd('\r'));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string BuildPlanMarkdown(JsonElement update)
    {
        if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        using var entryEnumerator = entries.EnumerateArray();
        while (entryEnumerator.MoveNext())
        {
            var entry = entryEnumerator.Current;
            var content = GetString(entry, "content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var status = GetString(entry, "status");
            lines.Add(string.IsNullOrWhiteSpace(status) ? $"- {content}" : $"- [{status}] {content}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string NormalizeToolStatus(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "failed" => "failed",
            "cancelled" => "cancelled",
            "canceled" => "cancelled",
            "in_progress" => "in_progress",
            "running" => "in_progress",
            _ => "in_progress"
        };
    }

    private static bool IsTerminalToolStatus(string status)
    {
        return status is "completed" or "failed" or "cancelled";
    }

    private static string NormalizeToolItemType(string? kind, string? title)
    {
        if (string.Equals(kind, "execute", StringComparison.OrdinalIgnoreCase) ||
            title?.Contains("command", StringComparison.OrdinalIgnoreCase) == true ||
            title?.Contains("bash", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "command_execution";
        }

        if (string.Equals(kind, "edit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "delete", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "move", StringComparison.OrdinalIgnoreCase))
        {
            return "file_change";
        }

        return "dynamic_tool_call";
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        return GetString(error, "message") ?? error.GetRawText();
    }

    private void ResetTurnState()
    {
        var completion = _activeTurnCompletion;
        _activeTurnCompletion = null;
        _activeTurnId = null;
        _cancellingTurnId = null;
        _activeTurnModel = null;
        _activeTurnEffort = null;
        if (_protocolGeneration == 1) _tools.Clear();
        completion?.TrySetResult();
    }

    private static string? GetString(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }

        return GetString(element.Value, path);
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = Traverse(element, path);
        return current is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static JsonElement? Traverse(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }

        return Traverse(element.Value, path);
    }

    private static JsonElement? Traverse(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string ResolveAttachmentMimeType(AppServerControlAttachmentReference attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.MimeType)) return attachment.MimeType.Trim().ToLowerInvariant();
        return Path.GetExtension(attachment.Path).ToLowerInvariant() switch
        {
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".avif" => "image/avif",
            _ => "application/octet-stream"
        };
    }

    private readonly record struct AcpPromptBlock(string Type, string? Text, string? Data, string? MimeType, string? Uri)
    {
        public static AcpPromptBlock CreateText(string text) => new("text", text, null, null, null);

        public static AcpPromptBlock CreateImage(string data, string mimeType, string uri) => new("image", null, data, mimeType, uri);
    }

    private readonly record struct PermissionOption(string OptionId, string? Name, string? Kind);

    private readonly record struct PendingPermissionRequest(JsonElement RpcId, string TurnId, IReadOnlyList<PermissionOption> Options);

    private sealed class AcpToolState
    {
        public string ItemId { get; init; } = string.Empty;
        public string ItemType { get; set; } = "dynamic_tool_call";
        public string Title { get; set; } = string.Empty;
        public string? TurnId { get; set; }
        public string Status { get; set; } = "in_progress";
        public AppServerControlToolPresentation Presentation { get; init; } = new();
        public BoundedToolOutputAccumulator Output { get; set; } = new();
    }
}
