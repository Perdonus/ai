using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class RuntimeWidgetService
{
    private const string WidgetReleaseZipUrl = "https://codeload.github.com/Perdonus/ai/zip/refs/heads/dist-widgets";
    private readonly RuntimeCatalogService _catalog = App.RuntimeCatalog;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public async Task<string> LaunchByIdAsync(string widgetId, CancellationToken cancellationToken)
    {
        var widget = await ResolveWidgetAsync(widgetId, cancellationToken);
        var readyRoot = await EnsureLocalWidgetRootAsync(widget, cancellationToken);
        return await TestLaunchAsync(readyRoot, cancellationToken);
    }

    public async Task<string> SendDataAsync(string widgetId, string payload, CancellationToken cancellationToken)
    {
        var widget = await ResolveWidgetAsync(widgetId, cancellationToken);
        if (!widget.SupportsDataInput)
        {
            throw new InvalidOperationException($"Widget {widgetId} does not accept data input.");
        }

        var readyRoot = await EnsureLocalWidgetRootAsync(widget, cancellationToken);
        var targetPath = Path.Combine(readyRoot, "widget-input.json");
        await File.WriteAllTextAsync(targetPath, payload, cancellationToken);
        return $"Widget {widgetId} data updated.";
    }

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
        widgetRootPath = await EnsureLaunchableRootAsync(widgetRootPath, root, widgetId, kind, cancellationToken);

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

    private async Task<RuntimeItem> ResolveWidgetAsync(string widgetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var widget = (await _catalog.LoadWidgetsAsync())
            .FirstOrDefault(item => string.Equals(item.Id, widgetId, StringComparison.OrdinalIgnoreCase));

        return widget ?? throw new InvalidOperationException($"Widget not found: {widgetId}");
    }

    private async Task<string> EnsureLocalWidgetRootAsync(RuntimeItem widget, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(widget.RootPath, "widget.json");
        if (!File.Exists(manifestPath))
        {
            StartupLogService.Warn($"Widget manifest missing for {widget.Id}. Attempting remote widget install.");
            return await InstallWidgetPackageAsync(widget.Id, cancellationToken);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = document.RootElement;
        var kind = (ReadString(root, "kind") ?? "html").Trim().ToLowerInvariant();
        return await EnsureLaunchableRootAsync(widget.RootPath, root, widget.Id, kind, cancellationToken);
    }

    private async Task<string> EnsureLaunchableRootAsync(
        string widgetRootPath,
        JsonElement manifestRoot,
        string widgetId,
        string kind,
        CancellationToken cancellationToken)
    {
        if (kind != "native")
        {
            return widgetRootPath;
        }

        var entry = ReadString(manifestRoot, "entry_executable") ?? ReadString(manifestRoot, "entry");
        if (string.IsNullOrWhiteSpace(entry))
        {
            return widgetRootPath;
        }

        var localExecutable = Path.IsPathFullyQualified(entry)
            ? entry
            : Path.Combine(widgetRootPath, entry);
        if (File.Exists(localExecutable))
        {
            return widgetRootPath;
        }

        StartupLogService.Warn($"Widget executable missing for {widgetId}. Attempting remote widget install.");
        return await InstallWidgetPackageAsync(widgetId, cancellationToken);
    }

    private async Task<string> InstallWidgetPackageAsync(string widgetId, CancellationToken cancellationToken)
    {
        var widgetsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopAIAgent",
            "widgets");
        var targetRoot = Path.Combine(widgetsRoot, widgetId);
        var tempRoot = Path.Combine(Path.GetTempPath(), "DesktopAIAgent", "widget-cache");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(widgetsRoot);

        var archivePath = Path.Combine(tempRoot, "dist-widgets.zip");
        using (var response = await _httpClient.GetAsync(WidgetReleaseZipUrl, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var archiveStream = File.Create(archivePath);
            await responseStream.CopyToAsync(archiveStream, cancellationToken);
        }

        var prefix = $"ai-dist-widgets/widgets/{widgetId}/";
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, true);
        }

        Directory.CreateDirectory(targetRoot);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = entry.FullName[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(targetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
        }

        var manifestPath = Path.Combine(targetRoot, "widget.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Widget package {widgetId} was not found in dist-widgets release.");
        }

        StartupLogService.Info($"Widget package installed from remote release: {widgetId}");
        return targetRoot;
    }
}
