using System.Text.Json.Serialization;

namespace Sargudl.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadStatus {
	Downloading,
	Completed,
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
}

// Snapshot of a job's externally observable state, returned by the status API.
public record JobStatus(
	string Url,
	string FileName,
	string DestinationPath,
	long BytesDownloaded,
	long? TotalBytes,
	DownloadStatus Status,
	string? Error);
