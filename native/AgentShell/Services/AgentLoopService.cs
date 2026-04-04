using System.Text;
using System.Text.Json;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentLoopService
{
    private readonly AgentChatService _chat = new();
    private readonly ScreenCaptureService _screen = new();
    private readonly DesktopActionService _desktop = new();
    private readonly InputAutomationService _input = new();

    public async Task<AgentLoopResult> RunAsync(
        ShellConfig config,
        AgentSessionState session,
        string prompt,
        IProgress<AgentLoopProgress>? progress,
        CancellationToken cancellationToken)
    {
        session.History.Add($"Пользователь: {prompt}");

        var visibleThoughts = new StringBuilder();
        string finalAnswer = string.Empty;

        for (var step = 1; step <= 8; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentLoopProgress($"Шаг {step}: смотрю на экран", visibleThoughts.ToString(), finalAnswer));
            var snapshot = _screen.Capture();

            string analysis = string.Empty;
            var analysisRoute = _chat.ResolveAnalysisRoute(config);
            if (analysisRoute is not null)
            {
                var analysisPrompt = BuildAnalysisPrompt(session, prompt, step);
                analysis = await _chat.RequestTextAsync(
                    config,
                    analysisRoute,
                    "Ты внутренний анализатор desktop-агента. Кратко реши, какой один следующий шаг нужен сейчас. Пиши по-русски.",
                    analysisPrompt,
                    cancellationToken);

                if (_chat.ShouldShowAnalysisThinking(config))
                {
                    AppendThought(visibleThoughts, step, analysis);
                    progress?.Report(new AgentLoopProgress($"Шаг {step}: анализ", visibleThoughts.ToString(), finalAnswer));
                }
            }

            var visionRoute = _chat.ResolveVisionRoute(config);
            var stepResponse = await _chat.RequestVisionJsonAsync(
                config,
                visionRoute,
                BuildDecisionSystemPrompt(),
                BuildDecisionUserPrompt(session, prompt, step, snapshot, analysis),
                snapshot,
                cancellationToken);

            var decision = ParseDecision(stepResponse);
            if (_chat.ShouldShowPrimaryThinking(config) && !string.IsNullOrWhiteSpace(decision.Thought))
            {
                AppendThought(visibleThoughts, step, decision.Thought);
                progress?.Report(new AgentLoopProgress($"Шаг {step}: думаю", visibleThoughts.ToString(), finalAnswer));
            }

            var actionSummary = await ExecuteActionAsync(decision.Action, snapshot, cancellationToken);
            session.History.Add($"Шаг {step}: {decision.Thought}");
            session.History.Add($"Действие {step}: {actionSummary}");

            if (!string.IsNullOrWhiteSpace(actionSummary))
            {
                AppendThought(visibleThoughts, step, $"Действие: {actionSummary}");
                progress?.Report(new AgentLoopProgress($"Шаг {step}: действие", visibleThoughts.ToString(), finalAnswer));
            }

            if (decision.Action.Type == "finish")
            {
                finalAnswer = string.IsNullOrWhiteSpace(decision.FinalResponse)
                    ? "Готово."
                    : decision.FinalResponse;
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty);
            }

            await _input.WaitAsync(500, cancellationToken);
        }

        finalAnswer = "Не успел закончить за лимит шагов. Сформулируй задачу уже точнее или продолжи её следующим сообщением.";
        session.History.Add($"Ассистент: {finalAnswer}");
        return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty);
    }

    private async Task<string> ExecuteActionAsync(AgentAction action, ScreenSnapshot snapshot, CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case "finish":
                return action.FinalResponse ?? "Завершение";
            case "observe":
                return "Наблюдение без действия";
            case "wait":
                await _input.WaitAsync(action.Milliseconds ?? 600, cancellationToken);
                return $"Подождал {Math.Clamp(action.Milliseconds ?? 600, 50, 5000)} мс";
            case "open_app":
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    throw new InvalidOperationException("open_app requires target");
                }

                await _desktop.OpenAppVisualAsync(action.Target, cancellationToken);
                return $"Открыл {action.Target}";
            case "type_text":
                if (string.IsNullOrWhiteSpace(action.Text))
                {
                    throw new InvalidOperationException("type_text requires text");
                }

                _input.TypeText(action.Text);
                return $"Ввел текст: {action.Text}";
            case "press_key":
                if (string.IsNullOrWhiteSpace(action.Key))
                {
                    throw new InvalidOperationException("press_key requires key");
                }

                _input.PressKey(action.Key);
                return $"Нажал {action.Key}";
            case "key_combo":
                if (action.Keys is null || action.Keys.Count == 0)
                {
                    throw new InvalidOperationException("key_combo requires keys");
                }

                _input.PressKeyCombo(action.Keys);
                return $"Нажал комбинацию {string.Join("+", action.Keys)}";
            case "click":
                if (action.X is null || action.Y is null)
                {
                    throw new InvalidOperationException("click requires x and y");
                }

                _input.LeftClick(snapshot.Left + action.X.Value, snapshot.Top + action.Y.Value);
                return $"Кликнул по координатам {action.X},{action.Y}";
            default:
                throw new InvalidOperationException($"Unsupported agent action: {action.Type}");
        }
    }

    private static string BuildAnalysisPrompt(AgentSessionState session, string prompt, int step)
    {
        return $"Текущий запрос пользователя: {prompt}\nНомер шага: {step}\nИстория диалога:\n{string.Join("\n", session.History.TakeLast(12))}\nДай краткую мысль о следующем шаге.";
    }

    private static string BuildDecisionSystemPrompt()
    {
        return """
Ты desktop-агент на Windows. Ты работаешь пошагово: смотришь на экран, думаешь, делаешь одно действие, потом снова смотришь.
Никогда не пытайся решить всю задачу одним сообщением.
Отвечай строго JSON-объектом без markdown.

Формат:
{
  "thought": "краткая мысль на русском",
  "action": {
    "type": "observe|open_app|type_text|press_key|key_combo|click|wait|finish",
    "target": "строка или null",
    "text": "строка или null",
    "key": "строка или null",
    "keys": ["строки"] или null,
    "x": число или null,
    "y": число или null,
    "milliseconds": число или null
  },
  "final_response": "строка или null"
}

Правила:
- Делай ровно одно действие за шаг.
- Если нужно открыть приложение, используй open_app.
- Если нужно ввести текст в уже активное окно, используй type_text.
- Если нужно нажать Enter/Tab/Escape и т.п., используй press_key.
- Если нужно сочетание клавиш, используй key_combo.
- Если нужно нажать в конкретную точку на скрине, используй click. Координаты относительные к присланному изображению.
- Если задача завершена, используй finish и дай final_response.
- Если сперва нужно просто посмотреть/подтвердить состояние, используй observe.
""";
    }

    private static string BuildDecisionUserPrompt(AgentSessionState session, string prompt, int step, ScreenSnapshot snapshot, string analysis)
    {
        var history = string.Join("\n", session.History.TakeLast(12));
        return $"""
Текущий запрос пользователя: {prompt}
Номер шага: {step}
Размер скриншота: {snapshot.Width}x{snapshot.Height}
История этой сессии:
{history}

Дополнительный анализ:
{analysis}

Посмотри на скриншот и выбери ОДИН следующий шаг.
""";
    }

    private static AgentDecision ParseDecision(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var decision = JsonSerializer.Deserialize<AgentDecision>(json, options)
            ?? throw new InvalidOperationException("Failed to deserialize agent decision.");

        decision.Action ??= new AgentAction { Type = "observe" };
        decision.Action.Type = string.IsNullOrWhiteSpace(decision.Action.Type) ? "observe" : decision.Action.Type.Trim().ToLowerInvariant();
        return decision;
    }

    private static void AppendThought(StringBuilder builder, int step, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine().AppendLine();
        }

        builder.Append($"Шаг {step}. {line.Trim()}");
    }
}

public sealed class AgentSessionState
{
    public List<string> History { get; } = [];

    public void Reset()
    {
        History.Clear();
    }
}

public sealed class AgentDecision
{
    public string Thought { get; set; } = string.Empty;

    public AgentAction? Action { get; set; }

    public string? FinalResponse { get; set; }
}

public sealed class AgentAction
{
    public string Type { get; set; } = "observe";

    public string? Target { get; set; }

    public string? Text { get; set; }

    public string? Key { get; set; }

    public List<string>? Keys { get; set; }

    public int? X { get; set; }

    public int? Y { get; set; }

    public int? Milliseconds { get; set; }

    public string? FinalResponse { get; set; }
}

public sealed record AgentLoopProgress(string Status, string Thinking, string Answer);
public sealed record AgentLoopResult(string Thinking, string Answer, string Error);
