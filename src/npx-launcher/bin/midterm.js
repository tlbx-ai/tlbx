#!/usr/bin/env node

'use strict';

const fs = require('node:fs');
const fsp = require('node:fs/promises');
const https = require('node:https');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const { version: PACKAGE_VERSION } = require('../package.json');
const DEFAULT_PORT = 2000;
const MAX_PORT_SCAN_ATTEMPTS = 100;
const SERVER_READY_TIMEOUT_MS = 15000;
const SERVER_READY_INTERVAL_MS = 500;
const REPO_OWNER = 'tlbx-ai';
const REPO_NAME = 'MidTerm';
const GITHUB_API = `https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}`;

async function main() {
  const { launcher, passthrough } = parseArgs(process.argv.slice(2));

  if (launcher.help) {
    printHelp();
    return;
  }

  const runtime = await detectRuntime();
  const target = await getPlatformTarget(runtime);
  const release = await resolveRelease(launcher.channel);
  const install = runtime.kind === 'wsl-interop'
    ? await ensureInstalledReleaseInWsl(release, target, runtime)
    : await ensureInstalledRelease(release, target);

  const childArgs = passthrough.slice();
  const hasExplicitPort = hasArg(childArgs, '--port');
  const explicitBind = getArgValue(childArgs, '--bind');
  const explicitPort = parsePortArg(getArgValue(childArgs, '--port'));
  const effectiveBind = explicitBind ?? '127.0.0.1';
  const startsServer = shouldStartServer(childArgs);

  let effectivePort = explicitPort ?? DEFAULT_PORT;

  if (!explicitBind) {
    childArgs.push('--bind', '127.0.0.1');
  }

  if (startsServer && !hasExplicitPort) {
    effectivePort = await findAvailablePort(effectiveBind, DEFAULT_PORT);
    childArgs.push('--port', String(effectivePort));

    if (effectivePort !== DEFAULT_PORT) {
      console.error(`@tlbx-ai/midterm: port ${DEFAULT_PORT} is unavailable, using ${effectivePort}`);
    }
  }

  const browserUrl = startsServer
    ? buildBrowserUrl(effectiveBind, effectivePort)
    : null;
  const childEnv = {
    ...process.env,
    MIDTERM_LAUNCH_MODE: 'npx',
    MIDTERM_NPX: '1',
    MIDTERM_NPX_CHANNEL: launcher.channel,
    MIDTERM_NPX_PACKAGE_VERSION: PACKAGE_VERSION,
    MIDTERM_NPX_RUNTIME: runtime.kind
  };

  const child = runtime.kind === 'wsl-interop'
    ? spawnMidTermInWsl(runtime, install.mtPath, childArgs, childEnv)
    : spawn(install.mtPath, childArgs, {
        stdio: 'inherit',
        env: childEnv
      });

  if (launcher.openBrowser && browserUrl) {
    void openBrowserWhenReady(browserUrl);
  }

  forwardSignal(child, 'SIGINT');
  forwardSignal(child, 'SIGTERM');
  forwardSignal(child, 'SIGHUP');

  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    process.exit(code ?? 0);
  });
}

function parseArgs(args) {
  const launcher = {
    help: false,
    channel: 'stable',
    openBrowser: true
  };
  const passthrough = [];

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];

    if (arg === '--') {
      passthrough.push(...args.slice(i + 1));
      break;
    }

    if (arg === '--help-launcher') {
      launcher.help = true;
      continue;
    }

    if (arg === '--no-browser') {
      launcher.openBrowser = false;
      continue;
    }

    if (arg === '--channel') {
      const value = args[i + 1];
      if (value !== 'stable' && value !== 'dev') {
        throw new Error('--channel must be stable or dev');
      }
      launcher.channel = value;
      i++;
      continue;
    }

    passthrough.push(arg);
  }

  return { launcher, passthrough };
}

function printHelp() {
  console.log('@tlbx-ai/midterm launcher');
  console.log('');
  console.log('Usage: npx @tlbx-ai/midterm [--channel stable|dev] [-- <mt args...>]');
  console.log('');
  console.log('Launcher options:');
  console.log('  --channel stable|dev  Choose the release channel (default: stable)');
  console.log('  --no-browser          Do not auto-open MidTerm in the default browser');
  console.log('  --help-launcher       Show launcher help');
  console.log('');
  console.log('All other arguments are passed to mt.');
}

