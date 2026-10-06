using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

/// <summary>
/// Owns the koboldcpp process that serves the local vision-language model.
/// The server is started with every layer pinned to the GPU so that no weights are
/// offloaded to system RAM, and it is released again after a period of inactivity.
/// </summary>
public sealed class LocalKoboldService : IDisposable
{
    private static readonly Regex OffloadRegex = new(
        @"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers to GPU",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private readonly Timer _idleTimer;
    private LocalRuntimePlan? _cachedPlan;
    private string? _cachedSettingsKey;
    private Process? _process;
    private string? _loadedSignature;
    private string _lastError = string.Empty;
    private DateTimeOffset _lastUsedAt = DateTimeOffset.MinValue;

    /// <summary>Model name reported to the OpenAI compatible endpoint.</summary>
    public string LoadedModelId { get; private set; } = "local-model";

    public LocalKoboldService()
    {
        _idleTimer = new Timer(CheckIdle, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return IsRunningLocked();
            }
        }
    }

    public string LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public string ResolveBaseUrl(ShellConfig config)
    {
        return LocalRuntimeLocator.BuildBaseUrl(config.LocalAi);
    }

    /// <summary>
    /// Makes sure koboldcpp is running with the configured model and returns its OpenAI base URL.
    /// </summary>
    public async Task<string> EnsureServerAsync(ShellConfig config, CancellationToken cancellationToken)
    {
        var plan = ResolvePlan(config.LocalAi);
        var waitForReady = false;

        lock (_gate)
        {
            if (IsRunningLocked() && string.Equals(_loadedSignature, plan.Signature, StringComparison.Ordinal))
            {
                _lastUsedAt = DateTimeOffset.UtcNow;
                return plan.BaseUrl;
            }

            StopLocked();
            StartLocked(plan);
            _loadedSignature = plan.Signature;
            _lastUsedAt = DateTimeOffset.UtcNow;
            waitForReady = true;
        }

        if (waitForReady)
        {
            await WaitUntilReadyAsync(plan.BaseUrl, cancellationToken);
        }

        return plan.BaseUrl;
    }

    /// <summary>Stops the server and frees video memory. Used by the settings window.</summary>
    public void StopServer()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        lock (_gate)
        {
            StopLocked();
        }
    }

    /// <summary>
    /// Resolving the plan walks the disk, and this runs before every single step, so the
    /// result is memoized until one of the relevant settings changes.
    /// </summary>
    private LocalRuntimePlan ResolvePlan(LocalAiSettings settings)
    {
        var key = string.Join(
            "|",
            settings.KoboldCppPath,
            settings.ModelPath,
            settings.MmprojPath,
            settings.ContextSize,
            settings.GpuLayers,
            settings.QuantKv,
            settings.Port,
            settings.Threads,
            settings.MmprojOnCpu,
            settings.VisionMaxRes,
            settings.ExtraArgs);

        lock (_gate)
        {
            if (_cachedPlan is not null && string.Equals(_cachedSettingsKey, key, StringComparison.Ordinal))
            {
                return _cachedPlan;
            }
        }

        var plan = LocalRuntimeLocator.Resolve(settings);
        lock (_gate)
        {
            _cachedPlan = plan;
            _cachedSettingsKey = key;
        }

        return plan;
    }

    private void StartLocked(LocalRuntimePlan plan)
    {
        _lastError = string.Empty;

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.KoboldCppPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(plan.KoboldCppPath) ?? AppContext.BaseDirectory
        };

        foreach (var argument in plan.BuildArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, args) => HandleServerLine(args.Data, isError: false);
        _process.ErrorDataReceived += (_, args) => HandleServerLine(args.Data, isError: true);

        if (!_process.Start())
        {
            _process = null;
            throw new InvalidOperationException("Не удалось запустить koboldcpp.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        LoadedModelId = plan.ModelId;
        StartupLogService.Info(
            $"Started koboldcpp: {plan.KoboldCppPath} · model={Path.GetFileName(plan.ModelPath)} · mmproj={Path.GetFileName(plan.MmprojPath)} · {plan.BaseUrl}");
    }

    private void HandleServerLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (isError)
        {
            StartupLogService.Warn($"koboldcpp err: {line}");
        }
        else
        {
            StartupLogService.Info($"koboldcpp: {line}");
        }

        var match = OffloadRegex.Match(line);
        if (!match.Success)
        {
            return;
        }

        var offloaded = int.Parse(match.Groups[1].Value);
        var total = int.Parse(match.Groups[2].Value);
        if (total > 0 && offloaded < total)
        {
            _lastError = $"В VRAM не влезли все слои: {offloaded}/{total}. Часть модели работает на CPU — уменьши context, включи mmproj на CPU или возьми квант меньше.";
            StartupLogService.Warn(_lastError);
        }
    }

    private async Task WaitUntilReadyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (!IsRunningLocked())
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(_lastError)
                            ? "koboldcpp завершился сразу после запуска. Смотри лог приложения."
                            : _lastError);
                }
            }

            try
            {
                using var response = await _httpClient.GetAsync($"{baseUrl}/models", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    StartupLogService.Info($"koboldcpp is ready at {baseUrl}.");
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("koboldcpp не поднялся вовремя (120 с).");
    }

    private void CheckIdle(object? _)
    {
        lock (_gate)
        {
            if (!IsRunningLocked())
            {
                return;
            }

            var idleSeconds = Math.Max(60, App.ConfigService.Current.LocalAi.IdleUnloadSeconds);
            if (DateTimeOffset.UtcNow - _lastUsedAt < TimeSpan.FromSeconds(idleSeconds))
            {
                return;
            }

            StartupLogService.Info($"Stopping koboldcpp after {idleSeconds}s idle.");
            StopLocked();
        }
    }

    private bool IsRunningLocked()
    {
        return _process is { HasExited: false };
    }

    private void StopLocked()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Failed to stop koboldcpp cleanly: {ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _loadedSignature = null;
        }
    }
}

