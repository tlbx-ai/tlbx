using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed partial class ClaudeAppServerControlAgentRuntime : IAppServerControlAgentRuntime
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Action<AppServerControlProviderEvent> _emit;
    private readonly Func<string, bool> _hasRetainedConversation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<int, ClaudeBlockState> _blocks = [];
    private readonly Dictionary<string, ClaudeToolState> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _bridgeResolutionAcks = new(StringComparer.Ordinal);
    private Process? _process;
    private StreamReader? _output;
    private StreamReader? _error;
    private StreamWriter? _input;
    private Task? _readerTask;
    private Task? _errorTask;
    private TaskCompletionSource _bridgeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionId;
    private string? _workingDirectory;
    private AppServerControlMcpCredentials? _mcpCredentials;
    private string? _binaryPath;
    private string? _userProfileDirectory;
    private string? _providerThreadId;
    private string? _newProviderSessionId;
    private string? _activeTurnId;
    private string? _activeTurnModel;
    private string? _activeTurnEffort;
    private string? _currentAssistantItemId;
    private AppServerControlQuickSettingsSummary _quickSettings = new();
    private bool _assistantStreamEmitted;
    private bool _turnStarted;
    private bool _assistantMessageEmitted;
    private bool _interruptRequested;
    private bool _steeringPending;
    private TaskCompletionSource? _interruptSettled;
    private bool _interruptHasQueuedWork;
    private bool _manualCompactionTurn;
    private string? _compactionFailure;
    private long? _compactionContextBefore;
    private long _sequence;

    public ClaudeAppServerControlAgentRuntime(Action<AppServerControlProviderEvent> emit, Func<string, bool>? hasRetainedConversation = null)
    {
        _emit = emit;
        _hasRetainedConversation = hasRetainedConversation ?? (_ => false);
    }

    public string Provider => "claude";

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await DisposeProcessAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _gate.Dispose();
        _mcpCredentials?.Dispose();
        _mcpCredentials = null;
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
                _ => throw new InvalidOperationException($"Unsupported Claude command '{command.Type}'.")
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
            throw new InvalidOperationException($"Claude runtime cannot attach provider '{attach.Provider}'.");
        }

        if (string.IsNullOrWhiteSpace(attach.WorkingDirectory) || !Directory.Exists(attach.WorkingDirectory))
        {
            throw new InvalidOperationException("Claude working directory is required.");
        }

        var binaryPath = string.IsNullOrWhiteSpace(attach.ExecutablePath)
            ? FindExecutableInPath("claude")
            : attach.ExecutablePath;
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            throw new InvalidOperationException("Claude CLI was not found on PATH.");
        }

        if (_process is { HasExited: false })
        {
            if (!string.Equals(_sessionId, command.SessionId, StringComparison.Ordinal) ||
                !string.Equals(_workingDirectory, attach.WorkingDirectory, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(attach.ResumeThreadId) &&
                 !string.Equals(_providerThreadId, attach.ResumeThreadId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Claude is already attached to another session, directory or native thread.");
            return Accepted(command.CommandId, command.SessionId);
        }

        _sessionId = command.SessionId;
        _workingDirectory = attach.WorkingDirectory;
        _binaryPath = binaryPath;
        _userProfileDirectory = attach.UserProfileDirectory;
        _mcpCredentials?.Dispose();
        _mcpCredentials = attach.McpServer is null ? null : new AppServerControlMcpCredentials(attach.McpServer);
        _newProviderSessionId = Guid.NewGuid().ToString();
        _quickSettings = CreateDefaultQuickSettings();
        _providerThreadId = AppServerControlQuickSettings.NormalizeOptionalValue(attach.ResumeThreadId);
        _nativeModels.Clear();
        _nativeCommands = [];
        _terminalCommands.Clear();
        _usage = null;
        _messageInputTokens = null;
        _messageOutputTokens = null;

        await EnsureClaudeBridgeAsync(ct).ConfigureAwait(false);

        var events = new List<AppServerControlProviderEvent>
        {
            CreateEvent("session.started", null, null, null, "mtagenthost.claude", "runtime.attach", attach, appServerControlEvent =>
            {
                appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "starting",
                    StateLabel = "Starting",
                    Reason = "Claude App Server Controller runtime attached."
                };
            }),
            CreateEvent("session.ready", null, null, null, "mtagenthost.claude", "runtime.attach", attach, appServerControlEvent =>
            {
                appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "ready",
                    StateLabel = "Ready",
                    Reason = "Claude App Server Controller runtime is ready for the next turn."
                };
            }),
            CreateQuickSettingsUpdatedEvent(_quickSettings, "mtagenthost.claude", "runtime.attach", attach)
        };

        if (!string.IsNullOrWhiteSpace(_providerThreadId))
        {
            events.Add(CreateEvent("thread.started", null, null, null, "mtagenthost.claude", "runtime.attach", attach, appServerControlEvent =>
            {
                appServerControlEvent.ThreadState = new AppServerControlProviderThreadStatePayload
                {
                    State = "active",
                    StateLabel = "Active",
                    ProviderThreadId = _providerThreadId
                };
            }));
        }

        return Accepted(command.CommandId, command.SessionId, events: events);
    }

    private async Task<HostCommandOutcome> StartTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (!string.IsNullOrWhiteSpace(_activeTurnId))
        {
            throw new InvalidOperationException("Claude already has an active App Server Controller turn.");
        }

        var request = command.StartTurn ?? throw new InvalidOperationException("turn.start payload is required.");
        var quickSettings = ResolveRequestedQuickSettings(request);
        var prompt = BuildPromptInput(request, out var addDirectories);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("App Server Controller turn input must include text or attachments.");
        }

        var turnId = "turn-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var commandText = (request.Text ?? string.Empty).TrimStart();
        _manualCompactionTurn = commandText.StartsWith("/compact", StringComparison.Ordinal) &&
            (commandText.Length == 8 || char.IsWhiteSpace(commandText[8]));
        _compactionFailure = null;
        _compactionContextBefore = _usage?.ContextUsedTokens;
        _activeTurnId = turnId;
        _activeTurnModel = quickSettings.Model;
        _activeTurnEffort = quickSettings.Effort;
        _quickSettings = quickSettings;
        _assistantStreamEmitted = false;
        _turnStarted = false;
        _assistantMessageEmitted = false;
        _interruptRequested = false;
        _blocks.Clear();
        _tools.Clear();

        await EnsureClaudeBridgeAsync(ct).ConfigureAwait(false);
        await SendTurnCommandAsync(
            "turn.start",
            prompt,
            request.Attachments,
            addDirectories,
            _activeTurnModel,
            _activeTurnEffort,
            _quickSettings.PlanMode,
            _quickSettings.PermissionMode,
            ct).ConfigureAwait(false);

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
                        EffortOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.EffortOptions)
                    }
                }
            },
            Events =
            [
                CreateQuickSettingsUpdatedEvent(_quickSettings, "midterm.appServerControl", "turn.start", request)
            ]
        };
    }

    private Task<HostCommandOutcome> CompactViaNativeCommandAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        if (!_nativeCommands.Any(native => native.Name.TrimStart('/').Equals("compact", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Claude does not advertise a native /compact command.");
        return StartTurnAsync(new AppServerControlHostCommandEnvelope
        {
            CommandId = command.CommandId, SessionId = command.SessionId, Type = "turn.start",
            StartTurn = new AppServerControlTurnRequest { Text = "/compact" }
        }, ct);
    }

    private async Task<HostCommandOutcome> SteerTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        EnsureAttached();
        if (string.IsNullOrWhiteSpace(_activeTurnId))
        {
            throw new InvalidOperationException("Claude does not have an active turn to steer.");
        }

        var request = command.SteerTurn ?? throw new InvalidOperationException("turn.steer payload is required.");
        var turnId = _activeTurnId;
        if (!string.IsNullOrWhiteSpace(request.ExpectedTurnId) &&
            !string.Equals(request.ExpectedTurnId, _activeTurnId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude steer target no longer matches the active turn.");
        }

        var turnRequest = new AppServerControlTurnRequest
        {
            Text = request.Text,
            Attachments = request.Attachments
        };
        var prompt = BuildPromptInput(turnRequest, out var addDirectories);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("Claude steer input must include text or attachments.");
        }

        await EnsureClaudeBridgeAsync(ct).ConfigureAwait(false);
        _steeringPending = true;
        await SendTurnCommandAsync(
            "turn.steer",
            prompt,
            turnRequest.Attachments,
            addDirectories,
            _activeTurnModel,
            _activeTurnEffort,
            _quickSettings.PlanMode,
            _quickSettings.PermissionMode,
            ct).ConfigureAwait(false);

        return Accepted(command.CommandId, command.SessionId, new AppServerControlCommandAcceptedResponse
        {
            SessionId = command.SessionId, Status = "accepted", TurnId = turnId
        });
    }

    private async Task<HostCommandOutcome> InterruptTurnAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var turnId = string.IsNullOrWhiteSpace(command.InterruptTurn?.TurnId)
            ? _activeTurnId
            : command.InterruptTurn!.TurnId;
        if (string.IsNullOrWhiteSpace(turnId))
        {
            throw new InvalidOperationException("Claude does not have an active turn to interrupt.");
        }
        if (!string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Claude turn to interrupt is no longer active.");

        if (_process is { HasExited: false })
        {
            _interruptRequested = true;
            _interruptHasQueuedWork = false;
            _interruptSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await WriteBridgeResolutionCommandAsync("interrupt", turnId, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "turn.interrupt");
                    writer.WriteString("turnId", turnId);
                    writer.WriteEndObject();
                }, ct).ConfigureAwait(false);
                if (_interruptHasQueuedWork)
                    await DisposeProcessAsync(resetTurnState: false).ConfigureAwait(false);
                else
                    await _interruptSettled.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await DisposeProcessAsync(resetTurnState: false).ConfigureAwait(false);
            }
            finally
            {
                _interruptSettled = null;
            }
        }

        ResetTurnState();

        return Accepted(
            command.CommandId,
            command.SessionId,
            accepted: new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                TurnId = turnId
            },
            events:
            [
                CreateEvent("turn.aborted", turnId, null, null, "mtagenthost.claude", "turn.interrupt", command.InterruptTurn, appServerControlEvent =>
                {
                    appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                    {
                        State = "interrupted",
                        StateLabel = "Interrupted",
                        StopReason = "interrupt"
                    };
                }),
                CreateEvent("session.state.changed", turnId, null, null, "mtagenthost.claude", "turn.interrupt", command.InterruptTurn, appServerControlEvent =>
                {
                    appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                    {
                        State = "ready",
                        StateLabel = "Ready",
                        Reason = "Claude turn interrupted."
                    };
                })
            ]);
    }

    private async Task<HostCommandOutcome> ResolvePermissionRequestAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveRequest ?? throw new InvalidOperationException("request.resolve payload is required.");
        await WriteBridgeResolutionCommandAsync("permission", request.RequestId, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "permission.resolve");
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("decision", request.Decision);
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        return Accepted(
            command.CommandId,
            command.SessionId,
            new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = request.RequestId,
                TurnId = _activeTurnId
            },
            [CreateEvent("request.resolved", _activeTurnId, null, request.RequestId, "claude.agent-sdk", "canUseTool/decision", request, appServerControlEvent =>
            {
                appServerControlEvent.RequestResolved = new AppServerControlProviderRequestResolvedPayload
                {
                    RequestType = "tool_approval",
                    Decision = request.Decision
                };
            })]);
    }

    private async Task<HostCommandOutcome> ResolveUserInputAsync(AppServerControlHostCommandEnvelope command, CancellationToken ct)
    {
        var request = command.ResolveUserInput ?? throw new InvalidOperationException("user-input.resolve payload is required.");
        await WriteBridgeResolutionCommandAsync("user_input", request.RequestId, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "user_input.resolve");
            writer.WriteString("requestId", request.RequestId);
            writer.WriteStartObject("answers");
            foreach (var answer in request.Answers)
            {
                writer.WriteStartArray(answer.QuestionId);
                foreach (var value in answer.Answers)
                {
                    writer.WriteStringValue(value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        return Accepted(
            command.CommandId,
            command.SessionId,
            new AppServerControlCommandAcceptedResponse
            {
                SessionId = command.SessionId,
                Status = "accepted",
                RequestId = request.RequestId,
                TurnId = _activeTurnId
            },
            [CreateEvent("user-input.resolved", _activeTurnId, null, request.RequestId, "claude.agent-sdk", "AskUserQuestion/resolved", request, appServerControlEvent =>
            {
                appServerControlEvent.UserInputResolved = new AppServerControlProviderUserInputResolvedPayload
                {
                    Answers = request.Answers
                };
            })]);
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
                    HandleClaudeLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            EmitRuntimeMessage("runtime.error", "Claude App Server Controller stream failed.", ex.Message);
        }
        finally
        {
            await FinalizeExitAsync(process).ConfigureAwait(false);
        }
    }

    private async Task ReadErrorLoopAsync(Process process, CancellationToken ct)
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
                    EmitRuntimeMessage("runtime.warning", line.Trim(), line.Trim());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }

        await Task.CompletedTask.ConfigureAwait(false);
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

        if (!_interruptRequested && !string.IsNullOrWhiteSpace(_activeTurnId) && process.ExitCode != 0)
        {
            _emit(CreateEvent("turn.completed", _activeTurnId, null, null, "claude.agent-sdk", "process.exit", new { exitCode = process.ExitCode }, appServerControlEvent =>
            {
                appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                {
                    State = "failed",
                    StateLabel = "Failed",
                    StopReason = "process_exit",
                    ErrorMessage = $"Claude exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                };
            }));
            EmitRuntimeMessage("runtime.error", "Claude App Server Controller process exited unexpectedly.", $"Exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (ReferenceEquals(_process, process))
        {
            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _error?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
            _process = null;
            _input = null;
            _output = null;
            _error = null;
        }
    }

    private void HandleClaudeLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = GetString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (type.StartsWith("bridge.", StringComparison.Ordinal))
        {
            HandleBridgeMessage(type, root, line);
            return;
        }

        EnsureProviderThreadId(root);

        switch (type)
        {
            case "system":
                HandleClaudeSystemMessage(root, line);
                break;
            case "stream_event":
                HandleStreamEvent(root, line);
                break;
            case "assistant":
                HandleAssistant(root, line);
                break;
            case "user":
                HandleUser(root, line);
                break;
            case "result":
                HandleResult(root, line);
                break;
        }
    }

    private void HandleBridgeMessage(string type, JsonElement root, string rawLine)
    {
        switch (type)
        {
            case "bridge.ready":
                ApplyClaudeNativeCatalogue(root, rawLine);
                _bridgeReady.TrySetResult();
                break;
            case "bridge.history":
                ReplayClaudeHistory(root);
                break;
            case "bridge.proposed_plan":
            {
                var markdown = GetString(root, "markdown");
                if (GetString(root, "toolUseId") is { } planToolId && _tools.TryGetValue(planToolId, out var planTool))
                    planTool.IsPlanReview = true;
                if (!string.IsNullOrWhiteSpace(markdown))
                    _emit(CreateEvent("plan.completed", _activeTurnId, GetString(root, "toolUseId"), null,
                        "claude.agent-sdk", "canUseTool/ExitPlanMode", root,
                        e => e.PlanCompleted = new AppServerControlProviderPlanCompletedPayload { PlanMarkdown = markdown, IsProposal = true }, rawLine));
                break;
            }
            case "bridge.permission_request":
            {
                var requestId = GetString(root, "requestId");
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                var toolName = GetString(root, "toolName") ?? "Claude tool";
                var input = Traverse(root, "input");
                _emit(CreateEvent("request.opened", _activeTurnId, GetString(root, "toolUseId"), requestId, "claude.agent-sdk", "canUseTool/request", root, appServerControlEvent =>
                {
                    appServerControlEvent.RequestOpened = new AppServerControlProviderRequestOpenedPayload
                    {
                        RequestType = "tool_approval",
                        RequestTypeLabel = "Tool approval",
                        Detail = input is { ValueKind: not JsonValueKind.Undefined }
                            ? $"{toolName}: {input.Value.GetRawText()}"
                            : toolName
                    };
                }, rawLine));
                break;
            }
            case "bridge.permission_resolved":
                CompleteBridgeResolutionAck("permission", GetString(root, "requestId"));
                break;
            case "bridge.user_input_request":
            {
                var requestId = GetString(root, "requestId");
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                _emit(CreateEvent("user-input.requested", _activeTurnId, null, requestId, "claude.agent-sdk", "canUseTool/AskUserQuestion", root, appServerControlEvent =>
                {
                    appServerControlEvent.UserInputRequested = new AppServerControlProviderUserInputRequestedPayload
                    {
                        Questions = ReadQuestions(root)
                    };
                }, rawLine));
                break;
            }
            case "bridge.interrupted":
                _interruptHasQueuedWork |= GetBoolean(root, "hasQueuedWork");
                CompleteBridgeResolutionAck("interrupt", GetString(root, "turnId"));
                break;
            case "bridge.user_input_resolved":
                CompleteBridgeResolutionAck("user_input", GetString(root, "requestId"));
                break;
            case "bridge.stderr":
                EmitRuntimeMessage("runtime.warning", "Claude Agent SDK diagnostic", GetString(root, "message"));
                break;
            case "bridge.error":
            {
                var message = GetString(root, "message") ?? "Claude Agent SDK bridge failed.";
                var detail = GetString(root, "detail");
                FailBridgeResolutionAck(
                    GetString(root, "commandType"),
                    GetString(root, "requestId"),
                    new InvalidOperationException(detail ?? message));
                _bridgeReady.TrySetException(new InvalidOperationException(detail ?? message));
                EmitRuntimeMessage("runtime.error", message, detail);
                if (!string.IsNullOrWhiteSpace(_activeTurnId))
                {
                    var failedTurnId = _activeTurnId;
                    _emit(CreateEvent("turn.completed", failedTurnId, null, null, "claude.agent-sdk", "bridge.error", root, appServerControlEvent =>
                    {
                        appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                        {
                            State = "failed",
                            StateLabel = "Failed",
                            StopReason = "bridge_error",
                            ErrorMessage = detail ?? message
                        };
                    }, rawLine));
                    _emit(CreateEvent("session.state.changed", failedTurnId, null, null, "claude.agent-sdk", "bridge.error", root, appServerControlEvent =>
                    {
                        appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                        {
                            State = "error",
                            StateLabel = "Error",
                            Reason = message
                        };
                    }, rawLine));
                    ResetTurnState();
                }
                break;
            }
            case "bridge.closed":
                if (!_shutdown.IsCancellationRequested)
                {
                    EmitRuntimeMessage("runtime.warning", "Claude Agent SDK bridge closed.", null);
                }
                break;
        }
    }

    private static List<AppServerControlQuestion> ReadQuestions(JsonElement root)
    {
        var questions = new List<AppServerControlQuestion>();
        var rawQuestions = Traverse(root, "questions");
        if (rawQuestions is not { ValueKind: JsonValueKind.Array } values)
        {
            return questions;
        }

        using var enumerator = values.EnumerateArray();
        while (enumerator.MoveNext())
        {
            var value = enumerator.Current;
            var question = new AppServerControlQuestion
            {
                Id = GetString(value, "id") ?? string.Empty,
                Header = GetString(value, "header") ?? string.Empty,
                Question = GetString(value, "question") ?? string.Empty,
                MultiSelect = GetBoolean(value, "multiSelect"),
                AllowCustomAnswer = true
            };
            var rawOptions = Traverse(value, "options");
            if (rawOptions is { ValueKind: JsonValueKind.Array } options)
            {
                using var optionEnumerator = options.EnumerateArray();
                while (optionEnumerator.MoveNext())
                {
                    var option = optionEnumerator.Current;
                    question.Options.Add(new AppServerControlQuestionOption
                    {
                        Label = GetString(option, "label") ?? string.Empty,
                        Description = GetString(option, "description") ?? string.Empty
                    });
                }
            }
            questions.Add(question);
        }

        return questions;
    }

    private void HandleStreamEvent(JsonElement root, string rawLine)
    {
        var eventType = GetString(root, "event", "type");
        switch (eventType)
        {
            case "message_start":
                _currentAssistantItemId = GetString(root, "event", "message", "id");
                _assistantStreamEmitted = false;
                _blocks.Clear();
                UpdateClaudeMessageUsage(root, Traverse(root, "event", "message", "usage"), rawLine);
                if (!string.IsNullOrWhiteSpace(_activeTurnId))
                {
                    _emit(CreateEvent("session.state.changed", _activeTurnId, null, null, "claude.agent-sdk", "message_start", root, appServerControlEvent =>
                    {
                        appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
                        {
                            State = "running",
                            StateLabel = "Running",
                            Reason = "Claude turn started."
                        };
                    }, rawLine));

                    if (!_turnStarted)
                    {
                        _turnStarted = true;
                        _emit(CreateEvent("turn.started", _activeTurnId, null, null, "claude.agent-sdk", "message_start", root, appServerControlEvent =>
                        {
                            appServerControlEvent.TurnStarted = new AppServerControlProviderTurnStartedPayload
                            {
                                Model = GetString(root, "event", "message", "model") ?? _activeTurnModel,
                                Effort = _activeTurnEffort
                            };
                        }, rawLine));
                    }
                }
                break;
            case "message_delta":
                UpdateClaudeMessageUsage(root, Traverse(root, "event", "usage"), rawLine);
                break;
            case "content_block_start":
                HandleContentBlockStart(root, rawLine);
                break;
            case "content_block_delta":
                HandleContentBlockDelta(root, rawLine);
                break;
        }
    }

    private void HandleContentBlockStart(JsonElement root, string rawLine)
    {
        var index = GetInt32(root, "event", "index");
        var block = Traverse(root, "event", "content_block");
        if (index is null || block is not { ValueKind: JsonValueKind.Object } contentBlock)
        {
            return;
        }

        var state = new ClaudeBlockState
        {
            Type = GetString(contentBlock, "type") ?? string.Empty,
            ProviderItemId = GetString(contentBlock, "id"),
            Title = GetString(contentBlock, "name") ?? "Tool"
        };
        _blocks[index.Value] = state;

        if (string.IsNullOrWhiteSpace(_activeTurnId) || !string.Equals(state.Type, "tool_use", StringComparison.Ordinal))
        {
            return;
        }

        var itemId = string.IsNullOrWhiteSpace(state.ProviderItemId)
            ? $"tool:{_activeTurnId}:{index.Value.ToString(CultureInfo.InvariantCulture)}"
            : state.ProviderItemId;
        state.ItemId = itemId;
        JsonElement? input = contentBlock.TryGetProperty("input", out var inputValue)
            ? inputValue
            : null;
        var itemType = NormalizeToolItemType(state.Title);
        var tool = new ClaudeToolState
        {
            ItemId = itemId,
            ItemType = itemType,
            Title = state.Title,
            Presentation = AppServerControlToolPresentationProjector.FromClaude(
                state.Title,
                "in_progress",
                input)
        };
        _tools[state.ProviderItemId ?? itemId] = tool;

        _emit(CreateEvent("item.started", _activeTurnId, itemId, null, "claude.agent-sdk", "content_block_start", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                ItemType = itemType,
                Status = "in_progress",
                Title = state.Title,
                Detail = tool.Presentation.Subject,
                ToolPresentation = AppServerControlToolPresentationProjector.Clone(tool.Presentation)
            };
        }, rawLine));
    }

    private void HandleContentBlockDelta(JsonElement root, string rawLine)
    {
        var index = GetInt32(root, "event", "index");
        if (index is null || !_blocks.TryGetValue(index.Value, out var state) || string.IsNullOrWhiteSpace(_activeTurnId))
        {
            return;
        }

        var deltaType = GetString(root, "event", "delta", "type");
        switch (deltaType)
        {
            case "text_delta":
            {
                var delta = GetString(root, "event", "delta", "text");
                if (string.IsNullOrWhiteSpace(delta))
                {
                    return;
                }

                if (IsReasoningBlock(state.Type))
                {
                    _emit(CreateEvent("content.delta", _activeTurnId, state.ItemId, null, "claude.agent-sdk", "content_block_delta", root, appServerControlEvent =>
                    {
                        appServerControlEvent.ContentDelta = new AppServerControlProviderContentDeltaPayload
                        {
                            StreamKind = "reasoning_text",
                            Delta = delta
                        };
                    }, rawLine));
                }
                else
                {
                    EmitAssistantDelta(delta, root, rawLine);
                }

                break;
            }
            case "input_json_delta":
            {
                var partialJson = GetString(root, "event", "delta", "partial_json");
                if (string.IsNullOrWhiteSpace(partialJson))
                {
                    return;
                }

                var toolKey = state.ProviderItemId ?? state.ItemId ?? string.Empty;
                if (_tools.TryGetValue(toolKey, out var tool))
                {
                    tool.InputJson.Append(partialJson);
                    if (TryUpdateClaudePresentationFromInputJson(tool))
                    {
                        _emit(CreateEvent("item.updated", _activeTurnId, tool.ItemId, null, "claude.agent-sdk", "content_block_delta", root, appServerControlEvent =>
                        {
                            appServerControlEvent.Item = new AppServerControlProviderItemPayload
                            {
                                ItemType = tool.ItemType,
                                Status = "in_progress",
                                Title = tool.Title,
                                Detail = tool.Presentation.Subject,
                                ToolPresentation = AppServerControlToolPresentationProjector.Clone(tool.Presentation)
                            };
                        }, rawLine));
                    }
                }

                break;
            }
            case "thinking_delta":
            {
                var delta = GetString(root, "event", "delta", "thinking");
                if (!string.IsNullOrWhiteSpace(delta))
                {
                    _emit(CreateEvent("content.delta", _activeTurnId, state.ItemId, null, "claude.agent-sdk", "content_block_delta", root, appServerControlEvent =>
                    {
                        appServerControlEvent.ContentDelta = new AppServerControlProviderContentDeltaPayload
                        {
                            StreamKind = "reasoning_text",
                            Delta = delta
                        };
                    }, rawLine));
                }
                break;
            }
        }
    }

    private void HandleAssistant(JsonElement root, string rawLine)
    {
        UpdateClaudeMessageUsage(root, Traverse(root, "message", "usage"), rawLine);
        if (GetString(root, "message", "id") is { } messageId &&
            !string.Equals(messageId, _currentAssistantItemId, StringComparison.Ordinal))
        {
            _currentAssistantItemId = messageId;
            _assistantStreamEmitted = false;
        }
        var text = JoinClaudeAssistantText(root);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_activeTurnId))
        {
            return;
        }

        EmitAssistantMessage(text, root, rawLine);
    }

    private void EmitAssistantMessage(string text, JsonElement root, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_activeTurnId))
        {
            return;
        }

        if (!_assistantStreamEmitted)
        {
            EmitAssistantDelta(text, root, rawLine);
        }

        _assistantMessageEmitted = true;
        var itemId = _currentAssistantItemId ?? $"assistant:{_activeTurnId}";
        _emit(CreateEvent("item.completed", _activeTurnId, itemId, null, "claude.agent-sdk", "assistant", root, appServerControlEvent =>
        {
            appServerControlEvent.Item = new AppServerControlProviderItemPayload
            {
                ItemType = "assistant_message",
                Status = "completed",
                Title = "Assistant message",
                Detail = text
            };
        }, rawLine));
    }

    private void HandleUser(JsonElement root, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(_activeTurnId) ||
            !root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        using var toolResultItems = content.EnumerateArray();
        while (toolResultItems.MoveNext())
        {
            var item = toolResultItems.Current;
            if (!string.Equals(GetString(item, "type"), "tool_result", StringComparison.Ordinal))
            {
                continue;
            }

            var toolUseId = GetString(item, "tool_use_id") ?? GetString(root, "tool_use_result", "tool_use_id");
            if (string.IsNullOrWhiteSpace(toolUseId) || !_tools.TryGetValue(toolUseId, out var tool))
            {
                continue;
            }

            TryUpdateClaudePresentationFromInputJson(tool);
            AppServerControlToolPresentationProjector.AppendClaudeResult(item, root, tool.Output);
            var failed = GetBoolean(item, "is_error") || GetBoolean(root, "tool_use_result", "is_error");
            var status = failed && !tool.IsPlanReview ? "failed" : "completed";
            AppServerControlToolPresentationProjector.ApplyOutput(tool.Presentation, tool.Output, status);

            _emit(CreateEvent("item.completed", _activeTurnId, tool.ItemId, null, "claude.agent-sdk", "tool_result", root, appServerControlEvent =>
            {
                appServerControlEvent.Item = new AppServerControlProviderItemPayload
                {
                    ItemType = tool.ItemType,
                    Status = status,
                    Title = tool.IsPlanReview ? "Plan review" : tool.Title,
                    Detail = tool.Presentation.Subject,
                    ToolPresentation = AppServerControlToolPresentationProjector.Clone(tool.Presentation)
                };
            }, rawLine));
            _tools.Remove(toolUseId);
        }
    }

    private void HandleResult(JsonElement root, string rawLine)
    {
        UpdateClaudeResultUsage(root, rawLine);
        if (string.IsNullOrWhiteSpace(_activeTurnId))
        {
            return;
        }

        var resultText = GetString(root, "result");
        var compactFailed = _manualCompactionTurn && (_compactionFailure is not null ||
            resultText?.StartsWith("Error during compaction:", StringComparison.OrdinalIgnoreCase) == true);
        var isError = GetBoolean(root, "is_error") || compactFailed;
        var subtype = compactFailed ? "compaction_failed" : GetString(root, "subtype") ?? (isError ? "error" : "success");
        if (compactFailed && _usage is not null)
        {
            _usage.ContextUsedTokens = _compactionContextBefore;
            EmitClaudeUsage(root, rawLine);
        }
        var errorMessage = isError ? _compactionFailure ?? resultText : null;
        if (!isError &&
            !_assistantMessageEmitted &&
            !string.IsNullOrWhiteSpace(resultText))
        {
            EmitAssistantMessage(resultText, root, rawLine);
        }

        var terminalReason = GetString(root, "terminal_reason");
        if (_interruptRequested)
        {
            _interruptHasQueuedWork |= GetInt32(root, "queued_turn_count") is > 0;
            _interruptSettled?.TrySetResult();
            return;
        }
        if (GetInt32(root, "queued_turn_count") is > 0 ||
            (_steeringPending && !_interruptRequested && terminalReason is "aborted_streaming" or "aborted_tools"))
        {
            _assistantMessageEmitted = false;
            return;
        }

        var completedTurnId = _activeTurnId;
        ResetTurnState();
        _emit(CreateEvent("turn.completed", completedTurnId, null, null, "claude.agent-sdk", "result", root, appServerControlEvent =>
        {
            appServerControlEvent.TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = isError ? "failed" : "completed",
                StateLabel = isError ? "Failed" : "Completed",
                StopReason = subtype,
                ErrorMessage = errorMessage
            };
        }, rawLine));
        _emit(CreateEvent("session.state.changed", completedTurnId, null, null, "claude.agent-sdk", "result", root, appServerControlEvent =>
        {
            appServerControlEvent.SessionState = new AppServerControlProviderSessionStatePayload
            {
                State = isError ? "error" : "ready",
                StateLabel = isError ? "Error" : "Ready",
                Reason = string.IsNullOrWhiteSpace(resultText)
                    ? (isError ? "Claude turn failed." : "Claude turn completed.")
                    : resultText
            };
        }, rawLine));

    }

    private void EnsureProviderThreadId(JsonElement root)
    {
        var providerThreadId = GetString(root, "session_id");
        if (string.IsNullOrWhiteSpace(providerThreadId) || string.Equals(providerThreadId, _providerThreadId, StringComparison.Ordinal))
        {
            return;
        }

        _providerThreadId = providerThreadId;
        _emit(CreateEvent("thread.started", null, null, null, "claude.agent-sdk", "session_id", root, appServerControlEvent =>
        {
            appServerControlEvent.ThreadState = new AppServerControlProviderThreadStatePayload
            {
                State = "active",
                StateLabel = "Active",
                ProviderThreadId = providerThreadId
            };
        }));
    }

    private void EmitAssistantDelta(string text, JsonElement root, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_activeTurnId))
        {
            return;
        }

        _assistantStreamEmitted = true;
        var itemId = _currentAssistantItemId ?? $"assistant:{_activeTurnId}";
        _emit(CreateEvent("content.delta", _activeTurnId, itemId, null, "claude.agent-sdk", "assistant", root, appServerControlEvent =>
        {
            appServerControlEvent.ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = text
            };
        }, rawLine));
    }

    private void EmitRuntimeMessage(string eventType, string message, string? detail)
    {
        _emit(CreateEvent(eventType, _activeTurnId, null, null, "mtagenthost.claude", eventType, new { message, detail }, appServerControlEvent =>
        {
            appServerControlEvent.RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = message,
                Detail = detail
            };
        }));
    }

    private HostCommandOutcome Accepted(
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
                Accepted = accepted ?? new AppServerControlCommandAcceptedResponse
                {
                    SessionId = sessionId,
                    Status = "accepted",
                    TurnId = _activeTurnId
                }
            },
            Events = events ?? []
        };
    }

    private AppServerControlProviderEvent CreateEvent(
        string eventType,
        string? turnId,
        string? itemId,
        string? requestId,
        string source,
        string? method,
        object? payload,
        Action<AppServerControlProviderEvent>? configure = null,
        string? rawPayloadJson = null)
    {
        var appServerControlEvent = new AppServerControlProviderEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            EventId = $"evt-{Provider}-{_sequence.ToString(CultureInfo.InvariantCulture)}",
            SessionId = _sessionId ?? string.Empty,
            Provider = Provider,
            ThreadId = _providerThreadId ?? _sessionId ?? string.Empty,
            TurnId = turnId,
            ItemId = itemId,
            RequestId = requestId,
            CreatedAt = _replayingHistory && payload is JsonElement historical &&
                DateTimeOffset.TryParse(GetString(historical, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : DateTimeOffset.UtcNow,
            Type = eventType,
            Raw = new AppServerControlProviderEventRaw
            {
                Source = source,
                Method = method,
                PayloadOmitted = payload is not null || !string.IsNullOrEmpty(rawPayloadJson)
            }
        };
        configure?.Invoke(appServerControlEvent);
        return appServerControlEvent;
    }

    private void EnsureAttached()
    {
        if (string.IsNullOrWhiteSpace(_sessionId) ||
            string.IsNullOrWhiteSpace(_workingDirectory) ||
            string.IsNullOrWhiteSpace(_binaryPath))
        {
            throw new InvalidOperationException("Claude App Server Controller runtime is not attached.");
        }
    }

    private async Task EnsureClaudeBridgeAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var nodePath = FindExecutableInPath("node");
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            throw new InvalidOperationException("Node.js 18 or newer is required for the Claude Agent SDK runtime.");
        }

        var bridgePath = ExtractBridgeResource();
        var process = new Process
        {
            StartInfo = CreateBridgeProcessStartInfo(nodePath, bridgePath, _workingDirectory!),
            EnableRaisingEvents = true
        };
        AppServerControlProviderRuntimeConfiguration.ApplyUserProfileEnvironment(process.StartInfo, _userProfileDirectory);
        AppServerControlProviderRuntimeConfiguration.ApplyEnvironmentVariables(process.StartInfo, Provider);
        process.StartInfo.Environment.Remove("FORCE_COLOR");
        process.StartInfo.Environment["NO_COLOR"] = "1";
        if (!process.Start())
        {
            throw new InvalidOperationException("Claude Agent SDK bridge could not be started.");
        }

        _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AttachOwnedProcess(process);
        _readerTask = Task.Run(() => ReadLoopAsync(process, CancellationToken.None), CancellationToken.None);
        _errorTask = Task.Run(() => ReadErrorLoopAsync(process, CancellationToken.None), CancellationToken.None);
        await WriteBridgeCommandAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "initialize");
            writer.WriteString("executablePath", _binaryPath);
            writer.WriteString("cwd", _workingDirectory);
            writer.WriteString("sessionId", _newProviderSessionId);
            writer.WriteString("permissionMode", _quickSettings.PermissionMode);
            writer.WriteBoolean("loadHistory", _sessionId is not null && !_hasRetainedConversation(_sessionId));
            AppServerControlMcpConfiguration.WriteClaude(writer, _mcpCredentials?.Server);
            if (_quickSettings.Model is not null) writer.WriteString("model", _quickSettings.Model);
            if (!string.IsNullOrWhiteSpace(_providerThreadId))
            {
                writer.WriteString("resume", _providerThreadId);
            }
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
        await _bridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
    }

    private async Task SendTurnCommandAsync(
        string commandType,
        string prompt,
        IReadOnlyList<AppServerControlAttachmentReference> attachments,
        IReadOnlyList<string> addDirectories,
        string? model,
        string? effort,
        string planMode,
        string permissionMode,
        CancellationToken ct)
    {
        await WriteBridgeCommandAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", commandType);
            writer.WriteString("turnId", _activeTurnId);
            writer.WriteString("prompt", prompt);
            if (!string.IsNullOrWhiteSpace(model)) writer.WriteString("model", model);
            if (!string.IsNullOrWhiteSpace(effort)) writer.WriteString("effort", effort);
            writer.WriteString("planMode", string.Equals(planMode, AppServerControlQuickSettings.PlanModeOn, StringComparison.Ordinal) ? "plan" : "off");
            writer.WriteString("permissionMode", permissionMode);
            writer.WriteStartArray("attachments");
            foreach (var attachment in attachments.Where(static value =>
                         string.Equals(value.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(value.Path)))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "image");
                writer.WriteString("path", attachment.Path);
                writer.WriteString("mimeType", ResolveClaudeImageMimeType(attachment));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("additionalDirectories");
            foreach (var directory in addDirectories.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStringValue(directory);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);
    }

    private async Task WriteBridgeCommandAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        var input = _input ?? throw new InvalidOperationException("Claude Agent SDK bridge input stream is unavailable.");
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        await input.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await input.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteBridgeResolutionCommandAsync(
        string resolutionType,
        string requestId,
        Action<Utf8JsonWriter> write,
        CancellationToken ct)
    {
        var key = BridgeResolutionAckKey(resolutionType, requestId);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_bridgeResolutionAcks.TryAdd(key, ack))
        {
            throw new InvalidOperationException($"Claude bridge resolution is already pending for request '{requestId}'.");
        }

        try
        {
            await WriteBridgeCommandAsync(write, ct).ConfigureAwait(false);
            await ack.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        finally
        {
            _bridgeResolutionAcks.TryRemove(key, out _);
        }
    }

    private void CompleteBridgeResolutionAck(string resolutionType, string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId) &&
            _bridgeResolutionAcks.TryGetValue(BridgeResolutionAckKey(resolutionType, requestId), out var ack))
        {
            ack.TrySetResult();
        }
    }

    private void FailBridgeResolutionAck(string? commandType, string? requestId, Exception error)
    {
        var resolutionType = commandType switch
        {
            "permission.resolve" => "permission",
            "user_input.resolve" => "user_input",
            "turn.interrupt" => "interrupt",
            _ => null
        };
        if (resolutionType is not null && !string.IsNullOrWhiteSpace(requestId) &&
            _bridgeResolutionAcks.TryGetValue(BridgeResolutionAckKey(resolutionType, requestId), out var ack))
        {
            ack.TrySetException(error);
        }
    }

    private static string BridgeResolutionAckKey(string resolutionType, string requestId) =>
        $"{resolutionType}:{requestId}";

    private static string ExtractBridgeResource()
    {
        const string resourceName = "Ai.Tlbx.MidTerm.AgentHost.ClaudeBridge.claude-agent-sdk-bridge.mjs";
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded Claude Agent SDK bridge resource was not found.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Path.GetTempPath(), "tlbx", "claude-agent-sdk-bridge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"claude-agent-sdk-bridge-0.3.170-{hash}.mjs");
        if (File.Exists(path))
        {
            return path;
        }

        var temporaryPath = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        try
        {
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporaryPath);
        }
        return path;
    }

    private static ProcessStartInfo CreateBridgeProcessStartInfo(string nodePath, string bridgePath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
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
        startInfo.ArgumentList.Add(bridgePath);
        return startInfo;
    }

    private async Task DisposeProcessAsync(bool resetTurnState = true)
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                try
                {
                    await WriteBridgeCommandAsync(writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "shutdown");
                        writer.WriteEndObject();
                    }, CancellationToken.None).ConfigureAwait(false);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch
        {
        }

        DisposeOwnedProcessHandles();

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

        if (resetTurnState)
        {
            ResetTurnState();
        }
    }

    private void AttachOwnedProcess(Process process)
    {
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _error?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
        _error = null;
        _process = process;
        _output = process.StandardOutput;
        _error = process.StandardError;
        _input = process.StandardInput;
    }

    private void DisposeOwnedProcessHandles()
    {
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _error?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _input = null;
        _output = null;
        _error = null;
    }

    private static string BuildPromptInput(
        AppServerControlTurnRequest request,
        out List<string> addDirectories)
    {
        addDirectories = [];
        var text = request.Text ?? string.Empty;
        if (request.Attachments.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine(text);
            builder.AppendLine();
        }

        builder.AppendLine(request.Attachments.Count == 1 ? "Attached resource:" : $"Attached resources ({request.Attachments.Count.ToString(CultureInfo.InvariantCulture)}):");
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

            var parent = Path.GetDirectoryName(attachment.Path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                addDirectories.Add(parent);
            }

            builder.Append("- ");
            builder.Append(string.Equals(attachment.Kind, "image", StringComparison.OrdinalIgnoreCase) ? "[image] " : "[file] ");
            builder.AppendLine(attachment.Path);
        }

        return builder.ToString().Trim();
    }

    private static string ResolveClaudeImageMimeType(AppServerControlAttachmentReference attachment)
    {
        var mimeType = string.IsNullOrWhiteSpace(attachment.MimeType)
            ? Path.GetExtension(attachment.Path).ToLowerInvariant() switch
            {
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => null
            }
            : attachment.MimeType.Trim().ToLowerInvariant();
        return mimeType is "image/gif" or "image/jpeg" or "image/png" or "image/webp"
            ? mimeType
            : throw new InvalidOperationException(
                $"Claude Agent SDK supports pasted GIF, JPEG, PNG, and WebP images; '{attachment.Path}' has type '{mimeType ?? "unknown"}'.");
    }

    private AppServerControlQuickSettingsSummary CreateDefaultQuickSettings()
    {
        var defaultPermissionMode = AppServerControlProviderRuntimeConfiguration.GetClaudeDangerouslySkipPermissionsDefault()
            ? AppServerControlQuickSettings.PermissionModeAuto
            : AppServerControlQuickSettings.PermissionModeManual;
        return AppServerControlQuickSettings.CreateSummary(
            AppServerControlProviderRuntimeConfiguration.GetClaudeDefaultModel(),
            null,
            AppServerControlQuickSettings.PlanModeOff,
            defaultPermissionMode,
            defaultPermissionMode);
    }

    private AppServerControlQuickSettingsSummary ResolveRequestedQuickSettings(AppServerControlTurnRequest request)
    {
        var defaultPermissionMode = AppServerControlProviderRuntimeConfiguration.GetClaudeDangerouslySkipPermissionsDefault()
            ? AppServerControlQuickSettings.PermissionModeAuto
            : AppServerControlQuickSettings.PermissionModeManual;
        var settings = AppServerControlQuickSettings.CreateSummary(
            request.Model ?? AppServerControlProviderRuntimeConfiguration.GetClaudeDefaultModel(),
            request.Effort,
            request.PlanMode,
            request.PermissionMode,
            defaultPermissionMode);
        settings.ModelOptions = AppServerControlQuickSettings.CloneOptions(_quickSettings.ModelOptions);
        settings.EffortOptions = ReadClaudeEffortOptions(settings.Model);
        return settings;
    }

    private AppServerControlProviderEvent CreateQuickSettingsUpdatedEvent(
        AppServerControlQuickSettingsSummary quickSettings,
        string source,
        string? method,
        object? payload)
    {
        return CreateEvent("quick-settings.updated", null, null, null, source, method, payload, appServerControlEvent =>
        {
            appServerControlEvent.QuickSettingsUpdated = AppServerControlQuickSettings.ToPayload(quickSettings);
        });
    }

    private static string JoinClaudeAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        using var textItems = content.EnumerateArray();
        while (textItems.MoveNext())
        {
            var item = textItems.Current;
            if (string.Equals(GetString(item, "type"), "text", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(GetString(item, "text"));
            }
        }

        return builder.ToString();
    }

    private static bool TryUpdateClaudePresentationFromInputJson(ClaudeToolState tool)
    {
        if (tool.InputJson.Length == 0 || tool.InputJson.IsTruncated)
        {
            return false;
        }

        var inputJson = tool.InputJson.ToString();
        if (!inputJson.AsSpan().TrimEnd().EndsWith("}".AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var updated = AppServerControlToolPresentationProjector.FromClaude(
                tool.Title,
                "in_progress",
                document.RootElement);
            var previousSubject = tool.Presentation.Subject;
            AppServerControlToolPresentationProjector.Merge(tool.Presentation, updated);
            return !string.Equals(previousSubject, tool.Presentation.Subject, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeToolItemType(string? toolName)
    {
        var normalized = toolName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is "bash" or "powershell" || normalized.Contains("shell", StringComparison.Ordinal))
        {
            return "command_execution";
        }
        if (normalized is "edit" or "write" or "notebookedit" || normalized.Contains("file", StringComparison.Ordinal))
        {
            return "file_change";
        }
        if (normalized.Contains("websearch", StringComparison.Ordinal))
        {
            return "web_search";
        }
        if (normalized.Contains("mcp", StringComparison.Ordinal))
        {
            return "mcp_tool_call";
        }
        return "dynamic_tool_call";
    }

    private static bool IsReasoningBlock(string? blockType)
    {
        return string.Equals(blockType, "thinking", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(blockType, "redacted_thinking", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindExecutableInPath(string commandName)
    {
        if (Path.IsPathRooted(commandName) && File.Exists(commandName))
        {
            return commandName;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        var candidateNames = OperatingSystem.IsWindows() ? GetWindowsExecutableNames(commandName) : [commandName];
        foreach (var rawDirectory in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

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

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = Traverse(element, path);
        return current is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static bool GetBoolean(JsonElement element, params string[] path)
    {
        var current = Traverse(element, path);
        return current is { ValueKind: JsonValueKind.True } || current is { ValueKind: JsonValueKind.False } value && value.GetBoolean();
    }

    private static int? GetInt32(JsonElement element, params string[] path)
    {
        var current = Traverse(element, path);
        return current is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
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

    private void ResetTurnState()
    {
        _activeTurnId = null;
        _activeTurnModel = null;
        _activeTurnEffort = null;
        _currentAssistantItemId = null;
        _assistantStreamEmitted = false;
        _turnStarted = false;
        _assistantMessageEmitted = false;
        _interruptRequested = false;
        _steeringPending = false;
        _manualCompactionTurn = false;
        _compactionFailure = null;
        _compactionContextBefore = null;
        _blocks.Clear();
        _tools.Clear();
    }

    private sealed class ClaudeBlockState
    {
        public string Type { get; set; } = string.Empty;
        public string? ProviderItemId { get; set; }
        public string? ItemId { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    private sealed class ClaudeToolState
    {
        public bool IsPlanReview { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public AppServerControlToolPresentation Presentation { get; set; } = new();
        public BoundedTextAccumulator InputJson { get; } = new(4_096);
        public BoundedToolOutputAccumulator Output { get; } = new();
    }

}
