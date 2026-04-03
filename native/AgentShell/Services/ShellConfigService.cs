using AgentShell.Models;
using System.Text.Json;

namespace AgentShell.Services;

public sealed class ShellConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ShellConfig Current { get; private set; } = new();

    public string ConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopAIAgent");

    public string NativeConfigPath => Path.Combine(ConfigRoot, "native-shell.json");

    public void EnsureLoaded()
    {
        Directory.CreateDirectory(ConfigRoot);

        if (!File.Exists(NativeConfigPath))
        {
            Current = new ShellConfig();
            Save();
            return;
        }

        var raw = File.ReadAllText(NativeConfigPath);
        Current = JsonSerializer.Deserialize<ShellConfig>(raw, JsonOptions) ?? new ShellConfig();
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(ConfigRoot);
        await File.WriteAllTextAsync(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigRoot);
        File.WriteAllText(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
