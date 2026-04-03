using System.Text.Json.Serialization;
using AgentShell.Services;

namespace AgentShell.Models;

public sealed class ShellConfig
{
    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; set; } = ProviderCatalog.CreateDefaultProviderConfig();

    [JsonPropertyName("models")]
    public ModelSettings Models { get; set; } = new();
}

public sealed class ProviderConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class ModelSettings
{
    [JsonPropertyName("primary")]
    public ModelRoute Primary { get; set; } = new("sosiskibot", string.Empty);

    [JsonPropertyName("use_separate_analysis")]
    public bool UseSeparateAnalysis { get; set; }

    [JsonPropertyName("analysis")]
    public ModelRoute Analysis { get; set; } = new("sosiskibot", string.Empty);

    [JsonPropertyName("use_separate_vision")]
    public bool UseSeparateVision { get; set; }

    [JsonPropertyName("vision")]
    public ModelRoute Vision { get; set; } = new("sosiskibot", string.Empty);

    [JsonPropertyName("use_separate_ocr")]
    public bool UseSeparateOcr { get; set; }

    [JsonPropertyName("ocr")]
    public ModelRoute Ocr { get; set; } = new("sosiskibot", string.Empty);
}

public sealed class ModelRoute(string provider, string model)
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = provider;

    [JsonPropertyName("model")]
    public string Model { get; set; } = model;
}

public sealed class ProviderDescriptor(string id, string name, string baseUrl)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string BaseUrl { get; } = baseUrl;
}

public sealed class RuntimeItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool HasSettings { get; set; }

    public bool SupportsDataInput { get; set; }

    public string RootPath { get; set; } = string.Empty;
}
