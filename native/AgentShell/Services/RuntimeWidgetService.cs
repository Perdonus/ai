using System.Diagnostics;
using System.Text.Json;

namespace AgentShell.Services;

public sealed class RuntimeWidgetService
{
    public async Task<string> TestLaunchAsync(string widgetRootPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var manifestPath = Path.Combine(widgetRootPath, "widget.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Widget manifest not found: {manifestPath}");
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = document.RootElement;
        var widgetId = ReadString(root, "id") ?? Path.GetFileName(widgetRootPath);
        var kind = (ReadString(root, "kind") ?? "html").Trim().ToLowerInvariant();

        return kind switch
        {
            "native" => LaunchNativeWidget(widgetRootPath, root, widgetId),
            "html" => LaunchHtmlWidget(widgetRootPath, root, widgetId),
            _ => throw new InvalidOperationException($"Unsupported widget kind: {kind}")
        };
    }

    private static string LaunchNativeWidget(string widgetRootPath, JsonElement root, string widgetId)
    {
        var entry = ReadString(root, "entry_executable") ?? ReadString(root, "entry");
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidOperationException($"Widget {widgetId} does not declare entry_executable.");
        }

        var executablePath = Path.IsPathFullyQualified(entry)
            ? entry
            : Path.Combine(widgetRootPath, entry);

        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException($"Widget executable not found: {executablePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? widgetRootPath,
            UseShellExecute = true
        };

        foreach (var argument in ReadStringArray(root, "launch_arguments"))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
        return $"Виджет {widgetId} запущен.";
    }

    private static string LaunchHtmlWidget(string widgetRootPath, JsonElement root, string widgetId)
    {
        var entry = ReadString(root, "entry_html");
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidOperationException($"Widget {widgetId} does not declare entry_html.");
        }

        var htmlPath = Path.Combine(widgetRootPath, entry);
        if (!File.Exists(htmlPath))
        {
            throw new InvalidOperationException($"Widget html entry not found: {htmlPath}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = htmlPath,
            WorkingDirectory = widgetRootPath,
            UseShellExecute = true
        });

        return $"Виджет {widgetId} открыт.";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
