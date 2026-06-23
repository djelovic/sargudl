using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MiniDl.Services;

// Thrown for download failures that should not be retried (e.g. 4xx responses).
public sealed class PermanentDownloadException(string message) : Exception(message);

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

	private const int _maxAttempts = 5;
	private static readonly TimeSpan _failureRetention = TimeSpan.FromDays(1);
	private static readonly TimeSpan _statusPollInterval = TimeSpan.FromSeconds(1);

	// Currently running downloads, keyed by URL.
	private readonly Dictionary<string, (DownloadJob Job, Task Task, CancellationTokenSource CancellationTokenSource)> _jobs = new();
	// Failed downloads, keyed by URL. A job that is neither running nor failed
	// (nor complete on disk) is considered paused — the failure exception is the
	// only thing that distinguishes a failed job from a paused one. Each record
	// carries a CancellationTokenSource used to cancel its delayed cleanup when
	// the entry is superseded before it expires.
	private readonly Dictionary<string, (Exception Exception, CancellationTokenSource Cleanup)> _failures = new();
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
	private readonly DownloadOptions _options = options.Value;
	private readonly ILogger<DownloadManager> _logger = logger;

	// Observes a worker task and updates the dictionaries when it ends:
	// success/cancellation simply drops it from _jobs; a fault records the
	// exception in _failures so the job becomes observable as Failed.
	private async void TrackCompletion(string url, Task task) {
		await Task.Yield(); // ensure asynchronous execution

		Exception? failure = null;
		try {
			await task;
		}
		catch (OperationCanceledException) {
			// Paused or cancelled — StopAsync owns the dictionary cleanup.
			return;
		}
		catch (Exception ex) {
			failure = ex;
		}

		try {
			using var _ = await _lock.LockAsync();
			if (!_jobs.TryGetValue(url, out var entry) || entry.Task != task) return;
			_jobs.Remove(url);
			if (failure != null) RecordFailure(url, failure);
		}
		catch {
		}
	}

	// Records a failure for the URL and schedules its delayed cleanup, cancelling
	// any failure previously recorded for the URL. Must be called under _lock.
	private void RecordFailure(string url, Exception failure) {
		ClearFailure(url);
		var cleanup = new CancellationTokenSource();
		_failures[url] = (failure, cleanup);
		// Fire-and-forget: keep the failure observable for a while, then drop it
		// unless another action changes (and cancels) this record first.
		_ = ExpireFailureAsync(url, cleanup);
	}

	// Removes a failure record (if any) and cancels its pending cleanup wait.
	// Must be called under _lock.
	private void ClearFailure(string url) {
		if (_failures.Remove(url, out var entry))
			entry.Cleanup.Cancel();
	}

	// Waits out the retention period, then drops the failure record unless it has
	// been superseded (its cleanup token cancelled) in the meantime.
	private async Task ExpireFailureAsync(string url, CancellationTokenSource cleanup) {
		try {
			try {
				await Task.Delay(_failureRetention, cleanup.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) {
				return; // superseded by another action
			}

			using var _ = await _lock.LockAsync();
			if (_failures.TryGetValue(url, out var entry) && entry.Cleanup == cleanup)
				_failures.Remove(url);
		}
		catch {
		}
		finally {
			cleanup.Dispose();
		}
	}

	private async ValueTask<JobStatus> StartOrResume(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		// Already running: report its live state.
		if (_jobs.TryGetValue(url, out var existing)) {
			var j = existing.Job;
			var running = j.IsWaiting ? DownloadStatus.Retrying : DownloadStatus.Downloading;
			return new JobStatus(url, j.FileName, j.DestinationPath, j.BytesDownloaded, j.TotalBytes, running, null);
		}

		// (Re)starting clears any prior failure for this URL.
		ClearFailure(url);

		DownloadJob job;
		try {
			job = CreateJob(url);
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Failed to create download job for {Url}", url);
			RecordFailure(url, ex);
			return new JobStatus(url, "-", "-", 0, null, DownloadStatus.Failed, ex.Message);
		}

		// Already fully downloaded on disk (no partial): report completed.
		if (File.Exists(job.DestinationPath) && !File.Exists(job.PartPath)) {
			var size = TryGetFileSize(job.DestinationPath) ?? 0;
			return new JobStatus(url, job.FileName, job.DestinationPath, size, size, DownloadStatus.Completed, null);
		}

		CancellationTokenSource cts = new();
		var task = DownloadFileAsync(job, cts.Token);
		_jobs.Add(url, (job, task, cts));
		TrackCompletion(url, task);

		// Just spawned: bytes come from any resumed partial; total isn't known
		// until the worker reads the response headers (the stream will fill it in).
		var startBytes = TryGetFileSize(job.PartPath) ?? 0;
		return new JobStatus(url, job.FileName, job.DestinationPath, startBytes, null, DownloadStatus.Downloading, null);
	}

	public ValueTask<JobStatus> StartAsync(string url, CancellationToken ct) => StartOrResume(url, ct);

	public ValueTask<JobStatus> ResumeAsync(string url, CancellationToken ct) => StartOrResume(url, ct);

	public Task<JobStatus> PauseAsync(string url, CancellationToken ct) => StopAsync(url, pause: true, ct);

	public Task<JobStatus> CancelAsync(string url, CancellationToken ct) => StopAsync(url, pause: false, ct);

	// Stops a download. Pause keeps the .part file; cancel deletes it. Both are
	// idempotent and also clear any failed state for the URL.
	private async Task<JobStatus> StopAsync(string url, bool pause, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		ClearFailure(url);

		string dest, fileName;
		if (_jobs.TryGetValue(url, out var entry)) {
			dest = entry.Job.DestinationPath;
			fileName = entry.Job.FileName;
			entry.CancellationTokenSource.Cancel();
			try {
				await entry.Task;
			}
			catch {
			}
			_jobs.Remove(url);
		}
		else {
			// No running job; resolve the path to report on the on-disk state.
			try {
				dest = ResolveDestinationPath(url);
			}
			catch (Exception ex) {
				return new JobStatus(url, "-", "-", 0, null, DownloadStatus.Failed, ex.Message);
			}
			fileName = Path.GetFileName(dest);
		}

		// Cancel is a hard stop: drop the partial. Pause keeps it for resume.
		var partPath = dest + ".part";
		if (!pause) DeletePart(partPath);

		// With a partial on disk it's paused; without one it was never started
		// (or was cancelled, which deletes the partial). Total is left to the
		// status stream to probe — no HEAD on the stop path.
		var partExists = File.Exists(partPath);
		var bytes = partExists ? TryGetFileSize(partPath) ?? 0 : 0;
		var status = partExists ? DownloadStatus.Paused : DownloadStatus.NotStarted;
		return new JobStatus(url, fileName, dest, bytes, null, status, null);
	}

	public async ValueTask<JobStatus> GetAsync(string url, CancellationToken ct) {
		using var _ = await _lock.LockAsync(ct);

		if (_jobs.TryGetValue(url, out var entry)) {
			var j = entry.Job;
			var status = j.IsWaiting ? DownloadStatus.Retrying : DownloadStatus.Downloading;
			return new JobStatus(url, j.FileName, j.DestinationPath, j.BytesDownloaded, j.TotalBytes, status, null);
		}

		string dest;
		try {
			dest = ResolveDestinationPath(url);
		}
		catch (Exception ex) {
			// Can't even resolve the URL — surface it as a failure.
			var msg = _failures.TryGetValue(url, out var fex) ? fex.Exception.Message : ex.Message;
			return new JobStatus(url, "-", "-", 0, null, DownloadStatus.Failed, msg);
		}

		var fileName = Path.GetFileName(dest);
		var partPath = dest + ".part";

		// Complete on disk: final file present, no partial.
		if (!File.Exists(partPath) && File.Exists(dest)) {
			var size = TryGetFileSize(dest) ?? 0;
			return new JobStatus(url, fileName, dest, size, size, DownloadStatus.Completed, null);
		}

		var partExists = File.Exists(partPath);
		var bytes = partExists ? TryGetFileSize(partPath) ?? 0 : 0;
		var total = await GetRemoteFileSizeAsync(new Uri(url), ct);

		if (_failures.TryGetValue(url, out var ex2))
			return new JobStatus(url, fileName, dest, bytes, total, DownloadStatus.Failed, ex2.Exception.Message);

		// With a partial on disk it's paused; without one it was never started
		// (or was cancelled, which deletes the partial).
		var stopped = partExists ? DownloadStatus.Paused : DownloadStatus.NotStarted;
		return new JobStatus(url, fileName, dest, bytes, total, stopped, null);
	}

	// Streams status snapshots for a URL, yielding only when the status changes
	// and completing once the download reaches a terminal state. Polls on a fixed
	// interval; honours the caller's token (cancelled when the client disconnects).
	// Self-contained: reads the live job from _jobs, otherwise derives state from
	// disk, probing the remote size at most once and caching it in a local.
	public async IAsyncEnumerable<JobStatus> WatchAsync(string url, [EnumeratorCancellation] CancellationToken ct = default) {
		// The destination never changes for a given URL; resolve it once. An
		// unresolvable URL can only ever be a failure, so report and stop.
		string? dest = null;
		string? resolveError = null;
		try {
			dest = ResolveDestinationPath(url);
		}
		catch (Exception ex) {
			resolveError = ex.Message;
		}

		if (dest == null) {
			string msg;
			using (await _lock.LockAsync(ct))
				msg = _failures.TryGetValue(url, out var fex) ? fex.Exception.Message : resolveError!;
			yield return new JobStatus(url, "-", "-", 0, null, DownloadStatus.Failed, msg);
			yield break;
		}

		var fileName = Path.GetFileName(dest);
		var partPath = dest + ".part";

		long? remoteTotal = null;
		bool remoteProbed = false;
		JobStatus? last = null;

		while (!ct.IsCancellationRequested) {
			// Snapshot the in-memory state under the lock; build/yield outside it.
			DownloadJob? running = null;
			string? failureMessage = null;
			using (await _lock.LockAsync(ct)) {
				if (_jobs.TryGetValue(url, out var entry)) running = entry.Job;
				else if (_failures.TryGetValue(url, out var fex)) failureMessage = fex.Exception.Message;
			}

			JobStatus status;
			if (running != null) {
				// Capture the live total so we never need a probe once it ends.
				if (running.TotalBytes is long t) { remoteTotal = t; remoteProbed = true; }
				var s = running.IsWaiting ? DownloadStatus.Retrying : DownloadStatus.Downloading;
				status = new JobStatus(url, running.FileName, running.DestinationPath, running.BytesDownloaded, running.TotalBytes, s);
			}
			else if (!File.Exists(partPath) && File.Exists(dest)) {
				var size = TryGetFileSize(dest) ?? 0;
				yield return new JobStatus(url, fileName, dest, size, size, DownloadStatus.Completed);
				break;
			}
			else {
				// Not running and not complete: paused if a partial exists,
				// otherwise never started (a cancel deletes the partial). A
				// recorded failure overrides either. Probe the remote size at
				// most once for the lifetime of this stream.
				var partExists = File.Exists(partPath);
				var bytes = partExists ? TryGetFileSize(partPath) ?? 0 : 0;
				if (!remoteProbed) {
					remoteTotal = await GetRemoteFileSizeAsync(new Uri(url), ct);
					remoteProbed = true;
				}
				var stopped = partExists ? DownloadStatus.Paused : DownloadStatus.NotStarted;
				status = new JobStatus(url, fileName, dest, bytes, remoteTotal, failureMessage != null ? DownloadStatus.Failed : stopped, failureMessage);
			}

			if (last != status) {
				yield return status;
				last = status;
			}

			if (status.Status is DownloadStatus.Completed or DownloadStatus.Failed) break;
			await Task.Delay(_statusPollInterval, ct);
		}
	}

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
				throw new PermanentDownloadException($"Server responded with {status} {response.ReasonPhrase}.");
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
		while (true) {
			ct.ThrowIfCancellationRequested();
			job.IsWaiting = false;

			attempt++;
			try {
				await foreach (var (downloaded, total) in DownloadFileAsync(new Uri(job.Url), job.DestinationPath, ct).WithCancellation(ct)) {
					job.BytesDownloaded = downloaded;
					job.TotalBytes = total;
				}
				return; // success
			}
			catch (PermanentDownloadException) {
				throw; // give up; TrackCompletion records the failure
			}
			catch (Exception ex) when (!ct.IsCancellationRequested) {
				if (attempt >= _maxAttempts) {
					_logger.LogWarning(ex, "Download of {Url} failed after {Attempt} attempts; giving up", job.Url, attempt);
					throw;
				}

				_logger.LogWarning(ex,
					"Transient error downloading {Url} (attempt {Attempt}); will retry",
					job.Url, attempt);

				var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
				job.IsWaiting = true;
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

	// Deletes the given .part file if present.
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
