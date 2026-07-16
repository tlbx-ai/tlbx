# Code Signing

tlbx uses three layers of code signing to protect users from tampered or fake binaries.

## Overview

| Layer | Platform | What it signs | How |
|-------|----------|---------------|-----|
| **Authenticode** | Windows | `mt.exe`, `mthost.exe`, `mtagenthost.exe` | Certum cloud certificate via SimplySign Desktop + signtool |
| **Apple codesign** | macOS | `mt`, `mthost`, `mtagenthost` | Apple Developer ID certificate + notarization |
| **ECDSA manifest** | All | `version.json` checksums | ECDSA P-384 signature over SHA256 hashes of binaries |

## When Binaries Get Signed

| Release type | Windows | macOS | Manifest (ECDSA) |
|-------------|---------|-------|------------------|
| Dev (`-dev` tags) | Unsigned | Signed + notarized | Signed |
| Stable (main branch) | **Authenticode signed** | Signed + notarized | Signed |

Dev releases skip Windows Authenticode signing because it requires manual authentication on a self-hosted runner. macOS signing and manifest signing are fully automated on GitHub-hosted runners for all releases.

## Windows Authenticode Signing

### Certificate

| Field | Value |
|-------|-------|
| Provider | Certum (by Asseco) |
| Product | Open Source Code Signing in the cloud, 365 days |
| CN | Open Source Developer Johannes Schmidt |
| Valid until | 2027-02-25 |
| Key storage | Certum cloud HSM (accessed via SimplySign Desktop) |
| Timestamp server | `http://time.certum.pl` |
| Thumbprint | `BD6FDDD59201F7A61F6B86D7F7EC69BFCD27724C` |

### How it works

The private key lives on Certum's cloud HSM. Signing requires:

1. **SimplySign Desktop** running on the signing machine — provides a virtual smart card / CSP that makes the cloud cert visible to `signtool.exe`
2. **SimplySign mobile app** for 2FA authentication — approx 2-hour session window per authentication
3. **signtool.exe** signs the binary using the certificate via the SimplySign CSP

There is no headless/CLI API. Each signing session requires mobile app confirmation.

### CI flow (stable releases only)

```
1. Tag push triggers release.yml
2. prepare job outputs is_dev=false
3. build-windows job dispatched to self-hosted runner [self-hosted, windows, signing]
4. dotnet publish builds mt.exe + mthost.exe + mtagenthost.exe
5. scripts/sign-windows-binaries.ps1 runs:
   a. Plays 5× notification sound + Windows toast notification
   b. Checks/launches SimplySign Desktop
   c. Retries signtool sign every 15s for up to 10 minutes
   d. Operator authenticates via SimplySign mobile app
   e. signtool signs all staged Windows binaries with SHA-256 digest + RFC 3161 timestamp
   f. signtool verify + Get-AuthenticodeSignature confirms valid signatures
6. SHA256SUMS.txt generated (checksums cover the signed binaries)
7. sign-release.ps1 ECDSA-signs version.json
8. Compress-Archive packages staging/ into mt-win-x64.zip
9. gh release upload publishes to GitHub Release
```

### Self-hosted runner

The runner `midterm-win-builder` is registered on the developer's Windows machine with labels `self-hosted`, `Windows`, `X64`, `signing`.

**Must run interactively** (not as a Windows service) because:
- Services run in Session 0 and cannot access SimplySign's virtual smart card
- The signing script plays notification sounds that require a desktop session

Start before a stable release: `C:\actions-runner\run.cmd`

Prerequisites: .NET 10 SDK, SimplySign Desktop (activated), Windows SDK (signtool), PowerShell 7+.

### Local testing

```powershell
# Ensure SimplySign Desktop is connected

# Sign any exe
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" `
  sign /a /tr http://time.certum.pl /td sha256 /fd sha256 mt.exe

