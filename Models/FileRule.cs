namespace FileOrganizer.Models;

public class FileRule
{
    /// <summary>Friendly name shown in logs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Set to false to disable this rule without removing it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>One or more glob patterns to match files, e.g. ["*.dmp", "*.log"]. All patterns are evaluated; duplicates are deduplicated. If empty, no files are matched.</summary>
    public List<string> FilePatterns { get; set; } = new();

    /// <summary>List of folders to scan for matching files.</summary>
    public List<string> SourceFolders { get; set; } = new();

    /// <summary>Folder where matched files are placed. Created automatically if absent.</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>Whether to Move or Copy matched files to the target folder.</summary>
    public FileOperation Operation { get; set; } = FileOperation.Copy;

    /// <summary>When true, sub-directories inside each SourceFolder are also scanned.</summary>
    public bool IncludeSubDirectories { get; set; } = false;

    /// <summary>
    /// How to handle a file that already exists in the target folder.
    ///   Skip      – leave the source file untouched.
    ///   Overwrite – replace the existing target file.
    ///   Rename    – append a timestamp to the filename (default).
    /// </summary>
    public DuplicateHandling DuplicateHandling { get; set; } = DuplicateHandling.Rename;

    /// <summary>
    /// Only process files whose most-recent timestamp (creation or last-write, whichever
    /// is newer) is within this many days. Fractional days are supported (e.g. 0.5 = 12 h,
    /// 0.083 ≈ 2 h). 0 = disabled (process all files). Negative = invalid (rule is skipped
    /// with an error logged).
    /// </summary>
    public double MostRecentAgeDays { get; set; } = 0;

    /// <summary>
    /// Only process files whose most-recent timestamp (creation or last-write, whichever
    /// is newer) is older than this many days. Fractional days are supported (e.g. 0.5 = 12 h).
    /// Primarily used for Delete rules to avoid deleting recently active files.
    /// 0 = disabled. Negative = invalid (rule is skipped with an error logged).
    /// </summary>
    public double OlderThanDays { get; set; } = 0;

    /// <summary>
    /// When true, the last segment of the source folder path is prepended to the
    /// destination file name, separated by a hyphen.
    /// Example: source = ...\Dumps\Partners\file.dmp  →  target = Partners-file.dmp
    /// </summary>
    public bool PrependSourceFolderName { get; set; } = false;

    /// <summary>
    /// When true and a file with the same name already exists in the target folder,
    /// the operation is skipped if the source and target have identical file sizes.
    /// This check runs before DuplicateHandling, regardless of that setting.
    /// </summary>
    public bool SkipIfSameSize { get; set; } = false;
}

public enum FileOperation
{
    Copy,
    Move,
    Delete
}

public enum DuplicateHandling
{
    Skip,
    Overwrite,
    Rename
}
