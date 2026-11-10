using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Security;
using Ai.Tlbx.MidTerm.Services.Secrets;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Security;

public sealed class ApiKeyService
{
    private const string TokenPrefix = "mtk";
    private const int MaxApiKeys = 128;
    private const int MaxNameLength = 80;
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(5);

    private readonly SettingsService _settingsService;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    internal event Action<string>? ApiKeyRevoked;

    public ApiKeyService(SettingsService settingsService, TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ApiKeyListResponse ListApiKeys()
    {
        lock (_lock)
        {
            var records = LoadRecordsOrThrow();
            return new ApiKeyListResponse
            {
                ApiKeys = records
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(ToResponse)
                    .ToList()
            };
        }
    }

    public CreateApiKeyResponse CreateApiKey(string? name)
    {
        lock (_lock)
        {
            var trimmedName = NormalizeName(name);
            var records = LoadRecordsOrThrow();
            if (records.Count >= MaxApiKeys)
            {
                throw new InvalidOperationException($"API key limit reached ({MaxApiKeys}).");
            }

            var id = GenerateId();
            var secret = GenerateSecret();
            var createdAt = _timeProvider.GetUtcNow();

            var record = new StoredApiKeyRecord
            {
                Id = id,
                Name = trimmedName,
                Preview = BuildPreview(id, secret),
                SecretHash = ComputeSecretHash(secret),
                CreatedAtUtc = createdAt,
                LastUsedAtUtc = null
            };

            records.Add(record);
            SaveRecords(records);

            return new CreateApiKeyResponse
            {
                ApiKey = ToResponse(record),
                Token = $"{TokenPrefix}_{id}_{secret}"
            };
        }
    }

    public bool DeleteApiKey(string id)
    {
        var trimmedId = id?.Trim();
        if (string.IsNullOrEmpty(trimmedId)) return false;
        lock (_lock)
        {
            var records = LoadRecordsOrThrow();
            if (records.RemoveAll(entry => string.Equals(entry.Id, trimmedId, StringComparison.Ordinal)) == 0)
                return false;
            SaveRecords(records);
        }
        ApiKeyRevoked?.Invoke(trimmedId);
        return true;
    }

    internal bool IsActive(string id)
    {
        lock (_lock)
        {
            return LoadRecordsOrThrow().Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    public bool TryValidateApiKey(string? token, out ApiKeyInfoResponse? apiKey)
    {
        lock (_lock)
        {
            apiKey = null;

            if (!TryParseToken(token, out var id, out var secret))
            {
                return false;
            }

            if (!TryLoadRecords(out var records))
            {
                return false;
            }

            var record = records.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (record is null)
            {
                return false;
            }

            var expected = Encoding.UTF8.GetBytes(record.SecretHash);
            var actual = Encoding.UTF8.GetBytes(ComputeSecretHash(secret));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            if (record.LastUsedAtUtc is null || now - record.LastUsedAtUtc.Value >= LastUsedWriteInterval)
            {
                record.LastUsedAtUtc = now;
                SaveRecords(records);
            }

            apiKey = ToResponse(record);
            return true;
        }
    }

    private static ApiKeyInfoResponse ToResponse(StoredApiKeyRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Preview = record.Preview,
        CreatedAtUtc = record.CreatedAtUtc,
        LastUsedAtUtc = record.LastUsedAtUtc
    };

    private List<StoredApiKeyRecord> LoadRecordsOrThrow()
    {
        if (!TryLoadRecords(out var records))
        {
            throw new InvalidOperationException("API key storage is unavailable or unreadable.");
        }

        return records;
    }

    private bool TryLoadRecords(out List<StoredApiKeyRecord> records)
    {
        records = [];
        var storage = _settingsService.SecretStorage;
        if (storage.LoadFailed)
        {
            return false;
        }

        var raw = storage.GetSecret(SecretKeys.ApiKeys);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            records = JsonSerializer.Deserialize(raw, AppJsonContext.Default.ListStoredApiKeyRecord)
                ?? [];
            return true;
        }
        catch (JsonException ex)
        {
            Log.Warn(() => $"Failed to parse stored API keys: {ex.Message}");
            return false;
        }
    }

    private void SaveRecords(List<StoredApiKeyRecord> records)
    {
        var storage = _settingsService.SecretStorage;
        if (records.Count == 0)
        {
            storage.DeleteSecret(SecretKeys.ApiKeys);
            return;
        }

        var json = JsonSerializer.Serialize(records, AppJsonContext.Default.ListStoredApiKeyRecord);
        storage.SetSecret(SecretKeys.ApiKeys, json);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("API key name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"API key name must be {MaxNameLength} characters or fewer.");
        }

        return trimmed;
    }

    private static string GenerateId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }

    private static string GenerateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    private static string ComputeSecretHash(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(bytes);
    }

    private static string BuildPreview(string id, string secret)
    {
        var start = secret[..4];
        var end = secret[^4..];
        return $"{TokenPrefix}_{id}_{start}...{end}";
    }

    private static bool TryParseToken(string? token, out string id, out string secret)
    {
        id = "";
        secret = "";

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (!trimmed.StartsWith($"{TokenPrefix}_", StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('_', TokenPrefix.Length + 1, StringComparison.Ordinal);
        if (separatorIndex <= TokenPrefix.Length + 1 || separatorIndex >= trimmed.Length - 1)
        {
            return false;
        }

        id = trimmed[(TokenPrefix.Length + 1)..separatorIndex];
        secret = trimmed[(separatorIndex + 1)..];
        return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret);
    }

}

public sealed class StoredApiKeyRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Preview { get; init; } = "";
    public string SecretHash { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }
}
