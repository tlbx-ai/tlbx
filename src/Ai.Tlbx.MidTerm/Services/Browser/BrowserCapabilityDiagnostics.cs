using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;

namespace Ai.Tlbx.MidTerm.Services.Browser;

internal static class BrowserCapabilityDiagnostics
{
    // Uses the existing read/evaluate bridge, without opening storage, creating
    // a graphics context, playing media, querying permissions or fetching URLs.
    internal const string PassivePageScript = """
        (() => {
          const access = name => {
            try { return window[name] == null ? 'absent' : 'getter-accessible'; }
            catch (e) { return e && e.name === 'SecurityError' ? 'denied' : 'getter-error'; }
          };
          const videos = document.getElementsByTagName('video');
          const audios = document.getElementsByTagName('audio');
          let sampled = 0, unpaused = 0, errors = 0, maxReady = 0;
          for (const list of [videos, audios]) {
            for (let i = 0; i < list.length && sampled < 8; i++, sampled++) {
              const media = list[i];
              if (!media.paused) unpaused++;
              if (media.error) errors++;
              maxReady = Math.max(maxReady, media.readyState);
            }
          }
          return {
            opaqueOrigin: window.origin === 'null', secureContext: window.isSecureContext === true,
            visibility: document.visibilityState,
            localStorage: access('localStorage'), sessionStorage: access('sessionStorage'),
            indexedDB: access('indexedDB'),
            webglApi: typeof window.WebGLRenderingContext === 'function' ? 'present' : 'absent',
            webgl2Api: typeof window.WebGL2RenderingContext === 'function' ? 'present' : 'absent',
            mediaElements: videos.length + audios.length, mediaSampled: sampled,
            mediaUnpaused: unpaused, mediaErrors: errors, mediaMaxReadyState: maxReady
          };
        })()
        """;

    internal static async Task<JsonObject> ReadAsync(BrowserCommandService commands, BrowserStatusResponse status,
        string sessionId, string? previewName, string? previewId, CancellationToken ct)
    {
        var page = new JsonObject { ["state"] = "not-probed" };
        if (status.Controllable)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
                {
                    // The outer two-second deadline also bounds attachment. Keep
                    // the command timer longer to avoid racing timeout classification.
                    Command = "exec", Value = PassivePageScript, Timeout = 3,
                    SessionId = sessionId, PreviewName = previewName, PreviewId = previewId
                }, deadline.Token).ConfigureAwait(false);
                page = result.Success ? ParsePage(result.Result)
                    : new JsonObject { ["state"] = deadline.IsCancellationRequested ? "timeout" : "unavailable" };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                page = new JsonObject { ["state"] = "timeout" };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A passive probe must not hide the authoritative owner snapshot.
                // Do not forward arbitrary page errors or exception messages.
                page = new JsonObject { ["state"] = "unavailable" };
            }
        }
        return Project(status, sessionId, previewName, previewId, page);
    }

    internal static JsonObject Project(BrowserStatusResponse status, string sessionId,
        string? previewName, string? previewId, JsonObject page)
    {
        var state = new JsonObject
        {
            ["state"] = status.State, ["phase"] = status.BridgePhase, ["controllable"] = status.Controllable,
            ["hasTarget"] = status.HasTarget, ["uiClients"] = status.ConnectedUiClientCount,
            ["matchingClients"] = status.ConnectedClientCount,
            ["owner"] = status.OwnerBrowserId, ["ownerConnected"] = status.OwnerConnected,
            ["generation"] = status.OwnershipGeneration
        };
        if (status.DefaultClient?.PreviewId is { } selected) state["previewId"] = selected;
        if (status.DefaultClient?.TargetRevision is { } revision) state["targetRevision"] = revision;
        var recovery = new JsonObject();
        if (!status.HasUiClient)
            recovery["action"] = "Open or reload the owning tlbx UI tab.";
        else if (!status.HasTarget)
            recovery["tool"] = "browser_open";
        else if (status.OwnerBrowserId is not null && !status.OwnerConnected)
        {
            recovery["tools"] = new JsonArray("browser_ui_clients", "browser_claim");
            recovery["action"] = "Explicit handoff only: select browserId; do not auto-claim an offline owner.";
        }
        else if (status.State == "ambiguous")
        {
            recovery["tool"] = "browser_status";
            recovery["action"] = "Select an exact previewId; preserve session and previewName.";
        }
        else if (!status.Controllable)
        {
            recovery["tools"] = new JsonArray("browser_reload", "browser_proxylog_summary");
            recovery["action"] = "Reconnect the owner; inspect before retrying an uncertain action.";
        }
        var result = new JsonObject
        {
            ["sessionId"] = sessionId, ["previewName"] = WebPreviewService.NormalizePreviewName(previewName),
            ["status"] = state, ["page"] = page,
            ["tools"] = new JsonObject
            {
                ["read"] = new JsonArray("browser_outline", "browser_query", "browser_evaluate"),
                ["diagnose"] = new JsonArray("browser_inspect", "browser_console", "browser_proxylog_summary", "browser_screenshot"),
                ["batch"] = new JsonArray("workflow_run", "browser_batch")
            },
            ["limits"] = "Getter/API presence does not verify operations or persistence. Media playback/decoding and WebGL contexts unverified; no active probes."
        };
        if (previewId is not null) result["previewId"] = previewId;
        if (recovery.Count > 0) result["recovery"] = recovery;
        return result;
    }

    internal static JsonObject ParsePage(string? value)
    {
        // Only bounded, expected scalar observations cross this diagnostic surface.
        // A site can replace globals; this remains a page observation, not attestation.
        var unavailable = new JsonObject { ["state"] = "invalid-response" };
        if (value is null || value.Length > 2048) return unavailable;
        try
        {
            using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 3 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var page = new JsonObject { ["state"] = "observed" };
            foreach (var name in new[] { "opaqueOrigin", "secureContext" })
            {
                if (!root.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unavailable;
                page[name] = field.GetBoolean();
            }
            foreach (var name in new[] { "visibility", "localStorage", "sessionStorage", "indexedDB", "webglApi", "webgl2Api" })
            {
                if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return unavailable;
                var text = field.GetString();
                var valid = name == "visibility" ? text is "visible" or "hidden" or "prerender"
                    : name is "webglApi" or "webgl2Api" ? text is "present" or "absent"
                    : text is "absent" or "getter-accessible" or "denied" or "getter-error";
                if (!valid) return unavailable;
                page[name] = text;
            }
            foreach (var name in new[] { "mediaElements", "mediaSampled", "mediaUnpaused", "mediaErrors", "mediaMaxReadyState" })
            {
                if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number
                    || !field.TryGetInt32(out var number) || number < 0) return unavailable;
                var max = name == "mediaElements" ? int.MaxValue : name == "mediaMaxReadyState" ? 4 : 8;
                if (number > max) return unavailable;
                page[name] = number;
            }
            return page;
        }
        catch (JsonException) { return unavailable; }
    }
}
