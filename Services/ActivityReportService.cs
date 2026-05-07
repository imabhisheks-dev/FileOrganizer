using System.Text;
using System.Text.Json;
using System.Web;
using FileOrganizer.Models;
using Microsoft.Extensions.Options;

namespace FileOrganizer.Services;

/// <summary>
/// Stores activity records and renders an HTML activity report.
/// Retention is controlled entirely by ReportRetentionDays (default 7 days).
/// </summary>
public class ActivityReportService : IDisposable
{
    private readonly IOptionsMonitor<FileOrganizerConfig> _config;
    private readonly ILogger<ActivityReportService> _logger;
    private readonly LinkedList<FileActivityRecord> _records = new();
    private readonly object _lock = new();
    private readonly Timer _refreshTimer;

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public ActivityReportService(
        IOptionsMonitor<FileOrganizerConfig> config,
        ILogger<ActivityReportService> logger)
    {
        _config = config;
        _logger = logger;

        // Reload records persisted from the previous run, then write the HTML
        LoadRecords();
        WriteHtml();

        // Periodically rewrite the HTML so "Last updated" stays current even when idle
        _refreshTimer = new Timer(OnRefreshTimer, null,
            RefreshInterval(), Timeout.InfiniteTimeSpan);
    }

    private TimeSpan RefreshInterval()
    {
        var secs = Math.Max(5, _config.CurrentValue.HtmlRefreshSeconds);
        return TimeSpan.FromSeconds(secs);
    }

