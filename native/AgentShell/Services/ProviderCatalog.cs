using AgentShell.Models;

namespace AgentShell.Services;

public static class ProviderCatalog
{
    private static readonly IReadOnlyList<ProviderDescriptor> Providers =
    [
        new("sosiskibot", "SosiskiBot", "https://sosiskibot.ru/api/v1"),
        new("openai", "OpenAI", "https://api.openai.com/v1"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1"),
        new("huggingface", "Hugging Face", "https://huggingface.co"),
        new("local", "Локальные ИИ", "http://127.0.0.1:39230/v1")
    ];

    public static IReadOnlyList<ProviderDescriptor> All => Providers;

    public static IReadOnlyList<ProviderDescriptor> RemoteOnly => Providers.Where(provider => provider.Id != "local").ToList();

    public static Dictionary<string, ProviderConfig> CreateDefaultProviderConfig()
    {
        return Providers.ToDictionary(provider => provider.Id, _ => new ProviderConfig());
    }
}
