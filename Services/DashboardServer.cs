using System.Net;
using System.Text;
using System.Text.Json;
using FileOrganizer.Logging;
using FileOrganizer.Models;
using Microsoft.Extensions.Options;

namespace FileOrganizer.Services;

/// <summary>
/// Hosts a lightweight HTTP server on localhost that serves the Dashboard UI
/// and a JSON status API polled by the browser every 5 seconds.
/// </summary>
public sealed class DashboardServer : BackgroundService
{
    private readonly ILogger<DashboardServer> _logger;
    private readonly IOptionsMonitor<FileOrganizerConfig> _config;
    private readonly HtmlLogBuffer _logBuffer;

    private static readonly byte[] DashboardHtmlBytes =
        Encoding.UTF8.GetBytes(BuildDashboardHtml());

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DashboardServer(
        ILogger<DashboardServer> logger,
        IOptionsMonitor<FileOrganizerConfig> config,
        HtmlLogBuffer logBuffer)
    {
        _logger  = logger;
        _config  = config;
        _logBuffer = logBuffer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port   = _config.CurrentValue.DashboardPort;
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            _logger.LogInformation("Dashboard available at {url}", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start Dashboard on {url}. The port may already be in use.", prefix);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try   { ctx = await listener.GetContextAsync().WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException)    { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dashboard: accept error.");
                    break;
                }
                _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
            }
        }
        finally { listener.Stop(); }
    }

    // -----------------------------------------------------------------------
    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var res  = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";

        try
        {
            // ── GET /  →  Dashboard HTML ────────────────────────────────────
            if (req.HttpMethod == "GET" && (path == "/" || path == ""))
            {
                res.StatusCode      = 200;
                res.ContentType     = "text/html; charset=utf-8";
                res.ContentLength64 = DashboardHtmlBytes.Length;
                await res.OutputStream.WriteAsync(DashboardHtmlBytes);
                res.Close();
                return;
            }

            // ── GET /api/status  →  JSON status payload ─────────────────────
            if (req.HttpMethod == "GET" && path == "/api/status")
            {
                var payload = BuildStatusPayload();
                var bytes   = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

                res.StatusCode = 200;
                res.ContentType = "application/json; charset=utf-8";
                // Allow the browser to cache for 0 seconds (always fresh)
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes);
                res.Close();
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard handler error.");
            try { res.StatusCode = 500; res.Close(); } catch { /* best-effort */ }
        }
    }

    // -----------------------------------------------------------------------
    private object BuildStatusPayload()
    {
        var cfg = _config.CurrentValue;
        var recentErrors = _logBuffer.CountRecentErrors(5);

        // Collect files from every enabled rule's target folder
        var folders = new List<object>();
        foreach (var rule in cfg.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.TargetFolder))
                continue;

            var files = new List<object>();

            if (Directory.Exists(rule.TargetFolder))
            {
                try
                {
                    var infos = new DirectoryInfo(rule.TargetFolder)
                        .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(f => f.LastWriteTime)
                        .Take(200);

                    foreach (var fi in infos)
                    {
                        files.Add(new
                        {
                            name         = fi.Name,
                            fullPath     = fi.FullName,
                            sizeBytes    = fi.Length,
                            lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dashboard: cannot enumerate '{folder}'.", rule.TargetFolder);
                }
            }

            folders.Add(new
            {
                folderName    = Path.GetFileName(rule.TargetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                path          = rule.TargetFolder,
                exists        = Directory.Exists(rule.TargetFolder),
                fileCount     = files.Count,
                files
            });
        }

        return new
        {
            serviceRunning    = true,
            hasRecentErrors   = recentErrors > 0,
            recentErrorCount  = recentErrors,
            pollingIntervalSec = cfg.PollingIntervalSeconds,
            lastUpdated       = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            logFilePath       = cfg.LogFilePath,
            reportFilePath    = cfg.ReportFilePath,
            folders
        };
    }

    // -----------------------------------------------------------------------
    private static string BuildDashboardHtml() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>FileOrganizer — Dashboard</title>
            <style>
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                body { font-family: 'Segoe UI', Arial, sans-serif; background: #0f0f1a; color: #e0e0f0; min-height: 100vh; }

                /* ── Top bar ─────────────────────────────────── */
                .top-bar {
                    position: sticky; top: 0; z-index: 100;
                    display: flex; align-items: center; flex-wrap: wrap; gap: 16px;
                    background: #0a0a15; border-bottom: 1px solid #2d2d50;
                    padding: 12px 24px;
                }
                h1 { font-size: 1.3rem; color: #a0c4ff; letter-spacing: 0.4px; white-space: nowrap; }
                .badge {
                    display: inline-block; font-size: 0.68rem; padding: 2px 9px;
                    border-radius: 10px; background: #1e3a5f; color: #7ec8e3;
                    vertical-align: middle; margin-left: 8px;
                    font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase;
                }
                .status-row { display: flex; align-items: center; gap: 20px; flex-wrap: wrap; margin-left: auto; }

                /* ── Status pills ────────────────────────────── */
                .pill {
                    display: inline-flex; align-items: center; gap: 8px;
                    font-size: 0.8rem; font-weight: 600;
                    padding: 5px 14px; border-radius: 20px;
                    border: 1px solid transparent; white-space: nowrap;
                }
                .pill-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
                .pill-service-ok  { background: #052e16; border-color: #166534; color: #4ade80; }
                .pill-service-ok  .pill-dot { background: #22c55e; animation: pulse 2s infinite; }
                .pill-service-err { background: #1c0505; border-color: #7f1d1d; color: #f87171; }
                .pill-service-err .pill-dot { background: #ef4444; }
                .pill-err-clear   { background: #052e16; border-color: #166534; color: #4ade80; }
                .pill-err-clear   .pill-dot { background: #22c55e; }
                .pill-err-warn    { background: #4c0519; border-color: #7f1d1d; color: #f87171; }
                .pill-err-warn    .pill-dot { background: #ef4444; animation: blink 1s infinite; }
                @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.35} }
                @keyframes blink { 0%,100%{opacity:1} 50%{opacity:0}    }

                .meta-text { font-size: 0.74rem; color: #4b5563; white-space: nowrap; }

                /* ── Main ────────────────────────────────────── */
                main { padding: 24px; display: flex; flex-direction: column; gap: 28px; max-width: stretch; margin: 0 auto; }

                /* ── Summary cards ───────────────────────────── */
                .summary-row { display: flex; gap: 16px; flex-wrap: wrap; }
                .summary-card {
                    flex: 1; min-width: 160px;
                    background: #141428; border: 1px solid #2d2d50; border-radius: 10px;
                    padding: 18px 20px;
                }
                .summary-card .sc-label { font-size: 0.7rem; color: #6b7280; font-weight: 700; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 6px; }
                .summary-card .sc-value { font-size: 1.7rem; font-weight: 700; color: #e0e0f0; line-height: 1; }
                .summary-card .sc-sub   { font-size: 0.72rem; color: #4b5563; margin-top: 4px; }

                /* ── Folder grid ────────────────────────────── */
                #folders-container {
                    display: grid;
                    grid-template-columns: auto;
                    gap: 20px;
                    align-items: normal;
                }

                /* ── Folder sections ─────────────────────────── */
                .folder-section {
                    background: #141428; border: 1px solid #2d2d50; border-radius: 10px;
                    overflow: hidden; min-width: 0;
                }
                .folder-head {
                    display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
                    padding: 13px 20px; background: #0f0f22; border-bottom: 1px solid #2d2d50;
                }
                .folder-head h2 {
                    font-size: 1rem; color: #a0c4ff; font-weight: 700; letter-spacing: 0.3px;
                }
                .folder-path {
                    font-size: 0.73rem; color: #4b5563; font-family: Consolas, monospace;
                    overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 600px;
                }
                .folder-count-tag {
                    margin-left: auto; font-size: 0.72rem; font-weight: 700;
                    background: #1e3a5f; color: #60a5fa; padding: 2px 10px; border-radius: 10px;
                    white-space: nowrap;
                }
                .folder-missing { padding: 28px 20px; color: #6b7280; font-size: 0.85rem; font-style: italic; }

                /* ── Scrollable table wrapper ────────────────── */
                .table-wrap {
                    overflow-x: auto;
                    overflow-y: auto;
                    max-height: 420px;   /* ~12 rows; scrolls beyond that */
                }
                table { border-collapse: collapse; font-size: 0.845rem; width: 100%; }
                thead th {
                    position: sticky; top: 0; z-index: 2;
                    text-align: left; padding: 9px 14px;
                    background: #0f0f22; color: #7eb8f7;
                    border-bottom: 1px solid #2d2d50;
                    font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.5px;
                    white-space: nowrap; cursor: pointer; user-select: none;
                }
                thead th:hover { color: #a0c4ff; }
                .sort-arrow { margin-left: 4px; font-size: 0.65rem; color: #374151; }
                thead th.sorted-asc  .sort-arrow::after { content: '▲'; color: #3b82f6; }
                thead th.sorted-desc .sort-arrow::after { content: '▼'; color: #3b82f6; }
                thead th .sort-arrow::after { content: '⇅'; }
                td { padding: 8px 14px; border-bottom: 1px solid #1a1a30; vertical-align: middle; white-space: nowrap; }
                tr:last-child td { border-bottom: none; }
                tr:hover td { background: rgba(255,255,255,0.025); }
                .col-date { color: #6b7280; font-variant-numeric: tabular-nums; font-size: 0.8rem; }
                .col-name { color: #dde0f8; font-weight: 500; max-width: 360px; overflow: hidden; text-overflow: ellipsis; }
                .col-size { color: #9ca3af; font-variant-numeric: tabular-nums; text-align: right; }
                .col-copy { text-align: center; width: 42px; }

                /* ── Copy button ─────────────────────────────── */
                .btn-copy {
                    background: none; border: 1px solid #2d2d50; border-radius: 5px;
                    color: #6b7280; cursor: pointer; padding: 3px 7px; font-size: 0.78rem;
                    transition: color .15s, border-color .15s;
                }
                .btn-copy:hover  { color: #60a5fa; border-color: #3b82f6; }
                .btn-copy.copied { color: #4ade80; border-color: #166534; }

                .no-files { padding: 30px 20px; text-align: center; color: #374151; font-style: italic; font-size: 0.85rem; }

                /* ── Offline banner ──────────────────────────── */
                #offline-banner {
                    display: none;
                    background: #4c0519; border: 1px solid #7f1d1d;
                    color: #fca5a5; padding: 14px 24px;
                    font-size: 0.9rem; font-weight: 600; text-align: center;
                }

                /* ── Toast ───────────────────────────────────── */
                #toast {
                    position: fixed; bottom: 28px; left: 50%; transform: translateX(-50%);
                    background: #1e3a5f; color: #a0c4ff; border: 1px solid #3b82f6;
                    padding: 8px 20px; border-radius: 8px; font-size: 0.82rem;
                    opacity: 0; pointer-events: none; transition: opacity .2s;
                    white-space: nowrap; z-index: 999;
                }
                #toast.show { opacity: 1; }
            </style>
        </head>
        <body>
            <div id="toast">Copied to clipboard</div>
            <div id="offline-banner">⚠ Cannot reach the service — it may have stopped. Retrying…</div>

            <div class="top-bar">
                <h1>FileOrganizer <span class="badge">Dashboard</span></h1>
                <div class="status-row">
                    <div class="pill pill-service-ok" id="svc-pill">
                        <span class="pill-dot"></span>
                        <span id="svc-label">Connecting…</span>
                    </div>
                    <div class="pill pill-err-clear" id="err-pill">
                        <span class="pill-dot"></span>
                        <span id="err-label">No recent errors</span>
                    </div>
                    <div class="meta-text">Updated: <span id="last-updated">—</span></div>
                </div>
            </div>

            <main>
                <div class="summary-row">
                    <div class="summary-card">
                        <div class="sc-label">Total Files</div>
                        <div class="sc-value" id="sum-files">—</div>
                        <div class="sc-sub">across all target folders</div>
                    </div>
                    <div class="summary-card">
                        <div class="sc-label">Target Folders</div>
                        <div class="sc-value" id="sum-folders">—</div>
                        <div class="sc-sub">configured rule(s)</div>
                    </div>
                    <div class="summary-card">
                        <div class="sc-label">Errors (last 5 min)</div>
                        <div class="sc-value" id="sum-errors" style="color:#f87171">—</div>
                        <div class="sc-sub">in the service log</div>
                    </div>
                    <div class="summary-card">
                        <div class="sc-label">Poll Interval</div>
                        <div class="sc-value" id="sum-poll">—</div>
                        <div class="sc-sub">seconds</div>
                    </div>
                </div>

                <div id="folders-container"></div>
            </main>

            <script>
                const sortState = {};

                // ── Poll ───────────────────────────────────────────
                async function poll() {
                    try {
                        const r = await fetch('/api/status');
                        if (!r.ok) throw new Error('HTTP ' + r.status);
                        const d = await r.json();
                        render(d);
                        document.getElementById('offline-banner').style.display = 'none';
                    } catch {
                        setOffline();
                    }
                }

                function setOffline() {
                    document.getElementById('offline-banner').style.display = 'block';
                    document.getElementById('svc-pill').className = 'pill pill-service-err';
                    document.getElementById('svc-label').textContent = 'Service Offline';
                }

                // ── Render ─────────────────────────────────────────
                function render(d) {
                    document.getElementById('svc-pill').className = 'pill pill-service-ok';
                    document.getElementById('svc-label').textContent = 'Service Running';

                    const ep = document.getElementById('err-pill');
                    if (d.hasRecentErrors) {
                        ep.className = 'pill pill-err-warn';
                        document.getElementById('err-label').textContent =
                            d.recentErrorCount + ' error' + (d.recentErrorCount !== 1 ? 's' : '') + ' in last 5 min';
                    } else {
                        ep.className = 'pill pill-err-clear';
                        document.getElementById('err-label').textContent = 'No recent errors';
                    }

                    document.getElementById('last-updated').textContent = d.lastUpdated;
                    const totalFiles = d.folders.reduce((s, f) => s + f.fileCount, 0);
                    document.getElementById('sum-files').textContent   = totalFiles.toLocaleString();
                    document.getElementById('sum-folders').textContent = d.folders.length;
                    document.getElementById('sum-errors').textContent  = d.recentErrorCount;
                    document.getElementById('sum-poll').textContent    = d.pollingIntervalSec;

                    const container = document.getElementById('folders-container');

                    if (d.folders.length === 0) {
                        container.innerHTML = '<p style="color:#4b5563;font-style:italic;text-align:center;padding:40px">No target folders configured.</p>';
                        return;
                    }

                    // Preserve scroll positions across re-renders
                    const scrollPositions = {};
                    container.querySelectorAll('.table-wrap').forEach(tw => {
                        if (tw.id) scrollPositions[tw.id] = tw.scrollTop;
                    });

                    d.folders.forEach((folder, fi) => {
                        const id = 'folder-' + fi;
                        let section = document.getElementById(id);
                        if (!section) {
                            section = document.createElement('div');
                            section.id = id;
                            section.className = 'folder-section';
                            container.appendChild(section);
                        }

                        const state = sortState[id] || { col: 'lastModified', asc: false };
                        sortState[id] = state;
                        section.innerHTML = folderHtml(folder, fi, sortFiles(folder.files || [], state.col, state.asc), state);
                    });

                    // Restore scroll positions
                    Object.entries(scrollPositions).forEach(([twId, top]) => {
                        const tw = document.getElementById(twId);
                        if (tw) tw.scrollTop = top;
                    });
                }

                // ── Sort ───────────────────────────────────────────
                function sortFiles(files, col, asc) {
                    return [...files].sort((a, b) => {
                        let va = a[col], vb = b[col];
                        if (col === 'sizeBytes') { va = +va; vb = +vb; }
                        const cmp = va < vb ? -1 : va > vb ? 1 : 0;
                        return asc ? cmp : -cmp;
                    });
                }

                function setSort(folderId, col) {
                    const s = sortState[folderId] || { col: 'lastModified', asc: false };
                    s.asc = s.col === col ? !s.asc : false;
                    s.col = col;
                    sortState[folderId] = s;
                    poll();
                }

                // ── Folder HTML ────────────────────────────────────
                function folderHtml(folder, fi, files, state) {
                    const id    = 'folder-' + fi;
                    const twId  = 'tw-' + fi;
                    const name  = folder.folderName || folder.path;

                    const head = `
                        <div class="folder-head">
                            <h2>${esc(name)}</h2>
                            <span class="folder-path" title="${esc(folder.path)}">${esc(folder.path)}</span>
                            <span class="folder-count-tag">${folder.fileCount.toLocaleString()} file${folder.fileCount !== 1 ? 's' : ''}</span>
                        </div>`;

                    if (!folder.exists)
                        return head + `<div class="folder-missing">Folder does not exist yet — it will be created when the first file is processed.</div>`;
                    if (files.length === 0)
                        return head + `<div class="no-files">No files in this folder.</div>`;

                    const th = (col, label) => {
                        const cls = state.col === col ? (state.asc ? 'sorted-asc' : 'sorted-desc') : '';
                        return `<th class="${cls}" onclick="setSort('${id}','${col}')">${label}<span class="sort-arrow"></span></th>`;
                    };

                    const rows = files.map((f, i) => `
                        <tr>
                            <td class="col-date">${esc(f.lastModified)}</td>
                            <td class="col-copy">
                                <button class="btn-copy" title="${esc(f.fullPath)}"
                                    onclick="copyPath(this,'${esc(f.fullPath)}')">⎘</button>
                            </td>
                            <td class="col-name" title="${esc(f.name)}">${esc(f.name)}</td>
                            <td class="col-size">${fmtSize(f.sizeBytes)}</td>
                        </tr>`).join('');

                    return `${head}
                        <div class="table-wrap" id="${twId}">
                            <table>
                                <thead><tr>
                                    ${th('lastModified','Last Modified')}
                                    <th title="Copy full path to clipboard" style="cursor:default">Copy Path</th>
                                    ${th('name','File Name')}
                                    ${th('sizeBytes','Size')}
                                </tr></thead>
                                <tbody>${rows}</tbody>
                            </table>
                        </div>`;
                }

                // ── Clipboard copy ─────────────────────────────────
                function copyPath(btn, path) {
                    navigator.clipboard.writeText(path).then(() => {
                        btn.classList.add('copied');
                        showToast('Copied: ' + path);
                        setTimeout(() => btn.classList.remove('copied'), 1500);
                    }).catch(() => {
                        // Fallback for non-secure contexts
                        const ta = document.createElement('textarea');
                        ta.value = path; ta.style.position = 'fixed'; ta.style.opacity = '0';
                        document.body.appendChild(ta); ta.select();
                        document.execCommand('copy');
                        document.body.removeChild(ta);
                        btn.classList.add('copied');
                        showToast('Copied: ' + path);
                        setTimeout(() => btn.classList.remove('copied'), 1500);
                    });
                }

                let toastTimer;
                function showToast(msg) {
                    const t = document.getElementById('toast');
                    t.textContent = msg;
                    t.classList.add('show');
                    clearTimeout(toastTimer);
                    toastTimer = setTimeout(() => t.classList.remove('show'), 2000);
                }

                // ── Helpers ────────────────────────────────────────
                function esc(s) {
                    return String(s ?? '')
                        .replace(/&/g,'&amp;').replace(/</g,'&lt;')
                        .replace(/>/g,'&gt;').replace(/"/g,'&quot;');
                }
                function fmtSize(b) {
                    if (b == null) return '—';
                    if (b < 1024)       return b + ' B';
                    if (b < 1048576)    return (b/1024).toFixed(1)     + ' KB';
                    if (b < 1073741824) return (b/1048576).toFixed(1)  + ' MB';
                    return                    (b/1073741824).toFixed(2) + ' GB';
                }

                poll();
                setInterval(poll, 5000);
            </script>
        </body>
        </html>
        """;
}
