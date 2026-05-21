using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.Common.Ipc;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;

namespace Ai.Tlbx.MidTerm.AgentHost;

internal sealed class AppServerControlAgentHostServer : IAsyncDisposable
{
    private readonly string? _syntheticProvider;
    private readonly string? _ownerInstanceId;
    private readonly string? _ownerToken;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TtyHostSessionManager _historySessions = new();
    private readonly SessionAppServerControlHistoryService _history;
    private readonly Lock _clientLock = new();
    private ConnectionState? _currentClient;
    private IAppServerControlAgentRuntime? _runtime;
    private readonly List<Task> _connectionTasks = [];

    public AppServerControlAgentHostServer(string? syntheticProvider, string? ownerInstanceId = null, string? ownerToken = null)
    {
        _syntheticProvider = string.IsNullOrWhiteSpace(syntheticProvider)
            ? null
            : syntheticProvider.Trim().ToLowerInvariant();
        _ownerInstanceId = string.IsNullOrWhiteSpace(ownerInstanceId) ? null : ownerInstanceId;
        _ownerToken = string.IsNullOrWhiteSpace(ownerToken) ? null : ownerToken;
        _history = new SessionAppServerControlHistoryService(sessionManager: _historySessions);
    }

    public async Task RunStdioAsync()
    {
        using var connection = ConnectionState.CreateStdioOwned(_shutdown.Token);

        PromoteCurrentClient(connection);
        await ProcessConnectionAsync(connection, requireOwnership: false, promoteOnAttach: false).ConfigureAwait(false);
    }

    public async Task RunIpcAsync(string endpoint)
    {
        using var server = IpcServerFactory.Create(endpoint);

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
#pragma warning disable IDISP004
                var task = ProcessAcceptedConnectionAsync(await server.AcceptAsync(_shutdown.Token).ConfigureAwait(false));
#pragma warning restore IDISP004
                lock (_connectionTasks)
                {
                    _connectionTasks.Add(task);
                }

                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (_connectionTasks)
                        {
                            _connectionTasks.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
            }
        }

        Task[] remaining;
        lock (_connectionTasks)
        {
            remaining = _connectionTasks.ToArray();
        }

        if (remaining.Length > 0)
        {
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        lock (_clientLock)
        {
            _currentClient?.Dispose();
            _currentClient = null;
        }

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }

        Task[] remaining;
        lock (_connectionTasks)
        {
            remaining = _connectionTasks.ToArray();
        }

