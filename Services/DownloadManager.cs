using System.Collections.Concurrent;

namespace Sargudl.Services;

public class DownloadManager
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly DownloadWorker _worker;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(DownloadWorker worker, ILogger<DownloadManager> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public DownloadJob? Get(string url) =>
        _jobs.TryGetValue(url, out var j) ? j : null;

    public DownloadJob StartOrGet(string url)
    {
        var existing = _jobs.GetValueOrDefault(url);
        if (existing is { Status: DownloadStatus.Downloading or DownloadStatus.Queued })
            return existing;

        if (existing != null)
        {
            _jobs.TryRemove(url, out _);
            existing.Cts.Dispose();
        }

        var job = new DownloadJob { Url = url };
        _jobs[url] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                await _worker.RunAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in download worker for {Url}", url);
                job.Status = DownloadStatus.Failed;
                job.Error = ex.Message;
            }
        });

        return job;
    }

    public void Cancel(string url)
    {
        if (_jobs.TryGetValue(url, out var job))
        {
            try { job.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