/// <summary>Fully resolved launch plan for the local runtime.</summary>
public sealed record LocalRuntimePlan(
    string KoboldCppPath,
    string ModelPath,
    string MmprojPath,
    string BaseUrl,
    string Signature,
    int ContextSize,
    int GpuLayers,
    string QuantKv,
    int Port,
    int Threads,
    bool MmprojOnCpu,
    int VisionMaxRes,
    string ExtraArgs)
{
    public string ModelId => Path.GetFileNameWithoutExtension(ModelPath);

    public IReadOnlyList<string> BuildArguments()
    {
        var arguments = new List<string>
        {
            "--model", ModelPath,
            "--mmproj", MmprojPath,
            "--usevulkan",
            "--gpulayers", Math.Max(0, GpuLayers).ToString(),
            "--contextsize", Math.Max(512, ContextSize).ToString(),
            "--port", Port.ToString(),
            "--host", "127.0.0.1",
            "--threads", Math.Max(1, Threads).ToString(),
            "--blasthreads", Math.Max(1, Threads).ToString(),
            "--jinja"
        };

        if (!string.IsNullOrWhiteSpace(QuantKv) &&
            !string.Equals(QuantKv, "f16", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--quantkv");
            arguments.Add(QuantKv);
        }

        if (MmprojOnCpu)
        {
            arguments.Add("--mmprojcpu");
        }

        if (VisionMaxRes is >= 512 and <= 2048)
        {
            arguments.Add("--visionmaxres");
            arguments.Add(VisionMaxRes.ToString());
        }

        foreach (var extra in (ExtraArgs ?? string.Empty)
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            arguments.Add(extra);
        }

        return arguments;
    }
}

/// <summary>
/// Finds koboldcpp and the GGUF model pair either from the config file or from the usual
/// places a llama.cpp user keeps their runtimes and models.
/// </summary>
public static class LocalRuntimeLocator
{
    private const int MaxSearchDepth = 4;

    public static string BuildBaseUrl(LocalAiSettings settings)
    {
        var port = settings.Port is > 0 and < 65536 ? settings.Port : 5002;
        return $"http://127.0.0.1:{port}/v1";
    }

    public static LocalRuntimePlan Resolve(LocalAiSettings settings)
    {
        var koboldCpp = ResolveKoboldCpp(settings.KoboldCppPath);
        var model = ResolveModel(settings.ModelPath);
        var mmproj = ResolveMmproj(settings.MmprojPath, model);

        var contextSize = Math.Clamp(settings.ContextSize, 2048, 131072);
        var gpuLayers = settings.GpuLayers <= 0 ? 999 : settings.GpuLayers;
        var signature = string.Join(
            "|",
            koboldCpp,
            model,
            mmproj,
            contextSize,
            gpuLayers,
            settings.QuantKv,
            settings.MmprojOnCpu,
            settings.VisionMaxRes,
            settings.ExtraArgs);

        return new LocalRuntimePlan(
            koboldCpp,
            model,
            mmproj,
            BuildBaseUrl(settings),
            signature,
            contextSize,
            gpuLayers,
            settings.QuantKv,
            settings.Port,
            settings.Threads,
            settings.MmprojOnCpu,
            settings.VisionMaxRes,
            settings.ExtraArgs);
    }

    /// <summary>Describes what the locator currently sees, for the settings window.</summary>
    public static string Describe(LocalAiSettings settings)
    {
        try
        {
            var plan = Resolve(settings);
            return $"koboldcpp: {plan.KoboldCppPath}{Environment.NewLine}модель: {plan.ModelPath}{Environment.NewLine}mmproj: {plan.MmprojPath}{Environment.NewLine}адрес: {plan.BaseUrl}";
        }
        catch (Exception ex)
        {
            return $"Не найдено: {ex.Message}";
        }
    }

    private static string ResolveKoboldCpp(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException($"koboldcpp не найден по пути: {configuredPath}");
            }

            return configuredPath;
        }

        var roots = KoboldCppRoots().ToArray();

        // The shell always launches koboldcpp with --usevulkan, so the no-CUDA build is
        // preferred when it is present next to the regular one.
        var candidates = FindFiles(roots, name =>
                name.StartsWith("koboldcpp", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("oldcpu", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Contains("nocuda", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .ToList();

        return candidates.FirstOrDefault() ?? throw new InvalidOperationException(
            "koboldcpp.exe не найден. Укажи путь в настройках или положи его в runtimes\\koboldcpp рядом с приложением.");
    }

    private static string ResolveModel(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException($"GGUF модель не найдена по пути: {configuredPath}");
            }

            return configuredPath;
        }

        var matches = FindFiles(ModelRoots(), name =>
                name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var preferred = matches.FirstOrDefault(path =>
                             Path.GetFileName(path).Contains("evocua", StringComparison.OrdinalIgnoreCase))
                         ?? matches.FirstOrDefault(path =>
                             Path.GetFileName(path).Contains("qwen3-vl", StringComparison.OrdinalIgnoreCase) ||
                             Path.GetFileName(path).Contains("qwen3vl", StringComparison.OrdinalIgnoreCase))
                         ?? matches.FirstOrDefault();

        return preferred ?? throw new InvalidOperationException(
            "GGUF модель не найдена. Скачай EvoCUA-8B или укажи путь к .gguf в настройках.");
    }

    private static string ResolveMmproj(string configuredPath, string modelPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException($"mmproj не найден по пути: {configuredPath}");
            }

            return configuredPath;
        }

        var modelDirectory = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrWhiteSpace(modelDirectory) && Directory.Exists(modelDirectory))
        {
            var local = Directory
                .EnumerateFiles(modelDirectory, "mmproj*.gguf", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(local))
            {
                return local;
            }
        }

        var found = FindFiles(ModelRoots(), name =>
                name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        return found ?? throw new InvalidOperationException(
            "mmproj (vision projector) не найден. Без него модель не видит экран.");
    }

    private static IEnumerable<string> KoboldCppRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "koboldcpp");
        yield return Path.Combine(AppContext.BaseDirectory, "koboldcpp");
        yield return Path.Combine(home, "llm", "koboldcpp");
        yield return Path.Combine(home, "llm", "ai");
        yield return Path.Combine(home, "llm");
        yield return @"Z:\ai\koboldcpp";
    }

    private static IEnumerable<string> ModelRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(AppContext.BaseDirectory, "models");
        yield return Path.Combine(home, "llm", "models");
        yield return Path.Combine(home, "llm", "ai", "models");
        yield return Path.Combine(home, "llm");
        yield return @"Z:\ai\models";
    }

    private static IEnumerable<string> FindFiles(IEnumerable<string> roots, Func<string, bool> predicate)
    {
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Walk(root, 0, predicate))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> Walk(string directory, int depth, Func<string, bool> predicate)
    {
        if (depth > MaxSearchDepth)
        {
            yield break;
        }

        string[] files;
        string[] directories;
        try
        {
            files = Directory.GetFiles(directory);
            directories = Directory.GetDirectories(directory);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (predicate(Path.GetFileName(file)))
            {
                yield return file;
            }
        }

        foreach (var child in directories)
        {
            foreach (var nested in Walk(child, depth + 1, predicate))
            {
                yield return nested;
            }
        }
    }
}