        if (remaining.Length > 0)
        {
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private async Task ProcessAcceptedConnectionAsync(IIpcClientConnection client)
    {
        using var connection = ConnectionState.CreateOwned(client.Stream, _shutdown.Token);
        await ProcessConnectionAsync(connection, requireOwnership: true, promoteOnAttach: true).ConfigureAwait(false);
    }

    private async Task ProcessConnectionAsync(ConnectionState connection, bool requireOwnership, bool promoteOnAttach)
    {
        try
        {
            await EnqueueHelloAsync(connection).ConfigureAwait(false);
            await ProcessIncomingAsync(connection, requireOwnership, promoteOnAttach).ConfigureAwait(false);
        }
        finally
        {
            ClearCurrentClient(connection);
        }
    }

    private void ClearCurrentClient(ConnectionState connection)
    {
        lock (_clientLock)
        {
            if (ReferenceEquals(_currentClient, connection))
            {
                Interlocked.CompareExchange(ref _currentClient, null, connection);
            }
        }
    }

    private async Task ProcessIncomingAsync(ConnectionState connection, bool requireOwnership, bool promoteOnAttach)
    {
        while (!connection.Token.IsCancellationRequested &&
               await connection.Reader.ReadLineAsync(connection.Token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AppServerControlHostCommandEnvelope? command;
            try
            {
                command = JsonSerializer.Deserialize(line, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);
            }
            catch (JsonException ex)
            {
                await EnqueueAsync(
                    connection,
                    new AppServerControlHostCommandResultEnvelope
                    {
                        CommandId = "invalid-json",
                        SessionId = string.Empty,
                        Status = "rejected",
                        Message = ex.Message
                    },
                    AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope).ConfigureAwait(false);
                continue;
            }

            if (command is null)
            {
                continue;
            }

            if (!connection.OwnerValidated)
            {
                if (!string.Equals(command.Type, "runtime.attach", StringComparison.Ordinal))
                {
                    await EnqueueRejectedAsync(connection, command, "runtime.attach must be the first command sent to mtagenthost.").ConfigureAwait(false);
                    break;
                }

                if (requireOwnership && !ValidateOwnership(command.AttachRuntime))
                {
                    await EnqueueRejectedAsync(connection, command, "mtagenthost ownership mismatch").ConfigureAwait(false);
                    break;
                }

                connection.OwnerValidated = true;
                if (promoteOnAttach)
                {
                    PromoteCurrentClient(connection);
                }
            }

            if (!connection.TryBindSession(command.SessionId))
            {
                await EnqueueRejectedAsync(connection, command, "mtagenthost only serves one App Server Controller session per connection.").ConfigureAwait(false);
                break;
            }

            EnsureHistoryPatchForwarder(connection, command.SessionId);

            HostCommandOutcome outcome;
            try
            {
                outcome = await ExecuteCommandAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = new HostCommandOutcome
                {
                    Result = new AppServerControlHostCommandResultEnvelope
                    {
                        CommandId = command.CommandId,
                        SessionId = command.SessionId,
                        Status = "rejected",
                        Message = ex.Message
                    }
                };
            }

            foreach (var appServerControlEvent in outcome.Events)
            {
                StoreRuntimeEvent(appServerControlEvent);
            }
            await EnqueueAsync(connection, outcome.Result, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope).ConfigureAwait(false);
        }
    }

    private async Task<HostCommandOutcome> ExecuteCommandAsync(AppServerControlHostCommandEnvelope command)
    {
        ValidateCommand(command);

        if (string.Equals(command.Type, "history.window.get", StringComparison.Ordinal))
        {
            return new HostCommandOutcome
            {
                Result = new AppServerControlHostCommandResultEnvelope
                {
                    CommandId = command.CommandId,
                    SessionId = command.SessionId,
                    Status = "accepted",
                    HistoryWindow = GetHistoryWindow(
                        command.SessionId,
                        command.HistoryWindow?.StartIndex,
                        command.HistoryWindow?.Count,
                        command.HistoryWindow?.ViewportWidth)
                }
            };
        }

        if (command.AttachRuntime is not null)
        {
            _historySessions.SetWorkingDirectory(command.SessionId, command.AttachRuntime.WorkingDirectory);
        }

        var runtime = await GetRuntimeAsync(command).ConfigureAwait(false);
        var outcome = await runtime.ExecuteAsync(command, _shutdown.Token).ConfigureAwait(false);
        return MaybeAppendSubmittedUserMessage(command, outcome, runtime.Provider);
    }

    private AppServerControlHistoryWindowResponse? GetHistoryWindow(
        string sessionId,
        int? startIndex,
        int? count,
        int? viewportWidth)
    {
        return _history.GetSnapshotWindow(sessionId, startIndex, count, viewportWidth);
    }

    private static HostCommandOutcome MaybeAppendSubmittedUserMessage(
        AppServerControlHostCommandEnvelope command,
        HostCommandOutcome outcome,
        string provider)
    {
        if (!string.Equals(command.Type, "turn.start", StringComparison.Ordinal))
        {
            return outcome;
        }

        var request = command.StartTurn;
        var turnStarted = outcome.Result.TurnStarted;
        if (request is null ||
            turnStarted is null ||
            !string.Equals(outcome.Result.Status, "accepted", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(request.Text) && request.Attachments.Count == 0))
        {
            return outcome;
        }

        var events = outcome.Events.ToList();
        events.Insert(0, new AppServerControlProviderEvent
        {
            EventId = $"evt-user-{Guid.NewGuid():N}",
            SessionId = command.SessionId,
            Provider = turnStarted.Provider,
            ThreadId = turnStarted.ThreadId,
            TurnId = turnStarted.TurnId,
            ItemId = $"user:{turnStarted.TurnId ?? Guid.NewGuid().ToString("N")}",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Title = "User message",
                Detail = request.Text,
                Attachments = CloneAttachments(request.Attachments)
            }
        });

        return new HostCommandOutcome
        {
            Result = outcome.Result,
            Events = events
        };
    }

    private void PromoteCurrentClient(ConnectionState nextClient)
    {
        lock (_clientLock)
        {
            if (ReferenceEquals(_currentClient, nextClient))
            {
                return;
            }

            var previous = _currentClient;
            _currentClient = nextClient;
            previous?.Dispose();
        }
    }

    private bool ValidateOwnership(AppServerControlAttachRuntimeRequest? attach)
    {
        if (attach is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_ownerInstanceId) || string.IsNullOrWhiteSpace(_ownerToken))
        {
            return true;
        }

        return string.Equals(attach.InstanceId, _ownerInstanceId, StringComparison.Ordinal) &&
               string.Equals(attach.OwnerToken, _ownerToken, StringComparison.Ordinal);
    }

