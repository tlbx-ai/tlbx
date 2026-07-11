# MidTerm relaunch: 1,000-user campaign

Campaign window: **2026-07-11 through 2026-07-19**  
Public destination: **https://github.com/tlbx-ai/MidTerm**  
Owner voice: **Johannes Schmidt / @spaceMonster — technical builder, concise, candid, no launch-bro theater**

## Goal and honest measurement

The stretch goal is **1,000 new people trying MidTerm by Sunday, July 19**.

MidTerm has no product telemetry, so an exact unique-user count is not available. Do not invent one and do not add invasive telemetry for a launch number. Track these independent signals without summing them into a fake “unique users” total:

1. GitHub rolling 14-day unique clones — strongest available activation proxy.
2. Weekly `@tlbx-ai/midterm` npm downloads — easiest trial path.
3. Incremental stable release-asset downloads — native-install path.
4. GitHub unique visitors and stars — discovery and intent, not users.
5. Replies from people who actually launched a session — qualitative activation proof.

The 1,000-user goal may be reported as reached only when one defensible channel proves it or when a later privacy-respecting activation measure can deduplicate channels. Until then, report the numbers separately.

### Baseline captured 2026-07-11

| Signal | Baseline |
| --- | ---: |
| GitHub stars | 96 |
| GitHub unique visitors, rolling 14 days | 18 |
| GitHub unique clones, rolling 14 days | 62 |
| npm downloads, previous 7 days | 36 |
| Latest stable release binary downloads | 19 |

At the current baseline, 1,000 activated users in eight days is a real stretch. Daily posting to an existing ~400-follower account will not be enough alone. The campaign therefore combines a clearer conversion surface, repeated product proof, a founder story, community launches, and direct feedback asks.

## Positioning

### Category

**The open control room for AI agents.**

MidTerm is not another coding agent. It is the provider-open operating layer around the agents a developer already uses.

### One-sentence promise

Run any AI coding agent locally, keep every session alive, validate its work, and steer the whole fleet from any browser.

### Three messages to repeat

1. **The model can change. Your control room stays.** Provider-open real PTYs plus structured runtimes where supported.
2. **“Done” is not proof.** Terminal, git, files, Dev Browser, logs, screenshots, and responsive/mobile validation stay in the same loop.
3. **The work stays home. The control surface goes anywhere.** Local tools and context remain on the machine; the browser follows the human.

### Copy to avoid

- “Web terminal multiplexer” as the opening category. It describes an implementation layer, not the user value.
- Long feature inventories before the first install command.
- “Revolutionary,” “game-changing,” “10x,” “autonomous,” or unsupported “best” claims.
- Attacks on Codex, Claude Code, cloud agents, IDEs, tmux, or SSH. MidTerm makes those tools work better together.
- Claims that repositories never reach an AI provider. MidTerm does not upload them, but the launched agent follows its own provider configuration.

## Asset map

### Brand and README SVG masters

- `docs/marketing/readme/midterm-mark.svg` — square logo for avatars, thumbnails, and social exports.
- `docs/marketing/readme/midterm-wordmark.svg` — category and promise.
- `docs/marketing/readme/agent-control-room.svg` — fleet, proof surfaces, mobile intervention.
- `docs/marketing/readme/agent-proof-loop.svg` — prompt → build → validate → steer.
- `docs/marketing/readme/local-first-anywhere.svg` — local ownership and browser reach.

### Existing verified video proof

Landscape clips live locally under:

`docs/marketing/ScreenshotAutomation/output/social-feature-series/final-landscape-2026-06-03/`

Mobile clips live locally under:

`docs/marketing/ScreenshotAutomation/output/mobile-feature-series/final-mobile-2026-06-13/`

Primary campaign clips:

| Story | Asset |
| --- | --- |
| Control room / fleet | `06-multi-agents.mp4` |
| Whole desktop workspace | `09-desktop-control.mp4` |
| Browser validation | `08-dev-browser-validation.mp4` |
| Files and git | `10-files-git-context.mp4` |
| Agent on the go | mobile `02-agents-on-the-go.mp4` |
| Pocket control surface | mobile `01-pocket-terminal.mp4` |
| Full product pass | `00-all-features-stitchup.mp4` — trim before social use; 83 seconds is too long for the opening post |

Every post should show the product. Avoid generic AI art. The current clips were already audited for private data and visible MidTerm state.

## Campaign rhythm

