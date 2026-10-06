namespace AgentShell.Services;

public static class StartupLogService
{
    private static readonly object Sync = new();

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

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
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                // FileShare.ReadWrite keeps a second app instance from crashing the first:
                // both processes append to the same log concurrently.
                using var stream = new FileStream(
                    StartupLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC] [{level}] {message}");
            }
        }
        catch
        {
            // Logging must never take the app down, even if the file is locked or the
            // disk is full. Drop the line instead.
        }
    }
}
