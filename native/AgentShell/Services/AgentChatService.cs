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

    public async Task<AgentTurnResult> RunAsync(ShellConfig config, string prompt, CancellationToken cancellationToken)
    {
        var primaryRoute = config.Models.Primary;
        if (string.IsNullOrWhiteSpace(primaryRoute.Model))
        {
            return new AgentTurnResult(string.Empty, string.Empty, "Select a primary model first.");
        }

        string thinking = string.Empty;
        if (config.Models.UseSeparateAnalysis && !string.IsNullOrWhiteSpace(config.Models.Analysis.Model))
        {
            thinking = await RequestReasoningAsync(config, config.Models.Analysis, prompt, cancellationToken);
        }
        else if (config.Models.PrimaryThinking)
        {
            thinking = await RequestReasoningAsync(config, primaryRoute, prompt, cancellationToken);
        }

        var answer = await RequestAnswerAsync(config, primaryRoute, prompt, thinking, cancellationToken);
        return new AgentTurnResult(thinking, answer, string.Empty);
    }

    private async Task<string> RequestReasoningAsync(
        ShellConfig config,
        ModelRoute route,
        string prompt,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "You are the reasoning engine for a desktop AI assistant. Think clearly and briefly. " +
            "Return only short plain-text reasoning notes in Russian, no markdown, no XML, no code fences.";
        return await RequestTextAsync(config, route, systemPrompt, prompt, cancellationToken);
    }

    private async Task<string> RequestAnswerAsync(
        ShellConfig config,
        ModelRoute route,
        string prompt,
        string thinking,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "You are a concise desktop AI assistant. Answer in Russian unless the user explicitly asks otherwise. " +
            "Be practical and direct.";

        var userPrompt = string.IsNullOrWhiteSpace(thinking)
            ? prompt
            : $"User request:\n{prompt}\n\nReasoning notes:\n{thinking}\n\nUse the reasoning notes to produce the final answer.";

        return await RequestTextAsync(config, route, systemPrompt, userPrompt, cancellationToken);
    }

    private async Task<string> RequestTextAsync(
        ShellConfig config,
        ModelRoute route,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var provider = ProviderCatalog.All.FirstOrDefault(item => item.Id == route.Provider)
            ?? throw new InvalidOperationException($"Unknown provider: {route.Provider}");

        var apiKey = config.Providers.GetValueOrDefault(provider.Id)?.ApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"API key is missing for provider {provider.Name}.");
        }

        if (string.IsNullOrWhiteSpace(route.Model))
        {
            throw new InvalidOperationException($"Model is missing for provider {provider.Name}.");
        }

        return provider.Id switch
        {
            "gemini" => await RequestGeminiAsync(provider, route.Model, apiKey, systemPrompt, userPrompt, cancellationToken),
            "huggingface" => await RequestOpenAiCompatibleAsync("https://router.huggingface.co/v1", route.Model, apiKey, systemPrompt, userPrompt, cancellationToken),
            _ => await RequestOpenAiCompatibleAsync(provider.BaseUrl, route.Model, apiKey, systemPrompt, userPrompt, cancellationToken)
        };
    }

    private async Task<string> RequestOpenAiCompatibleAsync(
        string baseUrl,
        string model,
        string apiKey,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0.3,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
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
        CancellationToken cancellationToken)
    {
        var url = $"{provider.BaseUrl.TrimEnd('/')}/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var fullPrompt = $"{systemPrompt}\n\nUser request:\n{userPrompt}";
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
                            parts = new object[]
                            {
                                new { text = fullPrompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.3
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
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Gemini response does not contain content parts.");
        }

        return string.Join(
            "\n",
            parts.EnumerateArray()
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
}

public sealed record AgentTurnResult(string Thinking, string Answer, string Error);
