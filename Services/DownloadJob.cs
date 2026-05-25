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
    public readonly string Url;
    public string FileName = "";
    public string DestinationPath = "";
    public long BytesDownloaded;
    public long? TotalBytes;
    public DownloadStatus Status = DownloadStatus.Queued;
    public string? Error;
    public readonly CancellationTokenSource Cts = new();

    public DownloadJob(string url) => Url = url;
}