async function detectRuntime() {
  const wslContext = await detectWslInteropContext();
  if (wslContext) {
    return wslContext;
  }

  return {
    kind: 'native',
    platform: process.platform,
    arch: process.arch
  };
}

async function detectWslInteropContext() {
  if (process.platform !== 'win32') {
    return null;
  }

  const parsed = findWslInteropPath();
  if (!parsed) {
    return null;
  }

  const linuxHome = getWslCommandOutput(parsed.distroName, ['pwd'], '~');
  if (!linuxHome.startsWith('/')) {
    throw new Error(`Failed to determine WSL home directory for ${parsed.distroName}`);
  }

  const archRaw = getWslCommandOutput(parsed.distroName, ['uname', '-m'], '/');

  return {
    kind: 'wsl-interop',
    distroName: parsed.distroName,
    linuxCwd: parsed.linuxPath,
    linuxHome,
    uncRoot: parsed.uncRoot,
    arch: normalizeWslArchitecture(archRaw)
  };
}

function findWslInteropPath() {
  const candidates = [
    process.cwd(),
    process.env.INIT_CWD,
    process.env.npm_config_local_prefix,
    getPackageDirectory(process.env.npm_package_json)
  ];

  for (const candidate of candidates) {
    const parsed = parseWslUncPath(candidate);
    if (parsed) {
      return parsed;
    }
  }

  return null;
}

function getPackageDirectory(packageJsonPath) {
  if (!packageJsonPath) {
    return '';
  }

  return path.win32.dirname(packageJsonPath);
}

function parseWslUncPath(value) {
  const normalized = String(value || '');
  const match = normalized.match(/^\\\\wsl(?:\.localhost|\$)\\([^\\]+)(\\.*)?$/i);
  if (!match) {
    return null;
  }

  const distroName = match[1];
  const suffix = match[2] || '';
  const linuxPath = suffix
    ? suffix.replace(/\\/g, '/')
    : '/';

  return {
    distroName,
    linuxPath,
    uncRoot: `\\\\wsl.localhost\\${distroName}`
  };
}

async function getPlatformTarget(runtime) {
  if (runtime.kind === 'wsl-interop') {
    if (runtime.arch === 'x64') {
      return {
        assetName: 'mt-linux-x64.tar.gz',
        binaryName: 'mt',
        hostBinaryName: 'mthost'
      };
    }

    if (runtime.arch === 'arm64') {
      return {
        assetName: 'mt-linux-arm64.tar.gz',
        binaryName: 'mt',
        hostBinaryName: 'mthost'
      };
    }

    throw new Error(`Unsupported WSL platform: linux ${runtime.arch}`);
  }

  if (process.platform === 'win32' && process.arch === 'x64') {
    return {
      assetName: 'mt-win-x64.zip',
      binaryName: 'mt.exe',
      hostBinaryName: 'mthost.exe'
    };
  }

  if (process.platform === 'win32' && process.arch === 'ia32') {
    return {
      assetName: 'mt-win-x86.zip',
      binaryName: 'mt.exe',
      hostBinaryName: 'mthost.exe'
    };
  }

  if (process.platform === 'darwin' && process.arch === 'arm64') {
    return {
      assetName: 'mt-osx-arm64.tar.gz',
      binaryName: 'mt',
      hostBinaryName: 'mthost'
    };
  }

  if (process.platform === 'darwin' && process.arch === 'x64') {
    return {
      assetName: 'mt-osx-x64.tar.gz',
      binaryName: 'mt',
      hostBinaryName: 'mthost'
    };
  }

  if (process.platform === 'linux' && process.arch === 'x64') {
    return {
      assetName: 'mt-linux-x64.tar.gz',
      binaryName: 'mt',
      hostBinaryName: 'mthost'
    };
  }

  if (process.platform === 'linux' && process.arch === 'arm64') {
    return {
      assetName: 'mt-linux-arm64.tar.gz',
      binaryName: 'mt',
      hostBinaryName: 'mthost'
    };
  }

  throw new Error(`Unsupported platform: ${process.platform} ${process.arch}`);
}

