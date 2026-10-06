using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentShell.Models;

namespace AgentShell.Services;

/// <summary>
/// A tiny HTTP server that exposes the same agent as the desktop panel, so the PC can be
/// driven from another machine on the LAN (and later through a port forward).
///
/// It builds on <see cref="TcpListener"/> on purpose: HttpListener would demand an admin-run
/// urlacl reservation for any interface other than loopback, and Kestrel would add a framework
/// reference to a self-contained WinUI app.
///
/// Every route requires the configured token. There is otherwise nothing between the network
/// and full keyboard and mouse control of this machine.
/// </summary>
public sealed class WebChatService : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly List<SseClient> _clients = [];
    private readonly List<WebTurn> _turns = [];
    private readonly AgentLoopService _agentLoop = new();
    private readonly AgentSessionState _session = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _runCts;
    private Timer? _heartbeat;
    private AgentLoopProgress? _progress;
    private bool _busy;
    private string _token = string.Empty;

    public bool IsRunning { get; private set; }

    public string ListenUrl { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public void Start(ShellConfig config)
    {
        Stop();
        LastError = string.Empty;

        var settings = config.Web;
        if (!settings.Enabled)
        {
            StartupLogService.Info("Web control surface is disabled in the settings.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            LastError = "Не задан токен доступа к веб-морде.";
            StartupLogService.Warn(LastError);
            return;
        }

        _token = settings.Token.Trim();
        var port = settings.Port is > 0 and < 65536 ? settings.Port : 4798;
        var bind = string.IsNullOrWhiteSpace(settings.Bind) ? "0.0.0.0" : settings.Bind.Trim();

        IPAddress address;
        if (bind is "0.0.0.0" or "*" or "+")
        {
            address = IPAddress.Any;
        }
        else if (IPAddress.TryParse(bind, out var parsed) && parsed is not null)
        {
            address = parsed;
        }
        else
        {
            LastError = $"Некорректный адрес прослушивания: {bind}";
            StartupLogService.Error(LastError);
            return;
        }

        try
        {
            _listener = new TcpListener(address, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            LastError = $"Не удалось занять порт {port}: {ex.Message}";
            StartupLogService.Error(LastError);
            return;
        }

        _serverCts = new CancellationTokenSource();
        IsRunning = true;

        var host = address.Equals(IPAddress.Any) ? ResolveLanAddress() : address.ToString();
        ListenUrl = $"http://{host}:{port}/?token={_token}";

        _heartbeat = new Timer(_ => Broadcast(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        _ = Task.Run(() => AcceptLoopAsync(_serverCts.Token));

        StartupLogService.Info($"Web control surface listening on {ListenUrl}");
    }

    public void Stop()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;

        try
        {
            _runCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _serverCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        _listener = null;
        IsRunning = false;

        List<SseClient> clients;
        lock (_gate)
        {
            clients = _clients.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        _serverCts?.Dispose();
        _serverCts = null;
    }

    public void Dispose() => Stop();

    /// <summary>Human readable state for the settings window.</summary>
    public string Describe()
    {
        if (IsRunning)
        {
            return $"Веб-морда работает: {ListenUrl}";
        }

        return string.IsNullOrWhiteSpace(LastError) ? "Веб-морда выключена." : LastError;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                StartupLogService.Warn($"Web accept failed: {ex.Message}");
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                if (!IsAuthorized(request))
                {
                    await WriteAsync(
                        stream,
                        401,
                        "text/plain; charset=utf-8",
                        "Нужен токен доступа. Открой адрес с ?token=… из настроек приложения.",
                        cancellationToken);
                    return;
                }

                switch (request.Path)
                {
                    case "/":
                    case "/index.html":
                        await WriteAsync(stream, 200, "text/html; charset=utf-8", WebChatPage.Html, cancellationToken);
                        return;

                    case "/api/state":
                        await WriteAsync(stream, 200, "application/json; charset=utf-8", CurrentStateJson(), cancellationToken);
                        return;

                    case "/api/prompt":
                        await HandlePromptAsync(stream, request, cancellationToken);
                        return;

                    case "/api/cancel":
                        await HandleCancelAsync(stream, cancellationToken);
                        return;

                    case "/api/reset":
                        await HandleResetAsync(stream, cancellationToken);
                        return;

                    case "/api/events":
                        await HandleEventsAsync(stream, cancellationToken);
                        return;

                    default:
                        await WriteAsync(stream, 404, "text/plain; charset=utf-8", "Not found", cancellationToken);
                        return;
                }
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Web request failed: {ex.Message}");
            }
        }
    }

    private async Task HandlePromptAsync(NetworkStream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        string prompt;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            prompt = document.RootElement.TryGetProperty("prompt", out var value)
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            await WriteAsync(stream, 400, "application/json; charset=utf-8", "{\"error\":\"bad json\"}", cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await WriteAsync(stream, 400, "application/json; charset=utf-8", "{\"error\":\"empty prompt\"}", cancellationToken);
            return;
        }

        CancellationTokenSource? runCts;
        lock (_gate)
        {
            if (_busy)
            {
                runCts = null;
            }
            else
            {
                _busy = true;
                _progress = new AgentLoopProgress("Смотрю на экран", string.Empty, string.Empty);
                runCts = new CancellationTokenSource();
                _runCts = runCts;
                _turns.Add(new WebTurn(prompt));
            }
        }

        if (runCts is null)
        {
            await WriteAsync(stream, 409, "application/json; charset=utf-8", "{\"error\":\"busy\"}", cancellationToken);
            return;
        }

        Broadcast();
        _ = RunPromptAsync(prompt, runCts);

        await WriteAsync(stream, 202, "application/json; charset=utf-8", "{\"ok\":true}", cancellationToken);
    }

    private async Task HandleCancelAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                _runCts?.Cancel();
            }
            catch
            {
            }
        }

        await WriteAsync(stream, 200, "application/json; charset=utf-8", "{\"ok\":true}", cancellationToken);
    }

    private async Task HandleResetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_busy)
            {
                _ = WriteAsync(stream, 409, "application/json; charset=utf-8", "{\"error\":\"busy\"}", cancellationToken);
                return;
            }

            _turns.Clear();
            _progress = null;
        }

        _session.Reset();
        Broadcast();
        await WriteAsync(stream, 200, "application/json; charset=utf-8", "{\"ok\":true}", cancellationToken);
    }

    private async Task RunPromptAsync(string prompt, CancellationTokenSource runCts)
    {
        WebTurn turn;
        lock (_gate)
        {
            turn = _turns[^1];
        }

        var progress = new Progress<AgentLoopProgress>(update =>
        {
            lock (_gate)
            {
                _progress = update;
            }

            Broadcast();
        });

        try
        {
            var result = await _agentLoop.RunAsync(App.ConfigService.Current, _session, prompt, progress, runCts.Token);

            lock (_gate)
            {
                turn.Answer = result.Answer;
                turn.Thinking = result.Thinking;
                turn.Error = result.Error;
                turn.WaitingForUser = result.WaitingForUser;
                turn.State = string.IsNullOrWhiteSpace(result.Error) ? "done" : "error";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                turn.State = "cancelled";
                turn.Answer = "Остановлено по твоей команде.";
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Web prompt failed: {ex}");

            lock (_gate)
            {
                turn.State = "error";
                turn.Error = ex.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                _progress = null;
                _runCts = null;
            }

            runCts.Dispose();
            Broadcast();
        }
    }

    private async Task HandleEventsAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: text/event-stream; charset=utf-8\r\n" +
                     "Cache-Control: no-cache\r\n" +
                     "Connection: keep-alive\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var client = new SseClient(stream);
        lock (_gate)
        {
            _clients.Add(client);
        }

        try
        {
            await client.SendAsync($"data: {CurrentStateJson()}\n\n", cancellationToken);

            // The browser never sends anything on this connection, so an incoming zero-byte
            // read means it went away and the client can be dropped.
            var scratch = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(scratch, cancellationToken);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
            }

            client.Dispose();
        }
    }

    private void Broadcast()
    {
        string payload;
        List<SseClient> clients;

        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            payload = $"data: {BuildStateJsonLocked()}\n\n";
            clients = _clients.ToList();
        }

        _ = Task.Run(async () =>
        {
            foreach (var client in clients)
            {
                if (await client.SendAsync(payload, CancellationToken.None))
                {
                    continue;
                }

                lock (_gate)
                {
                    _clients.Remove(client);
                }

                client.Dispose();
            }
        });
    }

    private string CurrentStateJson()
    {
        lock (_gate)
        {
            return BuildStateJsonLocked();
        }
    }

    private string BuildStateJsonLocked()
    {
        var state = new
        {
            busy = _busy,
            progress = _progress is null
                ? null
                : new { status = _progress.Status, thinking = _progress.Thinking, answer = _progress.Answer },
            turns = _turns
                .Select(turn => new
                {
                    prompt = turn.Prompt,
                    answer = turn.Answer,
                    thinking = turn.Thinking,
                    error = turn.Error,
                    state = turn.State,
                    waitingForUser = turn.WaitingForUser
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(state);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Auth-Token", out var header) && FixedEquals(header, _token))
        {
            return true;
        }

        if (request.Headers.TryGetValue("Authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            FixedEquals(authorization[7..].Trim(), _token))
        {
            return true;
        }

        return request.Query.TryGetValue("token", out var query) && FixedEquals(query, _token);
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string ResolveLanAddress()
    {
        try
        {
            foreach (var candidate in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (candidate.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(candidate))
                {
                    return candidate.ToString();
                }
            }
        }
        catch
        {
        }

        return "127.0.0.1";
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var scratch = new byte[8192];
        var headerEnd = -1;

        while (buffer.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(scratch, 0, read);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            return null;
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var target = parts[1];
        var questionMark = target.IndexOf('?');
        var path = questionMark < 0 ? target : target[..questionMark];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (questionMark >= 0)
        {
            foreach (var pair in target[(questionMark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = equals < 0 ? pair : pair[..equals];
                var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
        }

        var bodyLength = headers.TryGetValue("Content-Length", out var contentLength) &&
                         int.TryParse(contentLength, out var parsedLength)
            ? Math.Clamp(parsedLength, 0, MaxBodyBytes)
            : 0;

        var body = new byte[bodyLength];
        var bodyStart = headerEnd + 4;
        var buffered = (int)buffer.Length - bodyStart;
        var copied = Math.Min(Math.Max(buffered, 0), bodyLength);
        if (copied > 0)
        {
            Array.Copy(buffer.GetBuffer(), bodyStart, body, 0, copied);
        }

        while (copied < bodyLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(copied, bodyLength - copied), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            copied += read;
        }

        return new HttpRequest(parts[0].ToUpperInvariant(), path, query, headers, body);
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (data[index] == 13 && data[index + 1] == 10 && data[index + 2] == 13 && data[index + 3] == 10)
            {
                return index;
            }
        }

        return -1;
    }

    private static Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        return WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), cancellationToken);
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        409 => "Conflict",
        _ => "Error"
    };

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        byte[] Body);

    private sealed class WebTurn(string prompt)
    {
        public string Prompt { get; } = prompt;

        public string Answer { get; set; } = string.Empty;

        public string Thinking { get; set; } = string.Empty;

        public string Error { get; set; } = string.Empty;

        public string State { get; set; } = "running";

        public bool WaitingForUser { get; set; }
    }

    private sealed class SseClient(NetworkStream stream) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task<bool> SendAsync(string payload, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try
                {
                    _writeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
            }

            _writeLock.Dispose();
        }
    }
}
