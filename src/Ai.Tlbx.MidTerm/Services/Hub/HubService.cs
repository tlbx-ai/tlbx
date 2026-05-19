using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.Spaces;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Hub;

public sealed class HubService
{
    private readonly SettingsService _settingsService;
    private readonly Lock _lock = new();

    public HubService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public List<HubMachineSettings> GetMachines()
    {
        lock (_lock)
        {
            return _settingsService.Load().HubMachines
                .Select(CloneMachine)
                .ToList();
        }
    }

    public HubMachineSettings? GetMachine(string id)
    {
        lock (_lock)
        {
            var machine = _settingsService.Load().HubMachines
                .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            return machine is null ? null : CloneMachine(machine);
        }
    }

    public HubMachineInfo UpsertMachine(string? id, HubMachineUpsertRequest request)
    {
        var normalizedUrl = NormalizeBaseUrl(request.BaseUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            throw new ArgumentException("Machine URL is required.");
        }

        lock (_lock)
        {
            var settings = _settingsService.Load();
            var machines = settings.HubMachines;
            var machine = !string.IsNullOrWhiteSpace(id)
                ? machines.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
                : null;

            if (machine is null)
            {
                machine = new HubMachineSettings
                {
                    Id = Guid.NewGuid().ToString("N")
                };
                machines.Add(machine);
            }

            machine.Name = ResolveStoredMachineName(
                request.Name,
                normalizedUrl,
                machine.Name);
            machine.BaseUrl = normalizedUrl;
            machine.Enabled = request.Enabled;
            if (request.ApiKey is not null)
            {
                machine.ApiKey = NormalizeOptionalSecret(request.ApiKey);
            }

            if (request.Password is not null)
            {
                machine.Password = NormalizeOptionalSecret(request.Password);
            }

            settings.HubMachines = NormalizeMachines(machines);
            _settingsService.Save(settings);
            return ToMachineInfo(machine);
        }
    }

    public bool DeleteMachine(string id)
    {
        lock (_lock)
        {
            var settings = _settingsService.Load();
            var removed = settings.HubMachines.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            _settingsService.Save(settings);
            return true;
        }
    }

    public string PinFingerprint(string id, string fingerprint)
    {
        var normalizedFingerprint = NormalizeFingerprint(fingerprint);
        if (string.IsNullOrWhiteSpace(normalizedFingerprint))
        {
            throw new ArgumentException("Fingerprint is required.");
        }

        lock (_lock)
        {
            var settings = _settingsService.Load();
            var machine = settings.HubMachines.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
                ?? throw new ArgumentException("Hub machine not found.");
            machine.PinnedFingerprint = normalizedFingerprint;
            machine.LastFingerprint = normalizedFingerprint;
            _settingsService.Save(settings);
            return normalizedFingerprint;
        }
    }

    public bool ClearPinnedFingerprint(string id)
    {
        lock (_lock)
        {
            var settings = _settingsService.Load();
            var machine = settings.HubMachines.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (machine is null)
            {
                return false;
            }

            machine.PinnedFingerprint = null;
            _settingsService.Save(settings);
            return true;
        }
    }

    public async Task<HubStateResponse> GetStateAsync(CancellationToken ct = default)
    {
        var machines = GetMachines();
        var result = new HubStateResponse();
        foreach (var machine in machines)
        {
            result.Machines.Add(await GetMachineStateAsync(machine, ct));
        }

        return result;
    }

    public async Task<HubMachineState> GetMachineStateAsync(string id, CancellationToken ct = default)
    {
        var machine = GetMachine(id) ?? throw new ArgumentException("Hub machine not found.");
        return await GetMachineStateAsync(machine, ct);
    }

