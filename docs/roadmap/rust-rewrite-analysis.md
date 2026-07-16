# tlbx C# to Rust Rewrite Analysis (2025 Edition)

## Executive Summary

This document analyzes the technical architecture of tlbx (a web-based terminal multiplexer running on **.NET 10 Native AOT**) and evaluates whether a Rust rewrite would provide meaningful benefits in 2025.

**Current Stack**: .NET 10 Native AOT (with extensive trimming/optimization), Kestrel Minimal APIs, ConPTY/forkpty P/Invoke, Named Pipes/Unix Sockets

---

## Detailed Feature Comparison Table (2025 Current State)

| # | Feature | Current .NET 10 Implementation | Rust 2025 Equivalent | Rust LOC | Key Dependencies | Rust Advantage | .NET Advantage | **Verdict** |
|---|---------|-------------------------------|----------------------|----------|------------------|----------------|----------------|-------------|
| **1** | **Web Server** | Kestrel Minimal APIs (~400 LOC) | `axum` 0.8 | ~350 LOC | `axum`, `tokio` | Slightly smaller binary | Better tooling, faster iteration | **KEEP** |
| **2** | **HTTPS/TLS** | Kestrel built-in TLS 1.2/1.3, cipher config (~80 LOC) | `rustls` 0.23 | ~60 LOC | `rustls`, `tokio-rustls` | No OpenSSL, pure Rust | Built-in, zero config | **KEEP** |
| **3** | **Certificate Generation** | System.Security.Cryptography ECDSA P384 (~200 LOC) | `rcgen` 0.13 | ~100 LOC | `rcgen`, `ring` | 50% less code | Battle-tested .NET crypto | **KEEP** |
| **4** | **Windows DPAPI** | ProtectedData.Protect (~100 LOC) | `windows` crate | ~100 LOC | `windows` | None | Same API, better docs | **KEEP** |
| **5** | **macOS Keychain** | Security.framework P/Invoke (~150 LOC) | `security-framework` 2.x | ~60 LOC | `security-framework` | 60% less code, safer | Existing code works | **TOSS-UP** |
| **6** | **Linux secret storage** | AES-256 + chmod libc (~120 LOC) | `aes-gcm` + `libc` | ~90 LOC | `aes-gcm`, `libc` | Slightly cleaner | Existing works | **KEEP** |
| **7** | **WebSocket Mux Handler** | Custom binary handler (~300 LOC) | `tokio-tungstenite` 0.24 | ~250 LOC | `tokio-tungstenite` | Zero-copy possible | Existing protocol tested | **KEEP** |
| **8** | **WebSocket State Handler** | JSON over WS (~150 LOC) | `axum` extractors | ~100 LOC | `serde` | Cleaner integration | Existing works | **KEEP** |
| **9** | **Binary Mux Protocol** | Custom + GZip (~250 LOC) | `bytes` + `flate2` | ~180 LOC | `bytes`, `flate2` | `bytes` zero-copy | Existing protocol tested | **TOSS-UP** |
| **10** | **REST API Endpoints** | Minimal APIs (~500 LOC) | `axum` handlers | ~400 LOC | `axum` | Slightly less code | IDE support, refactoring | **KEEP** |
| **11** | **JSON Serialization** | Source-gen JsonSerializerContext (~150 LOC boilerplate) | `serde` derive | ~20 LOC | `serde` | **90% less boilerplate** | None | **RUST WINS** |
| **12** | **PBKDF2 Auth** | Rfc2898DeriveBytes (~70 LOC) | `pbkdf2` crate | ~40 LOC | `pbkdf2`, `sha2` | Type-safe | Built-in | **KEEP** |
| **13** | **HMAC-SHA256 Tokens** | System.Security.Crypto (~50 LOC) | `hmac` + `sha2` | ~35 LOC | `hmac`, `sha2` | Similar | Built-in | **KEEP** |
| **14** | **Rate Limiting** | ConcurrentDictionary (~60 LOC) | `governor` | ~40 LOC | `governor` | Battle-tested crate | Existing works | **KEEP** |
| **15** | **ConPTY (Windows)** | kernel32 P/Invoke (~380 LOC) | `portable-pty` 0.9.0 | ~50 LOC | `portable-pty` | **87% less code, battle-tested** | None | **RUST WINS** |
| **16** | **Unix PTY (forkpty)** | libc P/Invoke (~340 LOC) | `portable-pty` 0.9.0 | ~50 LOC | `portable-pty` | **85% less code, safer** | None | **RUST WINS** |
| **17** | **PTY Exec (setsid/execvp)** | libc P/Invoke (~90 LOC) | `nix` crate | ~30 LOC | `nix` | Type-safe syscalls | None | **RUST WINS** |
| **18** | **Windows Named Pipes** | NamedPipeServerStream (~100 LOC) | `tokio` named pipes | ~70 LOC | `tokio` | Native async | Existing works | **TOSS-UP** |
| **19** | **Unix Domain Sockets** | Socket + UnixDomainSocketEndPoint (~100 LOC) | `tokio::net::UnixListener` | ~50 LOC | `tokio` | Native async, 50% less | None | **RUST WINS** |
| **20** | **IPC Protocol** | Custom binary length-prefix (~200 LOC) | `tokio-util` codec | ~120 LOC | `bytes`, `tokio-util` | Codec abstraction | None | **RUST WINS** |
| **21** | **Process Spawn (Win Service)** | CreateProcessAsUser P/Invoke (~200 LOC) | `windows` crate | ~150 LOC | `windows` | Safer handles | Existing works | **TOSS-UP** |
| **22** | **Process Spawn (Unix sudo)** | Process.Start (~60 LOC) | `std::process::Command` | ~40 LOC | none | Similar | Similar | **KEEP** |
| **23** | **Circular Buffer** | Custom ring buffer (~100 LOC) | `ringbuffer` crate | ~30 LOC | `ringbuffer` | 70% less | None | **RUST WINS** |
| **24** | **Session Management** | ConcurrentDictionary (~500 LOC) | `dashmap` + channels | ~350 LOC | `dashmap`, `tokio` | Lock-free | ConcurrentDict works | **TOSS-UP** |
| **25** | **Priority Buffering** | Channel + batching (~200 LOC) | `tokio::sync::mpsc` | ~150 LOC | `tokio` | Native async | Existing works | **TOSS-UP** |
| **26** | **Settings Service** | JSON + file watcher (~200 LOC) | `serde` + `notify` | ~120 LOC | `serde`, `notify` | 40% less | Existing works | **TOSS-UP** |
| **27** | **Update Service** | HttpClient + GitHub API (~300 LOC) | `reqwest` | ~180 LOC | `reqwest`, `serde` | 40% less | Existing works | **TOSS-UP** |
| **28** | **User Enum (Windows)** | WMI queries (~150 LOC) | `wmi` crate | ~100 LOC | `wmi` | Similar | Similar | **KEEP** |
| **29** | **User Enum (Unix)** | /etc/passwd + getuid (~100 LOC) | `users` crate | ~20 LOC | `users` | **80% less** | None | **RUST WINS** |
| **30** | **Windows Service** | UseWindowsService() (~20 LOC) | `windows-service` | ~80 LOC | `windows-service` | None | **75% less code** | **.NET WINS** |
| **31** | **Static File Serving** | UseStaticFiles() embedded (~30 LOC) | `tower-http` | ~40 LOC | `tower-http`, `include_dir` | None | Simpler | **KEEP** |
| **32** | **Logging** | Custom Logger (~200 LOC) | `tracing` | ~60 LOC | `tracing` | **Superior ecosystem** | None | **RUST WINS** |
| **33** | **Shell Detection** | PATH scanning (~100 LOC) | `which` crate | ~30 LOC | `which` | 70% less | None | **RUST WINS** |
| **34** | **Environment Block** | Marshal + null terminators (~80 LOC) | `std::env` | ~10 LOC | none | **88% less** | None | **RUST WINS** |
| **35** | **Signal Handling** | PosixSignalRegistration (~30 LOC) | `tokio::signal` | ~15 LOC | `tokio` | Native async | Existing works | **KEEP** |
| **36** | **Instance Guard** | Mutex/FileStream (~100 LOC) | `fs2` file locks | ~40 LOC | `fs2` | 60% less | None | **RUST WINS** |
| **37** | **Native Compilation** | PublishAot + 40 config lines | `cargo build --release` | 0 config | none | **Zero config, native** | None | **RUST WINS** |

