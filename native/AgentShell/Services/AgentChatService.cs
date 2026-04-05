using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentChatService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(36)
    ];

    private static readonly IReadOnlyDictionary<string, TimeSpan> RequestSpacingByProvider = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
    {
        ["sosiskibot"] = TimeSpan.FromSeconds(10),
        ["openrouter"] = TimeSpan.FromSeconds(8),
        ["huggingface"] = TimeSpan.FromSeconds(8),
        ["gemini"] = TimeSpan.FromSeconds(6),
        ["mistral"] = TimeSpan.FromSeconds(6),
        ["openai"] = TimeSpan.FromSeconds(5),
        ["local"] = TimeSpan.Zero
    };
    private static readonly TimeSpan RateLimitSafetyPad = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProviderLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderAvailableAt = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    private readonly LocalLlamaService _localLlama = App.LocalLlama;

    public ModelRoute ResolveVisionRoute(ShellConfig config)
    {
        return config.Models.UseSeparateVision && !string.IsNullOrWhiteSpace(config.Models.Vision.Model)
            ? config.Models.Vision
            : config.Models.Primary;
    }

    public ModelRoute? ResolveAnalysisRoute(ShellConfig config)
    {
        return config.Models.UseSeparateAnalysis && !string.IsNullOrWhiteSpace(config.Models.Analysis.Model)
            ? config.Models.Analysis
            : null;
    }

    public bool ShouldShowPrimaryThinking(ShellConfig config) => config.Models.PrimaryThinking || config.Models.PrimaryMcpThinking;

    public bool ShouldShowAnalysisThinking(ShellConfig config) => config.Models.AnalysisThinking || config.Models.AnalysisMcpThinking;

    public bool CanLikelyUseImages(ModelRoute route)
    {
        var providerId = route.Provider.Trim().ToLowerInvariant();
        var model = route.Model.Trim().ToLowerInvariant();
        if (providerId == "gemini")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (model.Contains("codestral", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("embed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return model.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("pixtral", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("omni", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("4o", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("mistral-large", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> RequestTextAsync(
        ShellConfig config,
        ModelRoute route,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken,
        ScreenSnapshot? screenshot = null)
    {
        var provider = ResolveProvider(route, config, out var apiKey);
        var throttleKey = BuildThrottleKey(provider);
        StartupLogService.Info($"Running model request via {provider.Id}/{route.Model}.");
        var baseUrl = await ResolveBaseUrlAsync(provider, config, route.Model, cancellationToken);

        return provider.Id switch
        {
            "gemini" => await RequestGeminiAsync(provider, throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            "huggingface" => await RequestOpenAiCompatibleAsync("https://router.huggingface.co/v1", throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            _ => await RequestOpenAiCompatibleAsync(baseUrl, throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken)
        };
    }

    public async Task<string> RequestJsonAsync(
        ShellConfig config,
        ModelRoute route,
        string systemPrompt,
        string userPrompt,
        ScreenSnapshot? screenshot,
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider(route, config, out var apiKey);
        var throttleKey = BuildThrottleKey(provider);
        StartupLogService.Info($"Running agent step via {provider.Id}/{route.Model}. screenshot={screenshot is not null}");
        var baseUrl = await ResolveBaseUrlAsync(provider, config, route.Model, cancellationToken);
        var raw = provider.Id switch
        {
            "gemini" => await RequestGeminiAsync(provider, throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            "huggingface" => await RequestOpenAiCompatibleAsync("https://router.huggingface.co/v1", throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            _ => await RequestOpenAiCompatibleAsync(baseUrl, throttleKey, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken)
        };

        return ExtractJsonObject(raw);
    }

    private ProviderDescriptor ResolveProvider(ModelRoute route, ShellConfig config, out string apiKey)
    {
        var provider = ProviderCatalog.All.FirstOrDefault(item => item.Id == route.Provider)
            ?? throw new InvalidOperationException($"Unknown provider: {route.Provider}");

        apiKey = config.Providers.GetValueOrDefault(provider.Id)?.ApiKey ?? string.Empty;
        if (provider.Id != "local" && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API key is missing for provider {provider.Name}.");
        }

        if (string.IsNullOrWhiteSpace(route.Model))
        {
            throw new InvalidOperationException($"Model is missing for provider {provider.Name}.");
        }

        return provider;
    }

    private async Task<string> ResolveBaseUrlAsync(
        ProviderDescriptor provider,
        ShellConfig config,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (provider.Id != "local")
        {
            return provider.BaseUrl;
        }

        return await _localLlama.EnsureServerAsync(config, modelId, cancellationToken);
    }

    private async Task<string> RequestOpenAiCompatibleAsync(
        string baseUrl,
        string throttleKey,
        string model,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        ScreenSnapshot? screenshot,
        CancellationToken cancellationToken)
    {
        object userContent = screenshot is null
            ? userPrompt
            : new object[]
            {
                new { type = "text", text = userPrompt },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{screenshot.PngBase64}" } }
            };

        var body = await SendRequestWithBackoffAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model,
                        temperature = 0.2,
                        messages = new object[]
                        {
                            new { role = "system", content = systemPrompt },
                            new { role = "user", content = userContent }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json");
                return request;
            },
            $"{baseUrl.TrimEnd('/')}/chat/completions",
            throttleKey,
            cancellationToken);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Provider response does not contain choices.");
        }

        var content = choices[0].GetProperty("message").GetProperty("content");
        return ParseContent(content);
    }

    private async Task<string> RequestGeminiAsync(
        ProviderDescriptor provider,
        string throttleKey,
        string model,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        ScreenSnapshot? screenshot,
        CancellationToken cancellationToken)
    {
        var url = $"{provider.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var parts = new List<object>
        {
            new { text = $"{systemPrompt}\n\n{userPrompt}" }
        };

        if (screenshot is not null)
        {
            parts.Add(new
            {
                inlineData = new
                {
                    mimeType = "image/png",
                    data = screenshot.PngBase64
                }
            });
        }

        var body = await SendRequestWithBackoffAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        contents = new object[]
                        {
                            new
                            {
                                role = "user",
                                parts
                            }
                        },
                        generationConfig = new
                        {
                            temperature = 0.2
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            },
            url,
            throttleKey,
            cancellationToken);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini response does not contain candidates.");
        }

        if (!candidates[0].TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var responseParts) ||
            responseParts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Gemini response does not contain content parts.");
        }

        return string.Join(
            "\n",
            responseParts.EnumerateArray()
                .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)
                .OfType<string>());
    }

    private async Task<string> SendRequestWithBackoffAsync(
        Func<HttpRequestMessage> requestFactory,
        string endpointLabel,
        string throttleKey,
        CancellationToken cancellationToken)
    {
        var providerLock = ProviderLocks.GetOrAdd(throttleKey, static _ => new SemaphoreSlim(1, 1));
        await providerLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
            {
                await WaitForProviderAvailabilityAsync(throttleKey, cancellationToken);

                using var request = requestFactory();
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    ReserveProvider(throttleKey, GetRequestSpacing(throttleKey));
                    return body;
                }

                var delay = GetRetryDelay(response, body, attempt);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var cooldown = delay + GetRequestSpacing(throttleKey) + RateLimitSafetyPad;
                    ReserveProvider(throttleKey, cooldown);
                    if (attempt < RetryDelays.Length)
                    {
                        StartupLogService.Warn(
                            $"Provider rate-limited on {endpointLabel}. provider={throttleKey}. retry_in={cooldown.TotalSeconds:0}s. attempt={attempt + 1}");
                        continue;
                    }

                    throw new ProviderRateLimitException(throttleKey, cooldown, body);
                }

                if (attempt < RetryDelays.Length && IsRetryable(response.StatusCode))
                {
                    ReserveProvider(throttleKey, delay + GetRequestSpacing(throttleKey));
                    StartupLogService.Warn(
                        $"Provider throttled or failed temporarily on {endpointLabel}. provider={throttleKey}. status={(int)response.StatusCode}. retry_in={delay.TotalSeconds:0}s. attempt={attempt + 1}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var prefix = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Provider request hit rate limit"
                    : "Provider request failed";
                throw new InvalidOperationException($"{prefix}: {(int)response.StatusCode} {body}");
            }
        }
        finally
        {
            providerLock.Release();
        }

        throw new InvalidOperationException("Provider request retry loop ended unexpectedly.");
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout ||
               numeric >= 500;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, string body, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return ClampDelay(delta);
        }

        if (retryAfter?.Date is { } retryDate)
        {
            var candidate = retryDate - DateTimeOffset.UtcNow;
            if (candidate > TimeSpan.Zero)
            {
                return ClampDelay(candidate);
            }
        }

        var bodyDelay = TryExtractRetryDelayFromBody(body);
        if (bodyDelay is not null)
        {
            return ClampDelay(bodyDelay.Value);
        }

        return RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
    }

    private static TimeSpan? TryExtractRetryDelayFromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var retryAfterMatch = Regex.Match(
            body,
            "(?:retry_after|retryAfter|try again in|подожд(?:и|ать)\\s+примерно)\\D{0,10}(\\d{1,4})",
            RegexOptions.IgnoreCase);
        if (retryAfterMatch.Success &&
            int.TryParse(retryAfterMatch.Groups[1].Value, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(2))
        {
            return TimeSpan.FromSeconds(2);
        }

        return delay > TimeSpan.FromSeconds(90)
            ? TimeSpan.FromSeconds(90)
            : delay;
    }

    private static string BuildThrottleKey(ProviderDescriptor provider)
    {
        return provider.Id.Trim().ToLowerInvariant();
    }

    private static TimeSpan GetRequestSpacing(string throttleKey)
    {
        if (string.Equals(throttleKey, "local", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        var minimum = RequestSpacingByProvider.TryGetValue(throttleKey, out var spacing)
            ? spacing
            : TimeSpan.FromSeconds(5);
        var minimumSeconds = Math.Max(5, (int)Math.Ceiling(minimum.TotalSeconds));
        var randomizedSeconds = Random.Shared.Next(minimumSeconds, 21);
        return TimeSpan.FromSeconds(randomizedSeconds);
    }

    private static async Task WaitForProviderAvailabilityAsync(string throttleKey, CancellationToken cancellationToken)
    {
        while (ProviderAvailableAt.TryGetValue(throttleKey, out var readyAt))
        {
            var delay = readyAt - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
            {
                ProviderAvailableAt.TryRemove(throttleKey, out _);
                return;
            }

            StartupLogService.Info($"Waiting for provider cooldown. provider={throttleKey}; wait={delay.TotalSeconds:0.0}s");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static void ReserveProvider(string throttleKey, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var until = DateTimeOffset.UtcNow + ClampDelay(delay);
        ProviderAvailableAt.AddOrUpdate(
            throttleKey,
            until,
            (_, current) => current > until ? current : until);
    }

    private static string ParseContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n",
                content.EnumerateArray()
                    .Select(item =>
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            return item.GetString();
                        }

                        return item.TryGetProperty("text", out var text) ? text.GetString() : null;
                    })
                    .OfType<string>()),
            _ => string.Empty
        };
    }

    private static string ExtractJsonObject(string raw)
    {
        var trimmed = raw.Trim();
        var fenced = trimmed.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var start = fenced.IndexOf('{');
        var end = fenced.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Model did not return JSON: {raw}");
        }

        return fenced[start..(end + 1)];
    }
}

public sealed class ProviderRateLimitException(string providerKey, TimeSpan retryAfter, string body)
    : InvalidOperationException($"Provider {providerKey} is rate limited. Retry after about {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds. {body}")
{
    public string ProviderKey { get; } = providerKey;
    public TimeSpan RetryAfter { get; } = retryAfter;
}
