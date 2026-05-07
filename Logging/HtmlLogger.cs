namespace FileOrganizer.Logging;

internal sealed class HtmlLogger : ILogger
{
    private readonly string _categoryName;
    private readonly HtmlLogBuffer _buffer;

    public HtmlLogger(string categoryName, HtmlLogBuffer buffer)
    {
        _categoryName = categoryName;
        _buffer = buffer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        _buffer.Add(new HtmlLogEntry
        {
            Timestamp = DateTime.Now,
            Level = logLevel,
            Category = _categoryName,
            Message = formatter(state, exception),
            ExceptionText = exception?.ToString()
        });
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
