<p align="center">
  <img src="docs/marketing/readme/midterm-wordmark.svg" alt="MidTerm — persistent local PTYs in any browser" width="100%">
</p>

<p align="center">
  <a href="#install-midterm-recommended"><strong>Install MidTerm</strong></a>
  ·
  <a href="#system-model"><strong>System model</strong></a>
  ·
  <a href="docs/FEATURES.md"><strong>Feature inventory</strong></a>
  ·
  <a href="docs/ARCHITECTURE.md"><strong>Architecture</strong></a>
</p>

<p align="center">
  <a href="https://github.com/tlbx-ai/MidTerm/releases/latest"><img src="https://img.shields.io/github/v/release/tlbx-ai/MidTerm?style=flat-square&color=80b6f2" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--3.0-80b6f2?style=flat-square" alt="AGPL-3.0 license"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20macOS%20%C2%B7%20Linux-ready-80b6f2?style=flat-square" alt="Windows, macOS and Linux">
</p>

# Persistent local PTYs in any browser.

MidTerm is a self-hosted web interface for the terminal-native tools already on your machine. It multiplexes persistent PTYs, repository state, logs, and localhost previews over HTTPS/WebSocket.

**The browser is a client, not the runtime.** Disconnect it and the shell, agent, test run, or dev server continues. Reconnect from another browser to the same process state—without making SSH or remote desktop the interface.

<p align="center">
  <img src="docs/marketing/readme/browser-next-to-work.svg" alt="A browser client connected to persistent local PTYs, repository state, and a localhost preview" width="100%">
</p>

## Install MidTerm (recommended)

The native installer configures the local service, password-protected HTTPS, and the update path.

**macOS / Linux**

```bash
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

**Windows PowerShell**

```powershell
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Then open `https://localhost:2000` in your browser.

| Install mode       | Use it when                                                                            |
| ------------------ | -------------------------------------------------------------------------------------- |
| **System service** | MidTerm should stay available across logouts and reboots, including from other devices |
| **User install**   | You want a persistent personal install without administrator access                    |

## System model

| Property       | MidTerm's boundary                                                                                                 |
| -------------- | ------------------------------------------------------------------------------------------------------------------ |
| **Execution**  | Real PTYs and child processes run on your machine, against its repositories, credentials, tools, and network       |
| **Client**     | Any modern browser; disconnecting it does not terminate the process                                                |
| **Transport**  | HTTPS + WebSocket over loopback, LAN, your VPN, or your reverse tunnel                                             |
| **State**      | Sessions retain process, working-directory, scrollback, repository, notes, and layout context                      |
| **Agents**     | Any terminal-native agent; structured runtimes get tool activity, approvals, diffs, model controls, and interrupts |
| **Validation** | Local app previews, DOM control, console/proxy logs, screenshots, and responsive inspection stay session-scoped    |

Run Codex, Claude Code, Grok, Gemini CLI, Copilot CLI, Aider, or any other terminal-native tool where it already belongs. MidTerm does not introduce a hosted execution environment.

Files, Git state, commands, notes, previews, logs, screenshots, and responsive testing remain attached to the session. Multiple agents, shells, test runners, and servers can be split, reordered, bookmarked, and monitored across repositories.

<p align="center">
  <img src="docs/marketing/readme/agent-control-room.svg" alt="Persistent PTYs, repository state, browser evidence, and the same session across browser clients" width="100%">
</p>

## Network boundary

Remote access follows the network path that matches your threat model:

- [Tailscale](https://tailscale.com)
- Cloudflare Tunnel
- nginx, Caddy, or another HTTPS reverse proxy
- loopback or LAN

The service provides password authentication, local HTTPS and certificate-trust helpers, API keys, and scoped share links. SSH remains available _inside_ a MidTerm terminal when the target system requires it; it is not required as MidTerm's client protocol.

<p align="center">
  <img src="docs/marketing/readme/local-first-anywhere.svg" alt="Repositories, credentials, tools, terminals and local servers stay on the MidTerm machine while the same sessions open in desktop and mobile browsers without SSH" width="100%">
</p>

> [!IMPORTANT]
> MidTerm has no repository-hosting cloud. Agent processes still communicate with their configured providers under those providers' terms.

## Main surfaces

| Surface         | Purpose                                                                                                                    |
| --------------- | -------------------------------------------------------------------------------------------------------------------------- |
| **Terminal**    | Persistent real PTYs, split layouts, search, exact paste, uploads, touch controls, activity and recovery                   |
| **Agent view**  | Structured turns, tools, approvals, answers, diffs, interrupts and model settings where the provider runtime supports them |
| **Dev Browser** | Session-scoped previews, isolated contexts, DOM control, console/proxy logs, screenshots and responsive testing            |
| **Files + Git** | File tree, previews, editing, repository state, line deltas, conflicts, stashes and recent commits                         |
| **Command Bay** | Multiline input, files and images, reusable actions, mobile keys, prompt routing and scheduled follow-ups                  |
| **Operations**  | Authentication, HTTPS, API keys, scoped sharing, updates, diagnostics, logs and service controls                           |

See the complete [feature inventory](docs/FEATURES.md) and [architecture](docs/ARCHITECTURE.md).

## Fallback: run it once with `npx`

For an ephemeral loopback trial:

```bash
npx @tlbx-ai/midterm
```

The launcher downloads the stable native binary, binds it to loopback, and opens a browser. Use the [native installer](#install-midterm-recommended) for persistent or remote use.

## Uninstall

```bash
# macOS / Linux
curl -fsSL https://tlbx-ai.github.io/MidTerm/uninstall.sh | bash
```

```powershell
# Windows PowerShell
irm https://tlbx-ai.github.io/MidTerm/uninstall.ps1 | iex
```

The uninstallers remove only known MidTerm-owned locations and request elevation only when system-level cleanup requires it.

## Architecture

```text
browser on desktop · tablet · phone
                 │ HTTPS / WebSocket
                 ▼
            mt web server
              ├── mthost ─────── real shell / PTY / any CLI agent
              ├── mtagenthost ── structured agent runtimes
              ├── Dev Browser ── preview / DOM / logs / screenshots
              └── Files / Git / Commands / API / diagnostics
```

MidTerm is built with .NET 10 Native AOT, TypeScript, and xterm.js.

- [Architecture](docs/ARCHITECTURE.md)
- [Feature inventory](docs/FEATURES.md)
- [Dev Browser design](docs/devbrowser.md)
- [Contributing guide](docs/CONTRIBUTING.md)

## Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and [esbuild](https://esbuild.github.io/) in `PATH`.

```bash
git clone https://github.com/tlbx-ai/MidTerm.git
cd MidTerm
dotnet build src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj
dotnet test src/Ai.Tlbx.MidTerm.Tests/Ai.Tlbx.MidTerm.Tests.csproj
dotnet test src/Ai.Tlbx.MidTerm.UnitTests/Ai.Tlbx.MidTerm.UnitTests.csproj
```

## Contributing and license

Issues, field reports, and contributions are welcome. See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md); contributions require acceptance of the [Contributor License Agreement](docs/CLA.md).

MidTerm is licensed under [GNU AGPL v3](LICENSE). Commercial licensing is available from [tlbx-ai](https://github.com/tlbx-ai).