async function resolveRelease(channel) {
  const headers = {
    'User-Agent': '@tlbx-ai/midterm',
    'Accept': 'application/vnd.github+json'
  };

  if (channel === 'stable') {
    const release = await fetchJson(`${GITHUB_API}/releases/latest`, headers);
    return mapRelease(release);
  }

  const releases = await fetchJson(`${GITHUB_API}/releases?per_page=50`, headers);
  const prereleases = Array.isArray(releases) ? releases.filter((release) => release.prerelease) : [];
  if (prereleases.length === 0) {
    throw new Error('No dev releases found on GitHub');
  }

  prereleases.sort((left, right) => compareVersions(right.tag_name, left.tag_name));
  return mapRelease(prereleases[0]);
}

function mapRelease(release) {
  if (!release || !release.tag_name || !Array.isArray(release.assets)) {
    throw new Error('Unexpected GitHub release payload');
  }

  return {
    tag: release.tag_name,
    assets: release.assets
  };
}

async function ensureInstalledRelease(release, target) {
  const cacheRoot = getCacheRoot();
  const versionDir = path.join(cacheRoot, sanitizeTag(release.tag));
  const completeMarker = path.join(versionDir, '.complete');
  const targetAsset = release.assets.find((asset) => asset.name === target.assetName);

  if (!targetAsset || !targetAsset.browser_download_url) {
    throw new Error(`Release ${release.tag} does not contain ${target.assetName}`);
  }

  const mtPath = path.join(versionDir, target.binaryName);
  const mthostPath = path.join(versionDir, target.hostBinaryName);

  if (fs.existsSync(completeMarker)) {
    ensureInstalledFilesExist(mtPath, mthostPath, target);
    return { mtPath, mthostPath };
  }

  await fsp.mkdir(cacheRoot, { recursive: true });

  const tempRoot = await fsp.mkdtemp(path.join(cacheRoot, 'staging-'));
  const archivePath = path.join(tempRoot, target.assetName);
  const extractDir = path.join(tempRoot, 'extract');

  try {
    await fsp.mkdir(extractDir, { recursive: true });
    console.error(`MidTerm ${release.tag}: downloading ${target.assetName}`);
    await downloadFile(targetAsset.browser_download_url, archivePath);
    console.error(`MidTerm ${release.tag}: extracting`);
    extractArchive(archivePath, extractDir);
    await ensureExecutableBits(extractDir, target);
    await fsp.rm(versionDir, { recursive: true, force: true });
    await fsp.rename(extractDir, versionDir);
    await fsp.writeFile(completeMarker, `${release.tag}\n`, 'utf8');
    ensureInstalledFilesExist(mtPath, mthostPath, target);
    return { mtPath, mthostPath };
  } catch (error) {
    await fsp.rm(versionDir, { recursive: true, force: true }).catch(() => {});
    throw error;
  } finally {
    await fsp.rm(tempRoot, { recursive: true, force: true }).catch(() => {});
  }
}

