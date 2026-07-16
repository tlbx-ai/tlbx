# tlbx multi-instance installer guide

This document is the operational guide for installing more than one isolated
tlbx service instance on the same physical or virtual machine without Docker
or VMs.

The normal single-instance installers (`install.ps1` and `install.sh`) remain
the default path for regular users. Use this guide only when one machine must
host multiple independent tlbx users or test tenants.

## Core model

Every multi-instance tlbx installation is a separate service scope:

- one service identity
- one HTTPS port
- one install directory for binaries
- one settings directory for settings, secrets, certificates, logs, sessions,
  update results, and instance metadata
- one runtime identity passed to `mt` at process start
- one owned set of `mthost`, `mtagenthost`, and related child host processes

The key invariant is: never share settings directories, service names, or ports
between instances. Sharing the same release asset is fine; sharing the extracted
binary directory is not.

## When to use this installer

Use `install-multi.ps1` or `install-multi.sh` when:

- several people need their own tlbx instance on one machine
- each person needs a separate login password and session history
- each instance must be updated, removed, or restarted independently
- the host must avoid Docker, VMs, or per-user OS accounts

Use the normal installer when:

- there is only one tlbx service on the machine
- the service should stay at the default identity and port
- you are upgrading an existing normal installation

Do not mix installer families for the same instance. A normal `MidTerm` service
should be managed by the normal installer. `MidTerm-<name>`,
`midterm-<name>`, and `ai.tlbx.midterm.<name>` services should be managed by
the multi-instance installer.

## Platform and architecture support

The multi-instance installers use the same release assets as the normal
installers.

| OS | Architecture | Asset | Supported |
| --- | --- | --- | --- |
| Windows | x64 | `mt-win-x64.zip` | yes |
| Windows | x86 / 32-bit | `mt-win-x86.zip` | yes |
| Windows | ARM64 | `mt-win-x64.zip` | yes, via Windows x64 emulation |
| macOS | Apple Silicon / arm64 | `mt-osx-arm64.tar.gz` | yes |
| macOS | Intel x64 | `mt-osx-x64.tar.gz` | yes |
| macOS | x86 / 32-bit | none | no |
| Linux | x64 / amd64 | `mt-linux-x64.tar.gz` | yes |
| Linux | arm64 / aarch64 | `mt-linux-arm64.tar.gz` | yes |
| Linux | x86 / 32-bit | none | no |

32-bit macOS and 32-bit Linux are intentionally rejected by
`install-multi.sh`. The .NET self-contained release pipeline does not publish
viable 32-bit assets for those operating systems.

## Release asset lookup

By default, the installers download from the latest GitHub release of
`tlbx-ai/tlbx`. Set `VersionTag` / `--version-tag` to pin a release. Set
`AssetPath` / `--asset-path` to use a local release asset instead of GitHub.

Expected asset names:

- `mt-win-x64.zip`
- `mt-win-x86.zip`
- `mt-osx-arm64.tar.gz`
- `mt-osx-x64.tar.gz`
- `mt-linux-arm64.tar.gz`
- `mt-linux-x64.tar.gz`

If an asset is missing, stop and inspect the release. Do not substitute an asset
from a different OS. Windows ARM64 is the only intentional cross-architecture
fallback and it uses the Windows x64 asset.

## Instance identity rules

Instance names are normalized before use. Characters outside `A-Z`, `a-z`,
`0-9`, `_`, and `-` become `-`; leading and trailing `-` or `_` are removed.
If no explicit names are provided, the installers generate `user1`, `user2`,
and so on.

For an instance named `alice`, the generated identities are:

| Platform | Service identity |
| --- | --- |
| Windows | `MidTerm-alice` |
| Linux | `midterm-alice` |
| macOS | `ai.tlbx.midterm.alice` |

The settings directory contains an `instance.json` manifest with the effective
name, port, bind address, service identity, install directory, settings
directory, and update timestamp.

## Default directories

Windows defaults:

| Purpose | Default |
| --- | --- |
| Settings root | `%ProgramData%\MidTerm\instances` |
| Install root | `%ProgramFiles%\MidTerm\instances` |
| Instance settings | `%ProgramData%\MidTerm\instances\<name>` |
| Instance binaries | `%ProgramFiles%\MidTerm\instances\<name>` |

