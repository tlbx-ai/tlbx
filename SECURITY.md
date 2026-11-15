# Security policy

For certificate verification, authentication requirements, and local password
recovery, see [Communication security](docs/CommunicationSecurity.md).

## Report a vulnerability privately

Use GitHub's private vulnerability reporting form:

**[Report a vulnerability](https://github.com/tlbx-ai/tlbx/security/advisories/new)**

Do not open a public issue for an undisclosed vulnerability. Include:

- the affected tlbx version, update channel, and platform;
- the security impact and conditions required to reproduce it;
- reproduction steps or a minimal proof of concept;
- any known exploitation or suggested mitigation.

Remove passwords, API keys, tokens, personal data, and unrelated repository
content from the report. For security hardening ideas that do not disclose an
exploitable vulnerability, use the normal public issue tracker.

## What to expect

The project aims to acknowledge a complete report within three business days
and provide an initial assessment within seven business days. Complex reports
may take longer to reproduce or remediate. The reporter and maintainer should
coordinate publication so users have a practical mitigation or update before
technical details become public.

When a vulnerability affects users, tlbx publishes the appropriate combination
of a GitHub Security Advisory, release notes, mitigation guidance, and a fixed
release. Security updates are provided without an additional fee.

## Supported versions

The current stable release is the supported production line. Reports against a
dev prerelease are accepted, but prereleases are evaluation builds and may
require updating to a newer prerelease rather than receiving a backport.
Historical releases do not receive separate fixes unless a security advisory
explicitly says otherwise.

See [SUPPORT.md](SUPPORT.md) for the lifecycle policy.

## Release integrity

Native release archives include the latest available platform-specific SPDX scan
snapshot, which may predate the build. Fresh audits and SBOM generation are on
demand. GitHub build provenance covers the archive and copied snapshot digests. Installers and the built-in
updater additionally require a signed manifest that binds the release version,
platform, channel, and packaged-file hashes.

See [Release integrity](docs/RELEASE-INTEGRITY.md) for verification steps.

## Security contact discovery

The canonical human-readable policy is also published at
[tlbx.ai/security](https://tlbx.ai/security). Automated security-contact
discovery is available at
[tlbx.ai/.well-known/security.txt](https://tlbx.ai/.well-known/security.txt).
