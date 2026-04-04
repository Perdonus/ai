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

    public string BackupRoot => Path.Combine(ConfigRoot, "backups");

    public string ExportRoot => Path.Combine(ConfigRoot, "exports");

    public void EnsureLoaded()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(ExportRoot);

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
        Directory.CreateDirectory(BackupRoot);
        if (File.Exists(NativeConfigPath))
        {
            await CreateBackupSnapshotAsync("autosave");
        }

        await File.WriteAllTextAsync(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(BackupRoot);
        File.WriteAllText(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public async Task<string> CreateBackupSnapshotAsync(string label = "manual")
    {
        Directory.CreateDirectory(BackupRoot);
        var path = Path.Combine(
            BackupRoot,
            $"native-shell-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Current, JsonOptions));
        return path;
    }

    public async Task<string> ExportAsync()
    {
        Directory.CreateDirectory(ExportRoot);
        var path = Path.Combine(
            ExportRoot,
            $"native-shell-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Current, JsonOptions));
        return path;
    }

    public async Task<string?> RestoreLatestBackupAsync()
    {
        if (!Directory.Exists(BackupRoot))
        {
            return null;
        }

        var latest = new DirectoryInfo(BackupRoot)
            .GetFiles("native-shell-*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        var raw = await File.ReadAllTextAsync(latest.FullName);
        Current = JsonSerializer.Deserialize<ShellConfig>(raw, JsonOptions) ?? new ShellConfig();
        await File.WriteAllTextAsync(NativeConfigPath, JsonSerializer.Serialize(Current, JsonOptions));
        return latest.FullName;
    }
}
