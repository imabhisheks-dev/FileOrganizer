namespace FileOrganizer.Logging;

public sealed class HtmlLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ExceptionText { get; init; }
}