macOS and Linux defaults:

| Purpose | Default |
| --- | --- |
| Settings root | `/usr/local/etc/midterm-instances` |
| Install root | `/usr/local/lib/midterm/instances` |
| Instance settings | `/usr/local/etc/midterm-instances/<name>` |
| Instance binaries | `/usr/local/lib/midterm/instances/<name>` |

These roots are intentionally separate from the normal installer paths. Do not
point a multi-instance install at the normal service directory.

## Windows command reference

Run from an elevated PowerShell session.

```powershell
.\install-multi.ps1 -Mode plan -Names alice,bob -BasePort 2010
.\install-multi.ps1 -Names alice,bob -Ports 2010,2011
.\install-multi.ps1 -Mode update -Names alice
.\install-multi.ps1 -Mode update-all
.\install-multi.ps1 -Mode list
.\install-multi.ps1 -Mode remove -Names alice
```

### Windows parameters

| Parameter | Type | Default | Applies to | Meaning |
| --- | --- | --- | --- | --- |
| `-Mode` | `install`, `plan`, `list`, `update`, `update-all`, `remove` | `install` | all | Selects the operation. |
| `-Names` | `string[]` | empty | all except `update-all` / `list` | Comma-separated or array-style instance names. |
| `-Count` | `int` | `0` | `install`, `plan` | Generates `user1..userN` when `-Names` is empty. If both are empty, one instance is planned or installed. |
| `-BasePort` | `int` | `2000` | `install`, `plan` | First candidate port when `-Ports` is not provided. Used ports are skipped. |
| `-Ports` | `int[]` | empty | `install`, `plan`, `update`, `remove` | Explicit ports. Must contain exactly one port per resolved instance name. |
| `-BindAddress` | `string` | `0.0.0.0` | `install`, `update`, `update-all`, `plan` | Address passed to `mt --bind`. Use `127.0.0.1` for local-only access. |
| `-RootDir` | `string` | `%ProgramData%\MidTerm\instances` | all | Root for instance settings and manifests. |
| `-InstallRoot` | `string` | `%ProgramFiles%\MidTerm\instances` | install/update/remove | Root for instance binaries. |
| `-VersionTag` | `string` | `latest` | install/update/update-all | GitHub release tag, for example `v9.18.0-dev`. |
| `-AssetPath` | `string` | empty | install/update/update-all | Local zip asset. Overrides GitHub download. |
| `-PasswordHash` | `string` | empty | install | Precomputed PBKDF2 password hash. Preferred for automated agents. |
| `-Password` | `string` | empty | install | Plain password used to compute the hash. Avoid in shared shell history. |
| `-Force` | switch | false | install/remove | Replaces an existing service on install; deletes binaries and settings on remove. |

`-Mode plan` and `-Mode list` do not require elevation. All mutating modes do.

## macOS and Linux command reference

Run as root with `sudo`.

```bash
sudo ./install-multi.sh --mode plan --names alice,bob --base-port 2010
sudo ./install-multi.sh --names alice,bob --ports 2010,2011
sudo ./install-multi.sh --mode update --names alice
sudo ./install-multi.sh --mode update-all
sudo ./install-multi.sh --mode list
sudo ./install-multi.sh --mode remove --names alice
```

### macOS/Linux options