async function ensureInstalledReleaseInWsl(release, target, runtime) {
  const cacheRootLinux = path.posix.join(runtime.linuxHome, '.cache', 'midterm', 'npx-cache');
  const cacheRootUnc = toWslUncPath(runtime, cacheRootLinux);
  const versionDirLinux = path.posix.join(cacheRootLinux, sanitizeTag(release.tag));
  const versionDirUnc = toWslUncPath(runtime, versionDirLinux);
  const completeMarkerUnc = toWslUncPath(runtime, path.posix.join(versionDirLinux, '.complete'));
  const targetAsset = release.assets.find((asset) => asset.name === target.assetName);

  if (!targetAsset || !targetAsset.browser_download_url) {
    throw new Error(`Release ${release.tag} does not contain ${target.assetName}`);
  }

  const mtPath = path.posix.join(versionDirLinux, target.binaryName);
  const mthostPath = path.posix.join(versionDirLinux, target.hostBinaryName);
  const mtPathUnc = toWslUncPath(runtime, mtPath);
  const mthostPathUnc = toWslUncPath(runtime, mthostPath);

  if (fs.existsSync(completeMarkerUnc)) {
    ensureInstalledFilesExist(mtPathUnc, mthostPathUnc, target);
    return { mtPath, mthostPath };
  }

  await fsp.mkdir(cacheRootUnc, { recursive: true });

  const tempName = `staging-${Date.now()}-${process.pid}-${Math.random().toString(16).slice(2)}`;
  const tempRootLinux = path.posix.join(cacheRootLinux, tempName);
  const tempRootUnc = toWslUncPath(runtime, tempRootLinux);
  const archiveLinux = path.posix.join(tempRootLinux, target.assetName);
  const archiveUnc = toWslUncPath(runtime, archiveLinux);
  const extractLinux = path.posix.join(tempRootLinux, 'extract');
  const extractUnc = toWslUncPath(runtime, extractLinux);

  try {
    await fsp.mkdir(extractUnc, { recursive: true });
    console.error(`MidTerm ${release.tag}: downloading ${target.assetName}`);
    await downloadFile(targetAsset.browser_download_url, archiveUnc);
    console.error(`MidTerm ${release.tag}: extracting`);
    runWslCommand(runtime, ['tar', '-xzf', archiveLinux, '-C', extractLinux], '/');
    runWslCommand(runtime, ['chmod', '755', path.posix.join(extractLinux, target.binaryName), path.posix.join(extractLinux, target.hostBinaryName)], '/');
    await fsp.rm(versionDirUnc, { recursive: true, force: true });
    await fsp.rename(extractUnc, versionDirUnc);
    await fsp.writeFile(completeMarkerUnc, `${release.tag}\n`, 'utf8');
    ensureInstalledFilesExist(mtPathUnc, mthostPathUnc, target);
    return { mtPath, mthostPath };
  } catch (error) {
    await fsp.rm(versionDirUnc, { recursive: true, force: true }).catch(() => {});
    throw error;
  } finally {
    await fsp.rm(tempRootUnc, { recursive: true, force: true }).catch(() => {});
  }
}

function getCacheRoot() {
  if (process.platform === 'win32') {
    const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
    return path.join(localAppData, 'MidTerm', 'npx-cache');
  }

  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Caches', 'MidTerm', 'npx-cache');
  }

  const xdgCache = process.env.XDG_CACHE_HOME || path.join(os.homedir(), '.cache');
  return path.join(xdgCache, 'midterm', 'npx-cache');
}

function sanitizeTag(tag) {
  return String(tag).replace(/^v/, '');
}

async function fetchJson(url, headers) {
  const response = await fetch(url, { headers });
  if (!response.ok) {
    throw new Error(`GitHub API request failed: ${response.status} ${response.statusText}`);
  }

  return response.json();
}

async function downloadFile(url, filePath) {
  const response = await fetch(url, {
    headers: {
      'User-Agent': '@tlbx-ai/midterm'
    }
  });

  if (!response.ok) {
    throw new Error(`Download failed: ${response.status} ${response.statusText}`);
  }

  const arrayBuffer = await response.arrayBuffer();
  await fsp.writeFile(filePath, Buffer.from(arrayBuffer));
}

function extractArchive(archivePath, destinationPath) {
  if (archivePath.endsWith('.zip')) {
    const command = [
      '-NoProfile',
      '-Command',
      `Expand-Archive -LiteralPath '${escapePowerShell(archivePath)}' -DestinationPath '${escapePowerShell(destinationPath)}' -Force`
    ];
    const result = spawnSync('powershell', command, {
      stdio: 'inherit',
      cwd: getWindowsSubprocessCwd()
    });
    if (result.status !== 0) {
      throw new Error(`Failed to extract ${path.basename(archivePath)} with PowerShell`);
    }
    return;
  }

  const result = spawnSync('tar', ['-xzf', archivePath, '-C', destinationPath], {
    stdio: 'inherit',
    cwd: getWindowsSubprocessCwd()
  });
  if (result.status !== 0) {
    throw new Error(`Failed to extract ${path.basename(archivePath)} with tar`);
  }
}

async function ensureExecutableBits(installDir, target) {
  if (process.platform === 'win32') {
    return;
  }

  await Promise.all([
    fsp.chmod(path.join(installDir, target.binaryName), 0o755),
    fsp.chmod(path.join(installDir, target.hostBinaryName), 0o755)
  ]);
}

function hasArg(args, name) {
  return args.some((arg) => arg === name || arg.startsWith(`${name}=`));
}