One central idea per day. Post once, then spend the next hour answering real questions. Do not dump the same link into many communities on the same day.

Recommended X times are **12:30 or 18:30 Europe/Berlin**. Choose the slot when Johannes can stay present for at least 60 minutes. Product Hunt and Show HN have their own notes below.

### Saturday, July 11 — conversion surface and baseline

- Publish the new GitHub description, topics, README, and assets.
- Pin the final launch post after it is live.
- Capture the baseline table above and a screenshot of the public README.
- Test `npx @tlbx-ai/midterm` on a clean or temporary profile.
- No broad social push until the default-branch README is actually live.

### Sunday, July 12 — category launch

Asset: `06-multi-agents.mp4`

X draft:

> AI agents got better. The workflow around them didn't.
>
> MidTerm is the open control room for AI agents: run any CLI agent locally, keep every session alive, validate its work, and steer the fleet from any browser.
>
> Open source: https://github.com/tlbx-ai/MidTerm

First reply:

> Try it without installing a service:
>
> `npx @tlbx-ai/midterm`
>
> It downloads the native build, starts locally, and opens the browser.

Action: pin the first post for the campaign week. Reply to every substantive question with a direct answer, screenshot, or short clip.

### Monday, July 13 — the fleet, not the model

Asset: `09-desktop-control.mp4`

X draft:

> One model is never the point.
>
> I use different agents for different jobs. MidTerm keeps AI agents and normal shells alive in one place—with status, repos, terminals, files and approvals visible.
>
> The model can change. The control room stays.
>
> https://github.com/tlbx-ai/MidTerm

LinkedIn draft:

> AI coding agents have become good enough to work for long stretches. The workflow around them has not caught up.
>
> I kept ending up with the same problem: several terminal windows, different agent providers, hidden approval prompts, a browser somewhere else, and no clear answer to “which session needs me now?”
>
> That is what MidTerm has become: an open control room for AI agents.
>
> It runs on the machine that already owns the repositories, tools, credentials, dev servers and hardware. It keeps real agent and terminal sessions alive, puts files, git and browser validation around them, and exposes one protected browser workspace on desktop, tablet or phone.
>
> MidTerm is not another model and it does not ask you to move the project into a new cloud environment. Use Codex, Claude Code, Grok, Gemini CLI, Copilot CLI, Aider or another terminal-native agent. The model can change; the control room stays.
>
> It is open source and can be tried with one command:
>
> `npx @tlbx-ai/midterm`
>
> https://github.com/tlbx-ai/MidTerm
>
> I would especially value feedback from people already running two or more coding-agent sessions: what is still missing from your control loop?

### Tuesday, July 14 — proof, not “done”

Asset: `08-dev-browser-validation.mp4`

X draft:

> “The build passed” is not proof the feature works.
>
> MidTerm puts a Dev Browser beside the agent: DOM, console, proxy logs, screenshots, and responsive/mobile test frames.
>
> The agent can build, inspect and fix in one loop.
>
> https://github.com/tlbx-ai/MidTerm

First reply:

> This is the part I care about most: agents should not stop at code output. The validation surface must be close enough that checking the real result is the natural next step.

### Wednesday, July 15 — Show HN

Asset: public GitHub repository; no landing-page detour.

Suggested title:

> Show HN: MidTerm – an open control room for local AI coding agents

Do **not** paste an AI-written founder comment into Hacker News. HN explicitly asks users not to post generated or AI-edited comments. Johannes should write the submission context in his own words, using only these factual prompts:

- MidTerm began as a personal browser terminal and evolved through daily use with coding agents.
- The actual problem is supervising long-running local agents across terminals, repos, browser proof and devices.
- It is self-hosted, open source, cross-platform, and immediately runnable with `npx @tlbx-ai/midterm`.
- The interesting technical choices are real PTYs in `mthost`, structured provider runtimes in `mtagenthost`, Native AOT, reconnect/recovery, browser automation, and explicit human control.
- Ask for criticism of the control-room workflow, not votes.

