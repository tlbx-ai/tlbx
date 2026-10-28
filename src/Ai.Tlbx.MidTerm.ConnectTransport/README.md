# tlbx WebRTC transport

Small Pion transport process supervised by the C# tlbx host. It has no account,
device authorization, public listener, or application request-routing logic.
Pion WebRTC is MIT licensed; its version and transitive dependencies are pinned
by `go.mod` and `go.sum`.

## Build

Use Go 1.26 or later (verified with 1.27.3). `go test ./...` includes a real local encrypted WebRTC
DataChannel roundtrip. `./build.ps1` runs tests and cross-compiles six native
executables, without Cgo. Outputs are ignored under `bin/<RID>/mtconnect[.exe]`.
`./build.ps1 -RuntimeIdentifier linux-arm64 -SkipTests` builds only that target.
An optional `-GoExecutable` points to an existing Go installation. Otherwise the
script uses exact Go 1.27.3 from the repository's `.dev/tools/go/bin` cache or
PATH, or bootstraps that pinned version into `.dev/tools/toolchains` without a
system install. Only an explicit `-GoExecutable` may select another supported
version; the emitted build metadata and Go advisory scan identify its actual
compiler/runtime version.
Official archive SHA256 hashes for supported build hosts are tracked in
`toolchain.json`; downloads are verified before extraction. Source/compiler/RID
and binary hashes keep repeated unchanged builds fast.
Distribute the generated `THIRD-PARTY-NOTICES.txt` alongside the executable.

The x/net, x/crypto and x/sys overrides retain the current security fixes above
Pion's minimum versions. Audit with
`go run golang.org/x/vuln/cmd/govulncheck@v1.9.0 -show verbose ./...`.

The `build` release cluster runs `verify.ps1` (Go protocol tests and vet); its
existing release-build-system checks run the focused SPDX/advisory fixtures.
The server dependency gate audits imported Go
packages for all six release RIDs using pinned govulncheck 1.9.0. New imported
advisories and increased package/platform exposure block; unchanged findings
are compared with the last published same-channel tag. A baseline with no Go
module is empty. Only when findings exist does the gate export that tag's small
Go module (source plus go.mod/go.sum) into the existing audit cache to reconstruct
its actual import graph. Advisories exclusively in unimported packages remain
visible without blocking. Complete JSON reports and separate stderr logs are
preserved under `.artifacts/advisories`.

`build-info.json` next to each native executable records its actual Go build
information, module checksums and licenses. After the existing Syft generation,
the release attestation action must run
`complete-sbom.ps1 -SbomPath staging/mt-<RID>.spdx.json` before attesting/uploading
that final document. The helper checks the compiled binary's SHA256 and adds
the embedded companion, Go runtime and statically linked Go module packages.

## Standard input/output protocol, version 1

One UTF-8 JSON object per line; stdout contains only protocol events. stderr is
diagnostic output and must be drained concurrently into a separate named log.
One process owns one PeerConnection and accepts one offer. Reconnection starts
a new process. C# authenticates the remote browser before forwarding an offer.

Commands:

```json
{"type":"init","version":1,"iceServers":[{"urls":["stun:connect.tlbx.ai:3478"]}],"relayOnly":false}
{"type":"offer","sdp":"..."}
{"type":"candidate","candidate":"candidate:...","sdpMid":"0","sdpMLineIndex":0}
{"type":"send","data":"AAEC"}
{"type":"close"}
```

ICE servers can include `username` and `credential` strings. Pion supports TURN
over UDP, TCP and TLS; use expiring TURN credentials issued by the authenticated
Connect service. `relayOnly` is for policy or forced-relay verification. Empty
candidate strings mark the end of candidate gathering.

Events:

- `ready`: `version:1`; init succeeded and an offer can be submitted.
- `answer`: `sdp`; forward to the authorized browser.
- `candidate`: `candidate`, `sdpMid`, `sdpMLineIndex`; forward to that browser.
- `state`: `state` is the Pion PeerConnection state; a closed channel also emits
  `closed` before process teardown.
- `open`: the browser's DataChannel is ready; only now may C# submit `send`.
- `data`: `data` is the base64 representation of one incoming binary message.
- `path`: `localType`, `remoteType`, `protocol`; records the selected ICE pair
  and changes to it. A relay candidate on either side means relayed traffic.
  `protocol` describes the candidate pair, not the TCP/TLS transport of a TURN
  control connection. Payload encryption is provided by WebRTC DTLS throughout.
- `error`: `code`, `message`, `fatal:true`; stop using this process. An abruptly
  closed/blocked stdout can prevent error delivery, so process exit is also a
  failure signal.

The browser must create exactly one **reliable ordered** DataChannel named
`tlbx`. Text messages, extra channels and partial-reliability channels fail
closed. Parent and peer binary messages are limited to 65,536 bytes each; split
larger application transfers into bounded frames. Stdio lines are limited to
131,072 bytes, SDP to 65,536 bytes, candidates to 8,192 bytes. At most 128 remote
candidates wait for the offer. Stdout queue capacity is 32 events. SCTP send
buffer backlog is limited to 1 MiB. A send waits for the buffered-amount-low
notification at 512 KiB instead of rejecting transient congestion; the wait
aborts on shutdown/peer failure or a 30-second deadline. Deadline failures
terminate the transport instead of dropping/reordering frames. Application-level
flow control belongs in the C# tunnel and browser.

The parent closes stdin or sends `close` on shutdown. It must enforce an exit
deadline and terminate the child if its pipes or native network operations
stall. On normal parent pipe closure the process releases the PeerConnection.

The local integration test proves protocol interoperability between real Pion
peers, 64 KiB binary frame preservation, a 20 MiB streamed response, direct-path
reporting and teardown. Browser
interoperability, cross-network NAT traversal and TURN TCP/TLS require separate
end-to-end acceptance against the deployed signaling/TURN service.
