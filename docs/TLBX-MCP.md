# TLBX MCP (experimental)

The Bash/PowerShell CLI remains supported and maintained. MCP is additive until
real agent workflows demonstrate equal coverage and better stability. Keep CLI
protocol checks and use the same C# services for both interfaces.

## First slice

`POST /api/mcp` exposes Streamable HTTP using the official C# SDK. It is local
only and passes through existing tlbx authentication and origin checks. This is
an experimental read-only slice, not a replacement for the CLI or a public remote
MCP service. It does not alter provider configurations or install anything globally.

Every request also requires `X-Tlbx-Cli-Context`, containing a current verified
lease obtained from `/api/sessions/process-context` using the existing CLI caller
identity flow. A plain session ID, cwd, MCP transport session, or API key alone
cannot select `self`. Leases expire after two minutes and may be revoked sooner;
clients must obtain fresh context before subsequent calls. Never replay an
application mutation automatically. Do not persist leases in provider config.

The endpoint is stateless, so it revalidates the caller on every HTTP request and
again at tool dispatch. It supports the protocol revisions implemented by the
pinned SDK; legacy SSE endpoints are disabled.

| Tool | Result |
|---|---|
| `context_get` | Verified calling session, server version, experimental flag |
| `sessions_list` | Same full-user session projection as REST/CLI |
| `browser_snapshot` | Existing preview snapshot, bounded to 100,000 characters |
| `browser_screenshot` | Existing preview PNG as MCP image content |

Browser tools accept optional `previewName` and always use the verified calling
session. Existing browser ownership rules still apply. They never open, activate
or claim a preview. No arbitrary target-session argument is accepted in this slice.

## Next acceptance gates

1. Prove Native AOT publish and real MCP HTTP discovery/calls; preserve CLI tests.
2. Add a local stdio adapter and renewable runtime-bound credentials after proving
   caller identity for ordinary terminal agents and dedicated controller runtimes.
   Shared backends must not inherit the first launcher's identity.
3. Share the existing browser-open workflow before adding browser mutations;
   preserve sticky ownership, hidden-open defaults and readiness checks.
4. Verify real provider clients, concurrent siblings, resume, reconnect and service
   identity boundaries before changing default agent instructions to MCP.
5. Expand session orchestration with explicit caller/target authorization. Remove
   CLI paths only after their real users and scripts have a verified migration.