---

## Summary Verdict (2025)

| Verdict | Count | % |
|---------|-------|---|
| **KEEP (.NET)** | 16 | 43% |
| **RUST WINS** | 14 | 38% |
| **TOSS-UP** | 6 | 16% |
| **.NET WINS** | 1 | 3% |

---

## Where Rust Has Decisive Advantage (2025)

### 1. PTY Handling - The `portable-pty` 0.9.0 Difference

The Rust `portable-pty` crate (from WezTerm, released Feb 2025) now provides:
- Cross-platform PTY abstraction (ConPTY + Unix PTY)
- Production-tested in WezTerm terminal emulator
- ~50 LOC vs ~720 LOC of C# P/Invoke code

```rust
// Rust 2025 - entire PTY creation
use portable_pty::{native_pty_system, PtySize};

let pty_system = native_pty_system();
let pair = pty_system.openpty(PtySize { rows: 24, cols: 80, .. })?;
let child = pair.slave.spawn_command(cmd)?;
```

vs. current C# (380 LOC Windows + 340 LOC Unix + 90 LOC exec helper = **810 LOC**)

### 2. JSON Serialization - Serde vs Source-Gen

Current C# requires:
```csharp
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(ResizeResponse))]
[JsonSerializable(typeof(AuthResponse))]
// ... 40+ more types explicitly listed
internal partial class AppJsonContext : JsonSerializerContext { }
```