| Option | Value | Default | Applies to | Meaning |
| --- | --- | --- | --- | --- |
| `--mode` | `install`, `plan`, `list`, `update`, `update-all`, `remove` | `install` | all | Selects the operation. |
| `--names` | `a,b,c` | empty | all except `update-all` / `list` | Comma-separated instance names. |
| `--count` | `N` | `0` | `install`, `plan` | Generates `user1..userN` when `--names` is empty. If both are empty, one instance is planned or installed. |
| `--base-port` | `N` | `2000` | `install`, `plan` | First candidate port when `--ports` is not provided. Used ports are skipped when `ss` or `lsof` is available. |
| `--ports` | `p1,p2,p3` | empty | `install`, `plan`, `update`, `remove` | Explicit ports. Must contain exactly one port per resolved instance name. |
| `--bind` | address | `0.0.0.0` | `install`, `update`, `update-all`, `plan` | Address passed to `mt --bind`. Use `127.0.0.1` for local-only access. |
| `--root-dir` | path | `/usr/local/etc/midterm-instances` | all | Root for instance settings and manifests. |
| `--install-root` | path | `/usr/local/lib/midterm/instances` | install/update/remove | Root for instance binaries. |
| `--version-tag` | tag | `latest` | install/update/update-all | GitHub release tag, for example `v9.18.0-dev`. |
| `--asset-path` | path | empty | install/update/update-all | Local tar.gz asset. Overrides GitHub download. |
| `--password-hash` | hash | empty | install | Precomputed PBKDF2 password hash. Preferred for automated agents. |
| `--password` | password | empty | install | Plain password used to compute the hash. Avoid in shared shell history. |
| `--force` | flag | false | install/remove | Replaces an existing service on install; deletes binaries and settings on remove. |
| `--help` | flag | false | none | Prints built-in usage. |

`--mode plan` and `--mode list` do not require root. All mutating modes do.

## Runtime flags and environment variables

The installers start `mt` with explicit runtime flags. These flags are also
valid for manual diagnostics.

| Runtime flag | Environment variable | Meaning |
| --- | --- | --- |
| `--port <port>` | `MIDTERM_PORT` | HTTPS listener port. |
| `--bind <address>` | `MIDTERM_BIND` | Listener bind address. |
| `--settings-dir <path>` | `MIDTERM_SETTINGS_DIR` | Settings, secrets, certs, logs, sessions, update state. |
| `--service-mode` | `MIDTERM_SERVICE_MODE=true` | Force service-mode paths and secret storage behavior. |
| `--user-mode` | `MIDTERM_SERVICE_MODE=false` | Force user-mode behavior. Do not use for installed multi-instance services. |
| `--service-name <name>` | `MIDTERM_SERVICE_NAME` | Windows service identity used by updates and restarts. |
| `--launchd-label <label>` | `MIDTERM_LAUNCHD_LABEL` | macOS launchd identity used by updates and restarts. |
| `--systemd-service <name>` | `MIDTERM_SYSTEMD_SERVICE` | Linux systemd unit used by updates and restarts. |

For multi-instance services, always pass `--settings-dir` and `--service-mode`.
Do not rely on default settings paths, because defaults resolve to the normal
single-instance service scope.

## Install procedure for AI agents

Use this sequence for a new multi-instance deployment.

1. Identify the OS and architecture.
2. Choose instance names. Prefer stable human-readable names such as `alice`,
   `bob`, or team/user IDs.
3. Choose port policy. Either provide exact ports or choose a base port above
   the normal service port, for example `2010`.
4. Decide the bind address. Prefer `127.0.0.1` unless the instances must be
   reachable from other machines.
5. Run `plan` mode and inspect service names, ports, install directories, and
   settings directories.
6. Confirm the plan does not overlap with the normal tlbx installation or
   another instance.
7. Install with a password hash when automating. Use a prompted password for
   manual operations.
8. Verify each service is running.
9. Verify each endpoint with `https://localhost:<port>/api/version`.
10. Record the instance names, ports, roots, release tag, and bind address in
    the deployment notes.

Example: Windows, three users, fixed release, local-only access:

```powershell
.\install-multi.ps1 `
  -Mode plan `
  -Names alice,bob,charlie `
  -Ports 2010,2011,2012 `
  -BindAddress 127.0.0.1 `
  -VersionTag v9.18.0-dev

.\install-multi.ps1 `
  -Names alice,bob,charlie `
  -Ports 2010,2011,2012 `
  -BindAddress 127.0.0.1 `
  -VersionTag v9.18.0-dev `
  -PasswordHash '$PBKDF2$...'
```

Example: macOS/Linux, generated names and generated ports:

```bash
sudo ./install-multi.sh \
  --mode plan \
  --count 3 \
  --base-port 2010 \
  --bind 127.0.0.1 \
  --version-tag v9.18.0-dev

sudo ./install-multi.sh \
  --count 3 \
  --base-port 2010 \
  --bind 127.0.0.1 \
  --version-tag v9.18.0-dev \
  --password-hash '$PBKDF2$...'
```

