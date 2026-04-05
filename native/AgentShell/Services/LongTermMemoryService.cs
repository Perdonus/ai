using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentShell.Services;

public sealed class LongTermMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private LongTermMemoryStore _store = new();
    private bool _loaded;

    private string MemoryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopAIAgent");

    public string MemoryPath => Path.Combine(MemoryRoot, "long-term-memory.json");

    public void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        Directory.CreateDirectory(MemoryRoot);
        if (File.Exists(MemoryPath))
        {
            try
            {
                var raw = File.ReadAllText(MemoryPath);
                _store = JsonSerializer.Deserialize<LongTermMemoryStore>(raw, JsonOptions) ?? new LongTermMemoryStore();
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Failed to load long-term memory: {ex.Message}");
                _store = new LongTermMemoryStore();
            }
        }

        _loaded = true;
    }

    public async Task ApplyHeuristicsAsync(string prompt, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        var trimmed = prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (TryExtractWeatherLocation(trimmed, out var weatherLocation))
        {
            await RememberAsync("weather.default_location", weatherLocation, "auto:weather-location", cancellationToken);
        }

        if (TryExtractNamedFact(trimmed, out var factKey, out var factValue))
        {
            await RememberAsync(factKey, factValue, "auto:user-fact", cancellationToken);
        }

        if (TryExtractExplicitMemory(trimmed, out var explicitValue))
        {
            var key = BuildFreeformMemoryKey(explicitValue);
            await RememberAsync(key, explicitValue, "user:explicit-memory", cancellationToken);
        }
    }

    public string RewritePromptWithDefaults(string prompt)
    {
        EnsureLoaded();
        var trimmed = prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return prompt;
        }

        if (LooksLikeWeatherWithoutLocation(trimmed) &&
            TryGetValue("weather.default_location", out var defaultWeatherLocation) &&
            !string.IsNullOrWhiteSpace(defaultWeatherLocation))
        {
            return $"{trimmed}{Environment.NewLine}Use saved default weather location: {defaultWeatherLocation}.";
        }

        return prompt;
    }

    public string BuildPrompt()
    {
        EnsureLoaded();
        var entries = _store.Entries
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            return "No long-term memory saved.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Long-term memory:");
        foreach (var entry in entries)
        {
            builder.Append("- ")
                .Append(entry.Key)
                .Append(" = ")
                .Append(entry.Value)
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public async Task RememberAsync(string key, string value, string source, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var normalizedKey = key.Trim();
            var existing = _store.Entries.FirstOrDefault(entry => string.Equals(entry.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _store.Entries.Add(new MemoryEntry
                {
                    Key = normalizedKey,
                    Value = value.Trim(),
                    Source = source.Trim(),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.Value = value.Trim();
                existing.Source = source.Trim();
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            await SaveLockedAsync(cancellationToken);
            StartupLogService.Info($"Long-term memory updated: {normalizedKey} = {value.Trim()}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ForgetAsync(string key, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _store.Entries.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            await SaveLockedAsync(cancellationToken);
            StartupLogService.Info($"Long-term memory removed: {key}");
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool TryGetValue(string key, out string value)
    {
        EnsureLoaded();
        var entry = _store.Entries.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        value = entry?.Value ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(MemoryRoot);
        await File.WriteAllTextAsync(MemoryPath, JsonSerializer.Serialize(_store, JsonOptions), cancellationToken);
    }

    private static bool TryExtractWeatherLocation(string prompt, out string location)
    {
        string[] patterns =
        [
            @"^\s*погода(?:\s+сегодня|\s+сейчас)?(?:\s+в)?\s+(?<location>[^,.!?]+?)\s*$",
            @"^\s*(?:какая|покажи|скажи)?\s*погода(?:\s+сегодня|\s+сейчас)?(?:\s+в)?\s+(?<location>[^,.!?]+?)\s*$",
            @"^\s*weather(?:\s+in)?\s+(?<location>[^,.!?]+?)\s*$"
        ];

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(prompt, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                location = match.Groups["location"].Value.Trim();
                return !string.IsNullOrWhiteSpace(location);
            }
        }

        location = string.Empty;
        return false;
    }

    private static bool TryExtractNamedFact(string prompt, out string key, out string value)
    {
        var mappings = new (string Pattern, string Key)[]
        {
            (@"^\s*меня\s+зовут\s+(?<value>[^,.!?]+)", "user.name"),
            (@"^\s*my\s+name\s+is\s+(?<value>[^,.!?]+)", "user.name"),
            (@"^\s*я\s+живу\s+в\s+(?<value>[^,.!?]+)", "user.city"),
            (@"^\s*мой\s+город\s+(?<value>[^,.!?]+)", "user.city"),
            (@"^\s*мой\s+ник\s+(?<value>[^,.!?]+)", "user.nickname"),
            (@"^\s*мой\s+email\s+(?<value>[^,.!?]+)", "user.email"),
            (@"^\s*по\s+умолчанию\s+(?<value>[^,.!?]+)", "user.default.preference")
        };

        foreach (var (pattern, factKey) in mappings)
        {
            var match = Regex.Match(prompt, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                key = factKey;
                value = match.Groups["value"].Value.Trim();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool TryExtractExplicitMemory(string prompt, out string value)
    {
        var match = Regex.Match(prompt, @"^\s*запомни\s+(?<value>.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"^\s*remember\s+(?<value>.+)$", RegexOptions.IgnoreCase);
        }

        value = match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string BuildFreeformMemoryKey(string value)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
        var suffix = Convert.ToHexString(hashBytes[..6]).ToLowerInvariant();
        return $"note.{suffix}";
    }

    private static bool LooksLikeWeatherWithoutLocation(string prompt)
    {
        if (!Regex.IsMatch(prompt, @"\b(погода|weather)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return !TryExtractWeatherLocation(prompt, out _);
    }
}

public sealed class LongTermMemoryStore
{
    [JsonPropertyName("entries")]
    public List<MemoryEntry> Entries { get; set; } = [];
}

public sealed class MemoryEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