Be available for several hours. Never ask friends or followers to upvote or comment. The [Show HN guidelines](https://news.ycombinator.com/showhn.html) require a project people can run, a maker who is present, and no vote solicitation.

X companion post after the HN submission is live:

> I just put MidTerm on Show HN.
>
> It started as the browser terminal I wanted for myself. AI agents turned it into a control room for persistent sessions, browser proof and remote supervision.
>
> Technical criticism is very welcome: [Show HN URL]

### Thursday, July 16 — the human works in moments

Asset: mobile `02-agents-on-the-go.mp4`

X draft:

> AI agents work in long loops. Humans contribute in short moments.
>
> A quick phone answer can prevent an hour of wrong work. MidTerm keeps the local session alive and gives you the same control surface on desktop, tablet and mobile.
>
> https://github.com/tlbx-ai/MidTerm

LinkedIn follow-up:

> A useful AI-agent workflow is not fully autonomous. It is interruptible.
>
> The agent may work for 40 minutes, but the human contribution is often a 20-second decision: approve this migration, choose between two approaches, clarify the edge case, stop the wrong direction.
>
> If that question is hidden in a terminal on another machine, the loop stalls. If the agent continues without the answer, it may create expensive rework.
>
> MidTerm keeps the local session alive and makes those moments reachable from the browser on desktop, tablet and phone. The human stays in control without staying in the chair.
>
> https://github.com/tlbx-ai/MidTerm

### Friday, July 17 — local-first architecture

Asset: `02-web-terminal.mp4`, followed by the `local-first-anywhere.svg` export in a reply.

X draft:

> Your repo already has the tools, credentials, servers and hardware the agent needs.
>
> MidTerm leaves that context on your machine. It adds a protected browser control surface—not another cloud environment to rebuild.
>
> https://github.com/tlbx-ai/MidTerm

Technical thread reply:

> The runtime split is deliberate:
>
> • `mt` serves the workspace and APIs
> • `mthost` owns real PTYs
> • `mtagenthost` owns structured agent runtimes
> • the Dev Browser owns validation evidence
>
> Architecture: https://github.com/tlbx-ai/MidTerm/blob/main/docs/ARCHITECTURE.md

### Saturday, July 18 — Product Hunt + founder story

Product Hunt says weekends can work well for side projects and reports more Visit-button clicks on weekends. Schedule only if Johannes can stay present and respond. Their guide recommends a 12:01 a.m. Pacific start; in July that is approximately **09:01 Europe/Berlin**.

Product name:

> MidTerm

Tagline — 45 characters, below the 60-character limit:

> The open control room for local AI agents

Description:

> Run the AI coding agents you already use on the machine that owns your work. MidTerm keeps every session alive, brings terminals, files, git, browser validation and mobile controls into one self-hosted workspace, and lets you steer the fleet from any browser. Open source, provider-open, and runnable with `npx @tlbx-ai/midterm`.

Maker first comment draft:

> Hi Product Hunt — I’m Johannes, the developer behind MidTerm.
>
> MidTerm started as the browser terminal I wanted for myself. Once coding agents began running real tasks for 30 minutes, an hour, or longer, the bigger problem became obvious: the agents were capable, but the workflow around them was fragmented.
>
> I needed one place to see every live session, notice which agent needed a decision, inspect the repository and terminal context, open the app beside the shell, check browser logs and screenshots, and continue from another device without moving the project into a hosted IDE.
>
> That is the product today: an open, local-first control room for AI agents.
>
> A few principles matter to me:
>
> - Use the agent you prefer. MidTerm runs terminal-native agents in real PTYs and adds structured views where provider runtimes support them.
> - Keep the work on the machine that already has the repository, tools, credentials, servers and hardware.
> - Make validation part of the agent loop. Files, git, previews, DOM state, logs, screenshots and responsive testing belong beside the session.
> - Keep the human able to answer, approve, interrupt or redirect from desktop, tablet or phone.
>
> MidTerm is open source under AGPL-3.0. The fastest trial is:
>
> `npx @tlbx-ai/midterm`
>
> I would love blunt feedback on one question: if you already use coding agents for long-running work, what information or control is still missing when you step away from the terminal?

Product Hunt gallery preparation:

1. Export `midterm-mark.svg` as a 240×240 logo thumbnail, not the full horizontal wordmark.
2. Export `agent-control-room.svg` to 1270×760 with crop-safe margins.
3. Export `agent-proof-loop.svg` to 1270×760 with crop-safe margins.
4. Add a short public demo video; Product Hunt accepts YouTube links, not a local MP4 upload.
5. Ask people to visit and comment, never directly to upvote. This follows the [Product Hunt launch guide](https://www.producthunt.com/launch/preparing-for-launch).

X draft once live:

> MidTerm is live on Product Hunt today.
>
> It is the open control room I built for running local AI agents, validating their work, and steering long sessions from any browser.
>
> I would value a visit and an honest comment—especially from multi-agent users: [Product Hunt URL]

### Sunday, July 19 — invitation and transparent result

Asset: mobile `01-pocket-terminal.mp4` or the strongest reply/demo generated during the week.

X trial draft:

> Try this tonight:
>
> `npx @tlbx-ai/midterm`
>
> Start your usual agent. Close the browser. Reopen MidTerm. The same local session is waiting—with its terminal and context intact.
>
> I want the blunt version: what breaks or confuses you?
>
> https://github.com/tlbx-ai/MidTerm

End-of-day result post template — fill with real numbers only:

> One week of explaining MidTerm as an agent control room instead of a browser terminal:
>
> • GitHub unique visitors: [X]
> • unique clones: [X]
> • npm downloads: [X]
> • stable binary downloads: [X]
> • stars: [96 → X]
>
> Biggest lesson: [one concrete learning]
> Next fix: [one concrete action]

## Direct feedback outreach

Send this only to people Johannes genuinely knows or whose work makes the request relevant. Ten thoughtful asks are better than 100 cold DMs. Do not ask anyone to like, star, upvote, or repost.

Exact DM draft:

> Hey [name] — I rebuilt MidTerm around the problem it now actually solves: supervising local AI agents across terminals, repos, browser proof and devices.
>
> If that workflow is relevant to you, I’d value a blunt five-minute try and the first thing that confuses or disappoints you. No need to share it.
>
> `npx @tlbx-ai/midterm`
> https://github.com/tlbx-ai/MidTerm

## Community rules and exclusions

- **Hacker News:** valid only as a real Show HN with an immediately runnable product and Johannes present. Do not solicit votes. Johannes writes his own HN comments because HN rejects generated or AI-edited comments.
- **Product Hunt:** ask for visits and feedback, never direct upvotes. Use a personal maker account. Keep the product name and tagline plain.
- **r/commandline:** skip this campaign. Its current rules prohibit AI-generated post text and generally disallow generative-AI projects unless already popular. Forcing MidTerm into that community is bad targeting.
- **r/selfhosted and other subreddits:** post only after reading the current rules from the logged-in account and confirming Johannes has a real participation history. A public repo does not exempt self-promotion from community rules.
- **No cross-platform brigading:** never tell X/LinkedIn followers to upvote HN, Reddit, or Product Hunt.

## Daily operating loop

Before the post:

1. Confirm the GitHub default-branch README and install command are live.
2. Open the exact attached clip and inspect the first, middle and final frames.
3. Record current traffic/download/star numbers once, not repeatedly.
4. Choose a time when Johannes can answer for the next hour.

After the post:

1. Reply to questions before posting another promotion.
2. Turn repeated confusion into a README fix the same day.
3. Quote-post only when adding proof or a new technical insight.
4. Save strong user wording verbatim as future positioning evidence.
5. Record end-of-day metrics and one learning.

## Scorecard

| Date | Post/channel | Views | Link clicks | GitHub visitors | Unique clones | npm downloads | Stable downloads | Stars | Activated-user evidence | Learning |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| Jul 11 | Baseline | — | — | 18 / 14d | 62 / 14d | 36 / 7d | 19 total | 96 | — | — |
| Jul 12 | X category | | | | | | | | | |
| Jul 13 | X + LinkedIn fleet | | | | | | | | | |
| Jul 14 | X proof loop | | | | | | | | | |
| Jul 15 | Show HN | | | | | | | | | |
| Jul 16 | X + LinkedIn mobile | | | | | | | | | |
| Jul 17 | X local-first | | | | | | | | | |
| Jul 18 | Product Hunt + X | | | | | | | | | |
| Jul 19 | Trial ask + results | | | | | | | | | |

## Decision rules

- If a post gets attention but no repository clicks, tighten the first sentence and CTA; do not add more features to the copy.
- If people reach GitHub but do not run `npx`, inspect install trust, command visibility, and first-run friction.
- If trials rise but replies mention confusion, fix onboarding before increasing traffic.
- If one proof clip clearly outperforms the others, reuse its underlying story with a new technical angle; do not merely repost the same text.
- If Show HN or Product Hunt creates a live conversation, pause the scheduled post and serve that conversation first.
- If the 1,000-user stretch is missed, publish the real result and the strongest learning. Credibility compounds; inflated metrics do not.
