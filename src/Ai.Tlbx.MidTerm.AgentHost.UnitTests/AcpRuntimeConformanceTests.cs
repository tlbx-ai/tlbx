using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class AcpRuntimeConformanceTests
{
    private static string ResolveNodeExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd" } : new[] { "node" };
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Node.js executable not found on PATH for Fake ACP conformance tests.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not satisfied before timeout.");
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task V2CoreConformanceCoversInitializeVersionAuthPlansCancelAndContentBlocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "tlbx-acp-c2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "fake-acp-v2.mjs");
        var logPath = Path.Combine(tempDir, "rpc-log.jsonl");
        var sampleFile = Path.Combine(tempDir, "notes.txt");
        await File.WriteAllTextAsync(sampleFile, "hello attachment", Encoding.UTF8, ct);

        var fakeServerJs = """
import readline from 'node:readline';
import fs from 'node:fs';

const logPath = process.argv[2];
const mode = process.argv[3] || 'agent-auth';
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function log(obj) {
  fs.appendFileSync(logPath, JSON.stringify(obj) + '\n');
}

rl.on('line', (line) => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  log(msg);
  if (msg.method === 'initialize') {
    const authMethods = mode === 'terminal-auth'
      ? [{ type: 'terminal', methodId: 'term-login', name: 'CLI Login' }]
      : [{ type: 'agent', methodId: 'oauth', name: 'OAuth Login' }];
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: 2,
        info: { name: 'fake-acp-v2', version: '2.0.0' },
        capabilities: {
          session: {
            prompt: { image: {}, audio: {}, embeddedContext: {} },
            mcp: { stdio: {} }
          }
        },
        authMethods
      }
    });
  } else if (msg.method === 'auth/login') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else if (msg.method === 'auth/logout') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else if (msg.method === 'session/new') {
    send({ jsonrpc: '2.0', id: msg.id, result: { sessionId: 'ses-v2-1', configOptions: [] } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'replay_complete' } } });
  } else if (msg.method === 'session/prompt') {
    send({ jsonrpc: '2.0', id: msg.id, result: { messageId: 'user-msg-1' } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'state_update', state: 'running' } } });
    // Markdown plan + custom other plan + removal
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'plan_update', plan: { type: 'markdown', planId: 'plan-md', path: 'PLAN.md', content: '# Step 1\nDo work' } } } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'plan_update', plan: { type: '_vendor_graph', planId: 'plan-other', nodes: ['a', 'b'] } } } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'plan_removed', planId: 'plan-md' } } });
    // Rich content blocks
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-v2-1',
        update: {
          sessionUpdate: 'agent_message',
          messageId: 'asst-1',
          content: [
            { type: 'text', text: 'Intro' },
            { type: 'image', data: 'AA==', mimeType: 'image/png', uri: 'file:///img.png' },
            { type: 'audio', data: 'AA==', mimeType: 'audio/wav' },
            { type: 'resource_link', name: 'spec.md', uri: 'file:///spec.md' },
            { type: 'resource', resource: { uri: 'file:///inline.txt', text: 'embedded-body' } },
            { type: 'skill', name: 'deploy-helper' }
          ]
        }
      }
    });
    // Cancellable tool call & terminal
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-v2-1',
        update: {
          sessionUpdate: 'tool_call_update',
          toolCallId: 'tc-1',
          title: 'Long tool',
          kind: 'other',
          status: 'in_progress',
          capabilities: { cancel: {} }
        }
      }
    });
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-v2-1',
        update: {
          sessionUpdate: 'terminal_update',
          terminalId: 'term-1',
          command: 'sleep 30',
          capabilities: { cancel: {} }
        }
      }
    });
    // Open permission request then cancel via $/cancel_request
    send({
      jsonrpc: '2.0',
      id: 'perm-rpc-99',
      method: 'session/request_permission',
      params: {
        sessionId: 'ses-v2-1',
        toolCall: { toolCallId: 'tc-1', title: 'Run check' },
        options: [{ optionId: 'allow-1', name: 'Allow', kind: 'allow_once' }]
      }
    });
    setTimeout(() => {
      send({ jsonrpc: '2.0', method: '$/cancel_request', params: { requestId: 'perm-rpc-99' } });
    }, 100);
  } else if (msg.method === 'tool_call/cancel') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'tool_call_update', toolCallId: msg.params.toolCallId, status: 'cancelled' } } });
  } else if (msg.method === 'terminal/cancel') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'terminal_update', terminalId: msg.params.terminalId, exitStatus: { exitCode: 130, signal: 'SIGINT' } } } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-v2-1', update: { sessionUpdate: 'state_update', state: 'idle', stopReason: 'end_turn' } } });
  }
});
""";

        await File.WriteAllTextAsync(scriptPath, fakeServerJs, Encoding.UTF8, ct);
        var node = ResolveNodeExecutable();
        try
        {
            var events = new ConcurrentQueue<AppServerControlProviderEvent>();
            await using var runtime = new AcpAppServerControlAgentRuntime(
                "fake-v2",
                "Fake ACP v2",
                [scriptPath, logPath, "agent-auth"],
                events.Enqueue);

            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach",
                SessionId = "s-v2",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "s-v2",
                    Provider = "fake-v2",
                    RuntimeKind = "acp-v1",
                    WorkingDirectory = tempDir,
                    ExecutablePath = node,
                    ExecutableArguments = [scriptPath, logPath, "agent-auth"],
                    AuthenticationMethodId = "oauth"
                }
            }, CancellationToken.None);

            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-turn",
                SessionId = "s-v2",
                Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest
                {
                    Text = "Run conformance check",
                    Attachments = [new AppServerControlAttachmentReference { Kind = "file", Path = sampleFile, MimeType = "text/plain", DisplayName = "notes.txt" }]
                }
            }, CancellationToken.None);

            // Wait until $/cancel_request resolves the open permission request
            await WaitUntilAsync(() => events.Any(e => e.Type == "request.resolved" && e.RequestResolved?.Decision == "cancel"), ct);

            // Cancel tool call & terminal via ACP v2 RPCs
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-tc-cancel",
                SessionId = "s-v2",
                Type = "tool.cancel",
                CancelTarget = new AppServerControlCancelTargetRequest { TargetId = "tc-1" }
            }, ct);

            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-term-cancel",
                SessionId = "s-v2",
                Type = "terminal.cancel",
                CancelTarget = new AppServerControlCancelTargetRequest { TargetId = "term-1" }
            }, ct);

            await WaitUntilAsync(() => events.Any(e => e.Type == "turn.completed" && e.TurnCompleted?.State == "completed"), ct);

            // Logout via auth/logout
            await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-logout",
                SessionId = "s-v2",
                Type = "auth.logout"
            }, ct);

            // Assert rich content blocks were rendered (not dropped)
            var assistantText = string.Join("\n", events.Where(e => e.ContentDelta?.StreamKind == "assistant_text").Select(e => e.ContentDelta!.Delta));
            Assert.Contains("Intro", assistantText, StringComparison.Ordinal);
            Assert.Contains("[image: image/png (file:///img.png)]", assistantText, StringComparison.Ordinal);
            Assert.Contains("[audio: audio/wav]", assistantText, StringComparison.Ordinal);
            Assert.Contains("[spec.md](file:///spec.md)", assistantText, StringComparison.Ordinal);
            Assert.Contains("embedded-body", assistantText, StringComparison.Ordinal);
            Assert.Contains("[skill: deploy-helper]", assistantText, StringComparison.Ordinal);

            // Assert markdown plan, other plan, and plan_removed were handled
            Assert.Contains(events, e => e.ItemId == "acp:plan:plan-md" && e.Item?.Title == "Plan (PLAN.md)" && e.Item.Detail!.Contains("# Step 1", StringComparison.Ordinal));
            Assert.Contains(events, e => e.ItemId == "acp:plan:plan-other" && e.Item?.Title == "Plan (_vendor_graph)" && e.Item.Detail!.Contains("nodes", StringComparison.Ordinal));
            Assert.Contains(events, e => e.ItemId == "acp:plan:plan-md" && e.Item?.Status == "cancelled");

            // Assert wire RPC log: real version (not "dev"), auth/login, resource_link outbound, tool_call/cancel, terminal/cancel, auth/logout
            var rpcLines = (await File.ReadAllLinesAsync(logPath, ct)).Select(l => JsonDocument.Parse(l)).ToList();
            try
            {
                var init = rpcLines.First(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "initialize").RootElement;
                var sentVersion = init.GetProperty("params").GetProperty("info").GetProperty("version").GetString();
                Assert.False(string.IsNullOrWhiteSpace(sentVersion));
                Assert.NotEqual("dev", sentVersion, StringComparer.Ordinal);
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "auth/login");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "tool_call/cancel");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "terminal/cancel");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "auth/logout");

                var prompt = rpcLines.First(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/prompt").RootElement;
                Assert.Contains("\"type\":\"resource_link\"", prompt.GetProperty("params").GetProperty("prompt").GetRawText(), StringComparison.Ordinal);
            }
            finally
            {
                foreach (var doc in rpcLines) doc.Dispose();
            }

            // Assert terminal auth method is refused cleanly instead of sent to auth/login
            await using var termAuthRuntime = new AcpAppServerControlAgentRuntime(
                "fake-v2",
                "Fake ACP v2",
                [scriptPath, logPath, "terminal-auth"],
                _ => { });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => termAuthRuntime.ExecuteAsync(new AppServerControlHostCommandEnvelope
            {
                CommandId = "cmd-attach-term",
                SessionId = "s-v2-term",
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = "s-v2-term",
                    Provider = "fake-v2",
                    RuntimeKind = "acp-v1",
                    WorkingDirectory = tempDir,
                    ExecutablePath = node,
                    ExecutableArguments = [scriptPath, logPath, "terminal-auth"],
                    AuthenticationMethodId = "term-login"
                }
            }, CancellationToken.None));
            Assert.Contains("interactive terminal method", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task V2Campaign3LifecycleDiscoveryAndElicitationsWorkEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "tlbx-acp-c3-" + Guid.NewGuid().ToString("N"));
        var extraDir = Path.Combine(tempDir, "extra-workspace");
        Directory.CreateDirectory(extraDir);
        var scriptPath = Path.Combine(tempDir, "fake-acp-c3.mjs");
        var logPath = Path.Combine(tempDir, "rpc-log.jsonl");

        var fakeServerJs = """
import readline from 'node:readline';
import fs from 'node:fs';

const logPath = process.argv[2];
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function log(obj) {
  fs.appendFileSync(logPath, JSON.stringify(obj) + '\n');
}

rl.on('line', (line) => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  log(msg);
  if (msg.method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: 2,
        info: { name: 'fake-acp-c3', version: '2.0.0' },
        capabilities: {
          session: {
            prompt: {},
            mcp: { stdio: {}, http: {} },
            delete: {},
            additionalDirectories: {},
            systemPrompt: { append: {}, override: {} }
          }
        }
      }
    });
  } else if (msg.method === 'session/new') {
    send({ jsonrpc: '2.0', id: msg.id, result: { sessionId: 'ses-c3-main', configOptions: [] } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-c3-main', update: { sessionUpdate: 'session_info_update', title: 'Renamed C3 Session', updatedAt: '2026-11-26T19:00:00Z' } } });
  } else if (msg.method === 'session/list') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        sessions: [
          { sessionId: 'ses-c3-main', cwd: msg.params.cwd || '/tmp', title: 'Renamed C3 Session', updatedAt: '2026-11-26T19:00:00Z', additionalDirectories: ['/extra'] }
        ],
        nextCursor: 'cursor-page-2'
      }
    });
  } else if (msg.method === 'session/delete') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else if (msg.method === 'session/close') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else if (msg.method === 'session/prompt') {
    send({ jsonrpc: '2.0', id: msg.id, result: { messageId: 'user-c3-1' } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-c3-main', update: { sessionUpdate: 'state_update', state: 'requires_action' } } });
    send({
      jsonrpc: '2.0',
      id: 'elicit-form-1',
      method: 'elicitation/create',
      params: {
        sessionId: 'ses-c3-main',
        mode: 'form',
        message: 'Provide release settings',
        requestedSchema: {
          type: 'object',
          properties: {
            env: { type: 'string', title: 'Environment', enum: ['dev', 'prod'] },
            dryRun: { type: 'boolean', title: 'Dry Run' },
            retries: { type: 'integer', title: 'Retries' }
          },
          required: ['env']
        }
      }
    });
  } else if (msg.id === 'elicit-form-1') {
    send({
      jsonrpc: '2.0',
      id: 'elicit-url-2',
      method: 'elicitation/create',
      params: {
        sessionId: 'ses-c3-main',
        mode: 'url',
        elicitationId: 'url-auth-77',
        url: 'https://example.invalid/device-login',
        message: 'Complete device login'
      }
    });
  } else if (msg.id === 'elicit-url-2') {
    send({ jsonrpc: '2.0', method: 'elicitation/complete', params: { elicitationId: 'url-auth-77' } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-c3-main', update: { sessionUpdate: 'state_update', state: 'idle', stopReason: 'end_turn' } } });
  }
});
""";

        await File.WriteAllTextAsync(scriptPath, fakeServerJs, Encoding.UTF8, ct);
        var node = ResolveNodeExecutable();
        try
        {
            var events = new ConcurrentQueue<AppServerControlProviderEvent>();
            await using (var runtime = new AcpAppServerControlAgentRuntime(
                "fake-c3",
                "Fake ACP C3",
                [scriptPath, logPath],
                events.Enqueue))
            {
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-attach",
                    SessionId = "s-c3",
                    Type = "runtime.attach",
                    AttachRuntime = new AppServerControlAttachRuntimeRequest
                    {
                        SessionId = "s-c3",
                        Provider = "fake-c3",
                        RuntimeKind = "acp-v1",
                        WorkingDirectory = tempDir,
                        ExecutablePath = node,
                        ExecutableArguments = [scriptPath, logPath],
                        AdditionalDirectories = [extraDir],
                        SystemPromptMode = "append",
                        SystemPromptText = "Always stay concise.",
                        AdditionalMcpHttpServers =
                        [
                            new AppServerControlHttpMcpServer
                            {
                                Name = "docs",
                                Url = "https://mcp.example.invalid/sse",
                                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer token" }
                            }
                        ]
                    }
                }, ct);

                // session/list with cwd filter & cursor
                var listOutcome = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-list",
                    SessionId = "s-c3",
                    Type = "session.list",
                    ListSessions = new AppServerControlSessionListRequest { Cwd = tempDir }
                }, ct);
                Assert.NotNull(listOutcome.Result.SessionList);
                Assert.Single(listOutcome.Result.SessionList!.Sessions);
                Assert.Equal("Renamed C3 Session", listOutcome.Result.SessionList.Sessions[0].Title);
                Assert.Equal("cursor-page-2", listOutcome.Result.SessionList.NextCursor);

                // session/delete
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-delete",
                    SessionId = "s-c3",
                    Type = "session.delete",
                    SessionTarget = new AppServerControlSessionTargetRequest { ProviderSessionId = "ses-old-1" }
                }, ct);

                // Trigger form elicitation -> respond -> trigger url elicitation -> respond -> elicitation/complete
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-turn",
                    SessionId = "s-c3",
                    Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest { Text = "Deploy" }
                }, ct);

                await WaitUntilAsync(() => events.Any(e => e.UserInputRequested is not null && e.Raw?.Method == "elicitation/create"), ct);
                var formReq = events.First(e => e.UserInputRequested is not null && e.Raw?.Method == "elicitation/create");
                Assert.Equal(3, formReq.UserInputRequested!.Questions.Count);

                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-form-answer",
                    SessionId = "s-c3",
                    Type = "user-input.resolve",
                    ResolveUserInput = new AppServerControlUserInputResolutionCommand
                    {
                        RequestId = formReq.RequestId!,
                        Answers =
                        [
                            new AppServerControlAnsweredQuestion { QuestionId = "env", Answers = ["prod"] },
                            new AppServerControlAnsweredQuestion { QuestionId = "dryRun", Answers = ["true"] },
                            new AppServerControlAnsweredQuestion { QuestionId = "retries", Answers = ["3"] }
                        ]
                    }
                }, ct);

                await WaitUntilAsync(() => events.Count(e => e.UserInputRequested is not null && e.Raw?.Method == "elicitation/create") == 2, ct);
                var urlReq = events.Last(e => e.UserInputRequested is not null && e.Raw?.Method == "elicitation/create");
                Assert.Contains("https://example.invalid/device-login", urlReq.UserInputRequested!.Questions[0].Question, StringComparison.Ordinal);

                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c3-url-answer",
                    SessionId = "s-c3",
                    Type = "user-input.resolve",
                    ResolveUserInput = new AppServerControlUserInputResolutionCommand
                    {
                        RequestId = urlReq.RequestId!,
                        Answers = [new AppServerControlAnsweredQuestion { QuestionId = "url_action", Answers = ["accept"] }]
                    }
                }, ct);

                await WaitUntilAsync(() => events.Any(e => e.Type == "turn.completed" && e.TurnCompleted?.State == "completed"), ct);
                Assert.Contains(events, e => e.Raw?.Method == "session_info_update");
                Assert.Contains(events, e => e.Raw?.Method == "elicitation/complete");
            }

            // DisposeAsync above must have sent session/close cleanly before killing the process!
            var rpcLines = (await File.ReadAllLinesAsync(logPath, ct)).Select(l => JsonDocument.Parse(l)).ToList();
            try
            {
                var init = rpcLines.First(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "initialize").RootElement;
                Assert.Contains("\"elicitation\"", init.GetProperty("params").GetProperty("capabilities").GetRawText(), StringComparison.Ordinal);

                var sessionNew = rpcLines.First(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/new").RootElement;
                var newParamsRaw = sessionNew.GetProperty("params").GetRawText();
                Assert.Contains("\"additionalDirectories\"", newParamsRaw, StringComparison.Ordinal);
                Assert.Contains("\"systemPrompt\"", newParamsRaw, StringComparison.Ordinal);
                Assert.Contains("\"type\":\"http\"", newParamsRaw, StringComparison.Ordinal);

                var formReply = rpcLines.First(d => d.RootElement.TryGetProperty("id", out var id) && id.GetString() == "elicit-form-1").RootElement;
                Assert.Equal("accept", formReply.GetProperty("result").GetProperty("action").GetString());
                Assert.Equal("prod", formReply.GetProperty("result").GetProperty("content").GetProperty("env").GetString());
                Assert.True(formReply.GetProperty("result").GetProperty("content").GetProperty("dryRun").GetBoolean());
                Assert.Equal(3, formReply.GetProperty("result").GetProperty("content").GetProperty("retries").GetInt32());

                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/list");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/delete");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/close");
            }
            finally
            {
                foreach (var doc in rpcLines) doc.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task V2Campaign4SubagentsSessionMessagesAndCancelPropagationWorkEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "tlbx-acp-c4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "fake-acp-c4.mjs");
        var logPath = Path.Combine(tempDir, "rpc-log.jsonl");

        var fakeServerJs = """
import readline from 'node:readline';
import fs from 'node:fs';

const logPath = process.argv[2];
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function log(obj) {
  fs.appendFileSync(logPath, JSON.stringify(obj) + '\n');
}

rl.on('line', (line) => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  log(msg);
  if (msg.method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: 2,
        info: { name: 'fake-acp-c4', version: '2.0.0' },
        capabilities: { session: { prompt: {} } }
      }
    });
  } else if (msg.method === 'session/new') {
    send({ jsonrpc: '2.0', id: msg.id, result: { sessionId: 'ses-parent', configOptions: [] } });
  } else if (msg.method === 'session/prompt') {
    send({ jsonrpc: '2.0', id: msg.id, result: { messageId: 'u-p-1' } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-parent', update: { sessionUpdate: 'state_update', state: 'running' } } });
    // Announce subagent with cancel capability
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-parent',
        update: {
          sessionUpdate: 'subagent_update',
          sessionId: 'ses-child-1',
          title: 'Security Auditor',
          description: 'Checks auth flows',
          capabilities: { cancel: {} },
          state: { state: 'running' }
        }
      }
    });
    // Tool call referencing the child session
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-parent',
        update: {
          sessionUpdate: 'tool_call_update',
          toolCallId: 'tc-sub-1',
          title: 'Spawn Auditor',
          kind: 'agent',
          status: 'in_progress',
          content: [{ type: 'session', sessionId: 'ses-child-1' }]
        }
      }
    });
    // Child session sends streaming update automatically without separate subscribe
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-child-1',
        update: {
          sessionUpdate: 'agent_message_chunk',
          messageId: 'c-msg-1',
          content: { type: 'text', text: 'Auditing OAuth token refresh...' }
        }
      }
    });
    // Inter-session message + streamed chunk
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-parent',
        update: {
          sessionUpdate: 'session_message',
          messageId: 'sm-1',
          senderSessionId: 'ses-child-1',
          recipientSessionId: 'ses-parent',
          content: [{ type: 'text', text: 'Found 0 critical issues.' }]
        }
      }
    });
    send({
      jsonrpc: '2.0',
      method: 'session/update',
      params: {
        sessionId: 'ses-parent',
        update: {
          sessionUpdate: 'session_message_chunk',
          messageId: 'sm-1',
          content: { type: 'text', text: ' Verified PKCE.' }
        }
      }
    });
  } else if (msg.method === 'session/cancel' && msg.params.sessionId === 'ses-child-1') {
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-child-1', update: { sessionUpdate: 'state_update', state: 'idle', stopReason: 'cancelled' } } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-parent', update: { sessionUpdate: 'state_update', state: 'idle', stopReason: 'cancelled' } } });
  } else if (msg.method === 'session/close') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  }
});
""";

        await File.WriteAllTextAsync(scriptPath, fakeServerJs, Encoding.UTF8, ct);
        var node = ResolveNodeExecutable();
        try
        {
            var events = new ConcurrentQueue<AppServerControlProviderEvent>();
            var history = new Ai.Tlbx.MidTerm.Services.Sessions.SessionAppServerControlHistoryService();
            await using (var runtime = new AcpAppServerControlAgentRuntime(
                "fake-c4",
                "Fake ACP C4",
                [scriptPath, logPath],
                e => { history.Append(e); events.Enqueue(e); }))
            {
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c4-attach",
                    SessionId = "s-c4",
                    Type = "runtime.attach",
                    AttachRuntime = new AppServerControlAttachRuntimeRequest
                    {
                        SessionId = "s-c4",
                        Provider = "fake-c4",
                        RuntimeKind = "acp-v1",
                        WorkingDirectory = tempDir,
                        ExecutablePath = node,
                        ExecutableArguments = [scriptPath, logPath]
                    }
                }, ct);

                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c4-turn",
                    SessionId = "s-c4",
                    Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest { Text = "Run security audit" }
                }, ct);

                await WaitUntilAsync(() => events.Any(e => e.ItemId == "acp:session-msg:ses-parent:sm-1" && e.Item?.Detail?.Contains("Verified PKCE.", StringComparison.Ordinal) == true), ct);

                // Interrupt parent turn -> must propagate session/cancel to cancellable active child subagent!
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c4-interrupt",
                    SessionId = "s-c4",
                    Type = "turn.interrupt",
                    InterruptTurn = new AppServerControlInterruptRequest()
                }, ct);

                await WaitUntilAsync(() => events.Any(e => e.ItemId == "acp:subagent:ses-child-1" && e.Item?.Status == "cancelled"), ct);

                // Verify structured visibility in history snapshot (child output, session reference tool content, session message)
                var snapshot = history.GetSnapshot("s-c4")!;
                Assert.Contains(snapshot.History, h => h.ItemId == "acp:subagent:ses-child-1" && h.ToolPresentation?.Evidence?.Contains("Auditing OAuth token refresh", StringComparison.Ordinal) == true);
                Assert.Contains(snapshot.History, h => h.ItemId == "tc-sub-1" && h.ToolPresentation?.Evidence?.Contains("[subagent session: ses-child-1]", StringComparison.Ordinal) == true);
                Assert.Contains(snapshot.History, h => h.ItemId == "acp:session-msg:ses-parent:sm-1" && h.ToolPresentation?.Evidence?.Contains("Found 0 critical issues. Verified PKCE.", StringComparison.Ordinal) == true);
            }

            var rpcLines = (await File.ReadAllLinesAsync(logPath, ct)).Select(l => JsonDocument.Parse(l)).ToList();
            try
            {
                Assert.Contains(rpcLines, d =>
                    d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/cancel" &&
                    d.RootElement.GetProperty("params").GetProperty("sessionId").GetString() == "ses-child-1");
            }
            finally
            {
                foreach (var doc in rpcLines) doc.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task V2Campaign5UnstableFeaturesWorkEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Path.Combine(Path.GetTempPath(), "tlbx-acp-c5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "fake-acp-c5.mjs");
        var logPath = Path.Combine(tempDir, "rpc-log.jsonl");

        var fakeServerJs = """
import readline from 'node:readline';
import fs from 'node:fs';

const logPath = process.argv[2];
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
let speedVal = 'normal';

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function log(obj) {
  fs.appendFileSync(logPath, JSON.stringify(obj) + '\n');
}

function configOptions() {
  return [
    {
      id: 'speed',
      name: 'Speed',
      category: 'speed',
      type: 'select',
      currentValue: speedVal,
      options: [{ value: 'normal', name: 'Normal' }, { value: 'fast', name: 'Fast' }]
    }
  ];
}

rl.on('line', (line) => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  log(msg);
  if (msg.method === 'initialize') {
    send({
      jsonrpc: '2.0',
      id: msg.id,
      result: {
        protocolVersion: 2,
        info: { name: 'fake-acp-c5', version: '2.0.0' },
        capabilities: {
          auth: { status: {} },
          providers: {},
          session: {
            prompt: {},
            mcp: { stdio: {}, http: {}, acp: {} },
            fork: {},
            archive: {},
            status: {},
            options: {},
            title: {}
          }
        }
      }
    });
  } else if (msg.method === 'session/new') {
    send({ jsonrpc: '2.0', id: msg.id, result: { sessionId: 'ses-c5-root', configOptions: configOptions() } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-c5-root', update: { sessionUpdate: 'prompt_suggestions_update', suggestions: ['Run tests', 'Review diff'] } } });
    send({ jsonrpc: '2.0', method: 'session/update', params: { sessionId: 'ses-c5-root', update: { sessionUpdate: 'cursor_update', cursorId: 'cur-42', capabilities: { fork: {} } } } });
    send({ jsonrpc: '2.0', method: 'auth/status_update', params: { state: 'authenticated', accounts: [{ id: 'acc-1', label: 'user@example.com' }] } });
    send({ jsonrpc: '2.0', id: 'mcp-in-1', method: 'mcp/message', params: { serverId: 'client-srv', requestId: 'req-1', method: 'tools/list', sessionId: 'ses-c5-root', toolCallId: 'tc-9' } });
  } else if (msg.method === 'session/set_config_option') {
    speedVal = msg.params.value;
    send({ jsonrpc: '2.0', id: msg.id, result: { configOptions: configOptions() } });
  } else if (msg.method === 'session/fork') {
    send({ jsonrpc: '2.0', id: msg.id, result: { sessionId: 'ses-c5-fork-1', configOptions: configOptions(), availableCommands: [] } });
  } else if (msg.method === 'session/set_title' || msg.method === 'session/archive' || msg.method === 'session/unarchive' || msg.method === 'session/close') {
    send({ jsonrpc: '2.0', id: msg.id, result: {} });
  } else if (msg.method === 'session/status') {
    send({ jsonrpc: '2.0', id: msg.id, result: { status: 'active', state: { state: 'idle' } } });
  } else if (msg.method === 'session/options') {
    send({ jsonrpc: '2.0', id: msg.id, result: { configOptions: configOptions(), availableCommands: [] } });
  } else if (msg.method === 'auth/status') {
    send({ jsonrpc: '2.0', id: msg.id, result: { state: 'authenticated', accounts: [{ id: 'acc-1' }] } });
  } else if (msg.method === 'providers/list') {
    send({ jsonrpc: '2.0', id: msg.id, result: { providers: [{ providerId: 'openai', enabled: true }] } });
  } else if (msg.method === 'providers/set' || msg.method === 'providers/disable') {
    send({ jsonrpc: '2.0', id: msg.id, result: { ok: true } });
  } else if (msg.method === 'mcp/message') {
    send({ jsonrpc: '2.0', id: msg.id, result: { result: { tools: [] } } });
  }
});
""";

        await File.WriteAllTextAsync(scriptPath, fakeServerJs, Encoding.UTF8, ct);
        var node = ResolveNodeExecutable();
        try
        {
            var events = new ConcurrentQueue<AppServerControlProviderEvent>();
            await using (var runtime = new AcpAppServerControlAgentRuntime(
                "fake-c5",
                "Fake ACP C5",
                [scriptPath, logPath],
                events.Enqueue))
            {
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c5-attach",
                    SessionId = "s-c5",
                    Type = "runtime.attach",
                    AttachRuntime = new AppServerControlAttachRuntimeRequest
                    {
                        SessionId = "s-c5",
                        Provider = "fake-c5",
                        RuntimeKind = "acp-v1",
                        WorkingDirectory = tempDir,
                        ExecutablePath = node,
                        ExecutableArguments = [scriptPath, logPath]
                    }
                }, ct);

                await WaitUntilAsync(() => events.Any(e => e.Raw?.Method == "cursor_update") && events.Any(e => e.Raw?.Method == "mcp/message"), ct);

                // Configure fastMode -> ACP speed config option
                var cfgOutcome = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c5-speed",
                    SessionId = "s-c5",
                    Type = "runtime.configure",
                    Configure = new AppServerControlConfigureRequest
                    {
                        ExpectedThreadId = "ses-c5-root",
                        Settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["fastMode"] = JsonElement.Parse("\"on\"")
                        }
                    }
                }, ct);
                Assert.Equal("applied", cfgOutcome.Result.Configured!.Status);
                Assert.Equal("on", cfgOutcome.Result.Configured.EffectiveSettings["fastMode"].GetString());

                // Fork using captured cursor_update cursorId + initial configOptions
                var forkOutcome = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c5-fork",
                    SessionId = "s-c5",
                    Type = "session.fork",
                    ForkSession = new AppServerControlSessionForkRequest
                    {
                        ConfigOptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        {
                            ["speed"] = JsonElement.Parse("\"fast\"")
                        }
                    }
                }, ct);
                Assert.Equal("ses-c5-fork-1", forkOutcome.Result.ForkedSession!.SessionId);

                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-title", SessionId = "s-c5", Type = "session.set_title", SessionTitle = new() { Title = "Forked Plan" } }, ct);
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-arch", SessionId = "s-c5", Type = "session.archive", SessionTarget = new() { ProviderSessionId = "ses-c5-fork-1" } }, ct);
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-unarch", SessionId = "s-c5", Type = "session.unarchive", SessionTarget = new() { ProviderSessionId = "ses-c5-fork-1" } }, ct);

                var statusRes = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-status", SessionId = "s-c5", Type = "session.status", SessionTarget = new() { ProviderSessionId = "ses-c5-root" } }, ct);
                Assert.Equal("active", statusRes.Result.RawPayload!.Value.GetProperty("status").GetString());

                var optsRes = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-opts", SessionId = "s-c5", Type = "session.options" }, ct);
                Assert.True(optsRes.Result.RawPayload!.Value.TryGetProperty("configOptions", out _));

                var authRes = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-auth", SessionId = "s-c5", Type = "auth.status" }, ct);
                Assert.Equal("authenticated", authRes.Result.RawPayload!.Value.GetProperty("state").GetString());

                var provList = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-plist", SessionId = "s-c5", Type = "providers.list" }, ct);
                Assert.Equal(1, provList.Result.RawPayload!.Value.GetProperty("providers").GetArrayLength());

                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-pset", SessionId = "s-c5", Type = "providers.set", ProviderConfig = new() { ProviderId = "openai", ApiType = "openai", BaseUrl = "https://api.example.invalid/v1" } }, ct);
                await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope { CommandId = "c5-pdis", SessionId = "s-c5", Type = "providers.disable", ProviderConfig = new() { ProviderId = "openai" } }, ct);

                var mcpOut = await runtime.ExecuteAsync(new AppServerControlHostCommandEnvelope
                {
                    CommandId = "c5-mcp",
                    SessionId = "s-c5",
                    Type = "mcp.message",
                    McpMessage = new() { ServerId = "srv-1", RequestId = "m-1", Method = "tools/list", ProviderSessionId = "ses-c5-root", ToolCallId = "tc-1" }
                }, ct);
                Assert.True(mcpOut.Result.RawPayload!.Value.TryGetProperty("result", out _));
            }

            var rpcLines = (await File.ReadAllLinesAsync(logPath, ct)).Select(l => JsonDocument.Parse(l)).ToList();
            try
            {
                var forkRpc = rpcLines.First(d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "session/fork").RootElement;
                Assert.Equal("cur-42", forkRpc.GetProperty("params").GetProperty("cursorId").GetString());
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("id", out var id) && id.GetString() == "mcp-in-1");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "providers/set");
                Assert.Contains(rpcLines, d => d.RootElement.TryGetProperty("method", out var m) && m.GetString() == "providers/disable");
            }
            finally
            {
                foreach (var doc in rpcLines) doc.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
