using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace FileOrganizer.Logging;

/// <summary>
/// Thread-safe ring-buffer for log entries. Writes a self-contained HTML log
/// viewer after every add. Path and max-entries are read live from IConfiguration
/// so changes to appsettings.json take effect without restarting the service.
/// </summary>
public sealed class HtmlLogBuffer : IDisposable
{
    private readonly IConfiguration _config;
    private readonly LinkedList<HtmlLogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly Timer _refreshTimer;

    // Used for sidecar JSON persistence. LogLevel is stored as its integer value.
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public HtmlLogBuffer(IConfiguration config)
    {
        _config = config;
        LoadEntries();

        // Periodically rewrite the HTML so "Last updated" stays current even when idle
        _refreshTimer = new Timer(OnRefreshTimer, null,
            RefreshInterval(), Timeout.InfiniteTimeSpan);
    }

    private TimeSpan RefreshInterval()
    {
        var secs = Math.Max(5, HtmlRefreshSeconds);
        return TimeSpan.FromSeconds(secs);
    }

    private void OnRefreshTimer(object? _)
    {
        List<HtmlLogEntry> snapshot;
        lock (_lock)
            snapshot = [.. _entries];
        WriteHtml(snapshot);
        _refreshTimer.Change(RefreshInterval(), Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _refreshTimer.Dispose();

    private string LogFilePath =>
        _config["FileOrganizer:LogFilePath"] ?? string.Empty;

    private int MaxEntries =>
        int.TryParse(_config["FileOrganizer:LogMaxEntries"], out var n) && n > 0 ? n : 200;

    private int RetentionDays =>
        int.TryParse(_config["FileOrganizer:LogRetentionDays"], out var d) && d > 0 ? d : 7;

    private int HtmlRefreshSeconds =>
        int.TryParse(_config["FileOrganizer:HtmlRefreshSeconds"], out var s) && s > 0 ? s : 30;

    public void Add(HtmlLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(LogFilePath))
            return;

        List<HtmlLogEntry> snapshot;
        lock (_lock)
        {
            _entries.AddFirst(entry);           // newest at top

            // Drop entries beyond the count cap
            while (_entries.Count > MaxEntries)
                _entries.RemoveLast();

            // Drop entries older than the retention window
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            var node = _entries.Last;
            while (node is not null && node.Value.Timestamp < cutoff)
            {
                var prev = node.Previous;
                _entries.Remove(node);
                node = prev;
            }

            snapshot = [.. _entries];
        }

        WriteHtml(snapshot);
        SaveEntries(snapshot);
    }

    /// <summary>
    /// Returns the count of Error/Critical entries logged within the last <paramref name="withinMinutes"/> minutes.
    /// </summary>
    public int CountRecentErrors(int withinMinutes = 5)
    {
        var cutoff = DateTime.Now.AddMinutes(-withinMinutes);
        lock (_lock)
        {
            return _entries.Count(e =>
                e.Timestamp >= cutoff &&
                (e.Level == LogLevel.Error || e.Level == LogLevel.Critical));
        }
    }

    // -------------------------------------------------------------------------
    private void WriteHtml(List<HtmlLogEntry> snapshot)
    {
        var path = LogFilePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, BuildHtml(snapshot, HtmlRefreshSeconds), Encoding.UTF8);
        }
        catch
        {
            // Never propagate exceptions from the HTML writer back into the
            // logging pipeline — that would cause an infinite loop.
        }
    }

    // -------------------------------------------------------------------------
    private static string SidecarPath(string htmlPath) =>
        Path.ChangeExtension(htmlPath, ".entries.json");

    // -------------------------------------------------------------------------
    private void SaveEntries(List<HtmlLogEntry> snapshot)
    {
        var htmlPath = LogFilePath;
        if (string.IsNullOrWhiteSpace(htmlPath)) return;
        try
        {
            // Serialize using a plain DTO to avoid LogLevel enum issues
            var dtos = snapshot.Select(e => new LogEntryDto
            {
                Timestamp     = e.Timestamp,
                Level         = (int)e.Level,
                Category      = e.Category,
                Message       = e.Message,
                ExceptionText = e.ExceptionText
            }).ToList();
            File.WriteAllText(SidecarPath(htmlPath), JsonSerializer.Serialize(dtos, _jsonOpts), Encoding.UTF8);
        }
        catch { /* never propagate from logging pipeline */ }
    }

    // -------------------------------------------------------------------------
    private void LoadEntries()
    {
        var htmlPath = LogFilePath;
        if (string.IsNullOrWhiteSpace(htmlPath)) return;

        var sidecar = SidecarPath(htmlPath);
        if (!File.Exists(sidecar)) return;

        try
        {
            var dtos = JsonSerializer.Deserialize<List<LogEntryDto>>(File.ReadAllText(sidecar, Encoding.UTF8), _jsonOpts);
            if (dtos is null || dtos.Count == 0) return;

            var retentionDays = RetentionDays;
            var cutoff = retentionDays > 0 ? DateTime.Now.AddDays(-retentionDays) : DateTime.MinValue;

            lock (_lock)
            {
                // Entries in the file are newest-first; AddLast rebuilds oldest-first order
                // then we reverse so the list stays newest-first
                foreach (var dto in ((IEnumerable<LogEntryDto>)dtos).Reverse())
                {
                    if (dto.Timestamp < cutoff) continue;
                    _entries.AddFirst(new HtmlLogEntry
                    {
                        Timestamp     = dto.Timestamp,
                        Level         = (LogLevel)dto.Level,
                        Category      = dto.Category,
                        Message       = dto.Message,
                        ExceptionText = dto.ExceptionText
                    });
                }

                // Honour MaxEntries cap
                while (_entries.Count > MaxEntries)
                    _entries.RemoveLast();
            }
        }
        catch { /* corrupt sidecar — start fresh */ }
    }

    // DTO used for JSON persistence (avoids LogLevel enum serialization issues)
    private sealed class LogEntryDto
    {
        public DateTime Timestamp     { get; set; }
        public int      Level         { get; set; }
        public string   Category      { get; set; } = string.Empty;
        public string   Message       { get; set; } = string.Empty;
        public string?  ExceptionText { get; set; }
    }

    // -------------------------------------------------------------------------
    private static string BuildHtml(List<HtmlLogEntry> entries, int refreshSeconds)
    {
        var rows = new StringBuilder();

        if (entries.Count == 0)
        {
            rows.AppendLine("""<tr><td colspan="4" class="empty">No log entries yet.</td></tr>""");
        }
        else
        {
            foreach (var e in entries)
            {
                var levelCss = e.Level switch
                {
                    LogLevel.Trace       => "lvl-trace",
                    LogLevel.Debug       => "lvl-debug",
                    LogLevel.Information => "lvl-info",
                    LogLevel.Warning     => "lvl-warning",
                    LogLevel.Error       => "lvl-error",
                    LogLevel.Critical    => "lvl-critical",
                    _                    => "lvl-info"
                };

                var levelLabel = e.Level switch
                {
                    LogLevel.Trace       => "TRACE",
                    LogLevel.Debug       => "DEBUG",
                    LogLevel.Information => "INFO",
                    LogLevel.Warning     => "WARN",
                    LogLevel.Error       => "ERROR",
                    LogLevel.Critical    => "CRIT",
                    _                    => e.Level.ToString().ToUpper()
                };

                // Show only the last segment of the namespace-qualified category
                var category = e.Category.Contains('.')
                    ? e.Category[(e.Category.LastIndexOf('.') + 1)..]
                    : e.Category;

                var exceptionHtml = string.IsNullOrEmpty(e.ExceptionText)
                    ? string.Empty
                    : $"""<details class="ex-detail"><summary>Exception</summary><pre class="ex-pre">{HttpUtility.HtmlEncode(e.ExceptionText)}</pre></details>""";

                rows.AppendLine($"""
                        <tr class="{levelCss}" data-level="{levelCss}">
                            <td class="time-cell">{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}</td>
                            <td><span class="lvl-badge {levelCss}">{levelLabel}</span></td>
                            <td class="cat-cell" title="{HttpUtility.HtmlAttributeEncode(e.Category)}">{HttpUtility.HtmlEncode(category)}</td>
                            <td class="msg-cell">{HttpUtility.HtmlEncode(e.Message)}{exceptionHtml}</td>
                        </tr>
                    """);
            }
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var count = entries.Count;
        var entriesWord = count == 1 ? "entry" : "entries";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>FileOrganizer — Log Viewer</title>
                <style>
                    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                    body {
                        font-family: 'Segoe UI', Arial, sans-serif;
                        background: #0f0f1a;
                        color: #e0e0f0;
                        min-height: 100vh;
                        padding: 28px 24px;
                    }
                    header {
                        display: flex;
                        align-items: baseline;
                        flex-wrap: wrap;
                        gap: 12px;
                        margin-bottom: 16px;
                        border-bottom: 1px solid #2d2d50;
                        padding-bottom: 16px;
                    }
                    h1 { font-size: 1.4rem; color: #a0c4ff; letter-spacing: 0.4px; }
                    .badge {
                        display: inline-block;
                        font-size: 0.7rem;
                        padding: 2px 9px;
                        border-radius: 10px;
                        background: #1e3a5f;
                        color: #7ec8e3;
                        vertical-align: middle;
                        margin-left: 8px;
                        font-weight: 600;
                        letter-spacing: 0.6px;
                        text-transform: uppercase;
                    }
                    .meta { font-size: 0.8rem; color: #666; flex-grow: 1; text-align: right; }
                    .meta span { color: #9ca3af; }
                    .refresh-dot {
                        display: inline-block;
                        width: 7px; height: 7px;
                        background: #22c55e;
                        border-radius: 50%;
                        margin-right: 5px;
                        animation: pulse 2s infinite;
                    }
                    @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }
                    /* Filter bar */
                    .filter-bar {
                        display: flex;
                        gap: 8px;
                        flex-wrap: wrap;
                        margin-bottom: 16px;
                        align-items: center;
                    }
                    .filter-label { font-size: 0.78rem; color: #555; margin-right: 4px; }
                    .filter-btn {
                        font-size: 0.72rem;
                        font-weight: 700;
                        padding: 4px 12px;
                        border-radius: 12px;
                        border: none;
                        cursor: pointer;
                        letter-spacing: 0.5px;
                        transition: opacity 0.15s;
                        opacity: 0.4;
                    }
                    .filter-btn.active { opacity: 1; }
                    .btn-all     { background: #2d2d50; color: #e0e0f0; }
                    .btn-trace   { background: #374151; color: #9ca3af; }
                    .btn-debug   { background: #1e3a5f; color: #7ec8e3; }
                    .btn-info    { background: #14532d; color: #4ade80; }
                    .btn-warning { background: #451a03; color: #fb923c; }
                    .btn-error   { background: #4c0519; color: #f87171; }
                    .btn-crit    { background: #3b0764; color: #e879f9; }
                    /* Table */
                    table {
                        width: 100%;
                        border-collapse: collapse;
                        font-size: 0.855rem;
                        table-layout: auto;
                    }
                    th {
                        text-align: left;
                        padding: 10px 14px;
                        background: #141428;
                        color: #7eb8f7;
                        border-bottom: 2px solid #2d2d50;
                        white-space: nowrap;
                        font-weight: 600;
                        letter-spacing: 0.5px;
                        font-size: 0.78rem;
                        text-transform: uppercase;
                    }
                    td { padding: 9px 14px; border-bottom: 1px solid #1e1e35; vertical-align: top; }
                    col.c-time  { width: 185px; }
                    col.c-level { width: 62px;  }
                    col.c-cat   { width: 175px; }
                    col.c-msg   { width: auto;  }
                    /* Row tints per level */
                    tr.lvl-trace    td { background: #0d0d12; }
                    tr.lvl-debug    td { background: #0a0e18; }
                    tr.lvl-info     td { background: #08100c; }
                    tr.lvl-warning  td { background: #100c06; }
                    tr.lvl-error    td { background: #100608; }
                    tr.lvl-critical td { background: #0e0712; }
                    tr.lvl-trace:hover    td { background: #13131e; }
                    tr.lvl-debug:hover    td { background: #0d1220; }
                    tr.lvl-info:hover     td { background: #0d1a10; }
                    tr.lvl-warning:hover  td { background: #1a130a; }
                    tr.lvl-error:hover    td { background: #1a080c; }
                    tr.lvl-critical:hover td { background: #16091e; }
                    /* Cell styles */
                    .time-cell { color: #6b7280; font-size: 0.79rem; font-variant-numeric: tabular-nums; white-space: nowrap; }
                    .cat-cell  { color: #9da0b8; font-size: 0.8rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    .msg-cell  { color: #d1d5db; word-break: break-word; }
                    /* Level badge */
                    .lvl-badge {
                        display: inline-block;
                        font-size: 0.68rem;
                        font-weight: 800;
                        padding: 2px 7px;
                        border-radius: 4px;
                        letter-spacing: 0.6px;
                        white-space: nowrap;
                    }
                    .lvl-badge.lvl-trace    { background: #374151; color: #9ca3af; }
                    .lvl-badge.lvl-debug    { background: #1e3a5f; color: #60a5fa; }
                    .lvl-badge.lvl-info     { background: #14532d; color: #4ade80; }
                    .lvl-badge.lvl-warning  { background: #451a03; color: #fb923c; }
                    .lvl-badge.lvl-error    { background: #4c0519; color: #f87171; }
                    .lvl-badge.lvl-critical { background: #3b0764; color: #e879f9; }
                    /* Exception block */
                    .ex-detail { margin-top: 6px; }
                    .ex-detail summary { font-size: 0.76rem; color: #f87171; cursor: pointer; user-select: none; display: inline-block; }
                    .ex-detail summary:hover { color: #fca5a5; }
                    .ex-pre {
                        margin-top: 6px;
                        font-size: 0.74rem;
                        font-family: 'Cascadia Code', Consolas, monospace;
                        background: #1a1a2e;
                        color: #f9a8d4;
                        padding: 10px 14px;
                        border-radius: 6px;
                        border-left: 3px solid #4c0519;
                        white-space: pre-wrap;
                        word-break: break-word;
                        max-height: 220px;
                        overflow-y: auto;
                    }
                    .empty { text-align: center; padding: 50px; color: #444; font-style: italic; }
                </style>
            </head>
            <body>
                <header>
                    <h1>FileOrganizer <span class="badge">Log Viewer</span></h1>
                    <div class="meta">
                        <span class="refresh-dot"></span>
                        Last updated: <span>{{timestamp}}</span>
                        &nbsp;&middot;&nbsp;
                        <span>{{count}}</span> {{entriesWord}}
                        &nbsp;&middot;&nbsp;
                        Auto-refreshes every {{refreshSeconds}}&nbsp;s
                    </div>
                </header>
                <div class="filter-bar">
                    <span class="filter-label">Filter:</span>
                    <button class="filter-btn btn-all active"     data-filter="all">ALL</button>
                    <button class="filter-btn btn-trace"          data-filter="lvl-trace">TRACE</button>
                    <button class="filter-btn btn-debug"          data-filter="lvl-debug">DEBUG</button>
                    <button class="filter-btn btn-info active"    data-filter="lvl-info">INFO</button>
                    <button class="filter-btn btn-warning active" data-filter="lvl-warning">WARN</button>
                    <button class="filter-btn btn-error active"   data-filter="lvl-error">ERROR</button>
                    <button class="filter-btn btn-crit active"    data-filter="lvl-critical">CRIT</button>
                </div>
                <table>
                    <colgroup>
                        <col class="c-time">
                        <col class="c-level">
                        <col class="c-cat">
                        <col class="c-msg">
                    </colgroup>
                    <thead>
                        <tr>
                            <th>Timestamp</th>
                            <th>Level</th>
                            <th>Source</th>
                            <th>Message</th>
                        </tr>
                    </thead>
                    <tbody id="log-body">
            {{rows}}
                    </tbody>
                </table>
                <script>
                    const btns = document.querySelectorAll('.filter-btn');
                    const rows = document.querySelectorAll('#log-body tr[data-level]');
                    // Start: ALL active (show everything)
                    const active = new Set(['all']);

                    function applyFilter() {
                        const showAll = active.has('all');
                        rows.forEach(row => {
                            const lvl = row.getAttribute('data-level');
                            row.style.display = (showAll || active.has(lvl)) ? '' : 'none';
                        });
                    }

                    btns.forEach(btn => {
                        btn.addEventListener('click', () => {
                            const f = btn.getAttribute('data-filter');
                            if (f === 'all') {
                                active.clear();
                                if (!btn.classList.contains('active')) {
                                    active.add('all');
                                    btns.forEach(b => b.classList.remove('active'));
                                    btn.classList.add('active');
                                } else {
                                    btns.forEach(b => b.classList.remove('active'));
                                }
                            } else {
                                // Deselect ALL button when picking individual levels
                                active.delete('all');
                                document.querySelector('.btn-all').classList.remove('active');
                                if (active.has(f)) {
                                    active.delete(f);
                                    btn.classList.remove('active');
                                } else {
                                    active.add(f);
                                    btn.classList.add('active');
                                }
                            }
                            applyFilter();
                        });
                    });

                    // Auto-refresh every {{refreshSeconds}} seconds
                    setTimeout(() => location.reload(), {{refreshSeconds}} * 1000);
                </script>
            </body>
            </html>
            """;
    }
}
