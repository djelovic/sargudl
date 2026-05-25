namespace Sargudl.Services;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Cancelled,
    Failed
}

public class DownloadJob
{
    public string Url { get; init; } = "";
    public string FileName { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    public CancellationTokenSource Cts { get; init; } = new();
}
