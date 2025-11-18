using System;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Logger;

public class SimpleLoggerFactory : ILoggerFactory
{
    private readonly LogLevel _minLevel;

    public SimpleLoggerFactory(LogLevel minimumLevel = LogLevel.Information)
    {
        _minLevel = minimumLevel;
    }

    public ILogger CreateLogger(string category) => new SimpleConsoleLogger(category, _minLevel);

    public void AddProvider(ILoggerProvider provider) =>
        throw new InvalidOperationException("Cannot add provider to SimpleLoggerFactory!");


    public void Dispose()
    {
    }
}
