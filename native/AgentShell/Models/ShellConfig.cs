using System.Text.Json.Serialization;
using AgentShell.Services;

namespace AgentShell.Models;

public sealed class ShellConfig
{
    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; set; } = ProviderCatalog.CreateDefaultProviderConfig();

    [JsonPropertyName("models")]
    public ModelSettings Models { get; set; } = new();

    [JsonPropertyName("local_ai")]
    public LocalAiSettings LocalAi { get; set; } = new();
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

    [JsonPropertyName("primary_thinking")]
    public bool PrimaryThinking { get; set; }

    [JsonPropertyName("primary_mcp_thinking")]
    public bool PrimaryMcpThinking { get; set; }

    [JsonPropertyName("use_separate_analysis")]
    public bool UseSeparateAnalysis { get; set; }

    [JsonPropertyName("analysis")]
    public ModelRoute Analysis { get; set; } = new("sosiskibot", string.Empty);

    [JsonPropertyName("analysis_thinking")]
    public bool AnalysisThinking { get; set; }

    [JsonPropertyName("analysis_mcp_thinking")]
    public bool AnalysisMcpThinking { get; set; }

    [JsonPropertyName("use_separate_vision")]
    public bool UseSeparateVision { get; set; }

    [JsonPropertyName("vision")]
    public ModelRoute Vision { get; set; } = new("sosiskibot", string.Empty);
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

public sealed class ModelChoice
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public bool SupportsThinking { get; set; }

    public string ThinkingTagText => SupportsThinking ? "think" : string.Empty;

    public bool ShowThinkingTag => SupportsThinking;

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed class LocalAiSettings
{
    [JsonPropertyName("idle_unload_seconds")]
    public int IdleUnloadSeconds { get; set; } = 60;

    [JsonPropertyName("models")]
    public List<LocalModelConfig> Models { get; set; } = [];
}

public sealed class LocalModelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model_path")]
    public string ModelPath { get; set; } = string.Empty;

    [JsonPropertyName("context_size")]
    public int ContextSize { get; set; } = 4096;

    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; }

    [JsonPropertyName("supports_thinking")]
    public bool SupportsThinking { get; set; }

    public string ContextSummary => $"Context: {ContextSize} · GPU layers: {GpuLayers}";
}

public sealed class RuntimeItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool HasSettings { get; set; }

    public bool SupportsDataInput { get; set; }

    public string DataInputHint { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;
}
