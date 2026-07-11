<p align="center">
  <img src="docs/marketing/readme/midterm-wordmark.svg" alt="MidTerm — your machines are browser tabs" width="100%">
</p>

<p align="center">
  <a href="#install-midterm-recommended"><strong>Install MidTerm</strong></a>
  ·
  <a href="#not-ssh-in-a-browser"><strong>Mental model</strong></a>
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

# Your machines are browser tabs.

Install MidTerm on each machine you want to reach. From any browser, open that host's already-running agents, shells, files, Git state, logs, and localhost apps—across the room or across the country.

**The work stays on its machine. You don't.** Each MidTerm instance remains independent; your browser moves between them as ordinary tabs. Disconnecting the browser does not terminate the work.

<p align="center">
  <img src="docs/marketing/readme/browser-next-to-work.svg" alt="Home workstation and office laptop exposed as adjacent MidTerm browser tabs beside the rest of the user's work" width="100%">
</p>

## Not SSH in a browser

SSH opens a connection to a shell. MidTerm reopens the machine's living workspace: persistent agents and terminals plus their files, Git state, commands, notes, logs, screenshots, and app previews.

- Run one MidTerm instance on every host you want to reach.
- Open each independent instance in its own browser tab.
- Leave, switch networks, or change devices; the host-side sessions continue.
- Bring your own network path—LAN, VPN, or reverse tunnel—without making it the working interface.

<p align="center">
  <img src="docs/marketing/readme/your-machines-are-tabs.svg" alt="A browser anywhere connects to independent MidTerm instances on a home workstation, office laptop, and server; each machine keeps its own repositories, agents, and processes" width="100%">
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

| Property      | MidTerm's boundary                                                                                                    |
| ------------- | --------------------------------------------------------------------------------------------------------------------- |
| **Host**      | Each installation exposes one machine and remains operationally independent                                           |
| **Execution** | Real PTYs and child processes use that host's repositories, credentials, tools, hardware, and network                 |
| **Client**    | Any authorized browser; multiple MidTerm hosts can sit in adjacent tabs                                               |
| **Transport** | HTTPS + WebSocket over loopback, LAN, your VPN, or your reverse tunnel                                                |
| **Lifetime**  | Browser connections are transient; host-side agents, shells, tests, and servers persist                               |
| **Context**   | Working directory, scrollback, repository state, files, Git, notes, logs, and previews remain attached to the session |
| **Agents**    | Any terminal-native agent; structured runtimes get tool activity, approvals, diffs, model controls, and interrupts    |

Run Codex, Claude Code, Grok, Gemini CLI, Copilot CLI, Aider, or any other terminal-native tool where it already belongs. MidTerm does not move the repository into a hosted execution environment.

Within one host, multiple agents, shells, test runners, and servers can be split, reordered, bookmarked, and monitored across repositories.

<p align="center">
  <img src="docs/marketing/readme/agent-control-room.svg" alt="Persistent PTYs, repository state, browser evidence, and the same session across browser clients" width="100%">
</p>

## Network boundary

MidTerm is not a VPN, mesh network, or hosted relay. Remote access follows the network path that matches your threat model:

- [Tailscale](https://tailscale.com)
- Cloudflare Tunnel
- nginx, Caddy, or another HTTPS reverse proxy
- loopback or LAN

Each instance provides password authentication, local HTTPS and certificate-trust helpers, API keys, and scoped share links. SSH remains available _inside_ a MidTerm terminal when the target system requires it; it is not the interface used to reach MidTerm itself.

<p align="center">
  <img src="docs/marketing/readme/host-session-anywhere.svg" alt="A MidTerm session remains on its host while browsers at a desk, on a laptop, and on a phone reconnect to it from different locations" width="100%">
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
browser anywhere
   ├── HTTPS / WebSocket ──► MidTerm on home workstation
   │                           ├── mthost / mtagenthost
   │                           └── repos / tools / apps / processes
   └── HTTPS / WebSocket ──► MidTerm on office laptop
                               ├── mthost / mtagenthost
                               └── repos / tools / apps / processes
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
