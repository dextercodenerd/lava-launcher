using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Logger;

/// <summary>
/// We cannot have generic `ILogger<T>` because it is not native AOT compatible because the logger
/// factory has to create a type from string name/category in runtime.
/// </summary>
public class SimpleConsoleLogger : ILogger
{
    private static readonly Lock MultiLineLock = new();
    private readonly string _category;
    private readonly LogLevel _minLevel;

    public SimpleConsoleLogger(string category, LogLevel minLevel = LogLevel.Information)
    {
        _category = category;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var message = formatter(state, exception);
        var level = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => "UNKNOWN"
        };
        var eventInfo = eventId != default
            ? $" [EventId: {eventId.Id}{(eventId.Name != null ? $" {eventId.Name}" : "")}]"
            : "";
        var logMessage = $"{ts} [{level}]{eventInfo} {_category}: {message}";

        var ex = exception?.InnerException ?? exception;
        if (ex is null)
        {
            Console.WriteLine(logMessage);
            return;
        }

        lock (MultiLineLock)
        {
            Console.WriteLine(logMessage);
            Console.WriteLine(
                $"{ts} [{level}]{eventInfo} {_category}: {ex.GetType().Name}:  {ex.Message}");

            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                Console.WriteLine($"{ts} [{level}]{eventInfo} {_category}: StackTrace: {ex.StackTrace}");
            }
        }
    }
}