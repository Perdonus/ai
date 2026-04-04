using AgentShell.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentShell.Services;

public sealed class ModelDiscoveryService
{
    private readonly HttpClient _httpClient = new();

    public async Task<IReadOnlyList<ModelChoice>> LoadModelsAsync(ProviderDescriptor provider, string apiKey, ShellConfig config)
    {
        if (provider.Id == "local")
        {
            return config.LocalAi.Models
                .Where(model => !string.IsNullOrWhiteSpace(model.ModelPath))
                .Select(ModelCapabilityService.ToLocalChoice)
                .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        return provider.Id switch
        {
            "gemini" => await LoadGeminiModelsAsync(provider, apiKey),
            "huggingface" => await LoadHuggingFaceModelsAsync(apiKey),
            _ => await LoadOpenAiCompatibleModelsAsync(provider, apiKey)
        };
    }

    private async Task<IReadOnlyList<ModelChoice>> LoadOpenAiCompatibleModelsAsync(ProviderDescriptor provider, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{provider.BaseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .Select(model => ModelCapabilityService.ToChoice(provider, model))
            .ToList();
    }

    private async Task<IReadOnlyList<ModelChoice>> LoadGeminiModelsAsync(ProviderDescriptor provider, string apiKey)
    {
        using var response = await _httpClient.GetAsync($"{provider.BaseUrl.TrimEnd('/')}/models?key={Uri.EscapeDataString(apiKey)}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .OfType<string>()
            .Select(name => name.Replace("models/", string.Empty, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .Select(model => ModelCapabilityService.ToChoice(provider, model))
            .ToList();
    }

    private async Task<IReadOnlyList<ModelChoice>> LoadHuggingFaceModelsAsync(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/models?sort=downloads&direction=-1&limit=200");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .Select(model => ModelCapabilityService.ToChoice(new ProviderDescriptor("huggingface", "Hugging Face", "https://huggingface.co"), model))
            .ToList();
    }
}
