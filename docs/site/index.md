---
layout: default
title: MidTerm
---

# MidTerm

**Run your coding agents on your machines. Steer them from anywhere.**

Run Grok Build, Codex, Claude Code, OpenCode, Antigravity CLI, Copilot CLI—or all of them at once. MidTerm keeps their sessions alive and puts control in any browser.

**Product website:** [tlbx.ai](https://tlbx.ai) — screenshots, architecture, features, and installation.

- normal `Ctrl+V` / `Cmd+V` screenshot paste into terminal CLIs
- multiline prompts, per-session drafts, files, camera input, and scheduled follow-ups
- process, activity, repository, attention, approval, diff, and browser-proof surfaces
- independent MidTerm hosts as browser tabs over the network path you choose

Install MidTerm:

> **Temporary dev channel (`v9.19.6-dev`):** stable `v9.19.0` was published without platform packages. Use these verified commands until the corrected stable release is promoted.

- macOS/Linux: `curl -fsSL https://get.tlbx.ai/install.sh | bash -s -- --dev`
- Windows: `& ([scriptblock]::Create((irm https://get.tlbx.ai/install.ps1))) -Dev`
- Source repo: [github.com/tlbx-ai/tlbx](https://github.com/tlbx-ai/tlbx)
- Product website: [tlbx.ai](https://tlbx.ai)
- Product docs: [docs/FEATURES.md](https://github.com/tlbx-ai/tlbx/blob/main/docs/FEATURES.md)

For private remote access, use Tailscale—or an equivalent WireGuard mesh VPN—and open MidTerm through the host's private address.

Ephemeral loopback fallback: `npx @tlbx-ai/midterm --channel dev`. Native installation is required to experience persistent remote agent control properly.