Rust requires:
```rust
#[derive(Serialize, Deserialize)]
struct SessionInfo { ... }
// That's it. Works for all 40+ types.
```

### 3. Binary Size (Reality Check)

| | .NET 10 AOT | Rust |
|--|-------------|------|
| **Console app** | ~1-2 MB | ~500 KB |
| **Web app (Kestrel/Axum)** | ~9-10 MB | ~4-5 MB |
| **This app (tlbx)** | ~12-15 MB estimate | ~6-8 MB estimate |

The gap has **shrunk** from 3-4x to ~2x with .NET 10 optimizations.

### 4. Startup Time (2025 Reality)

| | .NET 10 AOT | Rust |
|--|-------------|------|
| **Cold start** | 10-14 ms | 2-5 ms |
| **Memory** | 30-50 MB | 15-25 MB |

Both are now "instant" for user perception. The difference is marginal for a web server.

---

## Where .NET 10 Retains Advantage (2025)

### 1. Windows Service Integration
```csharp
builder.Host.UseWindowsService();  // One line
```
vs. Rust's `windows-service` requiring ~80 LOC of boilerplate.

### 2. Developer Experience
- C# has superior IDE refactoring (Rider, VS)
- Faster compile times (~3s incremental vs ~8s Rust)
- Better debugging experience

### 3. Existing Investment
- ~8,500 LOC of working, tested code
- Existing release process, installers, CI/CD
- Domain knowledge in C#

---

## The Real Question: What Problem Are We Solving?

| Problem | .NET 10 Status | Would Rust Help? |
|---------|----------------|------------------|
| Binary too large? | 12-15 MB | Rust ~6-8 MB (2x smaller) |
| Startup too slow? | 10-14 ms | Rust 2-5 ms (marginal) |
| Memory too high? | 30-50 MB | Rust 15-25 MB (2x better) |
| P/Invoke bugs? | Working fine | Rust `portable-pty` is cleaner |
| JSON boilerplate? | Annoying but works | Serde is cleaner |
| Performance? | Excellent | Similar |

---

## Recommendations (2025)

