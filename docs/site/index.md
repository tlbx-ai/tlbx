---
layout: default
title: MidTerm
---

# MidTerm

**Persistent local PTYs in any browser.**

MidTerm multiplexes real local PTYs, repository state, logs, and localhost previews over HTTPS/WebSocket. The browser is a client; processes outlive it.

- self-hosted execution
- persistent process state across browser disconnects
- no SSH client or remote-desktop UI
- files, Git, logs, and previews in the same session context

Install MidTerm:

- macOS/Linux: `curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash`
- Windows: `irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex`
- Source repo: [github.com/tlbx-ai/MidTerm](https://github.com/tlbx-ai/MidTerm)
- Product docs: [docs/FEATURES.md](https://github.com/tlbx-ai/MidTerm/blob/main/docs/FEATURES.md)

Ephemeral loopback fallback: `npx @tlbx-ai/midterm`.
