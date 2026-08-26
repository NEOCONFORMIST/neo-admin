using System.Text;

namespace NeoAdmin;

internal static class CrashLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Sync = new();

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEO ADMIN",
        "logs");

    public static string LogPath { get; } =
        Path.Combine(LogDirectory, "application.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var entry = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [PID ")
                    .Append(Environment.ProcessId)
                    .Append("] ")
                    .AppendLine(message);

                if (exception is not null)
                    entry.AppendLine(exception.ToString());

                File.AppendAllText(
                    LogPath,
                    entry.ToString(),
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become another application failure.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) ||
            new FileInfo(LogPath).Length < MaxLogBytes)
        {
            return;
        }

        string previousPath =
            Path.Combine(LogDirectory, "application.previous.log");

        File.Move(LogPath, previousPath, true);
    }
}
