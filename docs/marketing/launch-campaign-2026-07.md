# MidTerm launch week — 12–19 July 2026

## Goal

Help developers understand and try the product that already exists:

> MidTerm runs local terminals and AI coding agents in a normal browser tab beside the rest of your work. No SSH client or remote desktop.

Target: 1,000 real users by the end of the week. Treat that as a direction, not a claim or vanity target. Optimize for useful installs, honest feedback and retained use.

## What we lead with

1. **It is in the browser.** Keep terminals and agents beside mail, issues, docs, dashboards and the app being built.
2. **No SSH client or remote desktop.** Open the machine's real local sessions through the browser.
3. **The work stays together.** Terminals, files, Git, logs and app previews are available in the same tab.
4. **Sessions survive the tab.** Close the browser and return later without killing the work.
5. **It runs locally.** Repositories, tools, credentials and processes remain on the machine that owns them.

Do not lead with category labels, agent-fleet language or installation gimmicks. Show the ordinary workflow clearly.

## Primary installation

Link people to the repository and native installers:

```bash
# macOS / Linux
curl -fsSL https://tlbx-ai.github.io/MidTerm/install.sh | bash
```

```powershell
# Windows
irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
```

Use the service install for an always-available local workspace. User mode is documented in the README.

The npm launcher is a temporary trial fallback. It is not the main call to action.

## Honest starting point

Snapshot taken 11 July 2026:

| Signal                          | Baseline |
| ------------------------------- | -------: |
| GitHub stars                    |       96 |
| GitHub unique visitors, 14 days |       18 |
| GitHub unique clones, 14 days   |       62 |
| npm downloads, 7 days           |       36 |
| Latest stable release downloads |       19 |

Record the same numbers once per day. Do not combine visitors, downloads and installs into one invented “users” number.

## Assets

Upload-ready PNGs live in `docs/marketing/launch-assets-2026-07/`.

| Story                                     | Asset                                            |
| ----------------------------------------- | ------------------------------------------------ |
| Browser tab beside existing work          | `x-01-browser-next-to-work-1600x900.png`         |
| Same sessions from another browser/device | `x-02-local-first-anywhere-1600x900.png`         |
| Product Hunt gallery 1                    | `product-hunt-01-browser-workspace-1270x760.png` |
| Product Hunt gallery 2                    | `product-hunt-02-live-sessions-1270x760.png`     |
| Product Hunt thumbnail                    | `product-hunt-thumbnail-240x240.png`             |

Use real product clips when motion explains the feature better:

| Feature             | Existing clip                                   |
| ------------------- | ----------------------------------------------- |
| Persistent sessions | `06-multi-agents.mp4`                           |
| Browser validation  | `13-dev-browser.mp4`                            |
| File and Git work   | `11-files-editor.mp4`, `12-source-control.mp4`  |
| Phone access        | `03-sidebar-swipe.mp4`, `04-quick-controls.mp4` |

## Publishing sequence

### Sunday 12 July — correct the first impression

Update GitHub metadata and publish the new README.

Post:

> I built MidTerm because I wanted my local terminals and coding agents in the browser next to mail, issues, docs and the app I’m building.
>
> No SSH client. No remote desktop. Close the tab and the sessions keep running.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-01-browser-next-to-work-1600x900.png`.

### Monday 13 July — show persistence

Post:

> A browser tab should not own the process behind it.
>
> MidTerm runs the terminal session locally, so you can close the tab, reopen it later and continue where the agent or shell is still running.
>
> https://github.com/tlbx-ai/MidTerm

Attach a short recording that closes and reopens the browser while the session continues.

### Tuesday 14 July — show the whole work surface

Post:

> Terminal on the left. The local app on the right. Files, Git and logs in the same browser tab.
>
> MidTerm removes the SSH/remote-desktop detour without moving the work off your machine.
>
> https://github.com/tlbx-ai/MidTerm

Attach the Dev Browser clip.

### Wednesday 15 July — Show HN

Title:

> Show HN: MidTerm – local terminals and AI agents in the browser

Opening:

> I built MidTerm because I kept switching between browser work and a separate terminal or remote-access client.
>
> MidTerm runs on the machine that owns the repository and exposes its real terminal sessions in a browser UI. The tab can sit next to issues, docs and the app being built. Closing the browser does not stop the session.
>
> It also brings files, Git, logs and local app previews into the same workspace. It works with terminal-native tools such as Codex, Claude Code, Gemini CLI, Copilot CLI and normal shells.
>
> Native installers are available for Windows, macOS and Linux. The project is AGPL-licensed:
>
> https://github.com/tlbx-ai/MidTerm
>
> I would especially value feedback on the no-SSH browser workflow and what still feels harder than using a normal local terminal.

Stay available to answer technical questions directly. Do not ask for upvotes.

### Thursday 16 July — another browser, same machine

Post:

> The work runs on your machine. The interface can be any browser you trust.
>
> Open the same live MidTerm sessions from your desk, tablet or phone over your VPN or tunnel—without turning SSH into the user interface.
>
> https://github.com/tlbx-ai/MidTerm

Attach `x-02-local-first-anywhere-1600x900.png`.

### Friday 17 July — explain the boundary

Post:

> MidTerm does not upload your repository to a hosted development environment.
>
> Repos, tools, credentials and processes stay on your machine. MidTerm provides the protected browser gateway and session UI.
>
> Architecture: https://github.com/tlbx-ai/MidTerm#architecture

Answer security questions with the documented defaults. Do not imply a security audit or guarantees the project has not earned.

### Saturday 18 July — Product Hunt

Name:

> MidTerm

Tagline:

> Local terminals and AI agents in your browser

Description:

> MidTerm runs on your computer and puts its real terminal sessions in a browser tab beside the rest of your work. Use normal shells or terminal-native coding agents, keep sessions alive after the tab closes, and work with files, Git, logs and local app previews without switching to an SSH client or remote desktop. Open source for Windows, macOS and Linux.

First comment:

> I built MidTerm to remove a very ordinary interruption: browser work in one place, terminals and remote access somewhere else.
>
> The server and PTYs run locally. The browser is the interface, so the terminal can stay beside issues, docs and the app you are building. Closing the tab does not terminate the work.
>
> I would value blunt feedback on setup, trust and whether the browser workflow is actually more useful than your current terminal plus SSH setup.

Use the native installer page as the main destination.

### Sunday 19 July — report what happened

Publish:

- traffic and install signals against the baseline,
- the three most useful pieces of feedback,
- what was fixed during the week,
- what remains unclear.

Avoid a victory post unless the data supports one.

## Direct feedback request

Send only to people for whom the workflow is relevant:

> I changed how I explain MidTerm and would value a blunt first-impression check.
>
> The core is: your local terminals and coding agents in a normal browser tab beside the rest of your work—no SSH client or remote desktop.
>
> Does the README make that clear in ten seconds? https://github.com/tlbx-ai/MidTerm

## Daily operating loop

1. Publish one concrete story.
2. Answer replies and issues.
3. Note repeated confusion in plain language.
4. Fix the smallest README, setup or product issue that blocks real use.
5. Record the same metrics once.

If people visit but do not install, inspect installation trust and clarity. If they install but do not return, inspect first-run usefulness. Do not respond by adding more slogans.

## Fallback trial command

When someone cannot or does not want to install yet, offer:

```bash
npx @tlbx-ai/midterm
```

Describe it explicitly as a temporary trial. Keep it out of the headline and primary call to action.