    public async Task<HubMachineState> GetMachineStateAsync(HubMachineSettings machine, CancellationToken ct = default)
    {
        var state = new HubMachineState
        {
            Machine = ToMachineInfo(machine),
            Status = machine.Enabled ? "connecting" : "disabled",
            Sessions = []
        };

        if (!machine.Enabled)
        {
            state.Error = "Machine is disabled.";
            return state;
        }

        try
        {
            await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: false, ct);
            var discoveredName = await PersistDiscoveredMachineNameAsync(
                machine,
                remote.Bootstrap?.Hostname);
            if (!string.IsNullOrWhiteSpace(discoveredName))
            {
                state.Machine.Name = discoveredName;
            }

            var sessionsResponse = await remote.Client.GetFromJsonAsync(
                "/api/sessions",
                AppJsonContext.Default.SessionListDto,
                ct);
            var sharePacket = await remote.Client.GetFromJsonAsync(
                "/api/certificate/share-packet",
                AppJsonContext.Default.SharePacketInfo,
                ct);
            var updateInfo = await remote.Client.GetFromJsonAsync(
                "/api/update/check",
                AppJsonContext.Default.UpdateInfo,
                ct);

            await PersistCapturedFingerprintAsync(machine.Id, remote.CapturedFingerprint);

            state.Machine.LastFingerprint = NormalizeFingerprint(remote.CapturedFingerprint);
            state.Machine.PinnedFingerprint = NormalizeFingerprint(machine.PinnedFingerprint);
            state.FingerprintMismatch = remote.HasPinnedFingerprintMismatch;
            state.RequiresTrust = remote.HasPinnedFingerprintMismatch;
            state.Status = "online";
            state.CurrentVersion = updateInfo?.CurrentVersion;
            state.LatestVersion = updateInfo?.LatestVersion;
            state.UpdateAvailable = updateInfo?.Available == true;
            state.Sessions = sessionsResponse?.Sessions ?? [];

            if (sharePacket?.Certificate?.FingerprintFormatted is not null)
            {
                state.Machine.LastFingerprint = NormalizeFingerprint(sharePacket.Certificate.FingerprintFormatted);
                await PersistCapturedFingerprintAsync(machine.Id, state.Machine.LastFingerprint);
            }
        }
        catch (Exception ex)
        {
            state.Status = "offline";
            state.Error = ex.Message;
            state.FingerprintMismatch = false;
            state.RequiresTrust = false;
        }

