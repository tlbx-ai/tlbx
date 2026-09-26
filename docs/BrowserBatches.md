# Fast browser actions

`mt_screenshot` captures the current viewport. Use `mt_screenshot -FullPage` in
PowerShell or `mt_screenshot --full-page` in bash for the entire document. Full-page
capture is more expensive; screenshots use in-page rendering, not native browser pixels.

Send several steps in one authenticated HTTP request:

```powershell
mt_batch @(
    @{command='fill'; selector='#name'; value='Browser test'}
    @{command='fill'; selector='#email'; value='test@example.invalid'}
    @{command='click'; selector='button[type=submit]'; waitForNavigation=$true}
    @{command='wait'; selector='#confirmation'; timeout=15}
    @{command='query'; selector='#confirmation'; textOnly=$true}
    @{command='screenshot'}
)
```

Bash accepts the same command array as JSON: `mt_batch '[{"command":"query","selector":"h1","textOnly":true},{"command":"screenshot"}]'`.
The API is `POST /api/browser/batch` with `sessionId`, `previewName`, `commands`,
and optional overall `timeout` (default 60 seconds; maximum 120). A batch has 1–32 steps.
Commands execute sequentially on the server, removing per-step shell/HTTP round trips.
Each step still uses the existing browser bridge and its ownership checks.

The response contains `success`, `failedIndex`, `error`, `durationMs`, and ordered
`results` with each step's result, duration and error. Execution stops on the first
failure. Earlier actions are not rolled back or retried. Inspect partial results
before deciding what to do next; a timeout does not prove an action failed to execute.
The outer scope overrides individual step scopes, and owner/target changes stop the batch.

For full-document navigation triggered by click/submit, set `waitForNavigation:true`.
`navigate` and `reload` wait for a new document bridge automatically. For SPA changes,
use a following `wait` for a selector specific to the new state instead.
Screenshots return saved file paths. Nested batches and ownership changes are rejected
before executing any steps. Normal browser action authorization still applies.