    private async Task EnqueueHelloAsync(ConnectionState connection)
    {
        await EnqueueAsync(
            connection,
            new AppServerControlHostHello
            {
                HostKind = "mtagenthost",
                HostVersion = "dev",
                Providers = _syntheticProvider is null ? ["codex", "claude", "grok"] : [_syntheticProvider],
                Capabilities =
                [
                    "attach",
                    "turn.start",
                    "turn.interrupt",
                    "thread.goal.set",
                    "request.resolve",
                    "user-input.resolve",
                    "history.window.get",
                    "history.patch"
                ]
            },
            AppServerControlHostJsonContext.Default.AppServerControlHostHello).ConfigureAwait(false);
    }

    private async Task EnqueueRejectedAsync(ConnectionState connection, AppServerControlHostCommandEnvelope command, string message)
    {
        await EnqueueAsync(
            connection,
            new AppServerControlHostCommandResultEnvelope
            {
                CommandId = command.CommandId,
                SessionId = command.SessionId,
                Status = "rejected",
                Message = message
            },
            AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope).ConfigureAwait(false);
    }

    private async Task<IAppServerControlAgentRuntime> GetRuntimeAsync(AppServerControlHostCommandEnvelope command)
    {
        if (_runtime is not null)
        {
            return _runtime;
        }

        if (!string.Equals(command.Type, "runtime.attach", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("runtime.attach must be the first command sent to mtagenthost.");
        }

        var provider = _syntheticProvider ?? command.AttachRuntime?.Provider?.Trim().ToLowerInvariant();
        _runtime = provider switch
        {
            "codex" when _syntheticProvider is null => new CodexAppServerControlAgentRuntime(EmitRuntimeEvent),
            "claude" when _syntheticProvider is null => new ClaudeAppServerControlAgentRuntime(EmitRuntimeEvent),
            "grok" when _syntheticProvider is null => new GrokAcpAppServerControlAgentRuntime(EmitRuntimeEvent),
            "codex" => new SyntheticAppServerControlAgentRuntime(provider, EmitRuntimeEvent),
            "claude" when _syntheticProvider is not null => new SyntheticAppServerControlAgentRuntime(provider, EmitRuntimeEvent),
            "grok" when _syntheticProvider is not null => new SyntheticAppServerControlAgentRuntime(provider, EmitRuntimeEvent),
            _ => throw new InvalidOperationException($"mtagenthost does not support provider '{provider ?? "(null)"}'.")
        };

        return _runtime;
    }

    private static void ValidateCommand(AppServerControlHostCommandEnvelope command)
    {
        if (!string.Equals(command.ProtocolVersion, AppServerControlHostProtocol.CurrentVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported protocol version '{command.ProtocolVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            throw new InvalidOperationException("Command id is required.");
        }

        if (string.IsNullOrWhiteSpace(command.SessionId))
        {
            throw new InvalidOperationException("Session id is required.");
        }
    }

    private void EmitRuntimeEvent(AppServerControlProviderEvent appServerControlEvent)
    {
        StoreRuntimeEvent(appServerControlEvent);
    }

    private void StoreRuntimeEvent(AppServerControlProviderEvent appServerControlEvent)
    {
        ArgumentNullException.ThrowIfNull(appServerControlEvent);
        _history.Append(CloneEvent(appServerControlEvent));
    }

    private void EnsureHistoryPatchForwarder(ConnectionState connection, string sessionId)
    {
        if (connection.HistoryPatchForwarderStarted)
        {
            return;
        }

        connection.HistoryPatchForwarderStarted = true;
        connection.HistoryPatchForwarder = Task.Run(
            async () =>
            {
                using var subscription = _history.SubscribeHistoryPatches(sessionId, connection.Token);
                try
                {
                    await foreach (var delta in subscription.Reader.ReadAllAsync(connection.Token).ConfigureAwait(false))
                    {
                        connection.Outbound.Writer.TryWrite(
                            JsonSerializer.Serialize(
                                new AppServerControlHostHistoryPatchEnvelope
                                {
                                    SessionId = sessionId,
                                    Patch = delta
                                },
                                AppServerControlHostJsonContext.Default.AppServerControlHostHistoryPatchEnvelope));
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            },
            CancellationToken.None);
    }

    private static AppServerControlProviderEvent CloneEvent(AppServerControlProviderEvent appServerControlEvent)
    {
        return new AppServerControlProviderEvent
        {
            Sequence = appServerControlEvent.Sequence,
            EventId = appServerControlEvent.EventId,
            SessionId = appServerControlEvent.SessionId,
            Provider = appServerControlEvent.Provider,
            ThreadId = appServerControlEvent.ThreadId,
            TurnId = appServerControlEvent.TurnId,
            ItemId = appServerControlEvent.ItemId,
            RequestId = appServerControlEvent.RequestId,
            CreatedAt = appServerControlEvent.CreatedAt,
            Type = appServerControlEvent.Type,
            Raw = appServerControlEvent.Raw is null ? null : new AppServerControlProviderEventRaw
            {
                Source = appServerControlEvent.Raw.Source,
                Method = appServerControlEvent.Raw.Method,
                PayloadJson = appServerControlEvent.Raw.PayloadJson
            },
            SessionState = appServerControlEvent.SessionState is null ? null : new AppServerControlProviderSessionStatePayload
            {
                State = appServerControlEvent.SessionState.State,
                StateLabel = appServerControlEvent.SessionState.StateLabel,
                Reason = appServerControlEvent.SessionState.Reason
            },
            ThreadState = appServerControlEvent.ThreadState is null ? null : new AppServerControlProviderThreadStatePayload
            {
                State = appServerControlEvent.ThreadState.State,
                StateLabel = appServerControlEvent.ThreadState.StateLabel,
                ProviderThreadId = appServerControlEvent.ThreadState.ProviderThreadId
            },
            TurnStarted = appServerControlEvent.TurnStarted is null ? null : new AppServerControlProviderTurnStartedPayload
            {
                Model = appServerControlEvent.TurnStarted.Model,
                Effort = appServerControlEvent.TurnStarted.Effort
            },
            TurnCompleted = appServerControlEvent.TurnCompleted is null ? null : new AppServerControlProviderTurnCompletedPayload
            {
                State = appServerControlEvent.TurnCompleted.State,
                StateLabel = appServerControlEvent.TurnCompleted.StateLabel,
                StopReason = appServerControlEvent.TurnCompleted.StopReason,
                ErrorMessage = appServerControlEvent.TurnCompleted.ErrorMessage
            },
            ContentDelta = appServerControlEvent.ContentDelta is null ? null : new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = appServerControlEvent.ContentDelta.StreamKind,
                Delta = appServerControlEvent.ContentDelta.Delta
            },
            PlanDelta = appServerControlEvent.PlanDelta is null ? null : new AppServerControlProviderPlanDeltaPayload
            {
                Delta = appServerControlEvent.PlanDelta.Delta
            },
            PlanCompleted = appServerControlEvent.PlanCompleted is null ? null : new AppServerControlProviderPlanCompletedPayload
            {
                PlanMarkdown = appServerControlEvent.PlanCompleted.PlanMarkdown
            },
            DiffUpdated = appServerControlEvent.DiffUpdated is null ? null : new AppServerControlProviderDiffUpdatedPayload
            {
                UnifiedDiff = appServerControlEvent.DiffUpdated.UnifiedDiff
            },
            Item = appServerControlEvent.Item is null ? null : new AppServerControlProviderItemPayload
            {
                ItemType = appServerControlEvent.Item.ItemType,
                Status = appServerControlEvent.Item.Status,
                Title = appServerControlEvent.Item.Title,
                Detail = appServerControlEvent.Item.Detail,
                Attachments = CloneAttachments(appServerControlEvent.Item.Attachments)
            },
            QuickSettingsUpdated = appServerControlEvent.QuickSettingsUpdated is null ? null : new AppServerControlQuickSettingsPayload
            {
                Model = appServerControlEvent.QuickSettingsUpdated.Model,
                Effort = appServerControlEvent.QuickSettingsUpdated.Effort,
                PlanMode = AppServerControlQuickSettings.NormalizePlanMode(appServerControlEvent.QuickSettingsUpdated.PlanMode),
                PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(appServerControlEvent.QuickSettingsUpdated.PermissionMode),
                ModelOptions = AppServerControlQuickSettings.CloneOptions(appServerControlEvent.QuickSettingsUpdated.ModelOptions),
                EffortOptions = AppServerControlQuickSettings.CloneOptions(appServerControlEvent.QuickSettingsUpdated.EffortOptions)
            },
            RequestOpened = appServerControlEvent.RequestOpened is null ? null : new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = appServerControlEvent.RequestOpened.RequestType,
                RequestTypeLabel = appServerControlEvent.RequestOpened.RequestTypeLabel,
                Detail = appServerControlEvent.RequestOpened.Detail
            },
            RequestResolved = appServerControlEvent.RequestResolved is null ? null : new AppServerControlProviderRequestResolvedPayload
            {
                RequestType = appServerControlEvent.RequestResolved.RequestType,
                Decision = appServerControlEvent.RequestResolved.Decision
            },
            UserInputRequested = appServerControlEvent.UserInputRequested is null ? null : new AppServerControlProviderUserInputRequestedPayload
            {
                Questions = appServerControlEvent.UserInputRequested.Questions.Select(CloneQuestion).ToList()
            },
            UserInputResolved = appServerControlEvent.UserInputResolved is null ? null : new AppServerControlProviderUserInputResolvedPayload
            {
                Answers = appServerControlEvent.UserInputResolved.Answers.Select(CloneAnsweredQuestion).ToList()
            },
            RuntimeMessage = appServerControlEvent.RuntimeMessage is null ? null : new AppServerControlProviderRuntimeMessagePayload
            {
                Message = appServerControlEvent.RuntimeMessage.Message,
                Detail = appServerControlEvent.RuntimeMessage.Detail
            }
        };
    }

    private static List<AppServerControlAttachmentReference> CloneAttachments(IReadOnlyList<AppServerControlAttachmentReference>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        return attachments.Select(static attachment => new AppServerControlAttachmentReference
        {
            Kind = attachment.Kind,
            Path = attachment.Path,
            MimeType = attachment.MimeType,
            DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? Path.GetFileName(attachment.Path)
                : attachment.DisplayName
        }).ToList();
    }

    private static AppServerControlQuestion CloneQuestion(AppServerControlQuestion source)
    {
        return new AppServerControlQuestion
        {
            Id = source.Id,
            Header = source.Header,
            Question = source.Question,
            MultiSelect = source.MultiSelect,
            Options = source.Options.Select(static option => new AppServerControlQuestionOption
            {
                Label = option.Label,
                Description = option.Description
            }).ToList()
        };
    }

    private static AppServerControlAnsweredQuestion CloneAnsweredQuestion(AppServerControlAnsweredQuestion source)
    {
        return new AppServerControlAnsweredQuestion
        {
            QuestionId = source.QuestionId,
            Answers = [.. source.Answers]
        };
    }

    private static async Task EnqueueAsync<T>(
        ConnectionState connection,
        T payload,
        JsonTypeInfo<T> typeInfo)
    {
        await connection.Outbound.Writer.WriteAsync(JsonSerializer.Serialize(payload, typeInfo), connection.Token).ConfigureAwait(false);
    }

    private sealed class ConnectionState : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private bool _disposed;

        public static ConnectionState CreateStdioOwned(CancellationToken shutdownToken)
        {
            return new ConnectionState(Console.OpenStandardInput(), Console.OpenStandardOutput(), shutdownToken);
        }

        public static ConnectionState CreateOwned(
            Stream stream,
            CancellationToken shutdownToken)
        {
            return new ConnectionState(stream, stream, shutdownToken);
        }

        private ConnectionState(Stream inputStream, Stream outputStream, CancellationToken shutdownToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            Reader = new StreamReader(inputStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);
            Writer = new StreamWriter(outputStream, System.Text.Encoding.UTF8, bufferSize: 1024, leaveOpen: false) { AutoFlush = true };
            Outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            WriterTask = Task.Run(() => WriteLoopAsync(this), CancellationToken.None);
        }

        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public Channel<string> Outbound { get; }
        public Task WriterTask { get; }
        public CancellationToken Token => _cts.Token;
        public bool OwnerValidated { get; set; }
        public string? SessionId { get; private set; }
        public bool HistoryPatchForwarderStarted { get; set; }
        public Task? HistoryPatchForwarder { get; set; }

        public bool TryBindSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(SessionId))
            {
                SessionId = sessionId;
                return true;
            }

            return string.Equals(SessionId, sessionId, StringComparison.Ordinal);
        }

        private static async Task WriteLoopAsync(ConnectionState state)
        {
            try
            {
                while (await state.Outbound.Reader.WaitToReadAsync(state.Token).ConfigureAwait(false))
                {
                    while (state.Outbound.Reader.TryRead(out var line))
                    {
                        await state.Writer.WriteLineAsync(line).ConfigureAwait(false);
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            Outbound.Writer.TryComplete();
            try { Writer.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { HistoryPatchForwarder?.WaitAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            try { WriterTask.WaitAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
        }
    }
}













