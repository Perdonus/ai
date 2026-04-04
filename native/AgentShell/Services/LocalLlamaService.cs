using System.Diagnostics;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class LocalLlamaService : IDisposable
{
    private const int Port = 39230;
    private readonly object _gate = new();
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly Timer _idleTimer;
    private Process? _process;
    private LocalModelConfig? _loadedModel;
    private DateTimeOffset _lastUsedAt = DateTimeOffset.MinValue;

    public LocalLlamaService()
    {
        _idleTimer = new Timer(CheckIdle, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    public async Task<string> EnsureServerAsync(ShellConfig config, string modelId, CancellationToken cancellationToken)
    {
        var model = config.LocalAi.Models.FirstOrDefault(item => string.Equals(item.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Локальная модель не найдена: {modelId}");

        if (string.IsNullOrWhiteSpace(model.ModelPath) || !File.Exists(model.ModelPath))
        {
            throw new InvalidOperationException($"GGUF файл не найден: {model.ModelPath}");
        }

        var shouldWaitForReady = false;
        lock (_gate)
        {
            if (IsRunningLocked() && _loadedModel is not null && string.Equals(_loadedModel.Id, model.Id, StringComparison.OrdinalIgnoreCase))
            {
                _lastUsedAt = DateTimeOffset.UtcNow;
                return BaseUrl;
            }

            StopLocked();
            StartLocked(model);
            _lastUsedAt = DateTimeOffset.UtcNow;
            shouldWaitForReady = true;
        }

        if (shouldWaitForReady)
        {
            await WaitUntilReadyAsync(cancellationToken);
        }

        return BaseUrl;
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StartLocked(LocalModelConfig model)
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "llama.cpp");
        var llamaServer = Directory.Exists(runtimeRoot)
            ? Directory.GetFiles(runtimeRoot, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(llamaServer) || !File.Exists(llamaServer))
        {
            throw new InvalidOperationException($"llama.cpp runtime не найден в {runtimeRoot}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = llamaServer,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(llamaServer) ?? AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(Port.ToString());
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(model.ModelPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Math.Max(512, model.ContextSize).ToString());
        startInfo.ArgumentList.Add("-ngl");
        startInfo.ArgumentList.Add(Math.Max(0, model.GpuLayers).ToString());
        startInfo.ArgumentList.Add("--jinja");

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                StartupLogService.Info($"llama.cpp: {args.Data}");
            }
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                StartupLogService.Warn($"llama.cpp err: {args.Data}");
            }
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("Не удалось запустить llama-server.exe.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _loadedModel = model;
        StartupLogService.Info($"Started local llama.cpp server for {model.Name}.");
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _httpClient.GetAsync($"{BaseUrl}/models", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new InvalidOperationException("Локальный llama.cpp сервер не поднялся вовремя.");
    }

    private void CheckIdle(object? _)
    {
        lock (_gate)
        {
            if (!IsRunningLocked())
            {
                return;
            }

            var idleSeconds = Math.Max(10, App.ConfigService.Current.LocalAi.IdleUnloadSeconds);
            if (DateTimeOffset.UtcNow - _lastUsedAt < TimeSpan.FromSeconds(idleSeconds))
            {
                return;
            }

            StartupLogService.Info($"Stopping local llama.cpp after {idleSeconds}s idle.");
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
                _process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Failed to stop llama.cpp server cleanly: {ex.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _loadedModel = null;
        }
    }
}
