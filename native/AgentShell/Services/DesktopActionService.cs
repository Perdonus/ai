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
        ["браузер"] = "browser",
        ["browser"] = "browser",
        ["гугл"] = "chrome",
        ["гугл хром"] = "chrome",
        ["хром"] = "chrome",
        ["edge"] = "msedge",
        ["эдж"] = "msedge",
        ["microsoft edge"] = "msedge",
        ["chrome"] = "chrome",
        ["google chrome"] = "chrome",
        ["firefox"] = "firefox",
        ["mozilla firefox"] = "firefox",
        ["brave"] = "brave",
        ["проводник"] = "explorer",
        ["explorer"] = "explorer",
        ["vscode"] = "code",
        ["visual studio code"] = "code",
        ["code"] = "code"
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

        if (TryExtractUrl(prompt, out var url))
        {
            await OpenUrlAsync(url, cancellationToken);
            return new DesktopActionResult($"Открыла {url}.");
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
        var spec = ResolveLaunchSpec(target);
        StartupLogService.Info($"Running visual desktop action for target: {target}; resolved={spec.RunCommand}");

        if (!string.IsNullOrWhiteSpace(spec.UrlToOpen))
        {
            await OpenUrlAsync(spec.UrlToOpen, cancellationToken);
            return;
        }

        if (TryLaunchDirect(spec))
        {
            return;
        }

        _input.PressKeyCombo(["WIN", "R"]);
        await _input.WaitAsync(320, cancellationToken);
        _input.TypeText(spec.RunCommand);
        await _input.WaitAsync(160, cancellationToken);
        _input.PressKey("ENTER");
    }

    public Task OpenBrowserAsync(string? url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OpenUrlAsync(string.IsNullOrWhiteSpace(url) ? "https://www.google.com" : url, cancellationToken);
    }

    public Task OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeUrl(url);
        Process.Start(new ProcessStartInfo
        {
            FileName = normalized,
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

    private static bool TryExtractUrl(string prompt, out string url)
    {
        var explicitUrl = Regex.Match(prompt, @"https?://\S+", RegexOptions.IgnoreCase);
        if (explicitUrl.Success)
        {
            url = explicitUrl.Value;
            return true;
        }

        var bareDomain = Regex.Match(prompt, @"\b(?:[a-z0-9-]+\.)+[a-z]{2,}(?:/\S*)?\b", RegexOptions.IgnoreCase);
        if (bareDomain.Success)
        {
            url = NormalizeUrl(bareDomain.Value);
            return true;
        }

        url = string.Empty;
        return false;
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Contains("://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }

    private static AppLaunchSpec ResolveLaunchSpec(string target)
    {
        var normalized = target.Trim();
        var lowered = normalized.ToLowerInvariant();
        var canonical = KnownTargets.TryGetValue(lowered, out var mapped)
            ? mapped
            : lowered;

        return canonical switch
        {
            "browser" => new AppLaunchSpec("https://www.google.com", null, null, "https://www.google.com"),
            "msedge" => new AppLaunchSpec("msedge", "msedge.exe", null),
            "chrome" => new AppLaunchSpec("chrome", "chrome.exe", null),
            "firefox" => new AppLaunchSpec("firefox", "firefox.exe", null),
            "brave" => new AppLaunchSpec("brave", "brave.exe", null),
            "explorer" => new AppLaunchSpec("explorer", "explorer.exe", null),
            "code" => new AppLaunchSpec("code", "code.exe", null),
            "wt" => new AppLaunchSpec("wt", "wt.exe", null),
            "powershell" => new AppLaunchSpec("powershell", "powershell.exe", null),
            "cmd" => new AppLaunchSpec("cmd", "cmd.exe", null),
            "notepad" => new AppLaunchSpec("notepad", "notepad.exe", null),
            "calc" => new AppLaunchSpec("calc", "calc.exe", null),
            "mspaint" => new AppLaunchSpec("mspaint", "mspaint.exe", null),
            _ when Regex.IsMatch(normalized, @"^https?://", RegexOptions.IgnoreCase) => new AppLaunchSpec(normalized, null, null, normalized),
            _ when Regex.IsMatch(normalized, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase) => new AppLaunchSpec(normalized, normalized, null),
            _ => new AppLaunchSpec(normalized, normalized, null)
        };
    }

    private static bool TryLaunchDirect(AppLaunchSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Executable))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = spec.Executable,
                    Arguments = spec.Arguments ?? string.Empty,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Direct launch failed for {spec.Executable}: {ex.Message}");
            }
        }

        if (File.Exists(spec.RunCommand))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = spec.RunCommand,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Direct file launch failed for {spec.RunCommand}: {ex.Message}");
            }
        }

        return false;
    }
}

public sealed record DesktopActionResult(string Message);
internal sealed class AppLaunchSpec
{
    public AppLaunchSpec(string runCommand, string? executable, string? arguments, string? urlToOpen = null)
    {
        RunCommand = runCommand;
        Executable = executable;
        Arguments = arguments;
        UrlToOpen = urlToOpen;
    }

    public string RunCommand { get; }
    public string? Executable { get; }
    public string? Arguments { get; }
    public string? UrlToOpen { get; }
}
