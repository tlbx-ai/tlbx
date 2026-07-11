<p align="center">
  <img src="docs/marketing/readme/midterm-wordmark.svg" alt="MidTerm — your coding agents, your machines, any browser" width="100%">
</p>

<p align="center">
  <a href="#install"><strong>Install</strong></a>
  ·
  <a href="#agent-cli-ergonomics"><strong>Agent ergonomics</strong></a>
  ·
  <a href="#private-remote-access"><strong>Remote access</strong></a>
  ·
  <a href="docs/FEATURES.md"><strong>All features</strong></a>
</p>

<p align="center">
  <a href="https://github.com/tlbx-ai/MidTerm/releases/latest"><img src="https://img.shields.io/github/v/release/tlbx-ai/MidTerm?style=flat-square&color=80b6f2" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--3.0-80b6f2?style=flat-square" alt="AGPL-3.0 license"></a>
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20macOS%20%C2%B7%20Linux-ready-80b6f2?style=flat-square" alt="Windows, macOS and Linux">
</p>

# Run your coding agents on your machines. Steer them from anywhere.

Start Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI—or several at once—where the repos live. MidTerm keeps every session alive and puts control in any browser.

**Your machines are browser tabs. Your agents keep working when you leave.**

<p align="center">
  <img src="docs/marketing/readme/browser-next-to-work.svg" alt="Codex, Claude Code, and Grok Build running concurrently on a home workstation controlled from a MidTerm browser tab beside another MidTerm host" width="100%">
</p>

## Agent CLI ergonomics

MidTerm runs any terminal-native tool in a real PTY, but it is shaped around long-running coding agents:

- **Run many:** split, reorder, bookmark, and revisit independent agent, shell, test, and server sessions.
- **Paste screenshots normally:** `Ctrl+V` / `Cmd+V` uploads the image to the host and inserts its path. Structured agent sessions stage it as an attachment.
- **Compose real prompts:** multiline input, per-session drafts, files, drag-and-drop, camera capture, reusable actions, and scheduled follow-ups.
- **Reuse exact inputs:** the direct **Prompt & Paste** sidebar entry (`Alt+H`) keeps Terminal prompts, pastes, images, and files replayable into any active session.
- **See what needs you:** Operator separates machine facts from agent-published status, todos, mail, coding tasks, next steps, and checkpoints across hosts.
- **Let agents operate MidTerm:** generated `mt` helpers expose history, capabilities, direct multi-session dispatch, ordered events, and the control plane as stable JSON.
- **Verify the result:** open the app beside the agent; inspect DOM, console/proxy logs, responsive layouts, and screenshots.
- **Leave and return:** sessions survive browser disconnects, device changes, and travel.

<p align="center">
  <img src="docs/marketing/readme/agent-control-room.svg" alt="Codex, Claude Code, Grok Build, OpenCode, Copilot CLI, and Antigravity CLI sessions controlled concurrently in MidTerm" width="100%">
</p>

## Not SSH in a browser

SSH opens a shell connection. MidTerm reopens the machine's living context: agents, terminals, files, Git, notes, logs, and app previews.

Run one independent MidTerm instance per host. Open your home workstation, office laptop, or server as adjacent tabs. Bring LAN, VPN, or reverse-tunnel connectivity; MidTerm becomes the working interface.

<p align="center">
  <img src="docs/marketing/readme/your-machines-are-tabs.svg" alt="A browser anywhere connects to independent MidTerm instances on a home workstation, office laptop, and server; each machine keeps its own repositories, agents, and processes" width="100%">
</p>

## Install

The native installer configures the service, password-protected HTTPS, and updates.

**macOS / Linux**

```bash
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

**Windows PowerShell**

```powershell
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Open `https://localhost:2000`. Choose service mode for a host that should survive logouts and reboots; user mode needs no administrator access.

## Private remote access

MidTerm is not a VPN or hosted relay. You choose the network path.

**Recommended default:** put the MidTerm host and your client devices in the same [Tailscale](https://tailscale.com/) tailnet—or use an equivalent WireGuard mesh VPN—and open MidTerm through its private address instead of exposing it publicly.

This is a strong security baseline: tailnet traffic is end-to-end encrypted, and [grants/ACLs](https://tailscale.com/docs/features/access-control) can restrict which identities and devices reach the host. Keep MidTerm's HTTPS/password authentication enabled, use least-privilege rules, and keep both products updated.

Cloudflare Tunnel, nginx/Caddy, LAN, and other private-network setups also work.

<p align="center">
  <img src="docs/marketing/readme/host-session-anywhere.svg" alt="A MidTerm session remains on its host while browsers at a desk, on a laptop, and on a phone reconnect to it from different locations" width="100%">
</p>

> [!IMPORTANT]
> MidTerm has no repository-hosting cloud. Repos, credentials, tools, and processes stay on each host. Agent-provider traffic remains subject to that provider's configuration and terms.

## System boundary

| Part          | Behavior                                                                                   |
| ------------- | ------------------------------------------------------------------------------------------ |
| **Host**      | One independent MidTerm instance exposes one machine                                       |
| **Execution** | Real PTYs use that host's repos, credentials, tools, hardware, and network                 |
| **Client**    | Any authorized browser; several hosts can sit in adjacent tabs                             |
| **Lifetime**  | Browser connections are transient; agents, shells, tests, and servers persist              |
| **Context**   | Working directory, scrollback, Git, files, notes, logs, and previews stay with the session |

Structured agent controls are runtime-dependent. Every terminal-native tool still works through its real PTY.

## Trial fallback

For an ephemeral loopback trial:

```bash
npx @tlbx-ai/midterm
```

The launcher downloads the stable native binary and opens a browser. Use the native installer for persistent remote operation.

## Architecture and source

```text
browser anywhere
   ├── HTTPS / WebSocket ──► MidTerm on home workstation ──► agents / repos / apps
   └── HTTPS / WebSocket ──► MidTerm on office laptop ─────► agents / repos / apps
```

MidTerm uses .NET 10 Native AOT, TypeScript, and xterm.js.

- [Architecture](docs/ARCHITECTURE.md)
- [Feature inventory](docs/FEATURES.md)
- [Contributing](docs/CONTRIBUTING.md)

```bash
git clone https://github.com/tlbx-ai/MidTerm.git
cd MidTerm
dotnet build src/Ai.Tlbx.MidTerm/Ai.Tlbx.MidTerm.csproj
```

Uninstallers: [macOS/Linux](https://tlbx-ai.github.io/MidTerm/uninstall.sh) · [Windows](https://tlbx-ai.github.io/MidTerm/uninstall.ps1)

MidTerm is [GNU AGPL v3](LICENSE). Commercial licensing is available from [tlbx-ai](https://github.com/tlbx-ai).
