# Build and release verification

Every release entry point (`release-dev.ps1`, `release-local.ps1`, `release.ps1`,
`promote.ps1`) requires `-TestCategories`. There is no default or skip-all choice.
Choose after reviewing **all changes since the previous release**, including shared
code, dependencies and build configuration; the dev release prints that diff.

```powershell
./scripts/release-dev.ps1 -Bump patch -ReleaseTitle 'Fix terminal layout' `
    -ReleaseNotes @('Keep the terminal input visible after resizing.') `
    -mthostUpdate no -TestCategories assets

./scripts/release-dev.ps1 -Bump patch -ReleaseTitle 'Fix session recovery' `
    -ReleaseNotes @('Restore the session after a connection interruption.') `
    -mthostUpdate no -TestCategories frontend,server

./scripts/promote.ps1 -TestCategories all
```

| Category | Checks | Choose for |
|---|---|---|
| `assets` | TypeScript typecheck, TS/CSS lint and theme/CSS audits | CSS, HTML, text, static assets; also inspect the affected UI |
| `frontend` | Typecheck, lint, full frontend tests including Kitty graphics | TypeScript behavior and browser contracts |
| `server` | Server unit and integration suites | Server C# and REST APIs |
| `runtime` | All three .NET suites, including AgentHost | PTY, AgentHost, shared code, IPC and protocol changes; add frontend for browser protocol changes |
| `installers` | Installer contract verification | Install/uninstall, startup, certificates and updater changes |
| `dependencies` | Server npm/NuGet audit, lock/signature checks and Claude bridge build | Server dependencies and lockfiles |
| `build` | Release script behavioral checks, runtime builds, Windows AOT execution probe | Build/release scripts, packaging, SDK and native dependency changes |
| `all` | All of the above, without duplicate frontend/.NET suites | Broad changes, uncertain impact; required for stable releases |

Use `all` by itself. Otherwise pass one category or a PowerShell array such as
`-TestCategories frontend,server`. Frontend packaging always runs; its one fresh
install is reused by the selected checks and audit. Hosted server dependency audits,
package signing, SBOM/provenance and platform builds remain release gates.

## PR workflow (tlbx code repository only)

`dev` and `main` require PRs and passing checks, including for administrators.
Never disable protection, use `--admin`, or push directly to these branches.
The terminal product does not impose this workflow on repositories it opens.

Start each task in an exclusively owned, clean numbered checkout:

```powershell
git fetch origin
git switch dev
git merge --ff-only origin/dev
git switch -c fix/short-kebab-description
```

Use `feat/`, `fix/`, or `chore/` followed by lowercase ASCII words separated by
hyphens. One task per branch. Open a draft PR once useful work is pushed; mark
it ready before releasing. Commit intended changes before invoking a release.

`release-dev.ps1` keeps the existing required release arguments. It prepares the
version bump and generated assets on the task branch, verifies locally, then
pushes and creates/reuses the PR into dev. It waits for required GitHub checks,
merges with the exact prepared head SHA, and tags that merge commit. Tags cannot
publish unless Release CI finds the matching merged PR and version. This is a
submission until the Release run and all platform assets succeed.

Add `-PrepareOnly` to file the PR without merging/tagging. Re-run the same command
without that switch to finish. Retry state is stored inside this checkout's Git
directory, keyed by task branch: retries reuse the version, PR and tag. Keep the
checkout until the release succeeds. Changed arguments do not replace a pending
release; it resumes the saved request. This is printed in the release summary.

If the target branch advances, preparation stops. Merge the target into the task
branch and resolve conflicts, commit the result, then use `-Reprepare` with the
release arguments to rerun verification and update the same open PR. Explicit
re-preparation retains the saved dev candidate version while it is unmerged and
untagged. Failed preparation retains its state so another correction cannot
silently allocate a second version. Both paths reuse the same PR.
Do not re-prepare a merged/closed PR or reuse a retired task branch name.

Live milestones and the PR URL are flushed to stderr and appended to
`.git/tlbx-release-progress.log`, independent of buffered build output. Dev and
stable releases remain draft until all six platform archives and their six
SBOMs pass the publication gate. SBOM generation retries once on failure; a
second failure still blocks publication. No checksum or attestation is skipped.

For stable releases, invoke `promote.ps1 -TestCategories all` from clean updated
dev. It freezes the accepted candidate on `chore/promote-X-Y-Z`, includes stable
version/generated changes in its PR into main, and tags only the merged result.
It then creates `chore/sync-main-X-Y-Z` from current dev, merges main into it,
and opens/merges its synchronization PR into dev. This preserves newer dev work
without having to update protected main to satisfy an up-to-date check. Both use merge commits;
never squash or rebase integrations between long-lived branches. Different tip
IDs are expected: dev must contain main's ancestry, including stable metadata.
If synchronization conflicts with newer dev work, resolve and commit on its retained synchronization branch, return to the
promotion branch, and rerun the promotion command. No bypass or force push.
`release.ps1` is an alias for promotion; the old direct-main bump path is removed.
`release-local.ps1` also requires a task branch and files a PR into dev; it
produces local artifacts without automatically merging or publishing a tag.

GitHub automatically deletes merged remote task branches; main/dev remain
protected. After successful release CI and assets, run `scripts/finish-task.ps1`
in the exclusively owned task checkout. It verifies the exact tip was merged,
requires a clean tree and dev ancestry, returns to updated dev and deletes the
local branch without force. If another session uses the checkout, defer cleanup
and report the branch and next step. Keep parked/unmerged work until deliberately
resumed or abandoned; never delete it merely because it is old.

Required checks on both branches: `PR frontend`, `PR server`,
`Release workflow (ubuntu-24.04)`, `Release workflow (windows-2025)`, and both
CodeQL `analyze` languages. PR checks run without path filters, so documentation
and synchronization PRs cannot wait forever for an untriggered required job.
No mandatory second-person approval is configured. GitHub still enforces
required checks, an up-to-date branch and resolved review conversations.

## Mobile and tooling

Android/iOS already load the UI from the configured tlbx server. A server release
does not build or publish their apps. Mobile verification selects each platform
independently on source changes, with both checked on a manual or weekly run.
Store publication remains manual through the dedicated `app-v...` workflows.
Android dependency auditing belongs to Android verification/releases. Screenshot
automation and npm-launcher checks run in the tooling workflow, also weekly.
`all` means all **server release** clusters, not an Xcode requirement on Windows.

## Reusing host binaries

Dev web-only releases may reuse `mthost`, `mtagenthost` and Windows `mttmux` plus
ConPTY from `v<pty>` in tlbx-ai/tlbx. The complete archive must pass GitHub provenance
verification for `release.yml`. Its runtime-input metadata must match RID, runtime
version, configuration, exact SDK and canonical source/dependency/build inputs;
binary checksums are checked before copying. New archives are signed and attested
again with freshly generated SBOMs. Reused macOS host signatures are preserved.

Full runtime and stable releases build hosts afresh. Missing baseline assets or
old/mismatched metadata fall back to a regular build; failed provenance or checksum
verification aborts. `mtagenthost` now shares `pty` versioning with `mthost`.
An initial full runtime release seeds the reuse metadata. `mt` still builds for all
six supported targets because it embeds the current web frontend.

Do not use `-mthostUpdate no` to hide runtime source changes: it controls installed
host replacement, independently of test selection or the cold-build fallback.
