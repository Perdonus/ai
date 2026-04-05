using System.Diagnostics;
using System.Runtime.InteropServices;
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

        if (TryExtractBrowserSearchRequest(prompt, out var searchQuery))
        {
            await OpenBrowserSearchAsync(searchQuery, cancellationToken);
            return new DesktopActionResult($"Открыла браузер и выполнила поиск: {searchQuery}.");
        }

        if (!ContainsActionVerb(normalized))
        {
            return null;
        }

        foreach (var pair in KnownTargets)
        {
            if (normalized.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                await OpenAppVisualAsync(pair.Value, cancellationToken);
                return new DesktopActionResult($"Открыла {pair.Key}.");
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
            return new DesktopActionResult($"Открыла {pathMatch.Value}.");
        }

        return null;
    }

    public async Task OpenAppVisualAsync(string target, CancellationToken cancellationToken)
    {
        var spec = ResolveLaunchSpec(target);
        StartupLogService.Info($"Running visual desktop action for target: {target}; resolved={spec.RunCommand}");

        if (spec.CanonicalTarget is "browser" or "msedge" or "chrome" or "firefox" or "brave")
        {
            if (await FocusWindowAsync(spec.CanonicalTarget, cancellationToken))
            {
                return;
            }
        }

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
        await _input.WaitAsync(180, cancellationToken);
        _input.PressKey("ENTER");
    }

    public async Task OpenBrowserAsync(string? url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(url))
        {
            if (await FocusWindowAsync("browser", cancellationToken))
            {
                return;
            }

            await OpenUrlAsync("https://www.google.com", cancellationToken);
            return;
        }

        await OpenUrlAsync(NormalizeUrl(url), cancellationToken);
    }

    public Task OpenBrowserSearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encodedQuery = Uri.EscapeDataString(query.Trim());
        return OpenBrowserAsync($"https://www.google.com/search?q={encodedQuery}", cancellationToken);
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

    public async Task<bool> FocusWindowAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var candidate in ExpandFocusCandidates(target))
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited || process.MainWindowHandle == nint.Zero)
                    {
                        continue;
                    }

                    var processName = process.ProcessName ?? string.Empty;
                    var title = process.MainWindowTitle ?? string.Empty;
                    if (!processName.Contains(candidate, StringComparison.OrdinalIgnoreCase) &&
                        !title.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var hwnd = process.MainWindowHandle;
                    _ = ShowWindow(hwnd, SwRestore);
                    _ = BringWindowToTop(hwnd);
                    _ = SetForegroundWindow(hwnd);
                    StartupLogService.Info($"Focused window for target {target}: {processName} | {title}");
                    await _input.WaitAsync(180, cancellationToken);
                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static bool ContainsActionVerb(string normalized)
    {
        return normalized.Contains("открой", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("открыть", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("нужно открыть", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("open ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("launch ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("запусти", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("запустить", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractBrowserSearchRequest(string prompt, out string query)
    {
        var normalized = prompt.Trim();
        var lowered = normalized.ToLowerInvariant();
        var mentionsBrowser = lowered.Contains("брауз", StringComparison.OrdinalIgnoreCase) ||
                              lowered.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
                              lowered.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
                              lowered.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                              lowered.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
                              lowered.Contains("brave", StringComparison.OrdinalIgnoreCase);
        var mentionsSearch = lowered.Contains("поиск", StringComparison.OrdinalIgnoreCase) ||
                             lowered.Contains("найди", StringComparison.OrdinalIgnoreCase) ||
                             lowered.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                             lowered.Contains("find ", StringComparison.OrdinalIgnoreCase);

        if (!mentionsBrowser || !mentionsSearch)
        {
            query = string.Empty;
            return false;
        }

        var quoted = Regex.Match(normalized, "['\"“”](?<query>[^'\"“”]+)['\"“”]");
        if (quoted.Success)
        {
            query = quoted.Groups["query"].Value.Trim();
            return !string.IsNullOrWhiteSpace(query);
        }

        var explicitQuery = Regex.Match(
            normalized,
            "(?:поиск(?:у)?|search(?:\\s+for)?|найди|find)(?:\\s+по\\s+запросу)?\\s+(?<query>.+)$",
            RegexOptions.IgnoreCase);
        if (explicitQuery.Success)
        {
            query = explicitQuery.Groups["query"].Value.Trim().Trim('.', '!', '?');
            query = Regex.Replace(query, "^(в|во)\\s+браузере\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
            return !string.IsNullOrWhiteSpace(query);
        }

        query = string.Empty;
        return false;
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

    private static IEnumerable<string> ExpandFocusCandidates(string target)
    {
        var normalized = target.Trim().ToLowerInvariant();
        if (KnownTargets.TryGetValue(normalized, out var mapped))
        {
            normalized = mapped;
        }

        return normalized switch
        {
            "browser" => ["firefox", "chrome", "msedge", "brave", "browser"],
            "msedge" => ["msedge", "edge", "microsoft edge"],
            "chrome" => ["chrome", "google chrome"],
            "firefox" => ["firefox", "mozilla firefox"],
            "brave" => ["brave"],
            "explorer" => ["explorer", "проводник"],
            _ => [normalized]
        };
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
            "browser" => new AppLaunchSpec("https://www.google.com", null, null, "browser", "https://www.google.com"),
            "msedge" => new AppLaunchSpec("msedge", "msedge.exe", null, "msedge"),
            "chrome" => new AppLaunchSpec("chrome", "chrome.exe", null, "chrome"),
            "firefox" => new AppLaunchSpec("firefox", "firefox.exe", null, "firefox"),
            "brave" => new AppLaunchSpec("brave", "brave.exe", null, "brave"),
            "explorer" => new AppLaunchSpec("explorer", "explorer.exe", null, "explorer"),
            "code" => new AppLaunchSpec("code", "code.exe", null, "code"),
            "wt" => new AppLaunchSpec("wt", "wt.exe", null, "wt"),
            "powershell" => new AppLaunchSpec("powershell", "powershell.exe", null, "powershell"),
            "cmd" => new AppLaunchSpec("cmd", "cmd.exe", null, "cmd"),
            "notepad" => new AppLaunchSpec("notepad", "notepad.exe", null, "notepad"),
            "calc" => new AppLaunchSpec("calc", "calc.exe", null, "calc"),
            "mspaint" => new AppLaunchSpec("mspaint", "mspaint.exe", null, "mspaint"),
            _ when Regex.IsMatch(normalized, @"^https?://", RegexOptions.IgnoreCase) => new AppLaunchSpec(normalized, null, null, canonical, normalized),
            _ when Regex.IsMatch(normalized, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase) => new AppLaunchSpec(normalized, normalized, null, canonical),
            _ => new AppLaunchSpec(normalized, normalized, null, canonical)
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

    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);
}

public sealed record DesktopActionResult(string Message);

internal sealed class AppLaunchSpec
{
    public AppLaunchSpec(string runCommand, string? executable, string? arguments, string canonicalTarget, string? urlToOpen = null)
    {
        RunCommand = runCommand;
        Executable = executable;
        Arguments = arguments;
        CanonicalTarget = canonicalTarget;
        UrlToOpen = urlToOpen;
    }

    public string RunCommand { get; }
    public string? Executable { get; }
    public string? Arguments { get; }
    public string CanonicalTarget { get; }
    public string? UrlToOpen { get; }
}
