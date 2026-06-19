using Sargudl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient("download")
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.Configure<DownloadOptions>(builder.Configuration.GetSection("Downloads"));
builder.Services.AddSingleton<DownloadManager>();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.MapGet("/api/status", async (string url, DownloadManager manager, CancellationToken ct) =>
{
    var job = await manager.GetAsync(url, ct);
    return Results.Json(new
    {
        url = job.Url,
        fileName = job.FileName,
        destinationPath = job.DestinationPath,
        bytesDownloaded = job.BytesDownloaded,
        totalBytes = job.TotalBytes,
        status = job.Status.ToString(),
        error = job.Error
    });
});

app.MapPost("/api/cancel", async (string url, DownloadManager manager, CancellationToken ct) =>
{
    await manager.CancelAsync(url, ct);
    return Results.Ok();
});

app.MapPost("/api/pause", async (string url, DownloadManager manager, CancellationToken ct) =>
{
    await manager.PauseAsync(url, ct);
    return Results.Ok();
});

app.MapPost("/api/start", async (string url, DownloadManager manager, CancellationToken ct) =>
{
    var job = await manager.StartAsync(url, ct);
    return Results.Json(new
    {
        url = job.Url,
        fileName = job.FileName,
        destinationPath = job.DestinationPath,
        bytesDownloaded = job.BytesDownloaded,
        totalBytes = job.TotalBytes,
        status = job.Status.ToString(),
        error = job.Error
    });
});

app.MapPost("/api/resume", async (string url, DownloadManager manager, CancellationToken ct) =>
{
    var job = await manager.ResumeAsync(url, ct);
    return Results.Json(new
    {
        url = job.Url,
        fileName = job.FileName,
        destinationPath = job.DestinationPath,
        bytesDownloaded = job.BytesDownloaded,
        totalBytes = job.TotalBytes,
        status = job.Status.ToString(),
        error = job.Error
    });});


app.Run();
