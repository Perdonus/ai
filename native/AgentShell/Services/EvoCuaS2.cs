using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentShell.Services;

/// <summary>
/// Reproduces the EvoCUA "S2" prompt contract: a system prompt carrying the
/// <c>computer_use</c> tool definition, and a user turn with the screenshot plus
/// the instruction and previous actions.
/// </summary>
public static partial class EvoCuaPrompt
{
    /// <summary>Response coordinates live on a 1000x1000 grid, exactly like the reference eval.</summary>
    public const int CoordinateGrid = 999;

    private static readonly string[] ActionNames =
    [
        "key", "type", "mouse_move", "left_click", "left_click_drag",
        "right_click", "middle_click", "double_click", "triple_click", "scroll",
        "wait", "terminate", "key_down", "key_up"
    ];

    private const string ActionDescription = """
* `key`: Performs key down presses on the arguments passed in order, then performs key releases in reverse order.
* `key_down`: Press and HOLD the specified key(s) down in order (no release). Use this for stateful holds like holding Shift while clicking.
* `key_up`: Release the specified key(s) in reverse order.
* `type`: Type a string of text on the keyboard.
* `mouse_move`: Move the cursor to a specified (x, y) pixel coordinate on the screen.
* `left_click`: Click the left mouse button at a specified (x, y) pixel coordinate on the screen.
* `left_click_drag`: Click and drag the cursor to a specified (x, y) pixel coordinate on the screen.
* `right_click`: Click the right mouse button at a specified (x, y) pixel coordinate on the screen.
* `middle_click`: Click the middle mouse button at a specified (x, y) pixel coordinate on the screen.
* `double_click`: Double-click the left mouse button at a specified (x, y) pixel coordinate on the screen.
* `triple_click`: Triple-click the left mouse button at a specified (x, y) pixel coordinate on the screen.
* `scroll`: Performs a scroll of the mouse scroll wheel.
* `hscroll`: Performs a horizontal scroll (mapped to regular scroll).
* `wait`: Wait specified seconds for the change to happen.
* `terminate`: Terminate the current task and report its completion status.
* `answer`: Answer a question.
""";

    private const string DescriptionTemplate = """
Use a mouse and keyboard to interact with a computer, and take screenshots.
* This is an interface to a desktop GUI. You must click on desktop icons to start applications.
* Some applications may take time to start or process actions, so you may need to wait and take successive screenshots to see the results of your actions. E.g. if you click on Firefox and a window doesn't open, try wait and taking another screenshot.
{resolution_info}
* Whenever you intend to move the cursor to click on an element like an icon, you should consult a screenshot to determine the coordinates of the element before moving the cursor.
* If you tried clicking on a program or link but it failed to load even after waiting, try adjusting your cursor position so that the tip of the cursor visually falls on the element that you want to click.
* Make sure to click any buttons, links, icons, etc with the cursor tip in the center of the element. Don't click boxes on their edges unless asked.
""";

    private static readonly JsonSerializerOptions ToolJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Lazy<string> SystemPrompt = new(BuildSystemPromptCore);

    public static string BuildSystemPrompt() => SystemPrompt.Value;

    public static string BuildUserPrompt(string instruction, string previousActions, string? extraContext = null)
    {
        var builder = new StringBuilder();
        builder.Append("\nPlease generate the next move according to the UI screenshot, instruction and previous actions.");
        builder.Append("\nInstruction: ").Append(instruction);
        builder.Append("\nPrevious actions:\n")
            .Append(string.IsNullOrWhiteSpace(previousActions) ? "None" : previousActions);

        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            builder.Append("\nAdditional context:\n").Append(extraContext.Trim());
        }

