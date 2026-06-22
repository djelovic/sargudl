using System.Text.Json;
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

app.MapGet("/api/status", async (string url, DownloadManager manager, CancellationToken ct) => Results.Json(await manager.GetAsync(url, ct)));

// Server-Sent Events stream of status updates for a single download. The manager
// decides what to emit (only on change) and when to stop (terminal state); this
// handler just frames each snapshot. The token is cancelled when the client
// disconnects (CancellationToken == RequestAborted).
var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
app.MapGet("/api/events", async (string url, DownloadManager manager, HttpContext ctx, CancellationToken ct) => {
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no"; // ask proxies not to buffer

    try {
        await foreach (var status in manager.WatchAsync(url, ct)) {
            var json = JsonSerializer.Serialize(status, sseJson);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) {
    }
});

app.MapPost("/api/cancel", async (string url, DownloadManager manager, CancellationToken ct) =>
    Results.Json(await manager.CancelAsync(url, ct)));

app.MapPost("/api/pause", async (string url, DownloadManager manager, CancellationToken ct) =>
    Results.Json(await manager.PauseAsync(url, ct)));

app.MapPost("/api/start", async (string url, DownloadManager manager, CancellationToken ct) =>
    Results.Json(await manager.StartAsync(url, ct)));

app.MapPost("/api/resume", async (string url, DownloadManager manager, CancellationToken ct) =>
    Results.Json(await manager.ResumeAsync(url, ct)));

app.Run();
