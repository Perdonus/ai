using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class RuntimeToolService
{
    public async Task<IReadOnlyList<RuntimeToolManifest>> LoadAsync()
    {
        List<RuntimeToolManifest> tools = [];
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RuntimeRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                var manifestPath = Path.Combine(directory, "tool.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
                    var rootElement = document.RootElement;
                    var tool = new RuntimeToolManifest
                    {
                        Id = ReadString(rootElement, "id") ?? Path.GetFileName(directory),
                        Name = ReadString(rootElement, "name") ?? Path.GetFileName(directory),
                        Description = ReadString(rootElement, "description") ?? string.Empty,
                        Kind = ReadString(rootElement, "kind") ?? "command",
                        Entry = ReadString(rootElement, "entry") ?? string.Empty,
                        ArgsTemplate = ReadStringArray(rootElement, "args_template"),
                        SystemPrompt = ReadString(rootElement, "system_prompt") ?? string.Empty,
                        ParameterHints = ReadStringMap(rootElement, "parameter_hints"),
                        RootPath = directory
                    };

                    if (!seenIds.Add(tool.Id))
                    {
                        continue;
                    }

                    tools.Add(tool);
                }
                catch (Exception ex)
                {
                    StartupLogService.Warn($"Skipping runtime tool manifest {manifestPath}: {ex.Message}");
                }
            }
        }

        return tools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string BuildToolPrompt(IReadOnlyList<RuntimeToolManifest> tools)
    {
        if (tools.Count == 0)
        {
            return "No runtime tools are installed.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Installed runtime tools:");
        foreach (var tool in tools)
        {
            builder.Append("- run_tool target=\"")
                .Append(tool.Id)
                .Append("\"");

            if (tool.ParameterHints.Count > 0)
            {
                builder.Append(" arguments={");
                builder.Append(string.Join(", ", tool.ParameterHints.Select(pair => $"\"{pair.Key}\":\"{pair.Value}\"")));
                builder.Append('}');
            }

            builder.Append(": ").Append(tool.Name);
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                builder.Append(" - ").Append(tool.Description.Trim());
            }

            if (!string.IsNullOrWhiteSpace(tool.SystemPrompt))
            {
                builder.Append(" Note: ").Append(tool.SystemPrompt.Trim());
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> ExecuteAsync(string toolId, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var tool = (await LoadAsync())
            .FirstOrDefault(item => string.Equals(item.Id, toolId, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            throw new InvalidOperationException($"Runtime tool not found: {toolId}");
        }

        return tool.Kind.Trim().ToLowerInvariant() switch
        {
            "command" => await ExecuteCommandAsync(tool, arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported runtime tool kind: {tool.Kind}")
        };
    }

    private static async Task<string> ExecuteCommandAsync(
        RuntimeToolManifest tool,
        IReadOnlyDictionary<string, string>? arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveEntry(tool),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = tool.RootPath
        };

        foreach (var arg in tool.ArgsTemplate.Select(template => ApplyTemplate(template, arguments)))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        StartupLogService.Info($"Running runtime tool {tool.Id} via {startInfo.FileName}.");

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start runtime tool {tool.Id}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Tool {tool.Id} failed with exit code {process.ExitCode}: {TrimOutput(combined)}");
        }

        return string.IsNullOrWhiteSpace(combined)
            ? $"Tool {tool.Id} completed successfully."
            : TrimOutput(combined);
    }

    private static string ResolveEntry(RuntimeToolManifest tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Entry))
        {
            throw new InvalidOperationException($"Runtime tool {tool.Id} does not define an entry.");
        }

        if (Path.IsPathFullyQualified(tool.Entry))
        {
            return tool.Entry;
        }

        var localPath = Path.Combine(tool.RootPath, tool.Entry);
        return File.Exists(localPath) ? localPath : tool.Entry;
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string>? arguments)
    {
        return Regex.Replace(
            template,
            "{{\\s*([a-zA-Z0-9_\\-]+)\\s*}}",
            match =>
            {
                var key = match.Groups[1].Value;
                return arguments is not null && arguments.TryGetValue(key, out var value)
                    ? value
                    : match.Value;
            });
    }

    private static string TrimOutput(string output)
    {
        return output.Length <= 700 ? output : $"{output[..700]}...";
    }

    private static IEnumerable<string> RuntimeRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopAIAgent", "tools");
        yield return Path.Combine("Z:\\ai", "tools");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
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

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        Dictionary<string, string> map = [];
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                map[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return map;
    }
}