        return builder.ToString();
    }

    private static string BuildSystemPromptCore()
    {
        var descriptionPrompt = DescriptionTemplate.Replace(
            "{resolution_info}",
            $"* The screen's resolution is {CoordinateGrid + 1}x{CoordinateGrid + 1}.",
            StringComparison.Ordinal);

        var tools = new
        {
            type = "function",
            function = new
            {
                name_for_human = "computer_use",
                name = "computer_use",
                description = descriptionPrompt.TrimEnd('\n'),
                parameters = new
                {
                    properties = new
                    {
                        action = new { description = ActionDescription.TrimEnd('\n'), @enum = ActionNames, type = "string" },
                        keys = new { description = "Required only by `action=key`.", type = "array" },
                        text = new { description = "Required only by `action=type`.", type = "string" },
                        coordinate = new { description = "The x,y coordinates for mouse actions.", type = "array" },
                        pixels = new { description = "The amount of scrolling.", type = "number" },
                        time = new { description = "The seconds to wait.", type = "number" },
                        status = new { description = "The status of the task.", type = "string", @enum = new[] { "success", "failure" } }
                    },
                    required = new[] { "action" },
                    type = "object"
                },
                args_format = "Format the arguments as a JSON object."
            }
        };

        var toolsXml = JsonSerializer.Serialize(tools, ToolJsonOptions);

        return $$"""
# Tools
You may call one or more functions to assist with the user query.
You are provided with function signatures within <tools></tools> XML tags:
<tools>
{{toolsXml}}
</tools>
For each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:
<tool_call>
{"name": <function-name>, "arguments": <args-json-object>}
</tool_call>
# Response format
Response format for every step:
1) Action: a short imperative describing what to do in the UI.
2) A single <tool_call>...</tool_call> block containing only the JSON: {"name": <function-name>, "arguments": <args-json-object>}.
Rules:
- Output exactly in the order: Action, <tool_call>.
- Be brief: one sentence for Action.
- Do not output anything else outside those parts.
- If finishing, use action=terminate in the tool call.
""";
    }
}

