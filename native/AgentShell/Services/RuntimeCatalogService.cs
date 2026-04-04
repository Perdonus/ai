using AgentShell.Models;
using System.Text.Json;

namespace AgentShell.Services;

public sealed class RuntimeCatalogService
{
    public async Task<IReadOnlyList<RuntimeItem>> LoadWidgetsAsync()
    {
        return await LoadRuntimeItemsAsync("widgets", "widget.json");
    }

    public async Task<IReadOnlyList<RuntimeItem>> LoadToolsAsync()
    {
        return await LoadRuntimeItemsAsync("tools", "tool.json");
    }

    private static async Task<IReadOnlyList<RuntimeItem>> LoadRuntimeItemsAsync(string folderName, string manifestName)
    {
        List<RuntimeItem> items = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RuntimeRoots(folderName))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                var manifestPath = Path.Combine(directory, manifestName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
                    var rootElement = document.RootElement;
                    var item = new RuntimeItem
                    {
                        Id = ReadString(rootElement, "id") ?? Path.GetFileName(directory),
                        Name = ReadString(rootElement, "name") ?? Path.GetFileName(directory),
                        Description = ReadString(rootElement, "description") ?? string.Empty,
                        HasSettings = rootElement.TryGetProperty("settings_schema", out _),
                        SupportsDataInput = rootElement.TryGetProperty("accepts_data_input", out var acceptsData) && acceptsData.GetBoolean(),
                        RootPath = directory
                    };

                    if (!seenIds.Add(item.Id))
                    {
                        continue;
                    }

                    items.Add(item);
                }
                catch
                {
                }
            }
        }

        return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> RuntimeRoots(string folderName)
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopAIAgent", folderName);
        yield return Path.Combine("Z:\\ai", folderName);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
