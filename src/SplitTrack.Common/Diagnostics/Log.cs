namespace SplitTrack.Common.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    None,
}

/// <summary>Console + daily file log in %LOCALAPPDATA%\SplitTrack\logs.</summary>
public static class Log
{
    private const int KeepDays = 7;

    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Warning;

    public static LogLevel FileLevel { get; set; } = LogLevel.Debug;

    public static string? FilePath { get; private set; }

    public static void Init(string appName)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitTrack", "logs");

        try
        {
            Directory.CreateDirectory(dir);
            DeleteOldFiles(dir);
            FilePath = Path.Combine(dir, $"{appName}-{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            lock (Gate)
            {
                _file = new StreamWriter(stream) { AutoFlush = true };
            }
        }
        catch (Exception ex)
        {
            // Logging must never stop the app; fall back to console only.
            Console.Error.WriteLine($"Log file unavailable: {ex.Message}");
            FilePath = null;
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);

    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private static void Write(LogLevel level, string message, Exception? ex)
    {
        string text = ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}";

        lock (Gate)
        {
            if (level >= FileLevel && _file is not null)
            {
                _file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {Tag(level)} {text}");
                if (ex is not null)
                {
                    _file.WriteLine(ex.ToString());
                }
            }

            if (level >= ConsoleLevel)
            {
                TextWriter console = level >= LogLevel.Warning ? Console.Error : Console.Out;
                console.WriteLine($"{Tag(level)} {text}");
            }
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };

    private static void DeleteOldFiles(string dir)
    {
        DateTime cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (string file in Directory.EnumerateFiles(dir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Another instance may hold it; retried next start.
            }
        }
    }
}
