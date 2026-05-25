# Sargudl

A mini ASP.NET Core download manager. The user enters a URL, the server downloads
the file in the background, and a progress page polls the server for status.

## Run

```
dotnet run
```

Then open `http://localhost:5000` (port configured in `Properties/launchSettings.json`).

## Configuration (`appsettings.json`)

```json
"Downloads": {
  "MoviesPath": "./downloads/Movies",
  "TvShowsPath": "./downloads/TvShows",
  "BasicAuth": {
    "example.com": { "Username": "...", "Password": "..." }
  }
}
```

- `MoviesPath` / `TvShowsPath`: destination roots.
- `BasicAuth`: keyed by host. A request to `foo.example.com` matches the
  `example.com` entry (suffix match), as does an exact match to `example.com`.

## Architecture

Razor Pages + a singleton `DownloadManager` holding jobs keyed by URL in a
`ConcurrentDictionary`. Each job runs on a fire-and-forget `Task.Run` calling
`DownloadWorker.RunAsync`.

- `Pages/Index.cshtml(.cs)` — URL form. `OnPost` validates and redirects to `/Download?url=...`.
- `Pages/Download.cshtml(.cs)` — progress page. JS polls `/api/status?url=...`
  every second, posts to `/api/cancel?url=...` for the Cancel button.
- `Services/DownloadManager.cs` — `StartOrGet(url)` returns the existing job if
  it's `Queued` or `Downloading`, otherwise replaces it and starts a new one.
- `Services/DownloadWorker.cs` — the actual download loop.
- `Services/DownloadJob.cs` — mutable job state (status, bytes, error, CTS).
- `Services/DownloadOptions.cs` — bound to the `Downloads` config section.
- `Program.cs` — DI registration and the two minimal-API endpoints
  (`/api/status`, `/api/cancel`).

Jobs live in memory only; restarting the server forgets all state.

## Download semantics (`DownloadWorker`)

- **Filename** comes from the URL path (`Uri.AbsolutePath` → `Path.GetFileName`).
- **Routing**: filename matched against `^(?<name>.+?)\.s(?<season>\d{2})e\d{2}`
  (case-insensitive).
  - Match → `<TvShowsPath>/<name with '.'→' '>/Season <n>/<filename>`
  - No match → `<MoviesPath>/<filename>`
- **Resume**: writes to `<finalPath>.part`. On retry it sends
  `Range: bytes=<existing-length>-`. If the server returns 200 instead of 206,
  the partial is overwritten via `FileMode.Create` and we restart from zero.
- **Total bytes**: prefer `Content-Range` total, else `Content-Length`
  (added to the resume offset when the server returned 206).
- **Retry policy**: 4xx → fail immediately with the status code in `job.Error`.
  Anything else (5xx, network errors, IO errors) → log a warning, sleep
  `min(30s, 2^min(attempt,5)s)`, retry. Loop exits only on success, 4xx, or
  cancellation.
- **Cancellation**: `job.Cts.Cancel()` propagates through `SendAsync`,
  `ReadAsync`, and `Task.Delay`. The `.part` file is left in place so a fresh
  `StartOrGet` resumes from where the user cancelled.
- **Completion**: any existing destination file is deleted, then the `.part`
  is renamed to the final path.

## API surface

- `GET /api/status?url=<url>` → JSON `{ url, fileName, destinationPath,
  bytesDownloaded, totalBytes, status, error }`. `status` is one of
  `Queued | Downloading | Completed | Cancelled | Failed`.
- `POST /api/cancel?url=<url>` → cancels the job for that URL. Idempotent.

Both endpoints are query-string–bound minimal APIs; no antiforgery applies
to them. The Index form POST goes to the Razor Page handler, which uses
the framework's default antiforgery.

## Things worth knowing before editing

- `HttpClient.Timeout` is set to `Timeout.InfiniteTimeSpan` for the
  `"download"` client because the cancellation token is the only thing that
  should stop a long download.
- `DownloadWorker` is a singleton — keep it stateless (per-job state lives
  on `DownloadJob`).
- `DownloadJob.BytesDownloaded` is read by the status endpoint while the
  worker writes to it. On 64-bit runtimes `long` reads/writes are atomic,
  which is good enough for a progress counter; don't add invariants that
  require two fields to be consistent without a lock.
- `Url` is the job key. Two requests for the same URL share one job, which
  is also how "resume after cancel" works — hitting Start again from the
  home page re-enters `StartOrGet` and continues from the `.part` file.