function getArgValue(args, name) {
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === name) {
      return args[i + 1];
    }
    if (arg.startsWith(`${name}=`)) {
      return arg.slice(name.length + 1);
    }
  }

  return undefined;
}

function parsePortArg(value) {
  if (!value) {
    return undefined;
  }

  const parsed = Number.parseInt(value, 10);
  if (!Number.isInteger(parsed) || parsed < 1 || parsed > 65535) {
    return undefined;
  }

  return parsed;
}

function shouldStartServer(args) {
  const nonServerFlags = [
    '--check-update',
    '--update',
    '--apply-update',
    '--version',
    '-v',
    '--help',
    '-h',
    '--hash-password',
    '--write-secret',
    '--generate-cert'
  ];

  return !nonServerFlags.some((flag) => hasArg(args, flag));
}

async function findAvailablePort(bindAddress, preferredPort) {
  for (let offset = 0; offset < MAX_PORT_SCAN_ATTEMPTS; offset++) {
    const port = preferredPort + offset;
    if (port > 65535) {
      break;
    }

    if (await isPortAvailable(bindAddress, port)) {
      return port;
    }
  }

  throw new Error(`Could not find a free port starting at ${preferredPort}`);
}

function isPortAvailable(bindAddress, port) {
  const host = normalizeBindForNetProbe(bindAddress);

  return new Promise((resolve) => {
    const server = net.createServer();

    server.once('error', (error) => {
      if (error && (error.code === 'EADDRINUSE' || error.code === 'EACCES')) {
        resolve(false);
        return;
      }

      resolve(false);
    });

    server.listen(port, host, () => {
      server.close(() => resolve(true));
    });
  });
}

function normalizeBindForNetProbe(bindAddress) {
  const raw = String(bindAddress || '').trim();
  if (!raw || raw === 'localhost') {
    return '127.0.0.1';
  }

  return raw.replace(/^\[(.*)\]$/, '$1');
}

function buildBrowserUrl(bindAddress, port) {
  const normalized = normalizeHostForBrowser(bindAddress);
  return `https://${normalized}:${port}`;
}

function normalizeHostForBrowser(bindAddress) {
  const raw = String(bindAddress || '').trim();
  if (!raw || raw === '0.0.0.0' || raw === '::' || raw === '[::]') {
    return '127.0.0.1';
  }

  const host = raw.replace(/^\[(.*)\]$/, '$1');
  if (host.includes(':')) {
    return `[${host}]`;
  }

  return host;
}

async function openBrowserWhenReady(url) {
  const ready = await waitForServer(url, SERVER_READY_TIMEOUT_MS);
  if (!ready) {
    console.error(`@tlbx-ai/midterm: server did not become ready within ${SERVER_READY_TIMEOUT_MS}ms, opening browser anyway`);
  }

  openUrl(url);
}

async function waitForServer(url, timeoutMs) {
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (await probeUrl(url)) {
      return true;
    }

    await sleep(SERVER_READY_INTERVAL_MS);
  }

  return false;
}

function probeUrl(url) {
  return new Promise((resolve) => {
    const request = https.request(url, {
      method: 'GET',
      rejectUnauthorized: false,
      timeout: SERVER_READY_INTERVAL_MS
    }, (response) => {
      response.resume();
      resolve(true);
    });

    request.on('error', () => resolve(false));
    request.on('timeout', () => {
      request.destroy();
      resolve(false);
    });
    request.end();
  });
}