## Offline or air-gapped install

Download the release asset separately, copy it to the target machine, then pass
the local asset path.

Windows:

```powershell
.\install-multi.ps1 `
  -Names alice,bob `
  -Ports 2010,2011 `
  -AssetPath C:\Temp\mt-win-x64.zip `
  -PasswordHash '$PBKDF2$...'
```

macOS:

```bash
sudo ./install-multi.sh \
  --names alice,bob \
  --ports 2010,2011 \
  --asset-path /tmp/mt-osx-arm64.tar.gz \
  --password-hash '$PBKDF2$...'
```

Linux:

```bash
sudo ./install-multi.sh \
  --names alice,bob \
  --ports 2010,2011 \
  --asset-path /tmp/mt-linux-x64.tar.gz \
  --password-hash '$PBKDF2$...'
```

Use the asset that matches the target OS and architecture. Do not unpack the
asset manually into a shared directory.

## Update model

Each instance owns its own binary directory, so updates are independent.

Update one instance:

```powershell
.\install-multi.ps1 -Mode update -Names alice -VersionTag v9.18.1-dev
```

```bash
sudo ./install-multi.sh --mode update --names alice --version-tag v9.18.1-dev
```

Update every discovered instance under the configured root:

```powershell
.\install-multi.ps1 -Mode update-all -VersionTag v9.18.1-dev
```

```bash
sudo ./install-multi.sh --mode update-all --version-tag v9.18.1-dev
```

Update behavior:

- binaries are copied only into the target instance install directory
- settings, secrets, certificates, sessions, logs, and `instance.json` stay in
  the target instance settings directory
- Windows updates stop and restart only the target Windows service
- Linux updates stop and restart only the target systemd unit
- macOS service operation uses the instance launchd label
- in-app update and restart flows use the service identity passed at runtime

Always update one low-risk instance first when operating a shared host. Verify
that instance before running `update-all`.

## Removal model

Remove unregisters the autostart service. Without force, it leaves binaries and
settings in place so the instance can be inspected or restored.

Windows:

```powershell
.\install-multi.ps1 -Mode remove -Names alice
.\install-multi.ps1 -Mode remove -Names alice -Force
```

macOS/Linux:

```bash
sudo ./install-multi.sh --mode remove --names alice
sudo ./install-multi.sh --mode remove --names alice --force
```

Use force only when you intentionally want to delete both:

- the instance install directory
- the instance settings directory

Back up the settings directory before forced removal if user data matters.

## Security precautions

Bind address:

- `0.0.0.0` exposes the instance on all network interfaces.
- `127.0.0.1` exposes the instance only to local clients.
- If using `0.0.0.0`, confirm firewall policy, password setup, certificate
  trust, and who can reach the machine.

Passwords:

- Prefer `-PasswordHash` / `--password-hash` for automation.
- Prefer prompted password entry for manual installs.
- Avoid `-Password` / `--password` on shared systems because shell history and
  process inspection may expose it.
- Use different passwords for different people unless a shared password is
  explicitly intended.

Secrets:

- Installed multi-instance services run in service mode.
- Windows service-mode secrets use machine/service-appropriate storage under
  the instance settings scope.
- macOS/Linux service-mode secrets are file-backed under the instance settings
  scope.
- Do not copy a settings directory between users unless you intend to copy the
  authentication state, secrets, sessions, and certificates too.

Networking:

- Every HTTPS port must be unique on the host.
- Plan mode skips currently listening ports when ports are generated, but it
  cannot guarantee another process will not claim the port later.
- Explicit ports are not auto-corrected. If you specify a busy port, install or
  service start can fail.

Process isolation:

- Do not terminate every `mt`, `mthost`, or `mtagenthost` process globally on a
  multi-instance host.
- Restart through the instance service identity.
- Treat child host processes as owned by the instance that spawned them.

File system:

- Do not place two instances in the same install directory.
- Do not place two instances in the same settings directory.
- Do not point multi-instance roots at the normal single-instance roots.
- Ensure service accounts can read and execute the install directory and read
  and write the settings directory.

## Service management commands

Windows:

```powershell
Get-Service MidTerm-alice
Restart-Service MidTerm-alice
Stop-Service MidTerm-alice
Start-Service MidTerm-alice
```

