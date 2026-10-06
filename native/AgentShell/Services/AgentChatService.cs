using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentShell.Services;

/// <summary>A single chat turn. When <see cref="ImagePngBase64"/> is set the turn is multimodal.</summary>
public sealed record ChatTurn(string Role, string Text, string? ImagePngBase64 = null);

/// <summary>
/// Talks to the local koboldcpp server over its OpenAI compatible endpoint.
/// There is no remote provider, no API key and no rate limiting anymore.
/// </summary>
public sealed class AgentChatService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6)
    ];

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    private readonly LocalKoboldService _kobold = App.LocalKobold;

    public async Task<string> RequestAsync(
        Models.ShellConfig config,
        IReadOnlyList<ChatTurn> turns,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var baseUrl = await _kobold.EnsureServerAsync(config, cancellationToken);
        var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
        var payload = JsonSerializer.Serialize(new
        {
            model = _kobold.LoadedModelId,
            temperature = 0.0,
            top_p = 0.9,
            max_tokens = Math.Max(64, maxTokens),
            messages = turns.Select(BuildMessage).ToArray()
        });

        StartupLogService.Info($"Local request: turns={turns.Count}; multimodal={turns.Any(turn => turn.ImagePngBase64 is not null)}; bytes={payload.Length}");

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length)
            {
                StartupLogService.Warn($"Local request failed ({ex.Message}); retrying in {RetryDelays[attempt].TotalSeconds:0}s.");
                await Task.Delay(RetryDelays[attempt], cancellationToken);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ExtractContent(body);
                }

                if (attempt < RetryDelays.Length && (int)response.StatusCode >= 500)
                {
                    StartupLogService.Warn($"Local server returned {(int)response.StatusCode}; retrying in {RetryDelays[attempt].TotalSeconds:0}s.");
                    await Task.Delay(RetryDelays[attempt], cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    throw new InvalidOperationException(
                        "Локальный сервер вернул 503. Обычно это значит, что контекст переполнен: уменьши context или max_steps.");
                }

                throw new InvalidOperationException($"Локальный сервер ответил {(int)response.StatusCode}: {Trim(body)}");
            }
        }

        throw new InvalidOperationException("Локальный запрос не удался после повторных попыток.");
    }

    private static object BuildMessage(ChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.ImagePngBase64))
        {
            return new { role = turn.Role, content = turn.Text };
        }

        return new
        {
            role = turn.Role,
            content = new object[]
            {
                new { type = "image_url", image_url = new { url = $"data:image/png;base64,{turn.ImagePngBase64}" } },
                new { type = "text", text = turn.Text }
            }
        };
    }

    private static string ExtractContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Ответ сервера без choices: {Trim(body)}");
        }

        if (!choices[0].TryGetProperty("message", out var message))
        {
            throw new InvalidOperationException($"Ответ сервера без message: {Trim(body)}");
        }

        var content = ReadString(message, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            content = ReadString(message, "reasoning_content");
        }

        return content ?? string.Empty;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(
                "\n",
                value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .OfType<string>()),
            _ => null
        };
    }

    private static string Trim(string value)
    {
        var single = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 300 ? single : $"{single[..300]}...";
    }
}
