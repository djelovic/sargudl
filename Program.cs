using Sargudl.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient("download")
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.Configure<DownloadOptions>(builder.Configuration.GetSection("Downloads"));
builder.Services.AddSingleton<DownloadWorker>();
builder.Services.AddSingleton<DownloadManager>();

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.MapGet("/api/status", (string url, DownloadManager manager) =>
{
    var job = manager.Get(url);
    if (job == null) return Results.NotFound();
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

app.MapPost("/api/cancel", (string url, DownloadManager manager) =>
{
    manager.Cancel(url);
    return Results.Ok();
});

app.Run();
