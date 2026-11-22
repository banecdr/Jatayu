// ==================================================================================================
// Jatayu Engine - Core Platform
// Module: Log.cs
// Role: Formatted console and file logging system with severity levels and timestamp formatting.
// ==================================================================================================

using System.Runtime.CompilerServices;

namespace Jatayu.Core.Logging;

/// <summary>
/// Severity levels for engine log messages.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Central static logging facility for Jatayu Engine.
/// Provides colored console output, formatted timestamps, and optional log sink hooks.
/// </summary>
public static class Log
{
    private static readonly object s_lock = new();
    private static LogLevel s_minimumLevel = LogLevel.Debug;
    private static Action<LogLevel, string>? s_logSink;
    private static StreamWriter? s_fileWriter;

    /// <summary>
    /// Gets or sets the minimum log level required for a message to be output.
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get => s_minimumLevel;
        set => s_minimumLevel = value;
    }

    /// <summary>
    /// Initializes optional file logging to the specified file path.
    /// </summary>
    public static void InitializeFileLogging(string filePath)
    {
        lock (s_lock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                s_fileWriter?.Dispose();
                s_fileWriter = new StreamWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FATAL] Failed to initialize file logger at '{filePath}': {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    /// <summary>
    /// Sets an external log sink delegate (e.g. for an in-game console or debug overlay).
    /// </summary>
    public static void SetSink(Action<LogLevel, string>? sink)
    {
        s_logSink = sink;
    }

    /// <summary>
    /// Writes an informational message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>
    /// Writes a debug diagnostic message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    /// <summary>
    /// Writes a trace verbose message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(string message) => Write(LogLevel.Trace, message);

    /// <summary>
    /// Writes a warning message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning(string message) => Write(LogLevel.Warning, message);

    /// <summary>
    /// Writes an error message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// Writes a critical failure message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Critical(string message) => Write(LogLevel.Critical, message);

    /// <summary>
    /// Writes a log entry with the specified severity level.
    /// </summary>
    public static void Write(LogLevel level, string message)
    {
        if (level < s_minimumLevel)
            return;

        string timeStr = DateTime.Now.ToString("HH:mm:ss.fff");
        string levelPrefix = level switch
        {
            LogLevel.Trace => "[TRACE]",
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Info => "[INFO ]",
            LogLevel.Warning => "[WARN ]",
            LogLevel.Error => "[ERROR]",
            LogLevel.Critical => "[FATAL]",
            _ => "[LOG  ]"
        };

        string formatted = $"{timeStr} {levelPrefix} {message}";

        lock (s_lock)
        {
            ConsoleColor prev = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Trace => ConsoleColor.DarkGray,
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.Cyan,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.Magenta,
                _ => ConsoleColor.White
            };

            Console.WriteLine(formatted);
            Console.ForegroundColor = prev;

            s_fileWriter?.WriteLine(formatted);
        }

        s_logSink?.Invoke(level, formatted);
    }

    /// <summary>
    /// Flushes and closes file writers.
    /// </summary>
    public static void Shutdown()
    {
        lock (s_lock)
        {
            s_fileWriter?.Dispose();
            s_fileWriter = null;
        }
    }
}
