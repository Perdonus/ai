using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentLoopService
{
    private const int MaxSteps = 12;

    private readonly AgentChatService _chat = new();
    private readonly ScreenCaptureService _screen = new();
    private readonly DesktopActionService _desktop = new();
    private readonly InputAutomationService _input = new();
    private readonly DesktopContextService _context = new();
    private readonly ClipboardService _clipboard = new();
    private readonly RuntimeToolService _runtimeTools = new();
    private readonly RuntimeCatalogService _runtimeCatalog = App.RuntimeCatalog;
    private readonly RuntimeWidgetService _widgets = new();
    private readonly TesseractOcrService _ocr = new();
    private readonly McpThinkingService _mcpThinking = new();

    public async Task<AgentLoopResult> RunAsync(
        ShellConfig config,
        AgentSessionState session,
        string prompt,
        IProgress<AgentLoopProgress>? progress,
        CancellationToken cancellationToken)
    {
        session.History.Add($"Пользователь: {prompt}");

        var memory = App.LongTermMemory;
        memory.EnsureLoaded();
        await memory.ApplyHeuristicsAsync(prompt, cancellationToken);
        var resolvedPrompt = memory.RewritePromptWithDefaults(prompt);
        var memoryPrompt = memory.BuildPrompt();

        var visibleThoughts = new StringBuilder();
        string finalAnswer = string.Empty;
        var runtimeTools = await _runtimeTools.LoadAsync();
        var runtimeWidgets = await _runtimeCatalog.LoadWidgetsAsync();
        var toolPrompt = _runtimeTools.BuildToolPrompt(runtimeTools);
        var widgetPrompt = _runtimeCatalog.BuildWidgetPrompt(runtimeWidgets);
        var simpleOpenRequest = LooksLikeSimpleOpenRequest(resolvedPrompt);
        var directDesktopRequest = simpleOpenRequest || LooksLikeDirectBrowserSearchRequest(resolvedPrompt);
        var repeatedActionCount = 0;
        var lastActionSignature = string.Empty;

        if (directDesktopRequest)
        {
            var directResult = await _desktop.TryHandleAsync(resolvedPrompt, cancellationToken);
            if (directResult is not null)
            {
                AppendThought(visibleThoughts, $"Локально распознала прямую команду: {directResult.Message}");
                session.History.Add($"Ассистент: {directResult.Message}");
                return new AgentLoopResult(visibleThoughts.ToString(), directResult.Message, string.Empty, false);
            }
        }

        for (var step = 1; step <= MaxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentLoopProgress("Смотрю на экран", visibleThoughts.ToString(), finalAnswer));

            var snapshot = _screen.Capture();
            var context = _context.Capture();
            var clipboardPreview = _clipboard.GetPreview();
            var ocrText = string.Empty;
            var mcpSupplement = string.Empty;

            string analysis = string.Empty;
            var analysisRoute = _chat.ResolveAnalysisRoute(config);
            try
            {
                try
                {
                    progress?.Report(new AgentLoopProgress("Читаю текст на экране", visibleThoughts.ToString(), finalAnswer));
                    ocrText = NormalizeOcrText(await _ocr.RecognizeAsync(snapshot, cancellationToken));
                }
                catch (Exception ex)
                {
                    StartupLogService.Warn($"Tesseract OCR failed: {ex.Message}");
                    ocrText = string.Empty;
                }

                if (config.Models.PrimaryMcpThinking || config.Models.AnalysisMcpThinking)
                {
                    mcpSupplement = _mcpThinking.BuildSupplement(resolvedPrompt, step, context, clipboardPreview, ocrText, runtimeTools, runtimeWidgets);
                    AppendThought(visibleThoughts, "MCP: сверила следующий ход через локальный desktop overlay.");
                    progress?.Report(new AgentLoopProgress("Сверяю ход через MCP", visibleThoughts.ToString(), finalAnswer));
                }

                if (ShouldRunSeparateAnalysis(config, analysisRoute, step))
                {
                    var analysisPrompt = BuildAnalysisPrompt(
                        session,
                        resolvedPrompt,
                        step,
                        context,
                        clipboardPreview,
                        ocrText,
                        memoryPrompt,
                        config.Models.AnalysisMcpThinking ? mcpSupplement : string.Empty);
                    analysis = await _chat.RequestTextAsync(
                        config,
                        analysisRoute!,
                        "Ты внутренний анализатор desktop-агента Windows. Смотри на текущий контекст и скажи, какой один следующий шаг сейчас самый разумный. Пиши коротко, по-русски, без болтовни.",
                        analysisPrompt,
                        cancellationToken);

                    if (_chat.ShouldShowAnalysisThinking(config))
                    {
                        AppendThought(visibleThoughts, analysis);
                        progress?.Report(new AgentLoopProgress("Планирую следующий ход", visibleThoughts.ToString(), finalAnswer));
                    }
                }

                var decision = await DecideNextActionAsync(
                    config,
                    session,
                    resolvedPrompt,
                    step,
                    snapshot,
                    context,
                    clipboardPreview,
                    ocrText,
                    analysis,
                    memoryPrompt,
                    config.Models.PrimaryMcpThinking ? mcpSupplement : string.Empty,
                    toolPrompt,
                    widgetPrompt,
                    cancellationToken);

                StartupLogService.Info(
                    $"Step {step} decision: {decision.Action.Type}; target={decision.Action.Target ?? "(none)"}; thought={TrimForLog(decision.Thought)}");

                if (_chat.ShouldShowPrimaryThinking(config) && !string.IsNullOrWhiteSpace(decision.Thought))
                {
                    AppendThought(visibleThoughts, decision.Thought);
                    progress?.Report(new AgentLoopProgress(string.IsNullOrWhiteSpace(decision.Thought) ? "Думаю" : decision.Thought, visibleThoughts.ToString(), finalAnswer));
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

                var repetitionLimit = decision.Action.Type is "observe" or "wait" ? 2 : 3;
                if (repeatedActionCount >= repetitionLimit && decision.Action.Type != "finish")
                {
                    finalAnswer = ResolveStuckMessage(decision);
                    session.History.Add($"Ассистент: {finalAnswer}");
                    return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, true);
                }

                var actionSummary = await ExecuteActionAsync(decision.Action, snapshot, cancellationToken);
                session.History.Add($"Мысль: {decision.Thought}");
                session.History.Add($"Сделано: {actionSummary}");

                if (!string.IsNullOrWhiteSpace(actionSummary))
                {
                    AppendThought(visibleThoughts, $"Сделала: {actionSummary}");
                    progress?.Report(new AgentLoopProgress(actionSummary, visibleThoughts.ToString(), finalAnswer));
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

                if (simpleOpenRequest && decision.Action.Type is "open_browser" or "open_url" or "open_path")
                {
                    finalAnswer = decision.Action.Type switch
                    {
                        "open_browser" => string.IsNullOrWhiteSpace(decision.Action.Target)
                            ? "Открыла браузер."
                            : $"Открыла браузер: {decision.Action.Target}.",
                        "open_url" => $"Открыла {decision.Action.Target}.",
                        _ => $"Открыла {decision.Action.Target}."
                    };
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
            }
            catch (ProviderRateLimitException ex)
            {
                finalAnswer = $"Уперлась в лимит провайдера {ex.ProviderKey}. Я остановилась и не стала спамить запросы. Подожди примерно {Math.Max(1, (int)Math.Ceiling(ex.RetryAfter.TotalSeconds))} с и повтори запрос.";
                AppendThought(visibleThoughts, finalAnswer);
                session.History.Add($"Ассистент: {finalAnswer}");
                progress?.Report(new AgentLoopProgress("Пауза из-за лимита провайдера", visibleThoughts.ToString(), finalAnswer));
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
        string ocrText,
        string analysis,
        string memoryPrompt,
        string mcpThinking,
        string toolPrompt,
        string widgetPrompt,
        CancellationToken cancellationToken)
    {
        var route = _chat.ResolveVisionRoute(config);
        var systemPrompt = BuildDecisionSystemPrompt(toolPrompt, widgetPrompt, !string.IsNullOrWhiteSpace(mcpThinking));
        var withScreenshotPrompt = BuildDecisionUserPrompt(
            session,
            prompt,
            step,
            snapshot,
            context,
            clipboardPreview,
            ocrText,
            analysis,
            memoryPrompt,
            mcpThinking,
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
            ocrText,
            analysis,
            memoryPrompt,
            mcpThinking,
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
            case "open_browser":
                await _desktop.OpenBrowserAsync(action.Target, cancellationToken);
                return string.IsNullOrWhiteSpace(action.Target)
                    ? "Открыла браузер"
                    : $"Открыла браузер: {action.Target}";
            case "open_url":
                EnsureTarget(action, "open_url");
                await _desktop.OpenUrlAsync(action.Target!, cancellationToken);
                return $"Открыла URL {action.Target}";
            case "open_path":
                EnsureTarget(action, "open_path");
                await _desktop.OpenPathAsync(action.Target!, cancellationToken);
                return $"Открыла путь {action.Target}";
            case "focus_window":
                EnsureTarget(action, "focus_window");
                var focused = await _desktop.FocusWindowAsync(action.Target!, cancellationToken);
                return focused
                    ? $"Сфокусировала окно {action.Target}"
                    : $"Не нашла окно {action.Target}";
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
            case "mouse_hold":
                MoveMouseIfNeeded(snapshot, action);
                await _input.HoldMouseAsync(NormalizeButton(action.Button), action.Milliseconds ?? 450, cancellationToken);
                return $"Удерживала кнопку мыши {NormalizeButton(action.Button)} {Math.Clamp(action.Milliseconds ?? 450, 50, 5000)} мс";
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
            case "open_widget":
                EnsureTarget(action, "open_widget");
                var widgetLaunch = await _widgets.LaunchByIdAsync(action.Target!, cancellationToken);
                return widgetLaunch;
            case "send_widget_data":
                EnsureTarget(action, "send_widget_data");
                EnsureText(action, "send_widget_data");
                var widgetResult = await _widgets.SendDataAsync(action.Target!, action.Text!, cancellationToken);
                return widgetResult;
            case "remember_memory":
                EnsureTarget(action, "remember_memory");
                EnsureText(action, "remember_memory");
                await App.LongTermMemory.RememberAsync(action.Target!, action.Text!, "agent", cancellationToken);
                return $"Р—Р°РїРѕРјРЅРёР»Р° {action.Target}: {action.Text}";
            case "forget_memory":
                EnsureTarget(action, "forget_memory");
                await App.LongTermMemory.ForgetAsync(action.Target!, cancellationToken);
                return $"Р—Р°Р±С‹Р»Р° {action.Target}";
            default:
                throw new InvalidOperationException($"Unsupported agent action: {action.Type}");
        }
    }

    private static string BuildAnalysisPrompt(
        AgentSessionState session,
        string prompt,
        int step,
        DesktopContextSnapshot context,
        string clipboardPreview,
        string ocrText,
        string memoryPrompt,
        string mcpThinking)
    {
        return $"""
Текущий запрос пользователя: {prompt}
Номер шага: {step}
История этой сессии:
{string.Join("\n", session.History.TakeLast(12))}

Desktop context:
{context.ToPromptString(clipboardPreview)}

OCR со скриншота:
{FormatSupplement(ocrText)}

Long-term memory:
{FormatSupplement(memoryPrompt)}

MCP thinking:
{FormatSupplement(mcpThinking)}

Дай краткую мысль о следующем одном шаге.
""";
    }

    private static string BuildDecisionSystemPrompt(string toolPrompt, string widgetPrompt, bool mcpThinkingEnabled)
    {
        var mcpRule = mcpThinkingEnabled
            ? "MCP thinking overlay is enabled for this step. Use it as extra planning signal, but still return only JSON."
            : string.Empty;

        return $$"""
You are a Windows desktop agent on a real PC, not a normal chat bot.
You can see screenshots, read OCR text, inspect window geometry, remember the current session, keep long-term memory, and act through mouse, keyboard, clipboard, browser, files, runtime tools, and runtime widgets.
When a task must be done on the PC, do it through actions instead of replying with plain chat text.
Work in short visible loops: inspect state, do one observable action, inspect again.
Geometry matters:
- x/y/x2/y2 are relative to the attached screenshot.
- The prompt also contains screenshot origin on the desktop, current monitor bounds, work area, cursor position, visible window rectangles, window centers, and tags like [foreground], [on_current_monitor], [on_screenshot].
- Use that geometry to reason about sliders, edges, drag paths, screen proportions, and which monitor you are on.
- If a control is ambiguous, prefer observe before clicking.
If required user data is missing (login, password, code, captcha, token, confirmation, personal choice), use await_user immediately and stop.
If the provider is rate-limited or the task cannot continue safely, stop cleanly instead of looping.
If a relevant widget is already installed, prefer open_widget or send_widget_data instead of pretending the widget does not exist.
If a widget accepts data input, you may send plain text or JSON payloads exactly as the widget prompt describes.
If the user reveals a stable preference, a default city, or any other durable fact, store it with remember_memory.
{{mcpRule}}
Return only one JSON object and nothing else. No markdown.

JSON schema:
{
  "thought": "brief reasoning in Russian",
  "action": {
    "type": "observe|await_user|open_app|open_browser|open_url|open_path|focus_window|type_text|set_clipboard|paste_clipboard|copy_selection|press_key|key_combo|key_down|key_up|hold_key|mouse_move|mouse_down|mouse_up|mouse_hold|click|right_click|double_click|drag|scroll|wait|run_tool|open_widget|send_widget_data|remember_memory|forget_memory|finish",
    "target": "string or null",
    "text": "string or null",
    "key": "string or null",
    "keys": ["strings"] or null,
    "button": "left|right|middle or null",
    "x": number or null,
    "y": number or null,
    "x2": number or null,
    "y2": number or null,
    "delta": number or null,
    "milliseconds": number or null,
    "arguments": {"key":"value"} or null
  },
  "final_response": "string or null"
}

Rules:
- Do exactly one action per step.
- Use observe when you need to verify screen state before acting.
- Use finish only when the desktop task is actually complete.
- Use focus_window to switch to an already open app or document.
- Use open_browser or open_url for browser navigation.
- Use drag, mouse_down/mouse_up, and modifier keys for selection, drawing, slider movement, and resize gestures.
- For browser tasks, prefer real keyboard and mouse actions after the browser is open.
- Use remember_memory target=<stable_key> text=<stable_value> for durable user facts and preferences.
- Use send_widget_data when the request is better solved by updating an existing widget instead of typing into some unrelated app.
- Do not repeat the same ineffective action forever. If you are blocked, use await_user.
- If the user asked only to open something simple and it is open, finish.
- Think and final_response should be in Russian.

Examples:
- "Открой браузер и найди погоду" -> open_browser or open_url, then key_combo/type_text if needed, then finish.
- "Переключись в блокнот" -> focus_window target="notepad".
- "Открой виджет погоды" -> open_widget target="weather-orbit".
- "Покажи погоду в Кудрово" -> open_widget target="weather-orbit", then send_widget_data target="weather-orbit" text="{\"location\":\"Кудрово\"}".
- "Открой таймер на 5 минут" -> open_widget target="timer-bloom", then send_widget_data target="timer-bloom" text="{\"duration_seconds\":300,\"command\":\"start\"}".
- "Создай заметку купить молоко" -> open_widget target="note-board", then send_widget_data target="note-board" text="купить молоко".
- "Обнови данные виджета" -> send_widget_data with widget id in target and payload in text.

{{toolPrompt}}

{{widgetPrompt}}
""";
    }

    private static string BuildDecisionUserPrompt(
        AgentSessionState session,
        string prompt,
        int step,
        ScreenSnapshot snapshot,
        DesktopContextSnapshot context,
        string clipboardPreview,
        string ocrText,
        string analysis,
        string memoryPrompt,
        string mcpThinking,
        bool screenshotAttached)
    {
        var history = string.Join("\n", session.History.TakeLast(12));
        return $"""
Current user request: {prompt}
Step number: {step}
Screenshot size: {snapshot.Width}x{snapshot.Height}
Screenshot top-left on desktop: {snapshot.Left},{snapshot.Top}
Screenshot attached: {(screenshotAttached ? "yes" : "no")}

Session history:
{(string.IsNullOrWhiteSpace(history) ? "(empty)" : history)}

Desktop context:
{context.ToPromptString(clipboardPreview, new RectSummary(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height))}

OCR from screenshot:
{FormatSupplement(ocrText)}

Long-term memory:
{FormatSupplement(memoryPrompt)}

Extra analysis:
{FormatSupplement(analysis)}

MCP thinking:
{FormatSupplement(mcpThinking)}

Pick exactly one next step.
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
        decision.Action = NormalizeAction(decision, decision.Action);
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

    private static bool LooksLikeDirectBrowserSearchRequest(string prompt)
    {
        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), "\\s+", " ");
        if (normalized.Length is < 12 or > 220)
        {
            return false;
        }

        var hasBrowser = normalized.Contains("брауз", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("brave", StringComparison.OrdinalIgnoreCase);
        var hasSearch = normalized.Contains("поиск", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("найди", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("find ", StringComparison.OrdinalIgnoreCase);

        return hasBrowser && hasSearch;
    }

    private static bool ShouldRunSeparateAnalysis(ShellConfig config, ModelRoute? analysisRoute, int step)
    {
        if (!config.Models.UseSeparateAnalysis || analysisRoute is null)
        {
            return false;
        }

        if (RoutesEqual(analysisRoute, config.Models.Primary))
        {
            return false;
        }

        return step == 1 || step % 3 == 0;
    }

    private static bool RoutesEqual(ModelRoute left, ModelRoute right)
    {
        return string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase);
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
            "browser" => ["msedge", "chrome", "firefox", "brave"],
            "msedge" => ["msedge"],
            "chrome" => ["chrome"],
            "firefox" => ["firefox"],
            "brave" => ["brave"],
            "explorer" => ["explorer"],
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

    private static AgentAction NormalizeAction(AgentDecision decision, AgentAction action)
    {
        var originalType = action.Type;
        action.Type = NormalizeActionType(action.Type);

        if (action.Type == "open_browser")
        {
            action.Target = string.IsNullOrWhiteSpace(action.Target) ? "https://www.google.com" : action.Target;
        }

        if ((action.Type == "open_app" || action.Type == "focus_window" || action.Type == "open_widget" || action.Type == "run_tool") &&
            string.IsNullOrWhiteSpace(action.Target) &&
            !string.IsNullOrWhiteSpace(action.Text))
        {
            action.Target = action.Text;
        }

        if ((action.Type == "remember_memory" || action.Type == "forget_memory") &&
            string.IsNullOrWhiteSpace(action.Target) &&
            action.Arguments is not null &&
            action.Arguments.TryGetValue("key", out var memoryKey) &&
            !string.IsNullOrWhiteSpace(memoryKey))
        {
            action.Target = memoryKey;
        }

        if (action.Type == "remember_memory" &&
            string.IsNullOrWhiteSpace(action.Text) &&
            action.Arguments is not null &&
            action.Arguments.TryGetValue("value", out var memoryValue) &&
            !string.IsNullOrWhiteSpace(memoryValue))
        {
            action.Text = memoryValue;
        }

        if ((action.Type == "press_key" || action.Type == "hold_key") &&
            string.IsNullOrWhiteSpace(action.Key) &&
            !string.IsNullOrWhiteSpace(action.Target))
        {
            action.Key = action.Target;
        }

        if (action.Type == "type_text" &&
            string.IsNullOrWhiteSpace(action.Text) &&
            !string.IsNullOrWhiteSpace(action.Target))
        {
            action.Text = action.Target;
        }

        if (action.Type == "send_widget_data")
        {
            if (string.IsNullOrWhiteSpace(action.Target) &&
                action.Arguments is not null &&
                action.Arguments.TryGetValue("widget", out var widgetId) &&
                !string.IsNullOrWhiteSpace(widgetId))
            {
                action.Target = widgetId;
            }

            if (string.IsNullOrWhiteSpace(action.Text) &&
                action.Arguments is not null &&
                action.Arguments.TryGetValue("payload", out var payload) &&
                !string.IsNullOrWhiteSpace(payload))
            {
                action.Text = payload;
            }
        }

        if (action.Type == "key_combo")
        {
            action.Keys = ExpandCombo(action.Keys, action.Key, action.Target);
        }

        if (string.Equals(originalType, "scroll_up", StringComparison.OrdinalIgnoreCase) && action.Delta is null)
        {
            action.Delta = 360;
        }
        else if (action.Type == "scroll" && action.Delta is null)
        {
            action.Delta = -360;
        }

        if (LooksLikeAwaitUser(decision, action))
        {
            action.Type = "await_user";
            action.Text ??= ExtractAwaitUserText(decision);
        }

        return action;
    }

    private static string NormalizeActionType(string? rawType)
    {
        var normalized = string.IsNullOrWhiteSpace(rawType)
            ? "observe"
            : rawType.Trim().ToLowerInvariant().Replace('-', '_');

        return normalized switch
        {
            "open_application" or "launch_app" or "launch_application" => "open_app",
            "open_browser_tab" or "launch_browser" => "open_browser",
            "activate_window" or "switch_window" or "focus_app" => "focus_window",
            "launch_widget" or "show_widget" or "open_runtime_widget" => "open_widget",
            "widget_data" or "update_widget" or "send_widget_payload" => "send_widget_data",
            "remember" or "save_memory" or "store_memory" or "save_fact" or "memorize" => "remember_memory",
            "forget" or "delete_memory" or "remove_memory" => "forget_memory",
            "type" or "write_text" or "input_text" or "enter_text" => "type_text",
            "press" or "keypress" => "press_key",
            "shortcut" or "hotkey" or "keyboard_combo" or "press_keys" => "key_combo",
            "ask_user" or "request_user_input" or "need_user_input" => "await_user",
            "done" or "complete" or "completed" or "finish_task" => "finish",
            "move_mouse" => "mouse_move",
            "hold_mouse" => "mouse_hold",
            "mouse_drag" or "drag_mouse" or "slide" => "drag",
            "scroll_down" => "scroll",
            "scroll_up" => "scroll",
            "doubleclick" => "double_click",
            "click_right" => "right_click",
            "click_left" => "click",
            _ => normalized
        };
    }

    private static List<string>? ExpandCombo(IReadOnlyList<string>? existingKeys, string? key, string? target)
    {
        if (existingKeys is { Count: > 0 })
        {
            return existingKeys.ToList();
        }

        var source = !string.IsNullOrWhiteSpace(key) ? key : target;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        return source
            .Split(['+', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim().ToUpperInvariant())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }

    private static bool LooksLikeAwaitUser(AgentDecision decision, AgentAction action)
    {
        if (action.Type == "await_user" || action.Type == "finish")
        {
            return false;
        }

        var signal = string.Join(
            " ",
            new[] { decision.Thought, decision.FinalResponse, action.Text, action.Target }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(signal))
        {
            return false;
        }

        if (!(action.Type is "observe" or "wait" or "run_tool"))
        {
            return false;
        }

        string[] markers =
        [
            "нужны данные",
            "нужен логин",
            "нужен пароль",
            "нужен код",
            "нужна капча",
            "нужен api key",
            "нужен api-key",
            "нужен токен",
            "нужно подтверждение",
            "нужен выбор",
            "введи",
            "введите",
            "пришли",
            "скажи",
            "дай мне",
            "подтверди",
            "подтвердите"
        ];

        return markers.Any(signal.Contains);
    }

    private static string ExtractAwaitUserText(AgentDecision decision)
    {
        var message = decision.FinalResponse ??
                      decision.Action?.Text ??
                      decision.Thought;

        return string.IsNullOrWhiteSpace(message)
            ? "Нужны данные от тебя, чтобы продолжить."
            : message.Trim();
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

    private static void AppendThought(StringBuilder builder, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine().AppendLine();
        }

        builder.Append(line.Trim());
    }

    private static string NormalizeOcrText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim()
            .Replace("```", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("OCR:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return cleaned.Length <= 1800 ? cleaned : $"{cleaned[..1800]}...";
    }

    private static string FormatSupplement(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(нет данных)" : value.Trim();
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
