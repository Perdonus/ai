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

    public async Task EnsureLoadedAsync()
    {
        Directory.CreateDirectory(ConfigRoot);

        if (!File.Exists(NativeConfigPath))
        {
            Current = new ShellConfig();
            await SaveAsync();
            return;
        }

        await using var stream = File.OpenRead(NativeConfigPath);
        Current = await JsonSerializer.DeserializeAsync<ShellConfig>(stream, JsonOptions) ?? new ShellConfig();
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(ConfigRoot);
        await File.WriteAllTextAsync(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
