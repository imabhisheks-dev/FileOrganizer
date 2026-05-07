namespace FileOrganizer.Models;

public enum ActivityStatus
{
    InProgress,
    Success,
    Failed
}

public class FileActivityRecord
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public string FileName { get; set; } = string.Empty;
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public FileOperation Operation { get; set; }
    public ActivityStatus Status { get; set; } = ActivityStatus.InProgress;
    public string? ErrorMessage { get; set; }
}
