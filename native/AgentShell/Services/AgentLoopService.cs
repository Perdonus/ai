using System.Diagnostics;
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
    private readonly DesktopContextService _context = new();
    private readonly ClipboardService _clipboard = new();
    private readonly RuntimeToolService _runtimeTools = new();

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
        var runtimeTools = await _runtimeTools.LoadAsync();
        var toolPrompt = _runtimeTools.BuildToolPrompt(runtimeTools);
        var simpleOpenRequest = LooksLikeSimpleOpenRequest(prompt);

        for (var step = 1; step <= 8; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentLoopProgress($"Шаг {step}: смотрю на экран", visibleThoughts.ToString(), finalAnswer));
            var snapshot = _screen.Capture();
            var context = _context.Capture();
            var clipboardPreview = _clipboard.GetPreview();

            string analysis = string.Empty;
            var analysisRoute = _chat.ResolveAnalysisRoute(config);
            if (analysisRoute is not null)
            {
                var analysisPrompt = BuildAnalysisPrompt(session, prompt, step, context, clipboardPreview);
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

            var decision = await DecideNextActionAsync(
                config,
                session,
                prompt,
                step,
                snapshot,
                context,
                clipboardPreview,
                analysis,
                toolPrompt,
                cancellationToken);
            StartupLogService.Info($"Step {step} decision: {decision.Action.Type}; target={decision.Action.Target ?? "(none)"}; thought={TrimForLog(decision.Thought)}");

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

            if (simpleOpenRequest &&
                decision.Action.Type == "open_app" &&
                !string.IsNullOrWhiteSpace(decision.Action.Target) &&
                await OpenedAppLooksReadyAsync(decision.Action.Target, cancellationToken))
            {
                finalAnswer = $"Открыл {decision.Action.Target}.";
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty);
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

        finalAnswer = "Не успел закончить за лимит шагов. Сформулируй задачу точнее или продолжи её следующим сообщением.";
        session.History.Add($"Ассистент: {finalAnswer}");
        return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty);
    }

    private async Task<AgentDecision> DecideNextActionAsync(
        ShellConfig config,
        AgentSessionState session,
        string prompt,
        int step,
        ScreenSnapshot snapshot,
        DesktopContextSnapshot context,
        string clipboardPreview,
        string analysis,
        string toolPrompt,
        CancellationToken cancellationToken)
    {
        var route = _chat.ResolveVisionRoute(config);
        var systemPrompt = BuildDecisionSystemPrompt(toolPrompt);
        var withScreenshotPrompt = BuildDecisionUserPrompt(
            session,
            prompt,
            step,
            snapshot,
            context,
            clipboardPreview,
            analysis,
            screenshotAttached: true);

        if (_chat.CanLikelyUseImages(route))
        {
            try
            {
                var json = await _chat.RequestJsonAsync(config, route, systemPrompt, withScreenshotPrompt, snapshot, cancellationToken);
                return ParseDecision(json);
            }
            catch (Exception ex) when (LooksLikeImageCapabilityFailure(ex))
            {
                StartupLogService.Warn($"Falling back to text-only planning for {route.Provider}/{route.Model}: {ex.Message}");
            }
        }
        else
        {
            StartupLogService.Warn($"Skipping image input for likely text-only model {route.Provider}/{route.Model}.");
        }

        var textOnlyPrompt = BuildDecisionUserPrompt(
            session,
            prompt,
            step,
            snapshot,
            context,
            clipboardPreview,
            analysis,
            screenshotAttached: false);

        var fallbackJson = await _chat.RequestJsonAsync(config, route, systemPrompt, textOnlyPrompt, null, cancellationToken);
        return ParseDecision(fallbackJson);
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
            case "open_url":
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    throw new InvalidOperationException("open_url requires target");
                }

                await _desktop.OpenUrlAsync(action.Target, cancellationToken);
                return $"Открыл URL {action.Target}";
            case "open_path":
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    throw new InvalidOperationException("open_path requires target");
                }

                await _desktop.OpenPathAsync(action.Target, cancellationToken);
                return $"Открыл путь {action.Target}";
            case "type_text":
                if (string.IsNullOrWhiteSpace(action.Text))
                {
                    throw new InvalidOperationException("type_text requires text");
                }

                _input.TypeText(action.Text);
                return $"Ввел текст: {action.Text}";
            case "set_clipboard":
                if (action.Text is null)
                {
                    throw new InvalidOperationException("set_clipboard requires text");
                }

                _clipboard.SetText(action.Text);
                return $"Записал в буфер обмена: {_clipboard.GetPreview()}";
            case "paste_clipboard":
                _input.PressKeyCombo(["CTRL", "V"]);
                return "Вставил буфер обмена";
            case "copy_selection":
                _input.PressKeyCombo(["CTRL", "C"]);
                await _input.WaitAsync(180, cancellationToken);
                return $"Скопировал выделение: {_clipboard.GetPreview()}";
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
            case "run_tool":
                if (string.IsNullOrWhiteSpace(action.Target))
                {
                    throw new InvalidOperationException("run_tool requires target");
                }

                var toolOutput = await _runtimeTools.ExecuteAsync(action.Target, action.Arguments, cancellationToken);
                return $"Запустил тулз {action.Target}: {toolOutput}";
            default:
                throw new InvalidOperationException($"Unsupported agent action: {action.Type}");
        }
    }

    private static string BuildAnalysisPrompt(
        AgentSessionState session,
        string prompt,
        int step,
        DesktopContextSnapshot context,
        string clipboardPreview)
    {
        return $"Текущий запрос пользователя: {prompt}\nНомер шага: {step}\nИстория диалога:\n{string.Join("\n", session.History.TakeLast(12))}\nТекущий desktop context:\n{context.ToPromptString(clipboardPreview)}\nДай краткую мысль о следующем шаге.";
    }

    private static string BuildDecisionSystemPrompt(string toolPrompt)
    {
        return $@"Ты desktop-агент на Windows. Ты работаешь пошагово: смотришь на экран, думаешь, делаешь одно действие, потом снова смотришь.
Никогда не пытайся решить всю задачу одним сообщением.
Отвечай строго JSON-объектом без markdown.

Формат:
{{
  ""thought"": ""краткая мысль на русском"",
  ""action"": {{
    ""type"": ""observe|open_app|open_url|open_path|type_text|set_clipboard|paste_clipboard|copy_selection|press_key|key_combo|click|wait|run_tool|finish"",
    ""target"": ""строка или null"",
    ""text"": ""строка или null"",
    ""key"": ""строка или null"",
    ""keys"": [""строки""] или null,
    ""x"": число или null,
    ""y"": число или null,
    ""milliseconds"": число или null,
    ""arguments"": {{""key"":""value""}} или null
  }},
  ""final_response"": ""строка или null""
}}

Правила:
- Делай ровно одно действие за шаг.
- Если нужно открыть приложение, используй open_app.
- Если нужно открыть сайт, используй open_url.
- Если нужно открыть файл или папку, используй open_path.
- Если нужно ввести текст в активное окно, используй type_text.
- Если нужно положить текст в буфер обмена, используй set_clipboard.
- Если нужно вставить буфер обмена, используй paste_clipboard.
- Если нужно скопировать выделение, используй copy_selection.
- Если нужно нажать Enter/Tab/Escape и т.п., используй press_key.
- Если нужно сочетание клавиш, используй key_combo.
- Если нужно нажать в конкретную точку на скрине, используй click. Координаты относительные к присланному изображению.
- Если нужно запустить runtime tool, используй run_tool, где target = id тулза, а arguments = объект параметров.
- Если задача завершена, используй finish и дай final_response.
- Не придумывай лишние действия. Если пользователь просил только открыть приложение, сайт, файл или папку, после успешного открытия сразу заверши задачу.
- Не используй type_text, set_clipboard, paste_clipboard или copy_selection, если пользователь прямо не просил текст, ввод или работу с буфером.
- Если сперва нужно просто посмотреть/подтвердить состояние, используй observe.
- Если скриншота нет, опирайся на текстовый desktop context: foreground window, список окон, буфер обмена и историю.

{toolPrompt}
";
    }

    private static string BuildDecisionUserPrompt(
        AgentSessionState session,
        string prompt,
        int step,
        ScreenSnapshot snapshot,
        DesktopContextSnapshot context,
        string clipboardPreview,
        string analysis,
        bool screenshotAttached)
    {
        var history = string.Join("\n", session.History.TakeLast(12));
        return $"""
Текущий запрос пользователя: {prompt}
Номер шага: {step}
Размер скриншота: {snapshot.Width}x{snapshot.Height}
Скриншот приложен: {(screenshotAttached ? "да" : "нет")}
История этой сессии:
{history}

Desktop context:
{context.ToPromptString(clipboardPreview)}

Дополнительный анализ:
{analysis}

Выбери ОДИН следующий шаг.
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
        decision.Action.Arguments ??= [];
        return decision;
    }

    private static bool LooksLikeImageCapabilityFailure(Exception exception)
    {
        return exception.Message.Contains("image input is not enabled", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSimpleOpenRequest(string prompt)
    {
        var normalized = prompt.Trim().ToLowerInvariant();
        if (normalized.Length > 80 || normalized.Contains('\n') || normalized.Contains(','))
        {
            return false;
        }

        return normalized.StartsWith("открой ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("запусти ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("open ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("launch ", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> OpenedAppLooksReadyAsync(string target, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (IsAppLikelyRunning(target))
            {
                return true;
            }

            await _input.WaitAsync(200, cancellationToken);
        }

        return false;
    }

    private static bool IsAppLikelyRunning(string target)
    {
        foreach (var processName in CandidateProcessNames(target))
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0)
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateProcessNames(string target)
    {
        var normalized = Path.GetFileNameWithoutExtension(target.Trim()).ToLowerInvariant();
        return normalized switch
        {
            "notepad" => ["notepad"],
            "calc" => ["CalculatorApp", "calc"],
            "wt" => ["WindowsTerminal", "wt"],
            "powershell" => ["powershell", "pwsh"],
            "cmd" => ["cmd"],
            "mspaint" => ["mspaint"],
            "msedge" => ["msedge"],
            "chrome" => ["chrome"],
            _ => [normalized]
        };
    }

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= 220 ? singleLine : $"{singleLine[..220]}...";
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

    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed record AgentLoopProgress(string Status, string Thinking, string Answer);
public sealed record AgentLoopResult(string Thinking, string Answer, string Error);
