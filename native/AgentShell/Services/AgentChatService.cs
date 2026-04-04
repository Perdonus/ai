using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentChatService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

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

    public bool ShouldShowPrimaryThinking(ShellConfig config) => config.Models.PrimaryThinking;

    public bool ShouldShowAnalysisThinking(ShellConfig config) => config.Models.AnalysisThinking;

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
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider(route, config, out var apiKey);
        StartupLogService.Info($"Running model request via {provider.Id}/{route.Model}.");

        return provider.Id switch
        {
            "gemini" => await RequestGeminiAsync(provider, route.Model, apiKey, systemPrompt, userPrompt, null, cancellationToken),
            "huggingface" => await RequestOpenAiCompatibleAsync("https://router.huggingface.co/v1", route.Model, apiKey, systemPrompt, userPrompt, null, cancellationToken),
            _ => await RequestOpenAiCompatibleAsync(provider.BaseUrl, route.Model, apiKey, systemPrompt, userPrompt, null, cancellationToken)
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
        StartupLogService.Info($"Running agent step via {provider.Id}/{route.Model}. screenshot={screenshot is not null}");
        var raw = provider.Id switch
        {
            "gemini" => await RequestGeminiAsync(provider, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            "huggingface" => await RequestOpenAiCompatibleAsync("https://router.huggingface.co/v1", route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken),
            _ => await RequestOpenAiCompatibleAsync(provider.BaseUrl, route.Model, apiKey, systemPrompt, userPrompt, screenshot, cancellationToken)
        };

        return ExtractJsonObject(raw);
    }

    private ProviderDescriptor ResolveProvider(ModelRoute route, ShellConfig config, out string apiKey)
    {
        var provider = ProviderCatalog.All.FirstOrDefault(item => item.Id == route.Provider)
            ?? throw new InvalidOperationException($"Unknown provider: {route.Provider}");

        apiKey = config.Providers.GetValueOrDefault(provider.Id)?.ApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API key is missing for provider {provider.Name}.");
        }

        if (string.IsNullOrWhiteSpace(route.Model))
        {
            throw new InvalidOperationException($"Model is missing for provider {provider.Name}.");
        }

        return provider;
    }

    private async Task<string> RequestOpenAiCompatibleAsync(
        string baseUrl,
        string model,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        ScreenSnapshot? screenshot,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        object userContent = screenshot is null
            ? userPrompt
            : new object[]
            {
                new { type = "text", text = userPrompt },
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{screenshot.PngBase64}" } }
            };

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

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Provider request failed: {(int)response.StatusCode} {body}");
        }

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

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Gemini request failed: {(int)response.StatusCode} {body}");
        }

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
