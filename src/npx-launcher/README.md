# @tlbx-ai/midterm

Ephemeral loopback launcher for [tlbx](https://tlbx.ai), the browser control station for AI coding agents. tlbx was previously named MidTerm; the package name remains stable so existing users keep receiving updates.

```bash
npx @tlbx-ai/midterm
```

The launcher downloads the native tlbx release for your platform, caches it in your user profile, runs it locally, and opens tlbx in your default browser.

For persistent or remote operation, use the [native installer](https://tlbx.ai/install). The npm launcher is the quick-trial fallback.

Supported platforms:

- Windows x64
- Windows x86
- macOS x64
- macOS ARM64
- Linux x64
- Linux ARM64

Extra arguments are passed through to `mt`:

```bash
npx @tlbx-ai/midterm -- --port 2001 --bind 127.0.0.1
```

Launcher-only options:

- `--channel stable|dev`
- `--no-browser`
- `--help-launcher`

Notes:

- Default channel is `stable`
- If you do not pass `--bind`, the launcher forces `127.0.0.1`
- If you do not pass `--port`, the launcher starts at `https://127.0.0.1:2000` and automatically moves to the next free port if `2000` is unavailable
- The launcher sets `MIDTERM_LAUNCH_MODE=npx` for the child process
- If you invoke `npx` from WSL but it resolves to Windows `node/npm`, the launcher detects the WSL working directory and runs the Linux MidTerm build inside that distro