Linux:

```bash
systemctl status midterm-alice
sudo systemctl restart midterm-alice
sudo journalctl -u midterm-alice -n 200 --no-pager
```

macOS:

```bash
sudo launchctl print system/ai.tlbx.midterm.alice
sudo launchctl kickstart -k system/ai.tlbx.midterm.alice
sudo log show --predicate 'process == "mt"' --last 10m
```

## Verification checklist

For each instance:

1. Service exists with the expected identity.
2. Service is running after install or update.
3. `instance.json` exists under the expected settings directory.
4. `settings.json` exists under the expected settings directory.
5. The endpoint responds: `https://localhost:<port>/api/version`.
6. Login works with the configured password.
7. Creating a session in one instance does not appear in another instance.
8. Updating one instance does not change the binary timestamp or version of
   another instance.
9. Stopping one service does not stop another service.
10. The normal `MidTerm` service on port `2000` is unchanged unless it was
    intentionally part of the plan.

PowerShell endpoint check:

```powershell
Invoke-RestMethod -SkipCertificateCheck https://localhost:2010/api/version
```

Bash endpoint check:

```bash
curl -k https://localhost:2010/api/version
```

## Troubleshooting

### Plan chooses an unexpected port

The generated-port path starts at the base port and skips ports that appear to
be listening. Either accept the proposed port or pass exact ports with
`-Ports` / `--ports`.

### Service fails immediately

Check the service logs first. Common causes are a busy port, wrong file
permissions, a missing executable bit on Unix, a corrupted asset, or an
incorrect settings directory.

### GitHub asset is not found

Confirm the selected release tag and asset list. The release must contain the
platform-specific asset name listed in this document. Use `AssetPath` /
`--asset-path` only with a matching local asset.

### Login fails after install

Verify that the password hash was written into the same settings directory used
by the service. The service command line must contain `--settings-dir` and
`--service-mode`.

### Updating one instance restarts the wrong service

Inspect the service command line. The runtime must receive the correct
`--service-name`, `--launchd-label`, or `--systemd-service` for that instance.
If the service identity is missing, in-app update and restart flows fall back to
the normal service identity.

### Sessions or hosts appear to cross between users

Stop and inspect immediately. Confirm every instance has a unique settings
directory, unique service identity, and unique port. Do not use global process
kills while investigating.

### 32-bit macOS or Linux is requested

This is unsupported by the current release assets. Use a supported x64 or arm64
host, or create a separate release pipeline before attempting install.

## Regression guard for the normal installer

The multi-instance installer is intentionally separate from the normal
installer. Do not change these normal-install invariants while working on
multi-instance behavior:

- default Windows service name remains `MidTerm`
- default macOS launchd label remains `ai.tlbx.midterm`
- default Linux service name remains `MidTerm`
- default port remains `2000`
- default service settings paths remain the normal installer paths
- `install.ps1` and `install.sh` keep their single-instance behavior

When changing multi-instance support, verify at least one plan-mode command and
review changes for accidental edits to the normal installer path.

## Agent implementation notes

Agents modifying this area should inspect these files before changing behavior:

- `install-multi.ps1`
- `install-multi.sh`
- `src/Ai.Tlbx.MidTerm/Startup/ArgumentParser.cs`
- `src/Ai.Tlbx.MidTerm/Startup/MidTermRuntimeOptions.cs`
- `src/Ai.Tlbx.MidTerm/Startup/MidTermServiceIdentity.cs`
- `src/Ai.Tlbx.MidTerm/Settings/SettingsService.cs`
- `src/Ai.Tlbx.MidTerm/Services/Updates/UpdateScriptGenerator.cs`

Minimum safe validation for documentation-only changes:

```powershell
pwsh -NoProfile -File .\install-multi.ps1 -Mode plan -Names alice,bob -BasePort 2010
```

```bash
bash ./install-multi.sh --mode plan --names alice,bob --base-port 2010
```

Minimum safe validation for installer behavior changes:

- run both plan-mode commands above
- run platform-specific install/update/remove smoke tests where available
- verify `/api/version` for each installed instance
- verify that the normal single-instance installer behavior was not changed
