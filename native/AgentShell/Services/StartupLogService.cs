namespace AgentShell.Services;

public static class StartupLogService
{
    private static readonly object Sync = new();

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopAIAgent", "logs");

    public static string StartupLogPath => Path.Combine(LogDirectory, "startup.log");

    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);
        Write("INFO", "========== app launch ==========");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                StartupLogPath,
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC] [{level}] {message}{Environment.NewLine}");
        }
    }
}
