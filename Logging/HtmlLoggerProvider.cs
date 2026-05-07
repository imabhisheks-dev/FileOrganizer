using System.Collections.Concurrent;

namespace FileOrganizer.Logging;

[ProviderAlias("HtmlLog")]
public sealed class HtmlLoggerProvider : ILoggerProvider
{
    private readonly HtmlLogBuffer _buffer;
    private readonly ConcurrentDictionary<string, HtmlLogger> _loggers = new();

    public HtmlLoggerProvider(HtmlLogBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new HtmlLogger(name, _buffer));

    public void Dispose() => _loggers.Clear();
}
