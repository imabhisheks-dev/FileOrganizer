using FileOrganizer.Models;
using Microsoft.Extensions.Options;

namespace FileOrganizer.Services;

public class FileProcessorService
{
    private readonly ILogger<FileProcessorService> _logger;
    private readonly IOptionsMonitor<FileOrganizerConfig> _config;
    private readonly ActivityReportService _report;

    public FileProcessorService(
        ILogger<FileProcessorService> logger,
        IOptionsMonitor<FileOrganizerConfig> config,
        ActivityReportService report)
    {
        _logger = logger;
        _config = config;
        _report = report;
    }

    public async Task ProcessAllRulesAsync(CancellationToken cancellationToken)
    {
        var config = _config.CurrentValue;
        var activeRules = config.Rules.Where(r => r.Enabled).ToList();

        if (activeRules.Count == 0)
        {
            _logger.LogInformation("No active rules found. Skipping scan.");
            return;
        }

        _logger.LogInformation("Starting scan — {count} active rule(s).", activeRules.Count);

        foreach (var rule in activeRules)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessRuleAsync(rule, cancellationToken);
        }

        _logger.LogInformation("Scan complete.");
    }

    // -------------------------------------------------------------------------
    private async Task ProcessRuleAsync(FileRule rule, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[{rule}] Processing rule — pattern: '{pattern}', operation: {op}.",
            rule.Name, string.Join(", ", rule.FilePatterns), rule.Operation);

        if (string.IsNullOrWhiteSpace(rule.TargetFolder))
        {
            _logger.LogWarning("[{rule}] TargetFolder is not configured. Skipping.", rule.Name);
            return;
        }

        if (rule.SourceFolders.Count == 0)
        {
            _logger.LogWarning("[{rule}] No SourceFolders configured. Skipping.", rule.Name);
            return;
        }

        // Ensure target folder exists (not needed for Delete operations)
        if (rule.Operation != FileOperation.Delete)
        {
            try
            {
                Directory.CreateDirectory(rule.TargetFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{rule}] Cannot create TargetFolder '{folder}'.", rule.Name, rule.TargetFolder);
                return;
            }
        }

        var searchOption = rule.IncludeSubDirectories
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        foreach (var sourceFolder in rule.SourceFolders)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessSourceFolderAsync(rule, sourceFolder, searchOption, cancellationToken);
        }
    }

    // -------------------------------------------------------------------------
    private async Task ProcessSourceFolderAsync(
        FileRule rule,
        string sourceFolder,
        SearchOption searchOption,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceFolder))
        {
            _logger.LogWarning("[{rule}] Source folder '{folder}' does not exist. Skipping.", rule.Name, sourceFolder);
            return;
        }

        // Collect files for all patterns, deduplicating by full path.
        // Use EnumerateFiles + explicit extension check to avoid the Windows legacy
        // quirk where a 3-char extension pattern (e.g. *.dmp) also matches files
        // whose extension merely *starts* with those characters (e.g. *.dmpx).
        var fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in rule.FilePatterns)
        {
            // Determine if we need strict extension filtering:
            // patterns like "*.ext" where ext has no wildcards itself need the extra check.
            var needsExtCheck = pattern.StartsWith("*.", StringComparison.Ordinal)
                                && !pattern.Substring(2).Contains('*')
                                && !pattern.Substring(2).Contains('?');
            var expectedExt = needsExtCheck
                ? pattern.Substring(1) // e.g. ".dmp"
                : null;

            try
            {
                foreach (var f in Directory.EnumerateFiles(sourceFolder, pattern, searchOption))
                {
                    if (expectedExt is not null &&
                        !Path.GetExtension(f).Equals(expectedExt, StringComparison.OrdinalIgnoreCase))
                        continue;

                    fileSet.Add(f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{rule}] Error scanning '{folder}' with pattern '{pattern}'.", rule.Name, sourceFolder, pattern);
            }
        }

        if (fileSet.Count == 0)
        {
            _logger.LogDebug("[{rule}] No files matching [{patterns}] in '{folder}'.",
                rule.Name, string.Join(", ", rule.FilePatterns), sourceFolder);
            return;
        }

        _logger.LogInformation("[{rule}] Found {count} file(s) in '{folder}'.", rule.Name, fileSet.Count, sourceFolder);

        foreach (var filePath in fileSet)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessFileAsync(rule, filePath);
        }
    }

    // -------------------------------------------------------------------------
    private async Task ProcessFileAsync(FileRule rule, string sourceFilePath)
    {
        // Guard: file must still exist (could have been moved by a prior rule iteration)
        if (!File.Exists(sourceFilePath))
        {
            _logger.LogDebug("[{rule}] '{file}' no longer exists — skipping.", rule.Name, sourceFilePath);
            return;
        }

        // Guard: MostRecentAgeDays — only process files modified/created within N days
        if (rule.MostRecentAgeDays < 0)
        {
            _logger.LogError("[{rule}] Invalid MostRecentAgeDays value ({val}) — must be 0 (disabled) or a positive number. Skipping '{file}'.",
                rule.Name, rule.MostRecentAgeDays, Path.GetFileName(sourceFilePath));
            return;
        }

        if (rule.MostRecentAgeDays > 0)
        {
            try
            {
                var lastWrite  = File.GetLastWriteTime(sourceFilePath);
                var createTime = File.GetCreationTime(sourceFilePath);
                var mostRecent = lastWrite > createTime ? lastWrite : createTime;
                var ageDays    = (DateTime.Now - mostRecent).TotalDays;
                if (ageDays > rule.MostRecentAgeDays)
                {
                    _logger.LogDebug("[{rule}] Skipping '{file}' — most recent change {age:F2}d ago, limit is {max:F2}d ({maxH:F1} h).",
                        rule.Name, Path.GetFileName(sourceFilePath), ageDays,
                        rule.MostRecentAgeDays, rule.MostRecentAgeDays * 24);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{rule}] Could not read timestamps for '{file}'.", rule.Name, sourceFilePath);
            }
        }

        // Guard: OlderThanDays — skip files changed more recently than N days
        if (rule.OlderThanDays < 0)
        {
            _logger.LogError("[{rule}] Invalid OlderThanDays value ({val}) — must be 0 (disabled) or a positive number. Skipping '{file}'.",
                rule.Name, rule.OlderThanDays, Path.GetFileName(sourceFilePath));
            return;
        }

        if (rule.OlderThanDays > 0)
        {
            try
            {
                var lastWrite  = File.GetLastWriteTime(sourceFilePath);
                var createTime = File.GetCreationTime(sourceFilePath);
                var mostRecent = lastWrite > createTime ? lastWrite : createTime;
                var ageDays    = (DateTime.Now - mostRecent).TotalDays;
                if (ageDays < rule.OlderThanDays)
                {
                    _logger.LogDebug("[{rule}] Skipping '{file}' — age {age:F2}d, must be older than {min:F2}d ({minH:F1} h).",
                        rule.Name, Path.GetFileName(sourceFilePath), ageDays,
                        rule.OlderThanDays, rule.OlderThanDays * 24);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{rule}] Could not read timestamps for '{file}'.", rule.Name, sourceFilePath);
            }
        }

        // --- Delete operation (no target folder involved) ---
        if (rule.Operation == FileOperation.Delete)
        {
            var deleteRecord = _report.AddRecord(new FileActivityRecord
            {
                Time         = DateTime.Now,
                FileName     = Path.GetFileName(sourceFilePath),
                SourceFolder = Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
                TargetFolder = string.Empty,
                Operation    = rule.Operation,
                Status       = ActivityStatus.InProgress
            });
            try
            {
                await Task.Run(() => File.Delete(sourceFilePath));
                _logger.LogInformation("[{rule}] Deleted: '{src}'", rule.Name, sourceFilePath);
                _report.UpdateRecord(deleteRecord, ActivityStatus.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{rule}] Failed to delete '{src}'.", rule.Name, sourceFilePath);
                _report.UpdateRecord(deleteRecord, ActivityStatus.Failed, ex.Message);
            }
            return;
        }

        var rawFileName = Path.GetFileName(sourceFilePath);

        // Optionally prefix the file name with the last segment of its source folder
        var fileName = rule.PrependSourceFolderName
            ? BuildPrefixedFileName(sourceFilePath, rawFileName)
            : rawFileName;

        var baseTarget = Path.Combine(rule.TargetFolder, fileName);
        string targetFilePath;
        bool overwrite;

        if (!File.Exists(baseTarget))
        {
            targetFilePath = baseTarget;
            overwrite = false;
        }
        else
        {
            // SkipIfSameSize: bail out early if the target already has the same byte count
            if (rule.SkipIfSameSize)
            {
                try
                {
                    var srcSize = new FileInfo(sourceFilePath).Length;
                    var dstSize = new FileInfo(baseTarget).Length;
                    if (srcSize == dstSize)
                    {
                        _logger.LogInformation(
                            "[{rule}] Skipping '{file}' — target exists with identical size ({bytes} bytes).",
                            rule.Name, fileName, srcSize);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{rule}] Could not compare file sizes for '{file}'.", rule.Name, fileName);
                }
            }

            switch (rule.DuplicateHandling)
            {
                case DuplicateHandling.Skip:
                    _logger.LogInformation("[{rule}] Skipping '{file}' — already exists in target.", rule.Name, fileName);
                    return;

                case DuplicateHandling.Overwrite:
                    targetFilePath = baseTarget;
                    overwrite = true;
                    break;

                case DuplicateHandling.Rename:
                default:
                    targetFilePath = BuildUniqueTargetPath(rule.TargetFolder, fileName);
                    overwrite = false;
                    break;
            }
        }

        var activityRecord = _report.AddRecord(new FileActivityRecord
        {
            Time = DateTime.Now,
            FileName = fileName,
            SourceFolder = Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
            TargetFolder = rule.TargetFolder,
            Operation = rule.Operation,
            Status = ActivityStatus.InProgress
        });

        try
        {
            if (rule.Operation == FileOperation.Move)
            {
                await Task.Run(() => File.Move(sourceFilePath, targetFilePath, overwrite));
                _logger.LogInformation("[{rule}] Moved:  '{src}' -> '{dst}'", rule.Name, sourceFilePath, targetFilePath);
            }
            else
            {
                await Task.Run(() => File.Copy(sourceFilePath, targetFilePath, overwrite));
                _logger.LogInformation("[{rule}] Copied: '{src}' -> '{dst}'", rule.Name, sourceFilePath, targetFilePath);
            }

            _report.UpdateRecord(activityRecord, ActivityStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{rule}] Failed to {op} '{src}'.", rule.Name, rule.Operation, sourceFilePath);
            _report.UpdateRecord(activityRecord, ActivityStatus.Failed, ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    private static string BuildUniqueTargetPath(string targetFolder, string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(targetFolder, $"{nameWithoutExt}_{timestamp}{extension}");
    }

    // -------------------------------------------------------------------------
    /// <summary>
    /// Prepends the last folder segment of the source path to the file name.
    /// e.g.  ...\\Dumps\\Partners\\file.dmp  →  Partners-file.dmp
    /// Invalid path characters in the folder segment are stripped.
    /// </summary>
    private static string BuildPrefixedFileName(string sourceFilePath, string rawFileName)
    {
        var sourceDir = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(sourceDir))
            return rawFileName;

        var folderSegment = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderSegment))
            return rawFileName;

        // Strip characters that are invalid in file names
        var invalid = Path.GetInvalidFileNameChars();
        var clean   = string.Concat(folderSegment.Where(c => !invalid.Contains(c)));

        return string.IsNullOrEmpty(clean) ? rawFileName : $"{clean}-{rawFileName}";
    }
}
