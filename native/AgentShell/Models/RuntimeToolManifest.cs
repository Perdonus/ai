namespace AgentShell.Models;

public sealed class RuntimeToolManifest
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = "command";

    public string Entry { get; set; } = string.Empty;

    public List<string> ArgsTemplate { get; set; } = [];

    public string SystemPrompt { get; set; } = string.Empty;

    public Dictionary<string, string> ParameterHints { get; set; } = [];

    public string RootPath { get; set; } = string.Empty;
}
