using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Sargudl.Services;

public partial class DownloadWorker
{
    [GeneratedRegex(@"^(?<name>.+?)\.s(?<season>\d{2})e\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex TvShowPattern();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DownloadOptions _options;
    private readonly ILogger<DownloadWorker> _logger;

    public DownloadWorker(
        IHttpClientFactory httpClientFactory,
        IOptions<DownloadOptions> options,
        ILogger<DownloadWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(DownloadJob job)
    {
        Uri uri;
        try
        {
            uri = new Uri(job.Url);
        }
        catch (UriFormatException ex)
        {
            job.Status = DownloadStatus.Failed;
            job.Error = $"Invalid URL: {ex.Message}";
            return;
        }

        var fileName = Path.GetFileName(WebUtility.UrlDecode(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            job.Status = DownloadStatus.Failed;
            job.Error = "Could not determine a filename from the URL.";
            return;
        }

        job.FileName = fileName;
        job.DestinationPath = ResolveDestinationPath(fileName);

        var destinationDir = Path.GetDirectoryName(job.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        var partPath = job.DestinationPath + ".part";
        job.Status = DownloadStatus.Downloading;

        if (File.Exists(job.DestinationPath) && !File.Exists(partPath))
        {
            try
            {
                if (await TrySkipIfMatchingAsync(uri, job))
                    return;
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                MarkCancelled(job, partPath);
                return;
            }
        }

        var attempt = 0;
        while (!job.Cts.IsCancellationRequested)
        {
            attempt++;
            try
            {
                var startPosition = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                job.BytesDownloaded = startPosition;

                var client = _httpClientFactory.CreateClient("download");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                AddAuth(request, uri);

                if (startPosition > 0)
                    request.Headers.Range = new RangeHeaderValue(startPosition, null);

                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, job.Cts.Token);

                var status = (int)response.StatusCode;
                if (status >= 400 && status < 500)
                {
                    job.Status = DownloadStatus.Failed;
                    job.Error = $"Server responded with {status} {response.ReasonPhrase}.";
                    return;
                }
                response.EnsureSuccessStatusCode();

                var append = response.StatusCode == HttpStatusCode.PartialContent && startPosition > 0;
                if (!append && startPosition > 0)
                {
                    startPosition = 0;
                    job.BytesDownloaded = 0;
                }

                long? total = null;
                if (response.Content.Headers.ContentRange?.Length is long rangeLen)
                    total = rangeLen;
                else if (response.Content.Headers.ContentLength is long contentLen)
                    total = append ? contentLen + startPosition : contentLen;
                job.TotalBytes = total;

                await using var responseStream = await response.Content.ReadAsStreamAsync(job.Cts.Token);
                await using (var fileStream = new FileStream(
                    partPath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await responseStream.ReadAsync(buffer, job.Cts.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), job.Cts.Token);
                        job.BytesDownloaded += read;
                    }
                    await fileStream.FlushAsync(job.Cts.Token);
                }

                if (File.Exists(job.DestinationPath))
                    File.Delete(job.DestinationPath);
                File.Move(partPath, job.DestinationPath);

                job.Status = DownloadStatus.Completed;
                job.TotalBytes ??= job.BytesDownloaded;
                return;
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                MarkCancelled(job, partPath);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Transient error downloading {Url} (attempt {Attempt}); will retry",
                    job.Url, attempt);

                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                try
                {
                    await Task.Delay(delay, job.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    MarkCancelled(job, partPath);
                    return;
                }
            }
        }

        MarkCancelled(job, partPath);
    }

    private async Task<bool> TrySkipIfMatchingAsync(Uri uri, DownloadJob job)
    {
        long localSize;
        try
        {
            localSize = new FileInfo(job.DestinationPath).Length;
        }
        catch
        {
            return false;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("download");
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            AddAuth(request, uri);

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, job.Cts.Token);

            if (!response.IsSuccessStatusCode) return false;
            if (response.Content.Headers.ContentLength is not long remoteSize) return false;
            if (remoteSize != localSize) return false;

            job.BytesDownloaded = localSize;
            job.TotalBytes = localSize;
            job.Status = DownloadStatus.Completed;
            _logger.LogInformation(
                "Skipping download of {Url}; existing file matches remote size ({Size} bytes)",
                job.Url, localSize);
            return true;
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "HEAD precheck for {Url} failed; will proceed with full download", job.Url);
            return false;
        }
    }

    private void AddAuth(HttpRequestMessage request, Uri uri)
    {
        var creds = GetBasicAuth(uri);
        if (creds == null) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private void MarkCancelled(DownloadJob job, string partPath)
    {
        job.Status = DownloadStatus.Cancelled;
        try
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete partial file {PartPath} after cancel", partPath);
        }
    }

    private BasicAuthCredentials? GetBasicAuth(Uri uri)
    {
        foreach (var (domain, creds) in _options.BasicAuth)
        {
            if (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return creds;
        }
        return null;
    }

    private string ResolveDestinationPath(string fileName)
    {
        var match = TvShowPattern().Match(fileName);
        if (match.Success)
        {
            var showName = match.Groups["name"].Value.Replace('.', ' ').Trim();
            var season = int.Parse(match.Groups["season"].Value);
            return Path.Combine(_options.TvShowsPath, showName, $"Season {season}", fileName);
        }
        return Path.Combine(_options.MoviesPath, fileName);
    }
}
