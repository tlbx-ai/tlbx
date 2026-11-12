# Claude Agent SDK bridge

This package is the narrow JavaScript boundary between `mtagenthost` and the official TypeScript `@anthropic-ai/claude-agent-sdk`. It is bundled into one ESM file and embedded into the native `mtagenthost` executable. At runtime, `mtagenthost` extracts the content-addressed bundle and launches it with the locally installed Node.js runtime.

The bridge deliberately uses `pathToClaudeCodeExecutable` so the SDK drives the user's installed Claude Code binary and local Claude profile. tlbx does not collect credentials, exchange OAuth tokens, or route Claude traffic through a tlbx service. Claude Code's own credential precedence remains authoritative, including an `ANTHROPIC_API_KEY` environment variable taking precedence over a Claude login.

Runtime requirements:

- Claude Code installed and resolvable on `PATH`
- Node.js 18 or newer installed and resolvable on `PATH`
- a locally valid Claude Code authentication method

To update the checked-in bundle:

```powershell
npm ci --omit=optional
npm run build
```

The SDK and bridge dependencies are pinned to the versions verified by tlbx's provider integration tests. Optional platform CLI packages are omitted because tlbx always supplies the locally installed Claude Code executable.

Runtime settings use `runtime.configure` with a request ID and exact observed thread/turn guard. `bridge.configured` is sent only after SDK control promises settle, carries per-field `confirmed`/`errors`, and exposes no invented effective readback. Model/effort use `setModel` / `applyFlagSettings({ effortLevel })`; fast mode uses the SDK flag-tier `fastMode` setting, starts explicitly off with per-session opt-in, and is offered only for models advertising `supportsFastMode`; plan and permissions share `setPermissionMode`, preserving the non-plan restore mode. No prompt, interrupt, resubmit or permission resolution is sent. `src/runtimeSettings.test.mjs` exercises these SDK contracts with a deterministic control double. Rebuild the embedded `dist` bridge before runtime acceptance; changing source alone does not update the embedded runtime.