/// <summary>
/// Translates an EvoCUA S2 response into the desktop actions this shell already knows how to run.
/// </summary>
public static partial class EvoCuaResponseParser
{
    [GeneratedRegex(@"```(?:json|python|code)?", RegexOptions.IgnoreCase)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"<tool_call>(.*?)</tool_call>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ToolCallRegex();

    [GeneratedRegex(@"^\s*action\s*:\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ActionLineRegex();

    public static AgentDecision Parse(string? raw, int screenshotWidth, int screenshotHeight)
    {
        var decision = new AgentDecision { Action = new AgentAction { Type = "observe" } };
        if (string.IsNullOrWhiteSpace(raw))
        {
            return decision;
        }

        var text = FenceRegex().Replace(raw, string.Empty).Replace("```", string.Empty).Trim();

        var actionMatch = ActionLineRegex().Match(text);
        if (actionMatch.Success)
        {
            decision.Thought = actionMatch.Groups["text"].Value.Trim();
        }

        var toolCallMatch = ToolCallRegex().Match(text);
        var payload = toolCallMatch.Success
            ? toolCallMatch.Groups[1].Value
            : ExtractBareObject(text);

        if (string.IsNullOrWhiteSpace(payload))
        {
            decision.Thought = string.IsNullOrWhiteSpace(decision.Thought)
                ? TrimForDisplay(text)
                : decision.Thought;
            decision.Action.Type = "observe";
            return decision;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            StartupLogService.Warn($"EvoCUA: не удалось разобрать tool_call: {ex.Message}. Payload={TrimForDisplay(payload)}");
            decision.Action.Type = "observe";
            return decision;
        }

        if (node is not JsonObject root)
        {
            decision.Action.Type = "observe";
            return decision;
        }

        var name = root["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name) &&
            !name.Equals("computer_use", StringComparison.OrdinalIgnoreCase))
        {
            StartupLogService.Warn($"EvoCUA: неожиданное имя функции '{name}', продолжаю как computer_use.");
        }

        var arguments = root["arguments"] as JsonObject
                        ?? root["parameters"] as JsonObject
                        ?? root;

        var action = arguments["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
        {
            decision.Action.Type = "observe";
            return decision;
        }

        ApplyAction(decision, action, arguments, screenshotWidth, screenshotHeight);
        decision.Action.Arguments ??= [];
        decision.Action.Arguments["evocua_action"] = action;
        return decision;
    }

    private static void ApplyAction(
        AgentDecision decision,
        string action,
        JsonObject arguments,
        int screenshotWidth,
        int screenshotHeight)
    {
        var target = decision.Action!;

        switch (action)
        {
            case "left_click":
            case "click":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "click";
                target.Button = "left";
                break;

            case "right_click":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "right_click";
                target.Button = "right";
                break;

            case "middle_click":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "click";
                target.Button = "middle";
                break;

            case "double_click":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "double_click";
                target.Button = "left";
                break;

            case "triple_click":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "triple_click";
                target.Button = "left";
                break;

            case "mouse_move":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "mouse_move";
                break;

            case "left_click_drag":
                ApplyPoint(target, arguments, screenshotWidth, screenshotHeight);
                target.Type = "drag_to";
                target.Button = "left";
                break;

            case "type":
                var text = arguments["text"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                {
                    target.Type = "observe";
                    break;
                }

                target.Type = "type_text";
                target.Text = UnescapeText(text);
                break;

            case "key":
                var keys = ReadKeys(arguments);
                if (keys.Count == 0)
                {
                    target.Type = "observe";
                    break;
                }

                if (keys.Count == 1)
                {
                    target.Type = "press_key";
                    target.Key = NormalizeKey(keys[0]);
                }
                else
                {
                    target.Type = "key_combo";
                    target.Keys = keys.Select(NormalizeKey).ToList();
                }

                break;

            case "key_down":
                var downKeys = ReadKeys(arguments);
                target.Type = "key_down";
                target.Key = downKeys.Count > 0 ? NormalizeKey(downKeys[0]) : "SHIFT";
                break;

            case "key_up":
                var upKeys = ReadKeys(arguments);
                target.Type = "key_up";
                target.Key = upKeys.Count > 0 ? NormalizeKey(upKeys[0]) : "SHIFT";
                break;

            case "scroll":
            case "hscroll":
                var pixels = ReadInt(arguments["pixels"]) ?? ReadInt(arguments["delta"]) ?? 3;
                target.Type = "scroll";
                target.Delta = Math.Clamp(pixels * 120, -2400, 2400);
                break;

            case "wait":
                var seconds = ReadDouble(arguments["time"]) ?? 2.0;
                target.Type = "wait";
                target.Milliseconds = (int)Math.Clamp(seconds * 1000, 100, 60000);
                break;

            case "terminate":
                var status = arguments["status"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                var answer = arguments["answer"]?.GetValue<string>();
                if (status == "failure")
                {
                    target.Type = "await_user";
                    target.Text = string.IsNullOrWhiteSpace(answer)
                        ? "Не удалось выполнить задачу. Нужно уточнение или другой подход."
                        : answer.Trim();
                }
                else
                {
                    target.Type = "finish";
                    decision.FinalResponse = string.IsNullOrWhiteSpace(answer) ? "Готово." : answer.Trim();
                }

                break;

            default:
                StartupLogService.Warn($"EvoCUA: неизвестное действие '{action}', шаг пропущен.");
                target.Type = "observe";
                break;
        }

        if (target.Type != "observe" &&
            string.IsNullOrWhiteSpace(decision.Thought))
        {
            decision.Thought = DescribeAction(target);
        }
    }

    private static void ApplyPoint(AgentAction target, JsonObject arguments, int width, int height)
    {
        var coordinate = arguments["coordinate"] as JsonArray;
        if (coordinate is null || coordinate.Count < 2)
        {
            coordinate = arguments["coordinates"] as JsonArray;
        }

        if (coordinate is null || coordinate.Count < 2)
        {
            return;
        }

        var x = ReadDouble(coordinate[0]);
        var y = ReadDouble(coordinate[1]);
        if (x is null || y is null)
        {
            return;
        }

        var (screenX, screenY) = ToScreenPoint(x.Value, y.Value, width, height);
        target.X = screenX;
        target.Y = screenY;
    }

    /// <summary>
    /// EvoCUA reports coordinates on a 0..999 grid. Values beyond the grid mean the model
    /// answered in real pixels instead, which is tolerated rather than thrown away.
    /// </summary>
    public static (int X, int Y) ToScreenPoint(double x, double y, int screenWidth, int screenHeight)
    {
        var width = Math.Max(1, screenWidth);
        var height = Math.Max(1, screenHeight);

        if (x > EvoCuaPrompt.CoordinateGrid || y > EvoCuaPrompt.CoordinateGrid)
        {
            if (x <= width && y <= height)
            {
                return ((int)Math.Round(x), (int)Math.Round(y));
            }

            return (
                Math.Clamp((int)Math.Round(x), 0, width - 1),
                Math.Clamp((int)Math.Round(y), 0, height - 1));
        }

        return (
            Math.Clamp((int)(x * width / EvoCuaPrompt.CoordinateGrid), 0, width - 1),
            Math.Clamp((int)(y * height / EvoCuaPrompt.CoordinateGrid), 0, height - 1));
    }

    private static List<string> ReadKeys(JsonObject arguments)
    {
        var keys = new List<string>();
        var node = arguments["keys"] ?? arguments["key"];

        switch (node)
        {
            case JsonArray array:
                keys.AddRange(array
                    .Select(item => item?.GetValue<string>())
                    .OfType<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                break;

            case JsonValue value:
                var raw = value.GetValue<string>();
                keys.AddRange(raw
                    .Split(['+', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                break;
        }

        return keys.Select(key => key.Trim().Trim('\'', '"', '[', ']')).Where(key => key.Length > 0).ToList();
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "return" => "ENTER",
            "escape" => "ESC",
            "cmd" or "command" or "super" or "meta" => "WIN",
            "control" => "CTRL",
            "option" => "ALT",
            "pgup" => "PAGEUP",
            "pgdn" => "PAGEDOWN",
            "del" => "DELETE",
            _ => normalized.ToUpperInvariant()
        };
    }

    private static string UnescapeText(string text)
    {
        return text
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\r", string.Empty, StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string DescribeAction(AgentAction action)
    {
        return action.Type switch
        {
            "click" when action.X is not null => $"Кликнула по координатам {action.X},{action.Y}.",
            "right_click" when action.X is not null => $"Кликнула правой кнопкой по {action.X},{action.Y}.",
            "double_click" when action.X is not null => $"Двойной клик по {action.X},{action.Y}.",
            "triple_click" when action.X is not null => $"Тройной клик по {action.X},{action.Y}.",
            "mouse_move" when action.X is not null => $"Передвинула мышь в {action.X},{action.Y}.",
            "drag_to" when action.X is not null => $"Перетащила в {action.X},{action.Y}.",
            "type_text" => "Ввела текст.",
            "key_combo" => $"Нажала {string.Join("+", action.Keys ?? [])}.",
            "press_key" => $"Нажала {action.Key}.",
            "scroll" => $"Прокрутила на {action.Delta ?? 0}.",
            _ => "Наблюдение."
        };
    }

    private static string? ExtractBareObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        var candidate = text[start..(end + 1)];
        return candidate.Contains("\"action\"", StringComparison.OrdinalIgnoreCase) ||
               candidate.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static int? ReadInt(JsonNode? node)
    {
        var value = ReadDouble(node);
        return value is null ? null : (int)Math.Round(value.Value);
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node switch
            {
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                JsonValue value when value.TryGetValue<int>(out var integer) => integer,
                JsonValue value when value.TryGetValue<string>(out var text) &&
                                      double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForDisplay(string text)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 200 ? single : $"{single[..200]}...";
    }
}
