using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Sargudl.Services;

public readonly struct SlimWaiter(SemaphoreSlim semaphore) : IDisposable {
	private readonly SemaphoreSlim _semaphore = semaphore;
	public readonly void Dispose() => _semaphore.Release();
}

static class SlimWaiterExtensions {
	public static async ValueTask<SlimWaiter> LockAsync(this SemaphoreSlim semaphore, CancellationToken ct = default) {
		await semaphore.WaitAsync(ct);
		return new SlimWaiter(semaphore);
	}
}

public partial class DownloadManager(
	IHttpClientFactory httpClientFactory,
	IOptions<DownloadOptions> options,
	ILogger<DownloadManager> logger) {
	[GeneratedRegex(@"^(?<name>.+?)\.s(?<season>\d{2})e\d{2}", RegexOptions.IgnoreCase)]
	private static partial Regex TvShowPattern();

	private readonly Dictionary<string, (DownloadJob Job, Task Task, CancellationTokenSource CancellationTokenSource)> _jobs = new();
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
	private readonly DownloadOptions _options = options.Value;
	private readonly ILogger<DownloadManager> _logger = logger;

	private async void RemoteWhenDone(string url, Task task, CancellationToken ct) {
		await Task.Yield(); // ensure asynchronous execution

		try {
			await task;
		}
		catch {
		}

		try {
			await Task.Delay(TimeSpan.FromDays(1), ct).ConfigureAwait(false);
			using var _ = await _lock.LockAsync(ct);
			if (_jobs.TryGetValue(url, out var entry) && entry.Task == task) _jobs.Remove(url);
		}
		catch {
		}
	}

	private async ValueTask<DownloadJob> StartOrResume(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		if (_jobs.TryGetValue(url, out var existing))
			return existing.Job;

		DownloadJob job;
		try {
			job = CreateJob(url);
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Failed to create download job for {Url}", url);
			return Failed(url, "-", ex.Message);
		}

		// Already fully downloaded on disk (no in-flight job, no partial):
		// report completed without re-downloading.
		if (File.Exists(job.DestinationPath) && !File.Exists(job.PartPath)) {
			var size = TryGetFileSize(job.DestinationPath) ?? 0;
			job.BytesDownloaded = size;
			job.TotalBytes = size;
			job.Status = DownloadStatus.Completed;
			return job;
		}

		CancellationTokenSource cts = new();
		var task = DownloadFileAsync(job, cts.Token);
		_jobs.Add(url, (job, task, cts));
		RemoteWhenDone(url, task, cts.Token);
		return job;
	}

	public async ValueTask<DownloadJob> GetAsync(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		if (_jobs.TryGetValue(url, out var existing)) return existing.Job;
		else {
			var dest = ResolveDestinationPath(url);
			if (File.Exists(dest + ".part") || !File.Exists(dest)) {
				return new DownloadJob(url, dest) {
					BytesDownloaded = TryGetFileSize(dest + ".part") ?? 0,
					TotalBytes = await GetRemoteFileSizeAsync(new Uri(url), ct),
					Status = DownloadStatus.Paused
				};
			}
			else {
				var size = TryGetFileSize(dest) ?? 0;
				return new DownloadJob(url, dest) {
					BytesDownloaded = size,
					TotalBytes = size,
					Status = DownloadStatus.Completed
				};
			}
		}
	}

	public async Task CancelAsync(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		if (!_jobs.TryGetValue(url, out var entry)) return;
		entry.CancellationTokenSource.Cancel();
		try {
			await entry.Task;
		}
		catch {
		}

		DeletePart(entry.Job.PartPath);
		_jobs.Remove(url);
	}

	public async Task PauseAsync(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		if (!_jobs.TryGetValue(url, out var entry)) return;
		entry.CancellationTokenSource.Cancel();
		try {
			await entry.Task;
		}
		catch {
		}

		_jobs.Remove(url);
	}

	public ValueTask<DownloadJob> ResumeAsync(string url, CancellationToken ct) => StartOrResume(url, ct);

	public ValueTask<DownloadJob> StartAsync(string url, CancellationToken ct) => StartOrResume(url, ct);

	private DownloadJob CreateJob(string url) {
		var destinationPath = ResolveDestinationPath(url);
		var destinationDir = Path.GetDirectoryName(destinationPath);
		if (!string.IsNullOrEmpty(destinationDir)) {
			try {
				Directory.CreateDirectory(destinationDir);
			}
			catch (Exception ex) {
				throw new IOException($"Failed to create destination directory: {ex.Message}", ex);
			}
		}

		return new(url, destinationPath);
	}

	private static DownloadJob Failed(string url, string destinationPath, string error) =>
		new(url, destinationPath) {
			Status = DownloadStatus.Failed,
			Error = error
		};

	private string ResolveDestinationPath(string url) {
		Uri uri;
		try {
			uri = new Uri(url);
		}
		catch (UriFormatException ex) {
			throw new ArgumentException($"Invalid URL: {ex.Message}", nameof(url), ex);
		}

		var fileName = Path.GetFileName(WebUtility.UrlDecode(uri.AbsolutePath));
		if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Could not determine a filename from the URL.", nameof(url));

		var match = TvShowPattern().Match(fileName);
		if (match.Success) {
			var showName = match.Groups["name"].Value.Replace('.', ' ').Trim();
			var season = int.Parse(match.Groups["season"].Value);
			return Path.Combine(_options.TvShowsPath, showName, $"Season {season}", fileName);
		}
		return Path.Combine(_options.MoviesPath, fileName);
	}

	private async IAsyncEnumerable<(long BytesDownloaded, long? TotalBytes)> DownloadFileAsync(Uri url, string destinationPath, [EnumeratorCancellation] CancellationToken ct = default) {
		var partPath = destinationPath + ".part";

		await using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
			yield return (file.Length, null);

			var client = _httpClientFactory.CreateClient("download");
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			AddAuth(request, url);

			if (file.Length > 0) request.Headers.Range = new RangeHeaderValue(file.Length, null);

			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

			var status = (int)response.StatusCode;
			if (status >= 400 && status < 500) {
				throw new Exception($"Server responded with {status} {response.ReasonPhrase}.");
			}
			response.EnsureSuccessStatusCode();

			var append = response.StatusCode == HttpStatusCode.PartialContent && file.Length > 0;
			if (append) {
				file.Seek(0, SeekOrigin.End);
			}
			else if (file.Length > 0) {
				file.SetLength(0);
				yield return (file.Length, null);
			}

			long? totalBytes =
				response.Content.Headers.ContentRange?.Length is long rangeLen ? rangeLen :
				response.Content.Headers.ContentLength is long contentLen ? (append ? contentLen + file.Length : contentLen) :
				null;

			yield return (file.Length, totalBytes);

			await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
			var buffer = new byte[81920];
			int read;
			while ((read = await responseStream.ReadAsync(buffer, ct)) > 0) {
				await file.WriteAsync(buffer.AsMemory(0, read), ct);
				yield return (file.Length, totalBytes);
			}
		}

		if (File.Exists(destinationPath)) File.Delete(destinationPath);
		File.Move(partPath, destinationPath);
	}

	private async Task DownloadFileAsync(DownloadJob job, CancellationToken ct) {
		await Task.Yield(); // ensure asynchronous execution

		var attempt = 0;
		while (!ct.IsCancellationRequested) {
			job.Status = DownloadStatus.Downloading;

			attempt++;
			try {
				await foreach (var (downloaded, total) in DownloadFileAsync(new Uri(job.Url), job.DestinationPath, ct).WithCancellation(ct)) {
					job.BytesDownloaded = downloaded;
					job.TotalBytes = total;
				}
				job.Status = DownloadStatus.Completed;
				break;
			}
			catch (Exception ex) when (!ct.IsCancellationRequested) {
				_logger.LogWarning(ex,
					"Transient error downloading {Url} (attempt {Attempt}); will retry",
					job.Url, attempt);

				job.Status = DownloadStatus.Retrying;
				var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
				await Task.Delay(delay, ct);
			}
		}
	}

	private static long? TryGetFileSize(string path) {
		try {
			return new FileInfo(path).Length;
		}
		catch {
			return null;
		}
	}

	private static long? GetRemoteFileSize(HttpResponseMessage response) =>
		response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;

	private async ValueTask<long?> GetRemoteFileSizeAsync(Uri uri, CancellationToken ct) {
		try {
			var client = _httpClientFactory.CreateClient("download");
			using var request = new HttpRequestMessage(HttpMethod.Head, uri);
			AddAuth(request, uri);

			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

			return response.IsSuccessStatusCode ? GetRemoteFileSize(response) : null;
		}
		catch (Exception ex) {
			_logger.LogDebug(ex, "HEAD precheck for {Url} failed; will proceed with full download", uri);
			return null;
		}
	}

	private void AddAuth(HttpRequestMessage request, Uri uri) {
		var creds = GetBasicAuth(uri);
		if (creds == null) return;
		var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
	}

	private void DeletePart(string partPath) {
		try {
			if (File.Exists(partPath))
				File.Delete(partPath);
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Failed to delete partial file {PartPath}", partPath);
		}
	}

	private BasicAuthCredentials? GetBasicAuth(Uri uri) {
		foreach (var (domain, creds) in _options.BasicAuth) {
			if (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
				uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
				return creds;
		}
		return null;
	}
}
