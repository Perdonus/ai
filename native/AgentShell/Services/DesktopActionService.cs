using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentShell.Services;

public sealed class DesktopActionService
{
    private readonly InputAutomationService _input = new();

    private static readonly IReadOnlyDictionary<string, string> KnownTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["блокнот"] = "notepad",
        ["notepad"] = "notepad",
        ["калькулятор"] = "calc",
        ["calc"] = "calc",
        ["терминал"] = "wt",
        ["terminal"] = "wt",
        ["powershell"] = "powershell",
        ["командную строку"] = "cmd",
        ["cmd"] = "cmd",
        ["paint"] = "mspaint",
        ["паинт"] = "mspaint",
        ["браузер"] = "msedge",
        ["browser"] = "msedge",
        ["edge"] = "msedge",
        ["chrome"] = "chrome"
    };

    public async Task<DesktopActionResult?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = prompt.Trim().ToLowerInvariant();
        if (!ContainsActionVerb(normalized))
        {
            return null;
        }

        foreach (var pair in KnownTargets)
        {
            if (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                await OpenAppVisualAsync(pair.Value, cancellationToken);
                return new DesktopActionResult($"Открыл {pair.Key}.");
            }
        }

        var urlMatch = Regex.Match(prompt, @"https?://\S+", RegexOptions.IgnoreCase);
        if (urlMatch.Success)
        {
            await OpenUrlAsync(urlMatch.Value, cancellationToken);
            return new DesktopActionResult($"Открыл {urlMatch.Value}.");
        }

        var pathMatch = Regex.Match(prompt, "[A-Za-z]:\\\\[^\\r\\n\"]+");
        if (pathMatch.Success && (File.Exists(pathMatch.Value) || Directory.Exists(pathMatch.Value)))
        {
            await OpenPathAsync(pathMatch.Value, cancellationToken);
            return new DesktopActionResult($"Открыл {pathMatch.Value}.");
        }

        return null;
    }

    public async Task OpenAppVisualAsync(string target, CancellationToken cancellationToken)
    {
        StartupLogService.Info($"Running visual desktop action for target: {target}");
        _input.PressKeyCombo(["WIN", "R"]);
        await _input.WaitAsync(300, cancellationToken);
        _input.TypeText(target);
        await _input.WaitAsync(120, cancellationToken);
        _input.PressKey("ENTER");
    }

    public Task OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    private static bool ContainsActionVerb(string normalized)
    {
        return normalized.Contains("открой", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("open ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("launch ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("запусти", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DesktopActionResult(string Message);
