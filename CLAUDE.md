# MiniDl

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
the manager's private `RunAsync`.

- `Pages/Index.cshtml(.cs)` — URL form. `OnPost` validates and redirects to `/Download?url=...`.
- `Pages/Download.cshtml(.cs)` — progress page. JS polls `/api/status?url=...`
  every second, posts to `/api/cancel?url=...` for the Cancel button.
- `Services/DownloadManager.cs` — owns the job dictionary, job construction
  (URI parsing, filename derivation, destination resolution, dir creation),
  and the download loop itself (HEAD skip check, retry/resume loop, cancel
  cleanup). `StartOrGet(url)` returns the existing job if any (regardless
  of status), otherwise creates and spawns. Terminal jobs are never
  silently replaced — to re-download a URL whose job is `Completed`/
  `Failed`/`Cancelled`, the file/job has to be removed first.
- `Services/DownloadJob.cs` — mutable job state (status, bytes, error) plus
  readonly `Url`/`FileName`/`DestinationPath` and an encapsulated
  `CancellationTokenSource` exposed as `CT`/`Cancel()`/`Dispose()`.
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
- **Skip-if-already-downloaded**: before the retry loop, if the destination
  file exists and no `.part` is present, a HEAD request is issued. If the
  server returns success with a `Content-Length` matching the local file,
  the job is marked `Completed` without re-downloading. Any HEAD failure
  (network error, 4xx/5xx, no `Content-Length`, server doesn't support HEAD)
  silently falls through to the normal GET, which will either succeed or
  fail with its own error handling.
- **Resume**: writes to `<finalPath>.part`. On retry it sends
  `Range: bytes=<existing-length>-`. If the server returns 200 instead of 206,
  the partial is overwritten via `FileMode.Create` and we restart from zero.
- **Total bytes**: prefer `Content-Range` total, else `Content-Length`
  (added to the resume offset when the server returned 206).
- **Retry policy**: 4xx → fail immediately with the status code in `job.Error`.
  Anything else (5xx, network errors, IO errors) → log a warning, sleep
  `min(30s, 2^min(attempt,5)s)`, retry. Loop exits only on success, 4xx, or
  cancellation.
- **Cancellation vs pause**: both cancel the CTS so the download loop
  unwinds at the next await. The `IsPauseRequested` flag on the job
  distinguishes intent: `HandleStop` reads it and either marks `Paused`
  (keeping the `.part` file) or marks `Cancelled` (deleting the `.part`).
  `Cancel()` always clears the flag first so the cancellation takes
  precedence if both are requested.
- **Resume**: `DownloadManager.Resume(url)` calls `job.ResetForResume()`
  which swaps in a fresh `CancellationTokenSource`, then re-enters
  `RunAsync` via `Spawn`. The retry loop notices the existing `.part`
  file and sends a `Range` request to continue from there.
- All public manager methods take a single manager-wide `lock(_lock)`
  around the dict access and any check-then-act on job state. The dict
  is `Dictionary<string, (DownloadJob Job, Task? Task)>`: the `Task?`
  slot holds the active worker task while one is running and is nulled
  out in the worker's `finally` block when it exits. The `Job` entry
  persists so terminal states stay observable through `/api/status`.
- All public methods return a `Task` that completes when the requested
  state change is observable. `StartOrGet` and `Resume` complete
  synchronously (the dict is mutated under the lock). `Cancel` and
  `Pause` return the active worker's task; awaiting it waits for the
  worker to observe the cancel/pause and exit with the new status.
  If several state-change requests race for the same URL, they all
  return the same worker task and complete together when the worker
  exits — possibly in a status that doesn't match the last request
  (e.g. Pause followed quickly by Cancel results in `Cancelled`, and
  both callers' awaits resolve at that point).
- **Completion**: any existing destination file is deleted, then the `.part`
  is renamed to the final path.

## API surface

- `POST /api/status?url=<url>` → JSON `{ url, fileName, destinationPath,
  bytesDownloaded, totalBytes, status, error }`. `status` is one of
  `Queued | Downloading | Completed | Cancelled | Failed | Paused`.
  This endpoint also starts the download if it isn't already known —
  the first poll from `/Download?url=X` is what kicks off X. POST not
  GET because the first call has a side effect.
- `POST /api/cancel?url=<url>` → cancels the job (deletes `.part`). Idempotent.
- `POST /api/pause?url=<url>` → pauses while `Downloading` (keeps `.part`).
- `POST /api/resume?url=<url>` → resumes from `Paused` via `Range` request.

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
- `Url` is the job key. Two requests for the same URL share one job. The
  `.part` file persists across transient retries and across pause (the
  `Range` request on resume picks up from there); it is deleted on cancel
  — Cancel is a hard stop, Pause is a soft stop.
