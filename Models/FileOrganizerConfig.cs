namespace FileOrganizer.Models;

public class FileOrganizerConfig
{
    /// <summary>How often (in seconds) the service scans all source folders. Minimum 5.</summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Full path of the HTML activity report file written after every file operation.
    /// Leave empty to disable the report.
    /// </summary>
    public string ReportFilePath { get; set; } = "FileOrganizer_Report.html";

    /// <summary>
    /// Full path of the HTML log viewer file updated after every log message.
    /// Leave empty to disable the log viewer.
    /// </summary>
    public string LogFilePath { get; set; } = "FileOrganizer_Log.html";

    /// <summary>Maximum number of log entries kept in the HTML log viewer. Default 200.</summary>
    public int LogMaxEntries { get; set; } = 200;

    /// <summary>
    /// Drop log entries older than this many days from the HTML log viewer. Default 7. 0 = disabled.
    /// </summary>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>
    /// Drop activity report records older than this many days. Default 7. 0 = disabled.
    /// </summary>
    public int ReportRetentionDays { get; set; } = 7;

    /// <summary>
    /// How often (in seconds) the HTML report and log viewer pages auto-refresh in the browser. Default 30.
    /// </summary>
    public int HtmlRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// Port the Dashboard UI listens on (localhost only). Default 5051.
    /// Changing this setting requires a service restart.
    /// </summary>
    public int DashboardPort { get; set; } = 5051;

    /// <summary>List of file-processing rules evaluated on each scan cycle.</summary>
    public List<FileRule> Rules { get; set; } = new();
}
