# File Radar Design

This document describes how tlbx's File Radar detects, validates, and opens file paths from terminal output.

## Goals

- Detect file and folder paths in terminal output and underline the full detected token.
- Support common Windows and Linux path formats.
- Keep path access scoped by per-session allowlisting.
- Keep scanning and hover-resolution efficient under high output volume.

## Source Files

- Frontend link detection/runtime:
  - `src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/fileLinks.ts`
- Frontend regex and filtering logic:
  - `src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/fileRadar.patterns.ts`
- Backend endpoints:
  - `src/Ai.Tlbx.MidTerm/Services/FileEndpoints.cs`
  - `src/Ai.Tlbx.MidTerm/Services/FileService.cs`
  - `src/Ai.Tlbx.MidTerm/Services/SessionPathAllowlistService.cs`

## End-to-End Flow

1. Terminal output frame arrives in `terminal/manager.ts`.
2. `scanOutputForPaths(sessionId, frame.data)` buffers output for debounce window.
3. `performScan()` strips ANSI, runs absolute path regexes, validates, then registers detected paths with `/api/files/register`.
4. Link providers in xterm underline candidate matches.
5. For relative/known/folder matches, hover triggers throttled `/api/files/resolve` (non-deep) to avoid false links.
6. On click:
   - Absolute path: verify with `/api/files/check` (plus fallback resolve for Unix-style on Windows).
   - Relative/folder path: resolve with `/api/files/resolve?deep=true`.
7. If file/folder exists, open in file viewer; otherwise show "not found" toast.

## Pattern Classes

`fileRadar.patterns.ts` defines:

- `UNIX_PATH_PATTERN`, `WIN_PATH_PATTERN`, `UNC_PATH_PATTERN`
- `QUOTED_ABSOLUTE_PATH_PATTERN` (handles spaces inside quoted absolute paths)
- Global scan versions:
  - `UNIX_PATH_PATTERN_GLOBAL`
  - `WIN_PATH_PATTERN_GLOBAL`
  - `UNC_PATH_PATTERN_GLOBAL`
  - `QUOTED_ABSOLUTE_PATH_PATTERN_GLOBAL`
- Relative/folder/known patterns:
  - `RELATIVE_PATH_PATTERN`
  - `FOLDER_PATH_PATTERN`
  - `KNOWN_FILE_PATTERN`

## URL and False-Positive Filtering

The filter layer rejects path-like strings that are likely URLs/domains:

- Explicit schemes (`http://`, `https://`, `ftp://`, etc.).
- `www.` prefixes.
- IPv4 host style (`1.2.3.4/...`).
- Host-like prefixes with common TLDs (curated list in code).

Additional heuristics reject:

- Version-like tokens (`1.2.3`).
- Common abbreviations (`e.g.`, `i.e.`, ...).
- Dot-heavy .NET FQNs without path separators.
- PascalCase extension-style method/project tokens.

## Matching Precedence (xterm Link Providers)

Order is intentional (later providers win in overlap):

1. Relative folders
2. Known extensionless files
3. Relative files with extensions
4. Quoted absolute paths
5. UNC absolute paths
6. Windows absolute paths
7. Unix absolute paths

This keeps absolute matches from being fragmented by relative patterns.

## Security Model

- Frontend registration via `/api/files/register` only allowlists paths detected in terminal output.
- Backend checks still enforce:
  - session validity,
  - allowlist membership or working-directory scoping,
  - path normalization/traversal protection.
- Session allowlist capacity is capped (FIFO eviction).

## Performance and Memory Controls

`fileLinks.ts` includes bounded behavior:

- `MIN_SCAN_FRAME_SIZE`: skips tiny frames.
- Quick path-character gate before regex scan.
- Debounced scanning (`SCAN_DEBOUNCE_MS`).
- Bounded per-session scan buffer (`MAX_PENDING_SCAN_BYTES`).
- Capped caches with TTL:
  - existence cache (`MAX_EXISTENCE_CACHE_ENTRIES`)
  - resolve cache (`MAX_RESOLVE_CACHE_ENTRIES`)
- Per-session hover resolve cancellation to avoid API spam.

## Important Runtime Details

- Absolute click flow waits briefly for allowlist registration before existence check to reduce first-click race misses.
- Cache keys include `sessionId` where session context affects access checks.
- Session teardown clears:
  - pending scan buffers/timers,
  - pending hover resolve for that session,
  - session-scoped cache entries.

## Testing

Primary regex/filter coverage:

- `src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/fileRadar.patterns.test.ts`

Key coverage areas:

- Unix/Windows/UNC absolute paths
- Quoted absolute paths with spaces
- Relative/folder/known-file matching
- URL/domain rejection behavior
- Real-world terminal snippets
- Global scanner behavior