function sleep(ms) {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

function openUrl(url) {
  let command;
  let args;

  if (process.platform === 'win32') {
    command = 'cmd';
    args = ['/c', 'start', '', url];
  } else if (process.platform === 'darwin') {
    command = 'open';
    args = [url];
  } else {
    command = 'xdg-open';
    args = [url];
  }

  const result = spawn(command, args, {
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
    cwd: getWindowsSubprocessCwd()
  });
  result.on('error', (error) => {
    console.error(`@tlbx-ai/midterm: failed to open browser automatically: ${error.message}`);
  });
  result.unref();
}

function spawnMidTermInWsl(runtime, mtPath, childArgs, childEnv) {
  const envArgs = ['env'];
  const passthroughEnv = [
    'MIDTERM_LAUNCH_MODE',
    'MIDTERM_NPX',
    'MIDTERM_NPX_CHANNEL',
    'MIDTERM_NPX_PACKAGE_VERSION',
    'MIDTERM_NPX_RUNTIME'
  ];

  for (const key of passthroughEnv) {
    if (childEnv[key]) {
      envArgs.push(`${key}=${childEnv[key]}`);
    }
  }

  envArgs.push(mtPath, ...childArgs);

  return spawn('wsl.exe', [
    '--distribution',
    runtime.distroName,
    '--cd',
    runtime.linuxCwd,
    '--exec',
    ...envArgs
  ], {
    stdio: 'inherit',
    cwd: getWindowsSubprocessCwd()
  });
}

function forwardSignal(child, signal) {
  process.on(signal, () => {
    if (!child.killed) {
      child.kill(signal);
    }
  });
}

function escapePowerShell(value) {
  return value.replace(/'/g, "''");
}

function ensureInstalledFilesExist(mtPath, mthostPath, target) {
  if (!fs.existsSync(mtPath) || !fs.existsSync(mthostPath)) {
    throw new Error(`Downloaded release is incomplete: expected ${target.binaryName} and ${target.hostBinaryName}`);
  }
}

function toWslUncPath(runtime, linuxPath) {
  const normalized = String(linuxPath || '/');
  const suffix = normalized === '/'
    ? ''
    : normalized.replace(/\//g, '\\');
  return `${runtime.uncRoot}${suffix}`;
}

function getWslCommandOutput(distroName, commandArgs, cwd) {
  const result = spawnSync('wsl.exe', [
    '--distribution',
    distroName,
    '--cd',
    cwd,
    '--exec',
    ...commandArgs
  ], {
    encoding: 'utf8',
    cwd: getWindowsSubprocessCwd()
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    const stderr = String(result.stderr || '').trim();
    throw new Error(`WSL command failed: ${stderr || commandArgs.join(' ')}`);
  }

  return String(result.stdout || '').trim();
}

function runWslCommand(runtime, commandArgs, cwd) {
  const result = spawnSync('wsl.exe', [
    '--distribution',
    runtime.distroName,
    '--cd',
    cwd,
    '--exec',
    ...commandArgs
  ], {
    stdio: 'inherit',
    cwd: getWindowsSubprocessCwd()
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`WSL command failed: ${commandArgs.join(' ')}`);
  }
}

function normalizeWslArchitecture(value) {
  const normalized = String(value || '').trim().toLowerCase();
  if (normalized === 'x86_64' || normalized === 'amd64') {
    return 'x64';
  }

  if (normalized === 'aarch64' || normalized === 'arm64') {
    return 'arm64';
  }

  return normalized || process.arch;
}

function getWindowsSubprocessCwd() {
  if (process.platform !== 'win32') {
    return undefined;
  }

  const cwd = process.cwd();
  if (/^\\\\/.test(cwd)) {
    return process.env.SystemRoot || 'C:\\Windows';
  }

  return undefined;
}

function compareVersions(leftTag, rightTag) {
  const left = parseVersion(leftTag);
  const right = parseVersion(rightTag);

  for (let i = 0; i < 3; i++) {
    if (left.base[i] !== right.base[i]) {
      return left.base[i] - right.base[i];
    }
  }

  if (left.prerelease === null && right.prerelease !== null) {
    return 1;
  }

  if (left.prerelease !== null && right.prerelease === null) {
    return -1;
  }

  if (left.prerelease === null && right.prerelease === null) {
    return 0;
  }

  return left.prerelease - right.prerelease;
}

function parseVersion(tag) {
  const clean = String(tag).replace(/^v/, '');
  const [basePart, prereleasePart] = clean.split('-', 2);
  const base = basePart.split('.').map((value) => Number.parseInt(value, 10) || 0);
  const prereleaseMatch = prereleasePart ? prereleasePart.match(/\.(\d+)$/) : null;

  return {
    base: [base[0] || 0, base[1] || 0, base[2] || 0],
    prerelease: prereleaseMatch ? Number.parseInt(prereleaseMatch[1], 10) : null
  };
}

main().catch((error) => {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`@tlbx-ai/midterm: ${message}`);
  process.exit(1);
});
