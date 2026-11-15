# Release integrity

Every native tlbx release is built for a specific runtime identifier (RID).
Use the archive and SPDX document with the same RID.

| Platform | Archive | SBOM |
| --- | --- | --- |
| Windows x64 | `mt-win-x64.zip` | `mt-win-x64.spdx.json` |
| Windows x86 | `mt-win-x86.zip` | `mt-win-x86.spdx.json` |
| macOS Apple Silicon | `mt-osx-arm64.tar.gz` | `mt-osx-arm64.spdx.json` |
| macOS Intel | `mt-osx-x64.tar.gz` | `mt-osx-x64.spdx.json` |
| Linux x64 | `mt-linux-x64.tar.gz` | `mt-linux-x64.spdx.json` |
| Linux arm64 | `mt-linux-arm64.tar.gz` | `mt-linux-arm64.spdx.json` |

## Verify a downloaded archive

Install the [GitHub CLI](https://cli.github.com/), authenticate it, and verify
the archive against the attestations published by this repository:

```shell
gh attestation verify mt-win-x64.zip --repo tlbx-ai/tlbx
```

Replace the file name with the archive for the target RID. Verification proves
that the archive digest is covered by an attestation issued through the tlbx
GitHub Actions release workflow. It does not by itself prove that the software
is vulnerability-free.

## Inspect the SBOM

The platform `.spdx.json` file is the latest available SPDX 2.3 scan snapshot.
It may predate this build and changes to code or dependencies. Fresh snapshots
are generated only by Tests (on demand), suite=all or security, from verified
published packages. Inspect `creationInfo.created` and snapshot annotations for
the scanned package and date; do not treat reuse as a fresh inventory.

Current releases attest the archive and copied SPDX file as build outputs. Verify
the snapshot digest independently with `gh attestation verify mt-win-x64.spdx.json
--repo tlbx-ai/tlbx`. Historical releases instead carry exact archive/SBOM
predicates. No fresh archive/SBOM predicate is issued for a reused snapshot.

The public SBOM is transparency material. The installer does not trust package
names from the SBOM as an installation authorization mechanism.

## Installer and updater verification

The native installers and built-in updater fail closed unless the extracted
archive contains a valid manifest-v2 signature. The signed payload binds:

- web, PTY, and protocol versions;
- platform and update channel;
- web-only/full-runtime update semantics;
- the complete set of packaged-file SHA-256 hashes.

Each packaged file is hashed again after extraction. A missing signature,
metadata mismatch, unlisted file, missing file, or hash mismatch stops the
installation or update.

The manifest protects the installed contents. GitHub artifact attestations
independently protect the downloaded archive and record build provenance.

## Installed mixed versions

A web-only update deliberately preserves the running `mthost` and
`mtagenthost`. An installed system can therefore report different server,
frontend, and host versions. A snapshot describes its recorded scanned package,
not every later archive or possible mixed-version installation.

Use **Settings -> Updates & About** to inspect the versions actually running on
the current host.
