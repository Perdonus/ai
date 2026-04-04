using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentLoopService
{
    private const int MaxSteps = 14;

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
        var repeatedActionCount = 0;
        var lastActionSignature = string.Empty;

        for (var step = 1; step <= MaxSteps; step++)
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
                    "Ты внутренний анализатор desktop-агента Windows. Смотри на текущий контекст и скажи, какой один следующий шаг сейчас самый разумный. Пиши коротко, по-русски, без болтовни.",
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

            StartupLogService.Info(
                $"Step {step} decision: {decision.Action.Type}; target={decision.Action.Target ?? "(none)"}; thought={TrimForLog(decision.Thought)}");

            if (_chat.ShouldShowPrimaryThinking(config) && !string.IsNullOrWhiteSpace(decision.Thought))
            {
                AppendThought(visibleThoughts, step, decision.Thought);
                progress?.Report(new AgentLoopProgress($"Шаг {step}: думаю", visibleThoughts.ToString(), finalAnswer));
            }

            if (decision.Action.Type == "await_user")
            {
                finalAnswer = ResolveAwaitUserMessage(decision);
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, true);
            }

            var actionSignature = BuildActionSignature(decision.Action);
            if (actionSignature == lastActionSignature)
            {
                repeatedActionCount++;
            }
            else
            {
                lastActionSignature = actionSignature;
                repeatedActionCount = 1;
            }

            if (repeatedActionCount >= 3 && decision.Action.Type != "finish")
            {
                finalAnswer = ResolveStuckMessage(decision);
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, true);
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
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, false);
            }

            if (decision.Action.Type == "finish")
            {
                finalAnswer = string.IsNullOrWhiteSpace(decision.FinalResponse)
                    ? "Готово."
                    : decision.FinalResponse;
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, false);
            }

            await _input.WaitAsync(450, cancellationToken);
        }

        finalAnswer = "Остановилась по лимиту шагов. Если хочешь продолжить, дай уточнение или следующий запрос.";
        session.History.Add($"Ассистент: {finalAnswer}");
        return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, false);
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
                return $"Подождала {Math.Clamp(action.Milliseconds ?? 600, 50, 5000)} мс";
            case "open_app":
                EnsureTarget(action, "open_app");
                await _desktop.OpenAppVisualAsync(action.Target!, cancellationToken);
                return $"Открыла {action.Target}";
            case "open_url":
                EnsureTarget(action, "open_url");
                await _desktop.OpenUrlAsync(action.Target!, cancellationToken);
                return $"Открыла URL {action.Target}";
            case "open_path":
                EnsureTarget(action, "open_path");
                await _desktop.OpenPathAsync(action.Target!, cancellationToken);
                return $"Открыла путь {action.Target}";
            case "type_text":
                EnsureText(action, "type_text");
                _input.TypeText(action.Text!);
                return $"Ввела текст: {action.Text}";
            case "set_clipboard":
                EnsureText(action, "set_clipboard");
                _clipboard.SetText(action.Text!);
                return $"Записала в буфер обмена: {_clipboard.GetPreview()}";
            case "paste_clipboard":
                _input.PressKeyCombo(["CTRL", "V"]);
                return "Вставила буфер обмена";
            case "copy_selection":
                _input.PressKeyCombo(["CTRL", "C"]);
                await _input.WaitAsync(180, cancellationToken);
                return $"Скопировала выделение: {_clipboard.GetPreview()}";
            case "press_key":
                EnsureKey(action, "press_key");
                _input.PressKey(action.Key!);
                return $"Нажала {action.Key}";
            case "key_combo":
                if (action.Keys is null || action.Keys.Count == 0)
                {
                    throw new InvalidOperationException("key_combo requires keys");
                }

                _input.PressKeyCombo(action.Keys);
                return $"Нажала комбинацию {string.Join("+", action.Keys)}";
            case "key_down":
                EnsureKey(action, "key_down");
                _input.KeyDown(action.Key!);
                return $"Зажала {action.Key}";
            case "key_up":
                EnsureKey(action, "key_up");
                _input.KeyUp(action.Key!);
                return $"Отпустила {action.Key}";
            case "hold_key":
                EnsureKey(action, "hold_key");
                await _input.HoldKeyAsync(action.Key!, action.Milliseconds ?? 500, cancellationToken);
                return $"Удерживала {action.Key} {Math.Clamp(action.Milliseconds ?? 500, 50, 5000)} мс";
            case "mouse_move":
                {
                    var point = ResolvePoint(snapshot, action.X, action.Y, "mouse_move");
                    _input.MoveMouse(point.X, point.Y);
                    return $"Передвинула мышь в {action.X},{action.Y}";
                }
            case "mouse_down":
                MoveMouseIfNeeded(snapshot, action);
                _input.MouseDown(NormalizeButton(action.Button));
                return $"Зажала кнопку мыши {NormalizeButton(action.Button)}";
            case "mouse_up":
                MoveMouseIfNeeded(snapshot, action);
                _input.MouseUp(NormalizeButton(action.Button));
                return $"Отпустила кнопку мыши {NormalizeButton(action.Button)}";
            case "click":
                MoveMouseIfNeeded(snapshot, action);
                _input.Click(NormalizeButton(action.Button));
                return DescribeMouseAction("Кликнула", action);
            case "right_click":
                MoveMouseIfNeeded(snapshot, action);
                _input.Click("right");
                return DescribeMouseAction("Кликнула правой кнопкой", action);
            case "double_click":
                MoveMouseIfNeeded(snapshot, action);
                if (action.X is not null && action.Y is not null)
                {
                    var point = ResolvePoint(snapshot, action.X, action.Y, "double_click");
                    _input.DoubleClick(point.X, point.Y, NormalizeButton(action.Button));
                }
                else
                {
                    _input.Click(NormalizeButton(action.Button));
                    _input.Click(NormalizeButton(action.Button));
                }

                return DescribeMouseAction("Сделала двойной клик", action);
            case "drag":
                {
                    var from = ResolvePoint(snapshot, action.X, action.Y, "drag");
                    var to = ResolvePoint(snapshot, action.X2, action.Y2, "drag");
                    await _input.DragAsync(
                        from.X,
                        from.Y,
                        to.X,
                        to.Y,
                        action.Milliseconds ?? 450,
                        NormalizeButton(action.Button),
                        cancellationToken);
                    return $"Протащила мышь из {action.X},{action.Y} в {action.X2},{action.Y2}";
                }
            case "scroll":
                MoveMouseIfNeeded(snapshot, action);
                _input.Scroll(action.Delta ?? -120);
                return $"Прокрутила колесо на {action.Delta ?? -120}";
            case "run_tool":
                EnsureTarget(action, "run_tool");
                var toolOutput = await _runtimeTools.ExecuteAsync(action.Target!, action.Arguments, cancellationToken);
                return $"Запустила тулз {action.Target}: {toolOutput}";
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
        return $"""
Текущий запрос пользователя: {prompt}
Номер шага: {step}
История этой сессии:
{string.Join("\n", session.History.TakeLast(12))}

Desktop context:
{context.ToPromptString(clipboardPreview)}

Дай краткую мысль о следующем одном шаге.
""";
    }

    private static string BuildDecisionSystemPrompt(string toolPrompt)
    {
        return $$"""
Ты не текстовый чат-бот. Ты desktop-агент на Windows, который реально управляет ПК.
Ты видишь экран, анализируешь контекст, помнишь текущую сессию и делаешь действия руками: мышью, клавиатурой, буфером обмена, запуском приложений, открытием файлов, сайтов и runtime-тулзов.
Все твои действия должны быть визуальными и пошаговыми: сначала оцени состояние, потом сделай один шаг, потом снова оцени.
Если пользователь просит что-то написать, стереть, выделить, вставить, нарисовать, открыть, перетащить, прокрутить или заполнить, делай это через действия ввода и навигации.
Если тебе не хватает обязательных данных от пользователя, не зацикливайся. Используй await_user и остановись до следующего сообщения пользователя.
Отвечай строго JSON-объектом без markdown, комментариев и лишнего текста.

Формат:
{
  "thought": "краткая мысль на русском",
  "action": {
    "type": "observe|await_user|open_app|open_url|open_path|type_text|set_clipboard|paste_clipboard|copy_selection|press_key|key_combo|key_down|key_up|hold_key|mouse_move|mouse_down|mouse_up|click|right_click|double_click|drag|scroll|wait|run_tool|finish",
    "target": "строка или null",
    "text": "строка или null",
    "key": "строка или null",
    "keys": ["строки"] или null,
    "button": "left|right или null",
    "x": число или null,
    "y": число или null,
    "x2": число или null,
    "y2": число или null,
    "delta": число или null,
    "milliseconds": число или null,
    "arguments": {"key":"value"} или null
  },
  "final_response": "строка или null"
}

Правила:
- Делай ровно одно действие за шаг.
- Если нужно просто посмотреть или перепроверить состояние, используй observe.
- Если нужна информация, секрет, код, подтверждение, выбор или данные, которые должен дать пользователь, используй await_user.
- Если задача завершена, используй finish и дай final_response.
- Если надо открыть приложение, используй open_app.
- Если надо открыть сайт, используй open_url.
- Если надо открыть файл или папку, используй open_path.
- Если надо ввести текст в активное окно, используй type_text.
- Для буфера обмена используй set_clipboard, paste_clipboard и copy_selection.
- Для клавиатуры используй press_key, key_combo, key_down, key_up, hold_key.
- Для мыши используй mouse_move, mouse_down, mouse_up, click, right_click, double_click, drag, scroll.
- Для рисования и выделения чаще всего подходят drag, mouse_down/mouse_up и клавиши-модификаторы.
- Координаты x/y/x2/y2 относительные к присланному скриншоту.
- Не повторяй один и тот же шаг бесконечно. Если упёрлась в необходимость данных или подтверждения, остановись через await_user.
- Если пользователь просил только открыть приложение, сайт, файл или папку, после успешного открытия заверши задачу.
- Помни, что ты агент на реальном ПК. Не отвечай как обычный чат без действий, если задачу нужно выполнить руками.

{{toolPrompt}}
""";
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
        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), "\\s+", " ");
        if (normalized.Length > 80 ||
            normalized.Contains('\n') ||
            normalized.Contains(',') ||
            normalized.Contains(';') ||
            normalized.Contains(" и ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" then ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" потом ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" затем ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 4)
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

    private static string ResolveAwaitUserMessage(AgentDecision decision)
    {
        var message = decision.Action.Text ??
                      decision.FinalResponse ??
                      decision.Action.FinalResponse ??
                      decision.Action.Target ??
                      decision.Thought;

        return string.IsNullOrWhiteSpace(message)
            ? "Нужны данные от тебя, чтобы продолжить."
            : message.Trim();
    }

    private static string ResolveStuckMessage(AgentDecision decision)
    {
        var detail = decision.Action.Type switch
        {
            "observe" => "Я несколько раз подряд пыталась только наблюдать.",
            "wait" => "Я несколько раз подряд только ждала.",
            _ => $"Я начала повторять один и тот же шаг: {decision.Action.Type}."
        };

        return $"{detail} Нужны уточнение, данные или следующий запрос от тебя.";
    }

    private static string BuildActionSignature(AgentAction action)
    {
        var keys = action.Keys is null ? string.Empty : string.Join("+", action.Keys);
        var arguments = action.Arguments is null
            ? string.Empty
            : string.Join("&", action.Arguments.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

        return string.Join(
            "|",
            action.Type,
            action.Target ?? string.Empty,
            action.Text ?? string.Empty,
            action.Key ?? string.Empty,
            keys,
            action.Button ?? string.Empty,
            action.X?.ToString() ?? string.Empty,
            action.Y?.ToString() ?? string.Empty,
            action.X2?.ToString() ?? string.Empty,
            action.Y2?.ToString() ?? string.Empty,
            action.Delta?.ToString() ?? string.Empty,
            action.Milliseconds?.ToString() ?? string.Empty,
            arguments);
    }

    private static void EnsureTarget(AgentAction action, string actionType)
    {
        if (string.IsNullOrWhiteSpace(action.Target))
        {
            throw new InvalidOperationException($"{actionType} requires target");
        }
    }

    private static void EnsureText(AgentAction action, string actionType)
    {
        if (string.IsNullOrWhiteSpace(action.Text))
        {
            throw new InvalidOperationException($"{actionType} requires text");
        }
    }

    private static void EnsureKey(AgentAction action, string actionType)
    {
        if (string.IsNullOrWhiteSpace(action.Key))
        {
            throw new InvalidOperationException($"{actionType} requires key");
        }
    }

    private static (int X, int Y) ResolvePoint(ScreenSnapshot snapshot, int? x, int? y, string actionType)
    {
        if (x is null || y is null)
        {
            throw new InvalidOperationException($"{actionType} requires x and y");
        }

        return (snapshot.Left + x.Value, snapshot.Top + y.Value);
    }

    private void MoveMouseIfNeeded(ScreenSnapshot snapshot, AgentAction action)
    {
        if (action.X is null || action.Y is null)
        {
            return;
        }

        var point = ResolvePoint(snapshot, action.X, action.Y, action.Type);
        _input.MoveMouse(point.X, point.Y);
    }

    private static string NormalizeButton(string? button)
    {
        return string.IsNullOrWhiteSpace(button) ? "left" : button.Trim().ToLowerInvariant();
    }

    private static string DescribeMouseAction(string verb, AgentAction action)
    {
        return action.X is not null && action.Y is not null
            ? $"{verb} по координатам {action.X},{action.Y}"
            : verb;
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

    public string? Button { get; set; }

    public int? X { get; set; }

    public int? Y { get; set; }

    public int? X2 { get; set; }

    public int? Y2 { get; set; }

    public int? Delta { get; set; }

    public int? Milliseconds { get; set; }

    public string? FinalResponse { get; set; }

    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed record AgentLoopProgress(string Status, string Thinking, string Answer);
public sealed record AgentLoopResult(string Thinking, string Answer, string Error, bool WaitingForUser);
