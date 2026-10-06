using System.Text;
using System.Text.RegularExpressions;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class AgentLoopService
{
    /// <summary>EvoCUA emits one short tool call per step, so a small budget is enough.</summary>
    private const int MaxResponseTokens = 512;

    private const int PreviousActionsInPrompt = 12;

    private readonly AgentChatService _chat = new();
    private readonly ScreenCaptureService _screen = new();
    private readonly DesktopActionService _desktop = new();
    private readonly InputAutomationService _input = new();
    private readonly DesktopContextService _context = new();
    private readonly ClipboardService _clipboard = new();
    private readonly RuntimeToolService _runtimeTools = new();
    private readonly RuntimeWidgetService _widgets = new();
    private readonly TesseractOcrService _ocr = new();

    /// <summary>Only one task may drive the desktop at a time, whoever asked for it.</summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public async Task<AgentLoopResult> RunAsync(
        ShellConfig config,
        AgentSessionState session,
        string prompt,
        IProgress<AgentLoopProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
        {
            const string busy = "Агент уже занят другой задачей. Дождись её завершения или отмени её.";
            StartupLogService.Warn("A second agent run was rejected because the desktop is already busy.");
            return new AgentLoopResult(string.Empty, busy, "busy", false);
        }

        try
        {
            return await RunCoreAsync(config, session, prompt, progress, cancellationToken);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<AgentLoopResult> RunCoreAsync(
        ShellConfig config,
        AgentSessionState session,
        string prompt,
        IProgress<AgentLoopProgress>? progress,
        CancellationToken cancellationToken)
    {
        var memory = App.LongTermMemory;
        memory.EnsureLoaded();
        await memory.ApplyHeuristicsAsync(prompt, cancellationToken);
        var resolvedPrompt = memory.RewritePromptWithDefaults(prompt);
        var memoryPrompt = memory.BuildPrompt();

        // Whatever is already on the clipboard belongs to the user, not to this task, so it
        // becomes the baseline and never lands in the notes by accident.
        session.LastClipboardText = TryReadClipboard();

        // Previous actions and notes describe THIS task only. Carrying steps from an older
        // task into the prompt made the model replay them instead of doing the new job.
        session.ActionLog.Clear();
        session.Notes.Clear();
        session.History.Add($"Пользователь: {resolvedPrompt}");
        session.RecordAction($"Instruction: {resolvedPrompt}");

        var maxSteps = Math.Clamp(config.Agent.MaxSteps, 1, 200);
        var stepDelay = Math.Clamp(config.Agent.StepDelayMs, 50, 5000);
        var systemPrompt = EvoCuaPrompt.BuildSystemPrompt();

        var visibleThoughts = new StringBuilder();
        string finalAnswer = string.Empty;

        if (LooksLikeSimpleOpenRequest(resolvedPrompt) || LooksLikeDirectBrowserSearchRequest(resolvedPrompt))
        {
            var directResult = await _desktop.TryHandleAsync(resolvedPrompt, cancellationToken);
            if (directResult is not null)
            {
                AppendThought(visibleThoughts, $"Локально распознала прямую команду: {directResult.Message}");
                session.History.Add($"Ассистент: {directResult.Message}");
                return new AgentLoopResult(visibleThoughts.ToString(), directResult.Message, string.Empty, false);
            }
        }

        var repeatedActionCount = 0;
        var lastActionSignature = string.Empty;
        var recoveries = 0;
        const int maxRecoveries = 3;

        // When the task names something that is already open, or hidden behind other windows,
        // bring it forward for real instead of making the model hunt for a small taskbar icon.
        if (!LooksLikeSimpleOpenRequest(resolvedPrompt) &&
            await TryFocusWindowMentionedInPromptAsync(resolvedPrompt, cancellationToken))
        {
            AppendThought(visibleThoughts, "Сфокусировала окно, которое упомянуто в задаче.");
        }

        for (var step = 1; step <= maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentLoopProgress("Смотрю на экран", visibleThoughts.ToString(), finalAnswer));

            var snapshot = _screen.Capture(config.LocalAi.ImageMaxPixels);
            var screenshotPath = TrySaveSnapshot(config, snapshot, step);
            var extraContext = await BuildExtraContextAsync(
                config,
                snapshot,
                memoryPrompt,
                session,
                screenshotPath,
                cancellationToken);

            var userPrompt = EvoCuaPrompt.BuildUserPrompt(
                resolvedPrompt,
                BuildPreviousActions(session),
                extraContext);

            AgentDecision decision;
            try
            {
                var turns = new[]
                {
                    new ChatTurn("system", systemPrompt),
                    new ChatTurn("user", userPrompt, snapshot.PngBase64)
                };

                var raw = await _chat.RequestAsync(
                    config,
                    turns,
                    MaxResponseTokens,
                    cancellationToken);

                StartupLogService.Info($"EvoCUA raw: {TrimForLog(raw, 500)}");
                decision = EvoCuaResponseParser.Parse(raw, snapshot.Width, snapshot.Height);
            }
            catch (InvalidOperationException ex)
            {
                finalAnswer = $"Локальная модель не ответила: {ex.Message}";
                AppendThought(visibleThoughts, finalAnswer);
                session.History.Add($"Ассистент: {finalAnswer}");
                progress?.Report(new AgentLoopProgress("Ошибка локальной модели", visibleThoughts.ToString(), finalAnswer));
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, ex.Message, false);
            }

            var action = decision.Action ?? new AgentAction { Type = "observe" };
            StartupLogService.Info(
                $"Step {step}/{maxSteps}: {action.Type}; target={action.Target ?? "(none)"}; x={action.X}; y={action.Y}; thought={TrimForLog(decision.Thought)}");

            if (!string.IsNullOrWhiteSpace(decision.Thought))
            {
                AppendThought(visibleThoughts, decision.Thought);
                progress?.Report(new AgentLoopProgress(decision.Thought, visibleThoughts.ToString(), finalAnswer));
            }

            if (action.Type == "await_user")
            {
                finalAnswer = ResolveAwaitUserMessage(decision);
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, true);
            }

            var actionSignature = BuildActionSignature(action);
            if (actionSignature == lastActionSignature)
            {
                repeatedActionCount++;
            }
            else
            {
                lastActionSignature = actionSignature;
                repeatedActionCount = 1;
            }

            var repetitionLimit = action.Type is "observe" or "wait" ? 2 : 3;
            if (repeatedActionCount >= repetitionLimit && action.Type != "finish")
            {
                if (recoveries < maxRecoveries)
                {
                    recoveries++;
                    repeatedActionCount = 0;
                    lastActionSignature = string.Empty;
                    await RecoverFromStuckAsync(config, resolvedPrompt, visibleThoughts, progress, cancellationToken);
                    continue;
                }

                finalAnswer = ResolveStuckMessage(decision);
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, true);
            }

            var actionSummary = await ExecuteActionAsync(action, snapshot, cancellationToken);
            CaptureClipboardIntoNotes(session);
            session.History.Add($"Мысль: {decision.Thought}");
            session.History.Add($"Сделано: {actionSummary}");
            session.RecordAction($"Step {step}: {action.Type} -> {actionSummary}");

            if (!string.IsNullOrWhiteSpace(actionSummary))
            {
                AppendThought(visibleThoughts, $"Сделала: {actionSummary}");
                progress?.Report(new AgentLoopProgress(actionSummary, visibleThoughts.ToString(), finalAnswer));
            }

            if (action.Type == "finish")
            {
                finalAnswer = string.IsNullOrWhiteSpace(decision.FinalResponse)
                    ? "Готово."
                    : decision.FinalResponse;
                session.History.Add($"Ассистент: {finalAnswer}");
                return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, false);
            }

            await _input.WaitAsync(stepDelay, cancellationToken);
        }

        finalAnswer = $"Остановилась по лимиту шагов ({maxSteps}). Если нужно продолжить, дай уточнение.";
        session.History.Add($"Ассистент: {finalAnswer}");
        return new AgentLoopResult(visibleThoughts.ToString(), finalAnswer, string.Empty, false);
    }

    private string TryReadClipboard()
    {
        try
        {
            return _clipboard.GetText();
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Clipboard read failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// EvoCUA already knows how to select and copy. Picking that up automatically turns ctrl+c
    /// into a scratchpad, which is what lets a long task carry text from one app into another.
    /// </summary>
    private void CaptureClipboardIntoNotes(AgentSessionState session)
    {
        var text = TryReadClipboard();
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, session.LastClipboardText, StringComparison.Ordinal))
        {
            return;
        }

        session.LastClipboardText = text;
        session.AddNote(text);
        StartupLogService.Info($"Notepad captured {text.Length} chars from the clipboard.");
    }

    private static string? TrySaveSnapshot(ShellConfig config, ScreenSnapshot snapshot, int step)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(config.Agent.ScreenshotDir)
                ? Path.Combine(Path.GetTempPath(), "DesktopAIAgent", "screenshots")
                : config.Agent.ScreenshotDir;

            var path = ScreenCaptureService.SaveSnapshot(snapshot, directory, $"step-{step:000}");
            ScreenCaptureService.PruneOldSnapshots(directory, Math.Clamp(config.Agent.KeepScreenshots, 1, 200));
            return path;
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Failed to save the screenshot file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Called instead of giving up when the model repeats itself. It dismisses whatever popup
    /// swallowed the clicks, then tries to surface the window the user talked about — which is
    /// how a taskbar or tray window gets found reliably.
    /// </summary>
    private async Task RecoverFromStuckAsync(
        ShellConfig config,
        string prompt,
        StringBuilder visibleThoughts,
        IProgress<AgentLoopProgress>? progress,
        CancellationToken cancellationToken)
    {
        const string note = "Застряла на одном действии — расшевеливаю экран.";
        StartupLogService.Warn("Recovery: repeated action detected, attempting to unstick the desktop.");
        AppendThought(visibleThoughts, note);
        progress?.Report(new AgentLoopProgress(note, visibleThoughts.ToString(), string.Empty));

        _input.PressKey("ESC");
        await _input.WaitAsync(250, cancellationToken);

        if (await TryFocusWindowMentionedInPromptAsync(prompt, cancellationToken))
        {
            AppendThought(visibleThoughts, "Сфокусировала окно, подходящее под запрос.");
        }
        else if (config.Agent.TrayRecovery)
        {
            OpenNotificationArea();
            AppendThought(visibleThoughts, "Открыла область уведомлений, чтобы поискать свёрнутое окно.");
        }

        await _input.WaitAsync(700, cancellationToken);
    }

    private async Task<bool> TryFocusWindowMentionedInPromptAsync(string prompt, CancellationToken cancellationToken)
    {
        var keywords = ExtractPromptKeywords(prompt);
        if (keywords.Count == 0)
        {
            return false;
        }

        IReadOnlyList<WindowSummary> windows;
        try
        {
            windows = _context.Capture().VisibleWindows;
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Window enumeration failed: {ex.Message}");
            return false;
        }

        foreach (var keyword in keywords)
        {
            var match = windows.FirstOrDefault(window =>
                !string.IsNullOrWhiteSpace(window.Title) &&
                window.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                continue;
            }

            if (await _desktop.FocusWindowAsync(match.Title, cancellationToken))
            {
                StartupLogService.Info($"Focused '{match.Title}' for keyword '{keyword}'.");
                return true;
            }
        }

        return false;
    }

    private static List<string> ExtractPromptKeywords(string prompt)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "зайди", "открой", "найди", "собери", "сделай", "потом", "затем", "логи", "логов",
            "мне", "надо", "нужно", "пожалуйста", "туда", "зайти", "перейди", "отправь",
            "open", "find", "collect", "gather", "then", "please", "logs", "from", "into"
        };

        return Regex.Matches(prompt, @"[\p{L}\p{N}]{4,}")
            .Cast<Match>()
            .Select(match => match.Value)
            .Where(word => !stopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    /// <summary>
    /// Best effort click on the notification area chevron, which reveals the icons of apps that
    /// were minimised to the tray. The model then sees the panel and picks the right icon itself.
    /// </summary>
    private void OpenNotificationArea()
    {
        try
        {
            var context = _context.Capture();
            var monitor = context.CurrentMonitor.Bounds;
            var virtualScreen = context.VirtualScreen;

            var x = monitor.Left + monitor.Width - 125;
            var y = virtualScreen.Top + virtualScreen.Height - 20;

            _input.LeftClick(x, y);
            StartupLogService.Info($"Recovery: clicked the notification area at {x},{y}.");
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Notification area click failed: {ex.Message}");
        }
    }

    private async Task<string?> BuildExtraContextAsync(
        ShellConfig config,
        ScreenSnapshot snapshot,
        string memoryPrompt,
        AgentSessionState session,
        string? screenshotPath,
        CancellationToken cancellationToken)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            // Phrased as reference data, never as an instruction: an 8B model will obey a
            // stray imperative here instead of the actual task.
            parts.Add($"Reference: this screen is saved as a file at {screenshotPath}");
        }

        // No coaching sentences about ctrl+c here. Telling the model to "select text and
        // press ctrl+c" made it copy and paste instead of doing the task.
        if (session.Notes.Count > 0)
        {
            parts.Add($"Collected notes (reference data):\n{session.BuildNotepad()}");
        }

        // Memory is only added once the user actually stored something, so the default
        // prompt stays exactly in the shape EvoCUA was trained on.
        if (!string.IsNullOrWhiteSpace(memoryPrompt))
        {
            parts.Add($"Long-term memory:\n{memoryPrompt.Trim()}");
        }

        if (config.Agent.UseOcrHints)
        {
            string ocrText = string.Empty;
            try
            {
                ocrText = NormalizeOcrText(await _ocr.RecognizeAsync(snapshot, cancellationToken));
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Tesseract OCR failed: {ex.Message}");
            }

            parts.Add(BuildScreenContext(snapshot, ocrText));
        }

        return string.Join("\n\n", parts);
    }

    private string BuildScreenContext(ScreenSnapshot snapshot, string ocrText)
    {
        try
        {
            var context = _context.Capture();
            var geometry = context.ToPromptString(
                _clipboard.GetPreview(),
                new RectSummary(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height));
            return $"{geometry}\n\nOCR:\n{FormatSupplement(ocrText)}";
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Desktop context failed: {ex.Message}");
            return $"OCR:\n{FormatSupplement(ocrText)}";
        }
    }

    private static string BuildPreviousActions(AgentSessionState session)
    {
        if (session.ActionLog.Count == 0)
        {
            return "None";
        }

        var lines = session.ActionLog.Count <= PreviousActionsInPrompt
            ? session.ActionLog
            : session.ActionLog.Skip(session.ActionLog.Count - PreviousActionsInPrompt);

        return string.Join("\n", lines);
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
            case "triple_click":
                if (action.X is not null && action.Y is not null)
                {
                    var point = ResolvePoint(snapshot, action.X, action.Y, "triple_click");
                    _input.TripleClick(point.X, point.Y, NormalizeButton(action.Button));
                }
                else
                {
                    _input.Click(NormalizeButton(action.Button));
                    _input.Click(NormalizeButton(action.Button));
                    _input.Click(NormalizeButton(action.Button));
                }

                return DescribeMouseAction("Сделала тройной клик", action);
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
            case "drag_to":
                {
                    // EvoCUA's left_click_drag drags from wherever the cursor already is.
                    if (action.X is null || action.Y is null)
                    {
                        throw new InvalidOperationException("drag_to requires x and y");
                    }

                    var from = _input.GetCursorPosition();
                    var to = ResolvePoint(snapshot, action.X, action.Y, "drag_to");
                    await _input.DragAsync(
                        from.X,
                        from.Y,
                        to.X,
                        to.Y,
                        action.Milliseconds ?? 450,
                        NormalizeButton(action.Button),
                        cancellationToken);
                    return $"Протащила мышь в {action.X},{action.Y}";
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
                return await _widgets.LaunchByIdAsync(action.Target!, cancellationToken);
            case "send_widget_data":
                EnsureTarget(action, "send_widget_data");
                EnsureText(action, "send_widget_data");
                return await _widgets.SendDataAsync(action.Target!, action.Text!, cancellationToken);
            case "remember_memory":
                EnsureTarget(action, "remember_memory");
                EnsureText(action, "remember_memory");
                await App.LongTermMemory.RememberAsync(action.Target!, action.Text!, "agent", cancellationToken);
                return $"Запомнила {action.Target}: {action.Text}";
            case "forget_memory":
                EnsureTarget(action, "forget_memory");
                await App.LongTermMemory.ForgetAsync(action.Target!, cancellationToken);
                return $"Забыла {action.Target}";
            default:
                throw new InvalidOperationException($"Unsupported agent action: {action.Type}");
        }
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

    private static string ResolveAwaitUserMessage(AgentDecision decision)
    {
        var message = decision.Action?.Text ??
                      decision.FinalResponse ??
                      decision.Action?.FinalResponse ??
                      decision.Action?.Target ??
                      decision.Thought;

        return string.IsNullOrWhiteSpace(message)
            ? "Нужны данные от тебя, чтобы продолжить."
            : message.Trim();
    }

    private static string ResolveStuckMessage(AgentDecision decision)
    {
        var detail = decision.Action?.Type switch
        {
            "observe" => "Я несколько раз подряд пыталась только наблюдать.",
            "wait" => "Я несколько раз подряд только ждала.",
            _ => $"Я начала повторять один и тот же шаг: {decision.Action?.Type}."
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

    private static string TrimForLog(string text, int maxLength = 220)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..maxLength]}...";
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
    /// <summary>Keeps the scratchpad small enough that it cannot crowd out the screenshot.</summary>
    private const int NotepadCharLimit = 4000;

    public List<string> History { get; } = [];

    /// <summary>Compact "Step N: action" lines fed back to the model as previous actions.</summary>
    public List<string> ActionLog { get; } = [];

    /// <summary>Text the agent copied during this task, carried across every step.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Last clipboard content seen, so the same text is not collected twice.</summary>
    public string LastClipboardText { get; set; } = string.Empty;

    public void RecordAction(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        ActionLog.Add(line.Trim());
    }

    public void AddNote(string text)
    {
        var note = text.Trim();
        if (note.Length == 0)
        {
            return;
        }

        if (Notes.Any(existing => string.Equals(existing, note, StringComparison.Ordinal)))
        {
            return;
        }

        if (note.Length > NotepadCharLimit)
        {
            note = note[..NotepadCharLimit];
        }

        Notes.Add(note);

        while (Notes.Count > 1 && Notes.Sum(existing => existing.Length + 3) > NotepadCharLimit)
        {
            Notes.RemoveAt(0);
        }
    }

    public string BuildNotepad()
    {
        return string.Join("\n---\n", Notes);
    }

    public void Reset()
    {
        History.Clear();
        ActionLog.Clear();
        Notes.Clear();
        LastClipboardText = string.Empty;
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