        return state;
    }

    public async Task<SessionInfoDto> CreateSessionAsync(
        string machineId,
        CreateSessionRequest? request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            "/api/sessions",
            request,
            AppJsonContext.Default.CreateSessionRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SessionInfoDto, ct)
            ?? throw new InvalidOperationException("Remote session response was empty.");
    }

    public async Task DeleteSessionAsync(string machineId, string sessionId, CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.DeleteAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task RenameSessionAsync(
        string machineId,
        string sessionId,
        RenameSessionRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PutAsJsonAsync(
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}/name",
            request,
            AppJsonContext.Default.RenameSessionRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<CreateHistoryResponse> CreateHistoryEntryAsync(
        string machineId,
        CreateHistoryRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            "/api/history",
            request,
            AppJsonContext.Default.CreateHistoryRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.CreateHistoryResponse, ct)
            ?? throw new InvalidOperationException("Remote history response was empty.");
    }

    public async Task SetSessionBookmarkAsync(
        string machineId,
        string sessionId,
        SetBookmarkRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PutAsJsonAsync(
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}/bookmark",
            request,
            AppJsonContext.Default.SetBookmarkRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<LauncherPathResponse> GetLauncherHomeAsync(string machineId, CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.GetAsync("/api/files/picker/home", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherPathResponse, ct)
            ?? throw new InvalidOperationException("Remote picker home response was empty.");
    }

    public async Task<LauncherDirectoryListResponse> GetLauncherRootsAsync(
        string machineId,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.GetAsync("/api/files/picker/roots", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherDirectoryListResponse, ct)
            ?? throw new InvalidOperationException("Remote picker roots response was empty.");
    }

    public async Task<LauncherDirectoryListResponse> GetLauncherDirectoriesAsync(
        string machineId,
        string path,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.GetAsync(
            $"/api/files/picker/directories?path={Uri.EscapeDataString(path)}",
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherDirectoryListResponse, ct)
            ?? throw new InvalidOperationException("Remote picker directories response was empty.");
    }

    public async Task<LauncherDirectoryAccessResponse> GetLauncherWritableAsync(
        string machineId,
        string path,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.GetAsync(
            $"/api/files/picker/writable?path={Uri.EscapeDataString(path)}",
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherDirectoryAccessResponse, ct)
            ?? throw new InvalidOperationException("Remote picker writable response was empty.");
    }

    public async Task<LauncherDirectoryMutationResponse> CreateLauncherFolderAsync(
        string machineId,
        LauncherCreateDirectoryRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            "/api/files/picker/folders",
            request,
            AppJsonContext.Default.LauncherCreateDirectoryRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherDirectoryMutationResponse, ct)
            ?? throw new InvalidOperationException("Remote create-folder response was empty.");
    }

    public async Task<LauncherDirectoryMutationResponse> CloneLauncherRepositoryAsync(
        string machineId,
        LauncherCloneRepositoryRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            "/api/files/picker/clone",
            request,
            AppJsonContext.Default.LauncherCloneRepositoryRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LauncherDirectoryMutationResponse, ct)
            ?? throw new InvalidOperationException("Remote clone response was empty.");
    }

    public async Task<List<SpaceSummaryDto>> GetSpacesAsync(
        string machineId,
        bool includeWorkspaces = true,
        bool pinnedOnly = false,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        var query = $"/api/spaces?includeWorkspaces={(includeWorkspaces ? "true" : "false")}&pinnedOnly={(pinnedOnly ? "true" : "false")}";
        using var response = await remote.Client.GetAsync(query, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListSpaceSummaryDto, ct)
            ?? [];
    }

    public async Task<SpaceSummaryDto> ImportSpaceAsync(
        string machineId,
        SpaceImportRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            "/api/spaces/import",
            request,
            AppJsonContext.Default.SpaceImportRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote space import response was empty.");
    }

    public async Task<SpaceSummaryDto> UpdateSpaceAsync(
        string machineId,
        string spaceId,
        SpaceUpdateRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PatchAsJsonAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}",
            request,
            AppJsonContext.Default.SpaceUpdateRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote space update response was empty.");
    }

    public async Task<SpaceSummaryDto> InitGitSpaceAsync(
        string machineId,
        string spaceId,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}/git/init",
            content: null,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote space init response was empty.");
    }

    public async Task<SpaceSummaryDto> CreateWorktreeAsync(
        string machineId,
        string spaceId,
        SpaceCreateWorktreeRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}/worktrees",
            request,
            AppJsonContext.Default.SpaceCreateWorktreeRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote worktree response was empty.");
    }

    public async Task<SpaceSummaryDto> UpdateWorkspaceAsync(
        string machineId,
        string spaceId,
        string workspaceKey,
        SpaceUpdateWorkspaceRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PatchAsJsonAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}/workspaces/{Uri.EscapeDataString(workspaceKey)}",
            request,
            AppJsonContext.Default.SpaceUpdateWorkspaceRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote workspace update response was empty.");
    }

    public async Task<SpaceSummaryDto> DeleteWorktreeAsync(
        string machineId,
        string spaceId,
        string workspaceKey,
        bool force,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.DeleteAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}/workspaces/{Uri.EscapeDataString(workspaceKey)}?force={(force ? "true" : "false")}",
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SpaceSummaryDto, ct)
            ?? throw new InvalidOperationException("Remote worktree delete response was empty.");
    }

    public async Task<SessionInfoDto> LaunchSpaceAsync(
        string machineId,
        string spaceId,
        string workspaceKey,
        SpaceLaunchRequest request,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.PostAsJsonAsync(
            $"/api/spaces/{Uri.EscapeDataString(spaceId)}/workspaces/{Uri.EscapeDataString(workspaceKey)}/launch",
            request,
            AppJsonContext.Default.SpaceLaunchRequest,
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.SessionInfoDto, ct)
            ?? throw new InvalidOperationException("Remote space launch response was empty.");
    }

    public async Task<List<LaunchEntry>> GetRecentsAsync(string machineId, int count = 6, CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var response = await remote.Client.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/recents?count={count}"),
            ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListLaunchEntry, ct)
            ?? [];
    }

    public async Task<HubUpdateRolloutResponse> ApplyUpdatesAsync(
        HubUpdateRolloutRequest request,
        CancellationToken ct = default)
    {
        var selectedIds = request.MachineIds.Count > 0
            ? new HashSet<string>(request.MachineIds, StringComparer.Ordinal)
            : null;

        var response = new HubUpdateRolloutResponse();
        foreach (var machine in GetMachines())
        {
            if (!machine.Enabled)
            {
                continue;
            }

            if (selectedIds is not null && !selectedIds.Contains(machine.Id))
            {
                continue;
            }

            try
            {
                await ApplyUpdateAsync(machine, ct).ConfigureAwait(false);
                response.Results.Add(new HubUpdateRolloutItem
                {
                    MachineId = machine.Id,
                    MachineName = machine.Name,
                    Status = "ok",
                    Message = "Update requested."
                });
            }
            catch (Exception ex)
            {
                response.Results.Add(new HubUpdateRolloutItem
                {
                    MachineId = machine.Id,
                    MachineName = machine.Name,
                    Status = "error",
                    Message = ex.Message
                });
            }
        }

        return response;
    }

    private async Task ApplyUpdateAsync(HubMachineSettings machine, CancellationToken ct)
    {
        await using var remote = await CreateRemoteContextAsync(machine, requireTrusted: true, ct);
        using var httpResponse = await remote.Client.PostAsync("/api/update/apply", content: null, ct);
        await EnsureSuccessAsync(httpResponse, ct);
    }

    public async Task ConfigureRemoteWebSocketAsync(
        string machineId,
        ClientWebSocket socket,
        CancellationToken ct = default)
    {
        var machine = GetRequiredMachine(machineId);
        await ConfigureRemoteWebSocketAsync(machine, socket, requireTrusted: true, ct);
    }

    private async Task ConfigureRemoteWebSocketAsync(
        HubMachineSettings machine,
        ClientWebSocket socket,
        bool requireTrusted,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine.BaseUrl))
        {
            throw new InvalidOperationException("Hub machine URL is not configured.");
        }

        var baseUri = new Uri(machine.BaseUrl, UriKind.Absolute);
        string? capturedFingerprint = null;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, sslPolicyErrors) =>
        {
            capturedFingerprint = FormatFingerprint(certificate);
            return IsRemoteCertificateTrusted(machine, requireTrusted, capturedFingerprint, sslPolicyErrors);
        };

        await using var preflight = await CreateRemoteContextAsync(machine, requireTrusted, ct);
        capturedFingerprint = preflight.CapturedFingerprint;
        if (preflight.AuthMode == RemoteAuthMode.ApiKey && !string.IsNullOrWhiteSpace(machine.ApiKey))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {machine.ApiKey.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(preflight.CookieHeader))
        {
            socket.Options.SetRequestHeader("Cookie", preflight.CookieHeader);
        }

        if (!string.IsNullOrWhiteSpace(capturedFingerprint))
        {
            await PersistCapturedFingerprintAsync(machine.Id, capturedFingerprint);
        }
    }

    private HubMachineSettings GetRequiredMachine(string id)
    {
        return GetMachine(id) ?? throw new ArgumentException("Hub machine not found.");
    }

    private static HubMachineSettings CloneMachine(HubMachineSettings machine)
    {
        return new HubMachineSettings
        {
            Id = machine.Id,
            Name = machine.Name,
            BaseUrl = machine.BaseUrl,
            Enabled = machine.Enabled,
            ApiKey = machine.ApiKey,
            Password = machine.Password,
            LastFingerprint = machine.LastFingerprint,
            PinnedFingerprint = machine.PinnedFingerprint
        };
    }

    private static List<HubMachineSettings> NormalizeMachines(IEnumerable<HubMachineSettings> machines)
    {
        return machines
            .Where(machine => !string.IsNullOrWhiteSpace(machine.BaseUrl))
            .GroupBy(machine => machine.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var machine = CloneMachine(group.First());
                machine.BaseUrl = NormalizeBaseUrl(machine.BaseUrl);
                machine.Name = ResolveStoredMachineName(machine.Name, machine.BaseUrl);
                machine.ApiKey = NormalizeOptionalSecret(machine.ApiKey);
                machine.Password = NormalizeOptionalSecret(machine.Password);
                machine.LastFingerprint = NormalizeFingerprint(machine.LastFingerprint);
                machine.PinnedFingerprint = NormalizeFingerprint(machine.PinnedFingerprint);
                if (string.IsNullOrWhiteSpace(machine.Id))
                {
                    machine.Id = Guid.NewGuid().ToString("N");
                }
                return machine;
            })
            .ToList();
    }

    private static string? NormalizeOptionalSecret(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return trimmed;
    }

    private static string ResolveStoredMachineName(
        string? requestedName,
        string baseUrl,
        string? existingName = null)
    {
        var trimmedRequested = requestedName?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedRequested))
        {
            return trimmedRequested;
        }

        var trimmedExisting = existingName?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedExisting))
        {
            return trimmedExisting;
        }

        return GetBaseUrlHost(baseUrl);
    }

    private static string GetBaseUrlHost(string? baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return string.Empty;
    }

    private static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        var parts = fingerprint
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant())
            .ToArray();
        return parts.Length == 0 ? null : string.Join(':', parts);
    }

    private static bool HasPinnedMismatch(string? pinnedFingerprint, string? fingerprint)
    {
        var normalizedPinned = NormalizeFingerprint(pinnedFingerprint);
        if (string.IsNullOrWhiteSpace(normalizedPinned))
        {
            return false;
        }

        var normalizedCurrent = NormalizeFingerprint(fingerprint);
        if (string.IsNullOrWhiteSpace(normalizedCurrent))
        {
            return false;
        }

        return !string.Equals(normalizedPinned, normalizedCurrent, StringComparison.Ordinal);
    }

    private static string? FormatFingerprint(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        try
        {
            var raw = certificate.GetRawCertData();
            var hash = SHA256.HashData(raw);
            return string.Join(':', hash.Select(b => b.ToString("X2")));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRemoteCertificateTrusted(
        HubMachineSettings machine,
        bool requireTrusted,
        string? fingerprint,
        SslPolicyErrors sslPolicyErrors)
    {
        if (!requireTrusted)
        {
            return true;
        }

        var pinnedFingerprint = NormalizeFingerprint(machine.PinnedFingerprint);
        if (!string.IsNullOrWhiteSpace(pinnedFingerprint))
        {
            return !HasPinnedMismatch(pinnedFingerprint, fingerprint);
        }

        return sslPolicyErrors == SslPolicyErrors.None;
    }

    private HubMachineInfo ToMachineInfo(HubMachineSettings machine)
    {
        return new HubMachineInfo
        {
            Id = machine.Id,
            Name = machine.Name,
            BaseUrl = machine.BaseUrl,
            Enabled = machine.Enabled,
            HasApiKey = !string.IsNullOrWhiteSpace(machine.ApiKey),
            HasPassword = !string.IsNullOrWhiteSpace(machine.Password),
            LastFingerprint = NormalizeFingerprint(machine.LastFingerprint),
            PinnedFingerprint = NormalizeFingerprint(machine.PinnedFingerprint)
        };
    }

    private async Task PersistCapturedFingerprintAsync(string machineId, string? fingerprint)
    {
        var normalizedFingerprint = NormalizeFingerprint(fingerprint);
        if (string.IsNullOrWhiteSpace(normalizedFingerprint))
        {
            return;
        }

        lock (_lock)
        {
            var settings = _settingsService.Load();
            var machine = settings.HubMachines.FirstOrDefault(entry => string.Equals(entry.Id, machineId, StringComparison.Ordinal));
            if (machine is null || string.Equals(machine.LastFingerprint, normalizedFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            machine.LastFingerprint = normalizedFingerprint;
            _settingsService.Save(settings);
        }

        await Task.CompletedTask;
    }

    private async Task<string?> PersistDiscoveredMachineNameAsync(
        HubMachineSettings machine,
        string? discoveredName)
    {
        var normalizedDiscoveredName = discoveredName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDiscoveredName))
        {
            return null;
        }

        var fallbackName = GetBaseUrlHost(machine.BaseUrl);
        lock (_lock)
        {
            var settings = _settingsService.Load();
            var stored = settings.HubMachines.FirstOrDefault(entry => string.Equals(entry.Id, machine.Id, StringComparison.Ordinal));
            if (stored is null)
            {
                return normalizedDiscoveredName;
            }

            var currentName = stored.Name?.Trim();
            var shouldOverwrite = string.IsNullOrWhiteSpace(currentName) ||
                string.Equals(currentName, fallbackName, StringComparison.OrdinalIgnoreCase);

            if (!shouldOverwrite)
            {
                return currentName;
            }

            if (string.Equals(currentName, normalizedDiscoveredName, StringComparison.Ordinal))
            {
                return currentName;
            }

            stored.Name = normalizedDiscoveredName;
            _settingsService.Save(settings);
            return normalizedDiscoveredName;
        }
    }

    private async Task<RemoteContext> CreateRemoteContextAsync(
        HubMachineSettings machine,
        bool requireTrusted,
        CancellationToken ct)
    {
        RemoteContext? remoteContext = null;

        try
        {
            remoteContext = RemoteContext.Create(machine.BaseUrl);
            var ownedClient = remoteContext.Client;
            string? capturedFingerprint = null;
            ownedClient.Handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
            {
                capturedFingerprint = FormatFingerprint(certificate);
                return HasPinnedFingerprint(machine.PinnedFingerprint)
                    ? string.Equals(
                        NormalizeFingerprint(machine.PinnedFingerprint),
                        NormalizeFingerprint(capturedFingerprint),
                        StringComparison.Ordinal)
                    : sslPolicyErrors == SslPolicyErrors.None;
            };

            var cookieContainer = ownedClient.Handler.CookieContainer;

            var authMode = RemoteAuthMode.None;
            BootstrapResponse? bootstrap = null;

            if (!string.IsNullOrWhiteSpace(machine.ApiKey))
            {
                ownedClient.SetBearerToken(machine.ApiKey.Trim());

                var apiKeyProbe = await TryGetBootstrapAsync(ownedClient, ct);
                if (apiKeyProbe.Success)
                {
                    authMode = RemoteAuthMode.ApiKey;
                    bootstrap = apiKeyProbe.Bootstrap;
                }
                else
                {
                    ownedClient.ClearAuthorization();
                    if (!apiKeyProbe.IsUnauthorized)
                    {
                        throw new InvalidOperationException(apiKeyProbe.ErrorMessage);
                    }
                }
            }

            if (bootstrap is null && !string.IsNullOrWhiteSpace(machine.Password))
            {
                await LoginWithPasswordAsync(ownedClient, machine, ct);
                var passwordProbe = await TryGetBootstrapAsync(ownedClient, ct);
                if (!passwordProbe.Success)
                {
                    throw new InvalidOperationException(passwordProbe.ErrorMessage);
                }

                authMode = RemoteAuthMode.Password;
                bootstrap = passwordProbe.Bootstrap;
            }

            if (bootstrap is null)
            {
                var anonymousProbe = await TryGetBootstrapAsync(ownedClient, ct);
                if (!anonymousProbe.Success)
                {
                    throw new InvalidOperationException(anonymousProbe.ErrorMessage);
                }

                bootstrap = anonymousProbe.Bootstrap;
            }

            if (requireTrusted && HasPinnedMismatch(machine.PinnedFingerprint, capturedFingerprint))
            {
                throw new InvalidOperationException(
                    $"Pinned fingerprint mismatch for \"{machine.Name}\". Replace the pin before controlling this machine.");
            }

            var baseAddress = ownedClient.BaseAddress;
            var cookieHeader = cookieContainer.GetCookieHeader(baseAddress);
            remoteContext.UpdateMetadata(
                NormalizeFingerprint(capturedFingerprint),
                HasPinnedMismatch(machine.PinnedFingerprint, capturedFingerprint),
                cookieHeader,
                authMode,
                bootstrap);
            return remoteContext;
        }
        catch
        {
            if (remoteContext is not null)
            {
                await remoteContext.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static bool HasPinnedFingerprint(string? fingerprint) =>
        !string.IsNullOrWhiteSpace(fingerprint);

    private static OwnedRemoteHttpClient CreateOwnedHttpClient(string baseUrl)
    {
        return OwnedRemoteHttpClient.Create(baseUrl);
    }

    private static async Task LoginWithPasswordAsync(
        OwnedRemoteHttpClient client,
        HubMachineSettings machine,
        CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("password", machine.Password);
            writer.WriteEndObject();
        }

        using var loginContent = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        loginContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };
        using var loginResponse = await client.PostAsync(
            "/api/auth/login",
            loginContent,
            ct);
        if (!loginResponse.IsSuccessStatusCode)
        {
            var message = await loginResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"Hub login failed for \"{machine.Name}\"."
                : message);
        }
    }

    private static async Task<BootstrapProbeResult> TryGetBootstrapAsync(
        OwnedRemoteHttpClient client,
        CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("/api/bootstrap", ct);

            if (response.IsSuccessStatusCode)
            {
                var bootstrap = await response.Content.ReadFromJsonAsync(
                    AppJsonContext.Default.BootstrapResponse,
                    ct);
                return bootstrap is null
                    ? BootstrapProbeResult.Failure("Remote bootstrap response was empty.")
                    : BootstrapProbeResult.WithSuccess(bootstrap);
            }

            var message = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return BootstrapProbeResult.WithUnauthorized(string.IsNullOrWhiteSpace(message)
                    ? string.Create(CultureInfo.InvariantCulture, $"Authentication failed ({(int)response.StatusCode}).")
                    : message);
            }

            return BootstrapProbeResult.Failure(string.IsNullOrWhiteSpace(message)
                ? string.Create(CultureInfo.InvariantCulture, $"Remote bootstrap failed ({(int)response.StatusCode}).")
                : message);
        }
        catch (Exception ex)
        {
            return BootstrapProbeResult.Failure(ex.Message);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? string.Create(CultureInfo.InvariantCulture, $"Remote request failed ({(int)response.StatusCode}).")
            : message);
    }

    private sealed class RemoteContext : IAsyncDisposable
    {
        public static RemoteContext Create(string baseUrl)
        {
            return new RemoteContext(baseUrl);
        }

        private RemoteContext(string baseUrl)
        {
            _ownedClient = OwnedRemoteHttpClient.Create(baseUrl);
        }

        private OwnedRemoteHttpClient? _ownedClient;
        public OwnedRemoteHttpClient Client => _ownedClient ?? throw new ObjectDisposedException(nameof(RemoteContext));
        public string? CapturedFingerprint { get; private set; }
        public bool HasPinnedFingerprintMismatch { get; private set; }
        public string CookieHeader { get; private set; } = string.Empty;
        public RemoteAuthMode AuthMode { get; private set; }
        public BootstrapResponse? Bootstrap { get; private set; }

        public void UpdateMetadata(
            string? capturedFingerprint,
            bool hasPinnedFingerprintMismatch,
            string cookieHeader,
            RemoteAuthMode authMode,
            BootstrapResponse? bootstrap)
        {
            CapturedFingerprint = capturedFingerprint;
            HasPinnedFingerprintMismatch = hasPinnedFingerprintMismatch;
            CookieHeader = cookieHeader;
            AuthMode = authMode;
            Bootstrap = bootstrap;
        }

        public ValueTask DisposeAsync()
        {
            var ownedClient = _ownedClient;
            _ownedClient = null;
            ownedClient?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OwnedRemoteHttpClient : IDisposable
    {
        private OwnedRemoteHttpClient(HttpClientHandler handler, Uri baseAddress, TimeSpan timeout)
        {
            Handler = handler;
            Invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            BaseAddress = baseAddress;
            Timeout = timeout;
        }

        public HttpClientHandler Handler { get; }
        public HttpMessageInvoker Invoker { get; }
        public Uri BaseAddress { get; }
        public TimeSpan Timeout { get; }
        private System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; set; }

        public static OwnedRemoteHttpClient Create(string baseUrl)
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer()
            };

            try
            {
                return new OwnedRemoteHttpClient(
                    handler,
                    new Uri(baseUrl, UriKind.Absolute),
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                handler.Dispose();
                throw;
            }
        }

        public void SetBearerToken(string token)
        {
            Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        public void ClearAuthorization()
        {
            Authorization = null;
        }

        public async Task<T?> GetFromJsonAsync<T>(string uri, JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct)
        {
            using var response = await GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync(jsonTypeInfo, ct).ConfigureAwait(false);
        }

        public Task<HttpResponseMessage> GetAsync(string uri, CancellationToken ct) =>
            SendAsync(HttpMethod.Get, uri, content: null, ct);

        public Task<HttpResponseMessage> DeleteAsync(string uri, CancellationToken ct) =>
            SendAsync(HttpMethod.Delete, uri, content: null, ct);

        public Task<HttpResponseMessage> PostAsync(string uri, HttpContent? content, CancellationToken ct) =>
            SendAsync(HttpMethod.Post, uri, content, ct);

        public Task<HttpResponseMessage> PostAsJsonAsync<T>(string uri, T? value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct) =>
            SendJsonAsync(HttpMethod.Post, uri, value, jsonTypeInfo, ct);

        public Task<HttpResponseMessage> PutAsJsonAsync<T>(string uri, T? value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct) =>
            SendJsonAsync(HttpMethod.Put, uri, value, jsonTypeInfo, ct);

        public Task<HttpResponseMessage> PatchAsJsonAsync<T>(string uri, T? value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken ct) =>
            SendJsonAsync(HttpMethod.Patch, uri, value, jsonTypeInfo, ct);

        private async Task<HttpResponseMessage> SendJsonAsync<T>(
            HttpMethod method,
            string uri,
            T? value,
            JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken ct)
        {
            using var content = JsonContent.Create(value, jsonTypeInfo);
            return await SendAsync(method, uri, content, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string uri,
            HttpContent? content,
            CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(method, new Uri(BaseAddress, uri))
            {
                Content = content,
            };
            if (Authorization is not null)
            {
                request.Headers.Authorization = Authorization;
            }
            return await Invoker.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Invoker.Dispose();
            Handler.Dispose();
        }
    }

    private enum RemoteAuthMode
    {
        None,
        ApiKey,
        Password
    }

    private readonly record struct BootstrapProbeResult(
        bool Success,
        bool IsUnauthorized,
        string ErrorMessage,
        BootstrapResponse? Bootstrap)
    {
        public static BootstrapProbeResult WithSuccess(BootstrapResponse bootstrap) =>
            new(true, false, string.Empty, bootstrap);

        public static BootstrapProbeResult WithUnauthorized(string errorMessage) =>
            new(false, true, errorMessage, null);

        public static BootstrapProbeResult Failure(string errorMessage) =>
            new(false, false, errorMessage, null);
    }
}
