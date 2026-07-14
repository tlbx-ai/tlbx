# Release verification and build audit

MidTerm release artifacts are built from a version tag by the public
[`release.yml`](../.github/workflows/release.yml) workflow. This document states
what that process currently proves and, equally importantly, what it does not.

## Current release chain

A pushed `v*` tag starts the release workflow. The workflow:

1. reads the annotated tag; development tags create/update a published
   prerelease, while stable tags create or reuse an unpublished draft
2. builds the frontend once; stable tags additionally run the configured
   frontend and .NET test jobs
3. publishes the runtime set for each release platform
4. generates a `SHA256SUMS.txt` file **inside each platform archive**
5. signs the release manifest and performs platform signing/notarization where
   configured
6. uploads the platform archives to the matching GitHub release
7. for stable tags, publishes the npm launcher, verifies the exact six expected
   platform archives are present and nonempty, and only then publishes the draft

Development releases use GitHub-hosted runners. Stable macOS and Linux builds
also use GitHub-hosted runners; stable Windows x64 and x86 builds use the repository's
self-hosted Windows signing runner. Apple signing/notarization, Windows signing,
and manifest signing require repository secrets. Compilation inputs are public,
but it is incorrect to describe the entire release job as secret-free or every
runner as third-party hosted.

The workflow declares its SDK and action versions, but hosted runner images and
major-version action tags can evolve. The environment is inspectable from the
run log; it is not an immutable build appliance.

## Verify a release

### 1. Match tag, commit, and workflow run

Open the release and the Release workflow:

- <https://github.com/tlbx-ai/MidTerm/releases>
- <https://github.com/tlbx-ai/MidTerm/actions/workflows/release.yml>

Check that:

- the run was triggered by the expected tag
- the workflow commit is the commit referenced by that tag
- required test and platform jobs succeeded
- the release assets were uploaded by that run

The release workflow is tag-triggered; it has no manual-dispatch trigger.

### 2. Verify the archive's internal manifest

Each archive contains a platform-local `SHA256SUMS.txt`. There is currently no
separate top-level `checksums.txt` release asset.

For a Unix archive:

```bash
mkdir midterm-release
tar -xzf mt-linux-x64.tar.gz -C midterm-release
cd midterm-release
sha256sum -c SHA256SUMS.txt
```

For a Windows archive, extract it and compare every line in
`SHA256SUMS.txt` with `Get-FileHash -Algorithm SHA256` for the named binary.

This check detects damaged or changed files *inside the extracted archive*.
Because the checksum manifest travels in the same archive, it is not an
independent signature of the archive itself. Authentication additionally relies
on the Git tag/workflow chain, the signed `version.json` manifest, and applicable
platform code signatures.

### 3. Inspect platform signatures

- Stable Windows binaries are Authenticode-signed and the workflow rejects an
  invalid signature before upload.
- macOS binaries in both release channels are Developer ID signed and submitted
  to Apple's notarization service before upload.
- Development Windows builds and Linux binaries do not provide the same
  platform trust signals; inspect the workflow run and internal checksum
  manifest instead.

## Rebuilding locally

The runtime-set publisher used by CI is
[`scripts/publish-runtime-set.ps1`](../scripts/publish-runtime-set.ps1). From a
checkout of the release tag, a representative Windows build is:

```powershell
pwsh -File scripts/publish-runtime-set.ps1 -Rid win-x64 -Configuration Release
```

Replace the runtime identifier for another supported target and use the matching
host operating system/toolchain.

The repository enables deterministic .NET compilation settings and
`ContinuousIntegrationBuild` in CI. MidTerm does **not** currently claim that a
local Native AOT build will be byte-identical to a published artifact. Native
toolchains, linker inputs, runner images, signing, and notarization can all
affect final bytes. A differing local binary hash is therefore a reason to
inspect inputs and logs, not by itself proof of tampering.

## Honest trust boundary

The verifiable evidence available today is:

- public source and release workflow at the tagged commit
- public GitHub Actions logs for hosted portions of the build while retained
- tag-to-run-to-release correspondence
- tests executed by the workflow for stable tags
- internal per-platform binary checksums
- signed release manifest and stable platform signatures where applicable

The process does not yet provide independently published archive checksums,
SLSA provenance, or guaranteed byte-for-byte reproducibility. Those would be
meaningful future improvements; this document must not imply they already exist.
