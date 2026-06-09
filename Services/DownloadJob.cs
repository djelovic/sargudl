namespace Sargudl.Services;

public enum DownloadStatus {
	Downloading,
	Retrying,
	Completed,
	Cancelled,
	Failed,
	Paused
}

public class DownloadJob(string url, string destinationPath) {
	public readonly string Url = url;
	public string FileName => Path.GetFileName(DestinationPath);
	public readonly string DestinationPath = destinationPath;

	public string PartPath => DestinationPath + ".part";

	public long BytesDownloaded;
	public long? TotalBytes;
	public DownloadStatus Status = DownloadStatus.Downloading;
	public string? Error;
}