    private void OnRefreshTimer(object? _)
    {
        WriteHtml();
        _refreshTimer.Change(RefreshInterval(), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _refreshTimer.Dispose();

    // -------------------------------------------------------------------------
    /// <summary>
    /// Adds a record to the ring-buffer and immediately writes the HTML report.
    /// Returns the same record so the caller can update it later.
    /// </summary>
    public FileActivityRecord AddRecord(FileActivityRecord record)
    {
        lock (_lock)
        {
            _records.AddFirst(record);

            // Evict by age (ReportRetentionDays)
            var retentionDays = _config.CurrentValue.ReportRetentionDays;
            if (retentionDays > 0)
            {
                var cutoff = DateTime.Now.AddDays(-retentionDays);
                var node = _records.Last;
                while (node is not null && node.Value.Time < cutoff)
                {
                    var prev = node.Previous;
                    _records.Remove(node);
                    node = prev;
                }
            }
        }

        WriteHtml();
        return record;
    }

    // -------------------------------------------------------------------------
    /// <summary>
    /// Updates the status (and optional error message) of an existing record
    /// and re-renders the HTML report.
    /// </summary>
    public void UpdateRecord(FileActivityRecord record, ActivityStatus status, string? errorMessage = null)
    {
        lock (_lock)
        {
            record.Status = status;
            if (errorMessage is not null)
                record.ErrorMessage = errorMessage;
        }

        WriteHtml();
    }

    // -------------------------------------------------------------------------
    /// <summary>Returns a snapshot of the current records (newest first).</summary>
    public IReadOnlyList<FileActivityRecord> GetRecords()
    {
        lock (_lock)
            return _records.ToList();
    }

    // -------------------------------------------------------------------------
    private void WriteHtml()
    {
        var path = _config.CurrentValue.ReportFilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var html = BuildHtml();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, html, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write activity report to '{path}'.", path);
        }

        SaveRecords();
    }

    // -------------------------------------------------------------------------
    /// <summary>Returns the sidecar JSON path for a given HTML report path.</summary>
    private static string SidecarPath(string htmlPath) =>
        Path.ChangeExtension(htmlPath, ".records.json");

    // -------------------------------------------------------------------------
    private void SaveRecords()
    {
        var htmlPath = _config.CurrentValue.ReportFilePath;
        if (string.IsNullOrWhiteSpace(htmlPath)) return;

        try
        {
            IReadOnlyList<FileActivityRecord> snapshot;
            lock (_lock)
                snapshot = _records.ToList();

            var json = JsonSerializer.Serialize(snapshot, _jsonOpts);
            File.WriteAllText(SidecarPath(htmlPath), json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save activity records sidecar.");
        }
    }

    // -------------------------------------------------------------------------
    private void LoadRecords()
    {
        var htmlPath = _config.CurrentValue.ReportFilePath;
        if (string.IsNullOrWhiteSpace(htmlPath)) return;

        var sidecar = SidecarPath(htmlPath);
        if (!File.Exists(sidecar)) return;

        try
        {
            var json = File.ReadAllText(sidecar, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<FileActivityRecord>>(json, _jsonOpts);
            if (records is null || records.Count == 0) return;

            lock (_lock)
            {
                foreach (var r in records)
                    _records.AddLast(r);

                // Re-evict by age in case RetentionDays changed since last run
                var retentionDays = _config.CurrentValue.ReportRetentionDays;
                if (retentionDays > 0)
                {
                    var cutoff = DateTime.Now.AddDays(-retentionDays);
                    var node = _records.Last;
                    while (node is not null && node.Value.Time < cutoff)
                    {
                        var prev = node.Previous;
                        _records.Remove(node);
                        node = prev;
                    }
                }
            }

            _logger.LogInformation("ActivityReportService: restored {count} record(s) from previous run.", records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load activity records sidecar '{path}'. Starting fresh.", sidecar);
        }
    }

    // -------------------------------------------------------------------------
    private string BuildHtml()
    {
        IReadOnlyList<FileActivityRecord> snapshot;
        lock (_lock)
            snapshot = _records.ToList();

        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine($"<meta http-equiv=\"refresh\" content=\"{_config.CurrentValue.HtmlRefreshSeconds}\">" );
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>FileOrganizer — Activity Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(@"
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: 'Segoe UI', Arial, sans-serif; background: #1e1e2e; color: #cdd6f4; padding: 24px; }
  h1   { font-size: 1.4rem; margin-bottom: 4px; color: #cba6f7; }
  .sub { font-size: 0.8rem; color: #6c7086; margin-bottom: 20px; }
  table {
    table-layout: auto;
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    background: #181825;
    border-radius: 8px;
    overflow: hidden;
  }
  th {
    background: #313244;
    color: #b4befe;
    padding: 10px 12px;
    text-align: left;
    white-space: nowrap;
    user-select: none;
    position: relative;
  }
  th .resize-handle {
    position: absolute;
    right: 0; top: 0; bottom: 0;
    width: 6px;
    cursor: col-resize;
    background: transparent;
  }
  th .resize-handle:hover { background: rgba(203,166,247,0.3); }
  td { padding: 8px 12px; border-top: 1px solid #313244; vertical-align: top; word-break: break-all; }
  tr:hover td { background: #1e1e2e; }
  tr.success td { border-left: 3px solid #a6e3a1; }
  tr.failed  td { border-left: 3px solid #f38ba8; }
  tr.inprogress td { border-left: 3px solid #f9e2af; }
  .pill {
    display: inline-block;
    padding: 2px 10px;
    border-radius: 999px;
    font-size: 0.75rem;
    font-weight: 600;
    white-space: nowrap;
  }
  .pill-success    { background: #a6e3a1; color: #1e1e2e; }
  .pill-failed     { background: #f38ba8; color: #1e1e2e; cursor: help; }
  .pill-inprogress { background: #f9e2af; color: #1e1e2e; }
  .pill-copy { background: #89b4fa; color: #1e1e2e; }
  .pill-move { background: #cba6f7; color: #1e1e2e; }
  .pill-delete { background: #f38ba8; color: #1e1e2e; }
  .empty { color: #6c7086; text-align: center; padding: 24px; }
");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<h1>FileOrganizer &mdash; Activity Report</h1>");
        var refreshSec = _config.CurrentValue.HtmlRefreshSeconds;
        var retentionDays = _config.CurrentValue.ReportRetentionDays;
        var retentionLabel = retentionDays > 0 ? $"last {retentionDays} day(s)" : "all time";
        var totalRecords = snapshot.Count;
        sb.AppendLine($"<p class=\"sub\">Last updated: {HttpUtility.HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))} &nbsp;|&nbsp; {totalRecords} operation(s) &mdash; {retentionLabel} &nbsp;|&nbsp; Auto-refreshes every {refreshSec} s</p>");

        sb.AppendLine("<table id=\"activityTable\">");
        sb.AppendLine("<thead><tr>");

        string[] headers = { "#", "Time", "File Name", "Source Folder", "Target Folder", "Operation", "Status" };
        foreach (var h in headers)
        {
            sb.Append($"<th>{HttpUtility.HtmlEncode(h)}<span class=\"resize-handle\"></span></th>");
        }

        sb.AppendLine("</tr></thead>");
        sb.AppendLine("<tbody>");

        if (snapshot.Count == 0)
        {
            sb.AppendLine($"<tr><td colspan=\"{headers.Length}\" class=\"empty\">No activity recorded yet.</td></tr>");
        }
        else
        {
            int row = 1;
            foreach (var rec in snapshot)
            {
                var rowClass = rec.Status switch
                {
                    ActivityStatus.Success    => "success",
                    ActivityStatus.Failed     => "failed",
                    ActivityStatus.InProgress => "inprogress",
                    _ => ""
                };

                var statusPill = rec.Status switch
                {
                    ActivityStatus.Success    => "<span class=\"pill pill-success\">Success</span>",
                    ActivityStatus.Failed     => $"<span class=\"pill pill-failed\" title=\"{HttpUtility.HtmlAttributeEncode(rec.ErrorMessage ?? "Unknown error")}\">Failed</span>",
                    ActivityStatus.InProgress => "<span class=\"pill pill-inprogress\">In Progress</span>",
                    _ => rec.Status.ToString()
                };

                var opPill = rec.Operation switch
                {
                    FileOperation.Copy   => "<span class=\"pill pill-copy\">Copy</span>",
                    FileOperation.Move   => "<span class=\"pill pill-move\">Move</span>",
                    FileOperation.Delete => "<span class=\"pill pill-delete\">Delete</span>",
                    _ => rec.Operation.ToString()
                };

                sb.AppendLine($"<tr class=\"{rowClass}\">");
                sb.AppendLine($"  <td>{row}</td>");
                sb.AppendLine($"  <td style=\"white-space:nowrap\">{HttpUtility.HtmlEncode(rec.Time.ToString("yyyy-MM-dd HH:mm:ss"))}</td>");
                sb.AppendLine($"  <td>{HttpUtility.HtmlEncode(rec.FileName)}</td>");
                sb.AppendLine($"  <td>{HttpUtility.HtmlEncode(rec.SourceFolder)}</td>");
                sb.AppendLine($"  <td>{HttpUtility.HtmlEncode(rec.TargetFolder)}</td>");
                sb.AppendLine($"  <td>{opPill}</td>");
                sb.AppendLine($"  <td>{statusPill}</td>");
                sb.AppendLine("</tr>");
                row++;
            }
        }

        sb.AppendLine("</tbody></table>");

        // Column drag-resize script
        sb.AppendLine(@"
<script>
(function () {
  var table = document.getElementById('activityTable');
  if (!table) return;

  var handles = table.querySelectorAll('th .resize-handle');
  handles.forEach(function (handle) {
    var th = handle.parentElement;
    var startX, startW;

    handle.addEventListener('mousedown', function (e) {
      startX = e.pageX;
      startW = th.offsetWidth;
      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
      e.preventDefault();
    });

    function onMove(e) {
      var newW = startW + (e.pageX - startX);
      if (newW > 40) th.style.width = newW + 'px';
    }

    function onUp() {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
    }
  });
})();
</script>
");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
