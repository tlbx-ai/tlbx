# Multi-instance install

MidTerm can run multiple isolated service instances on one machine without
Docker or VMs. Each instance must have its own:

- settings directory and secret store
- install directory
- service identity
- HTTPS port
- owned `mthost` / `mtagenthost` child process scope

The normal `install.ps1` / `install.sh` path is unchanged and still installs the
single default `MidTerm` instance on port `2000`.

## Install

Windows:

```powershell
.\install-multi.ps1 -Count 3 -BasePort 2010
.\install-multi.ps1 -Names alice,bob -Ports 2010,2011
```

macOS/Linux:

```bash
sudo ./install-multi.sh --count 3 --base-port 2010
sudo ./install-multi.sh --names alice,bob --ports 2010,2011
```

Use `plan` mode first to review service names, ports, and directories without
installing anything:

```powershell
.\install-multi.ps1 -Mode plan -Names alice,bob -BasePort 2010
./install-multi.sh --mode plan --names alice,bob --base-port 2010
```

## Update model

Every instance has its own binary directory, so updates are per instance:

```powershell
.\install-multi.ps1 -Mode update -Names alice
.\install-multi.ps1 -Mode update-all
```

```bash
sudo ./install-multi.sh --mode update --names alice
sudo ./install-multi.sh --mode update-all
```

The in-app updater is also instance-aware. Runtime flags set the active service
identity:

- Windows: `--service-name MidTerm-alice`
- macOS: `--launchd-label ai.tlbx.midterm.alice`
- Linux: `--systemd-service midterm-alice`

Generated update scripts stop and restart only that instance's service and copy
only that instance's binaries. Settings, secrets, certificates, logs, sessions,
and update result files stay under that instance's settings directory.

## Runtime isolation

The `mt` runtime accepts:

```text
--port <port>
--bind <address>
--settings-dir <path>
--service-mode
--service-name <windows-service-name>
--launchd-label <macos-launchd-label>
--systemd-service <linux-systemd-unit>
```

`--settings-dir` changes the install scope. `--service-mode` keeps service-mode
paths and secret behavior even when a custom settings directory is used.

MidTerm already namespaces `mthost` and `mtagenthost` ownership by instance
identity. With separate settings directories and ports, parallel `mt` processes
do not enumerate or terminate each other's child hosts.

## Platform and architecture assets

Dev releases now publish these installer assets:

- `mt-win-x64.zip`
- `mt-win-x86.zip`
- `mt-osx-arm64.tar.gz`
- `mt-osx-x64.tar.gz`
- `mt-linux-arm64.tar.gz`
- `mt-linux-x64.tar.gz`

The installers select the matching asset automatically. Windows ARM64 currently
uses the Windows x64 asset because Windows supports x64 emulation. 32-bit macOS
and 32-bit Linux are intentionally rejected with a clear error because this
.NET 10 self-contained release pipeline does not produce viable assets for
those OS/architecture combinations.

## Remove

By default, remove unregisters the autostart service and leaves instance data in
place. Add `-Force` / `--force` to delete binaries and settings too.

```powershell
.\install-multi.ps1 -Mode remove -Names alice
.\install-multi.ps1 -Mode remove -Names alice -Force
```

```bash
sudo ./install-multi.sh --mode remove --names alice
sudo ./install-multi.sh --mode remove --names alice --force
```
