using System.Text;
using AgentShell.Models;

namespace AgentShell.Services;

public sealed class McpThinkingService
{
    public string BuildSupplement(
        string userPrompt,
        int step,
        DesktopContextSnapshot context,
        string clipboardPreview,
        string ocrText,
        IReadOnlyList<RuntimeToolManifest> runtimeTools,
        IReadOnlyList<RuntimeItem> runtimeWidgets)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MCP planning overlay:");
        builder.AppendLine("- Think like a PC operator: first verify state, then perform one observable action.");
        builder.AppendLine("- Before typing, confirm the correct window is focused and the caret is where it should be.");
        builder.AppendLine("- Before deleting, replacing, selecting, dragging, or drawing, verify what is selected and where the cursor is.");
        builder.AppendLine("- Before opening a site or app, make sure it actually appeared among visible windows or OCR text.");
        builder.AppendLine("- If login, password, token, code, captcha, or explicit user choice is required, stop through await_user.");
        builder.AppendLine("- Do not imitate execution with plain text. Prefer real desktop actions.");
        builder.Append("Loop step: ").AppendLine(step.ToString());
        builder.Append("Clipboard: ").AppendLine(string.IsNullOrWhiteSpace(clipboardPreview) ? "(empty)" : clipboardPreview.Trim());

        if (context.ForegroundWindow is not null)
        {
            builder.Append("Foreground window: ")
                .Append(context.ForegroundWindow.ProcessName)
                .Append(" | \"")
                .Append(context.ForegroundWindow.Title)
                .AppendLine("\"");
        }

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            var normalizedOcr = ocrText.Trim();
            builder.Append("OCR: ")
                .AppendLine(normalizedOcr.Length <= 400 ? normalizedOcr : $"{normalizedOcr[..400]}...");
        }

        if (runtimeTools.Count > 0)
        {
            builder.AppendLine("Runtime tools:");
            foreach (var tool in runtimeTools.Take(8))
            {
                builder.Append("- ")
                    .Append(tool.Id)
                    .Append(": ")
                    .AppendLine(string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : tool.Description.Trim());
            }
        }

        if (runtimeWidgets.Count > 0)
        {
            builder.AppendLine("Runtime widgets:");
            foreach (var widget in runtimeWidgets.Take(6))
            {
                builder.Append("- ")
                    .Append(widget.Id)
                    .Append(": ")
                    .Append(widget.Name);
                if (!string.IsNullOrWhiteSpace(widget.Description))
                {
                    builder.Append(" - ").Append(widget.Description.Trim());
                }

                if (widget.SupportsDataInput)
                {
                    builder.Append(" (accepts widget data)");
                }

                builder.AppendLine();
            }
        }

        builder.Append("User request: ").AppendLine(userPrompt.Trim());
        return builder.ToString().TrimEnd();
    }
}
