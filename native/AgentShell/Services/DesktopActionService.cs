using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AgentShell.Services;

public sealed class DesktopActionService
{
    private static readonly IReadOnlyDictionary<string, string> KnownTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["блокнот"] = "notepad.exe",
        ["notepad"] = "notepad.exe",
        ["калькулятор"] = "calc.exe",
        ["calc"] = "calc.exe",
        ["терминал"] = "wt.exe",
        ["terminal"] = "wt.exe",
        ["powershell"] = "powershell.exe",
        ["командную строку"] = "cmd.exe",
        ["cmd"] = "cmd.exe",
        ["paint"] = "mspaint.exe",
        ["паинт"] = "mspaint.exe",
        ["браузер"] = "msedge.exe",
        ["browser"] = "msedge.exe",
        ["edge"] = "msedge.exe",
        ["chrome"] = "chrome.exe"
    };

    public Task<DesktopActionResult?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = prompt.Trim().ToLowerInvariant();
        if (!ContainsActionVerb(normalized))
        {
            return Task.FromResult<DesktopActionResult?>(null);
        }

        foreach (var pair in KnownTargets)
        {
            if (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<DesktopActionResult?>(Launch(pair.Value, $"Открыл {pair.Key}."));
            }
        }

        var urlMatch = Regex.Match(prompt, @"https?://\S+", RegexOptions.IgnoreCase);
        if (urlMatch.Success)
        {
            return Task.FromResult<DesktopActionResult?>(Launch(urlMatch.Value, $"Открыл {urlMatch.Value}."));
        }

        var pathMatch = Regex.Match(prompt, "[A-Za-z]:\\\\[^\\r\\n\"]+");
        if (pathMatch.Success && (File.Exists(pathMatch.Value) || Directory.Exists(pathMatch.Value)))
        {
            return Task.FromResult<DesktopActionResult?>(Launch(pathMatch.Value, $"Открыл {pathMatch.Value}."));
        }

        return Task.FromResult<DesktopActionResult?>(null);
    }

    private static bool ContainsActionVerb(string normalized)
    {
        return normalized.Contains("открой", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("open ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("launch ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("запусти", StringComparison.OrdinalIgnoreCase);
    }

    private static DesktopActionResult Launch(string target, string successMessage)
    {
        StartupLogService.Info($"Running local desktop action for target: {target}");
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });

        return new DesktopActionResult(successMessage);
    }
}

public sealed record DesktopActionResult(string Message);
