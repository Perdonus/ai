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
        IReadOnlyList<RuntimeToolManifest> runtimeTools)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MCP planning overlay:");
        builder.AppendLine("- Думай как оператор ПК: сначала проверка состояния, потом один наблюдаемый шаг.");
        builder.AppendLine("- Перед вводом текста убедись, что нужное окно активно и курсор стоит в нужном месте.");
        builder.AppendLine("- Перед удалением, заменой, выделением или рисованием оцени, что сейчас выделено и где находится курсор.");
        builder.AppendLine("- Если нужно открыть сайт или приложение, сначала добейся его появления среди окон или в OCR.");
        builder.AppendLine("- Если не хватает логина, пароля, токена, кода, капчи или выбора пользователя, останавливайся через await_user.");
        builder.AppendLine("- Не имитируй выполнение текстом. Выбирай реальные desktop-действия.");
        builder.Append("Шаг цикла: ").AppendLine(step.ToString());
        builder.Append("Буфер обмена: ").AppendLine(string.IsNullOrWhiteSpace(clipboardPreview) ? "(пусто)" : clipboardPreview.Trim());

        if (context.ForegroundWindow is not null)
        {
            builder.Append("Активное окно: ")
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

        builder.Append("Запрос пользователя: ").AppendLine(userPrompt.Trim());
        return builder.ToString().TrimEnd();
    }
}