# Verify
Get-AuthenticodeSignature mt.exe
```

Use `/sha1 BD6FDDD59201F7A61F6B86D7F7EC69BFCD27724C` instead of `/a` if multiple certificates are installed.

## macOS Code Signing

### Certificate

Apple Developer ID Application certificate for Johannes Schmidt (Team ID: `FK7G5C74WH`).

### CI flow (all releases)

```
1. Import P12 certificate from GitHub secret into a temporary keychain
2. codesign --force --sign "Developer ID Application: ..." --options runtime --timestamp
3. codesign --verify --strict confirms valid signature
4. Notarize via xcrun notarytool submit (mt, mthost when present, and mtagenthost zipped together)
5. Gatekeeper verifies notarization online when users download the binary
```

Stapling is not supported for standalone Mach-O binaries distributed as tarballs. macOS checks notarization status online via the quarantine extended attribute set on download.

## ECDSA Manifest Signing

Every release (dev and stable) gets an ECDSA P-384 signature in `version.json`. This lets the update client verify that the release contract for that update mode has not been tampered with.

### How it works

1. `scripts/sign-release.ps1` computes SHA-256 hashes for the binaries that belong to that release mode
   - Full update: `mt`, `mthost`, and `mtagenthost`
   - Web-only update: `mt` and `mtagenthost`; `mthost` is intentionally omitted so running installs preserve the current PTY host
2. Creates a deterministic JSON string of the checksums (sorted keys, compact format)
3. Signs the JSON with an ECDSA P-384 private key (stored as `SIGNING_PRIVATE_KEY` GitHub secret)
4. Writes `checksums` and `signature` fields into `version.json`

tlbx intentionally keeps this as a two-mode model:
- `webOnly=true`: frequent web/UI/web-facing releases; running installs preserve their current `mthost` and `mtagenthost`
- full update: low-level runtime refresh; running installs replace both host binaries

Release archives may still carry host binaries in web-only releases for fresh installs, offline/manual installation flows, and signing/notarization steps. That does not create a third release decision.

### Verification

`UpdateVerification.cs` verifies signatures during update checks:
1. Reads `checksums` and `signature` from the downloaded `version.json`
2. Reconstructs the deterministic JSON from checksums
3. Verifies the ECDSA P-384 signature against the hardcoded public key
4. Verifies SHA-256 hashes of the downloaded binaries listed in the manifest match the checksums

The public key is embedded in the binary. Unsigned releases (missing `signature` field) are accepted with a warning — this maintains backward compatibility.

## Runtime Self-Check

The running `mt.exe` binary checks its own code signature at startup and reports the result in the Settings UI under "About & Updates → Code Signing":

- **Windows**: `X509Certificate2.CreateFromSignedFile()` on `Environment.ProcessPath`
- **macOS**: `codesign --verify --strict` on `Environment.ProcessPath`
- **Linux**: Always reports unsigned (no standard signing mechanism)

The result is cached and served via the `/api/bootstrap` response (`codeSigned` field). The frontend shows a green "Signed" badge or gray "Unsigned" text.

## Key Files

| File | Purpose |
|------|---------|
| `.github/workflows/release.yml` | CI workflow — `build-windows` job with conditional Authenticode signing, `build` job with macOS codesign + notarize |
| `scripts/sign-windows-binaries.ps1` | Windows Authenticode signing with notification, retry loop, verification |
| `scripts/sign-release.ps1` | ECDSA P-384 manifest signing for all platforms |
| `src/Ai.Tlbx.MidTerm/Services/Updates/UpdateVerification.cs` | ECDSA signature + checksum verification during updates |
| `src/Ai.Tlbx.MidTerm/Startup/EndpointSetup.cs` | Runtime self-check (`DetectCodeSigning()`) |

## Alternatives Considered

| Option | Status |
|--------|--------|
| SignPath.io (free OSS) | Application pending since 2026-02-18. Would eliminate manual SimplySign step. |
| Azure Trusted Signing ($10/mo) | Azure tenant blocked due to inactivity |
| SSL.com eSigner ($200-400/yr) | Costs money, SmartScreen reputation builds over time |
| Full Certum automation (TOTP extraction) | Fragile 2-hour window, not officially supported by Certum |
