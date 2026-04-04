using AgentShell.Models;

namespace AgentShell.Services;

public static class ModelCapabilityService
{
    public static bool SupportsThinking(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        if (string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        return normalized.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("reason", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("r1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("qwq", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("o3", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("o4", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gpt-5", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase);
    }

    public static ModelChoice ToChoice(ProviderDescriptor provider, string modelId)
    {
        return new ModelChoice
        {
            Id = modelId,
            DisplayName = modelId,
            ProviderId = provider.Id,
            SupportsThinking = SupportsThinking(provider.Id, modelId)
        };
    }

    public static ModelChoice ToLocalChoice(LocalModelConfig model)
    {
        return new ModelChoice
        {
            Id = model.Id,
            DisplayName = string.IsNullOrWhiteSpace(model.Name) ? Path.GetFileNameWithoutExtension(model.ModelPath) : model.Name,
            ProviderId = "local",
            SupportsThinking = model.SupportsThinking
        };
    }
}