### Option A: Stay on .NET 10 (Recommended if no pain)
If current binary size (~12-15 MB) and memory (~30-50 MB) are acceptable, the rewrite ROI is low.

**Effort:** 0 | **Benefit:** 0 | **Risk:** 0

### Option B: Rewrite mthost Only (Recommended if PTY code is painful)
The mthost process is:
- Isolated (separate binary, IPC interface)
- Where 85%+ of P/Invoke complexity lives
- Where `portable-pty` provides 15:1 code reduction

**Rust mthost:** ~400-500 LOC vs current ~2,500 LOC

**Effort:** Medium | **Benefit:** Cleaner PTY code, smaller binary | **Risk:** Low (isolated component)

### Option C: Full Rewrite (Only if binary size/memory are critical)
Only justified if:
- Need <8 MB binary size
- Need <25 MB memory footprint
- Team has Rust expertise
- Long-term maintenance will be in Rust

**Effort:** High | **Benefit:** 2x smaller, 2x less memory | **Risk:** Medium

---

## Rust Dependency Manifest (2025 Current)

```toml
[dependencies]
# Web (Axum 0.8 - released Jan 2025)
axum = "0.8"
tokio = { version = "1.43", features = ["full"] }
tokio-tungstenite = "0.24"
tower-http = "0.6"

# PTY (portable-pty 0.9.0 - released Feb 2025)
portable-pty = "0.9"

# Serialization
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"

# Crypto
ring = "0.17"
rcgen = "0.13"
pbkdf2 = { version = "0.12", features = ["simple"] }
hmac = "0.12"
sha2 = "0.10"
aes-gcm = "0.10"

# Platform
nix = { version = "0.29", features = ["term", "process", "signal"] }

# Windows
[target.'cfg(windows)'.dependencies]
windows = { version = "0.58", features = [...] }
windows-service = "0.7"

# macOS
[target.'cfg(target_os = "macos")'.dependencies]
security-framework = "2.11"

# Logging
tracing = "0.1"
tracing-subscriber = "0.3"

# Utility
bytes = "1.7"
flate2 = "1.0"
dashmap = "6.0"
notify = "7.0"
reqwest = { version = "0.12", features = ["json"] }
```

---

## Conclusion (2025 Reality)

In 2025, **both ecosystems are mature and production-ready:**

| Aspect | .NET 10 AOT | Rust 2025 |
|--------|-------------|-----------|
| Web frameworks | Kestrel (excellent) | Axum 0.8 (excellent) |
| Async | Task-based (excellent) | Tokio (excellent) |
| PTY handling | Manual P/Invoke (works) | `portable-pty` (cleaner) |
| Binary size | ~9-15 MB | ~4-8 MB |
| Startup | 10-14 ms | 2-5 ms |
| Memory | 30-50 MB | 15-25 MB |

The decision should be based on:
1. **Is current binary size a problem?** If no → stay on .NET 10
2. **Is PTY P/Invoke code causing maintenance pain?** If yes → consider Rust mthost
3. **Is memory footprint critical?** If yes → consider full rewrite
4. **Does team know Rust?** If no → rewrite cost is much higher

---

## Sources

- [.NET 10 Native AOT Performance (InfoQ, Nov 2025)](https://www.infoq.com/news/2025/11/dotnet-10-release/)
- [.NET 10 AOT 80% Startup Reduction (Medium, Mar 2025)](https://isitvritra101.medium.com/cut-your-net-10-api-startup-time-by-80-native-aot-for-high-performance-dcfd1fd916ae)
- [State of Native AOT in .NET 10](https://code.soundaranbu.com/state-of-nativeaot-net10)
- [Axum 0.8.0 Announcement (Jan 2025)](https://tokio.rs/blog/2025-01-01-announcing-axum-0-8-0)
- [Rust Ecosystem 2025 Maturity](https://developersvoice.com/blog/technology/rust-for-reliability/)
- [portable-pty 0.9.0 (Feb 2025)](https://docs.rs/portable-pty)
- [Rust vs C# Benchmarks 2025](https://programming-language-benchmarks.vercel.app/rust-vs-csharp)
