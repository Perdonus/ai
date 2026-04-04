using System.Diagnostics;

namespace AgentShell.Services;

public sealed class TesseractOcrService
{
    public async Task<string> RecognizeAsync(ScreenSnapshot snapshot, CancellationToken cancellationToken)
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "tesseract");
        var executablePath = Directory.Exists(runtimeRoot)
            ? Directory.GetFiles(runtimeRoot, "tesseract.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException($"Tesseract runtime не найден в {runtimeRoot}");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "DesktopAIAgent", "ocr");
        Directory.CreateDirectory(tempRoot);
        var token = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(tempRoot, $"{token}.png");
        var outputBase = Path.Combine(tempRoot, token);
        var outputTextPath = $"{outputBase}.txt";

        try
        {
            await File.WriteAllBytesAsync(inputPath, Convert.FromBase64String(snapshot.PngBase64), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = runtimeRoot
            };
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add(outputBase);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("rus+eng");
            startInfo.ArgumentList.Add("--psm");
            startInfo.ArgumentList.Add("11");
            startInfo.Environment["TESSDATA_PREFIX"] = runtimeRoot;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить Tesseract OCR.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            _ = await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Tesseract exited with {process.ExitCode}" : stderr.Trim());
            }

            if (!File.Exists(outputTextPath))
            {
                return string.Empty;
            }

            var text = await File.ReadAllTextAsync(outputTextPath, cancellationToken);
            StartupLogService.Info($"Tesseract OCR completed. chars={text.Length}");
            return text.Trim();
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputTextPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
