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
| `assets` | TypeScript typecheck and TS/CSS lint | CSS, HTML, text, static assets; also inspect the affected UI |
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
