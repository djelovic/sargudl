# MiniDl

A mini ASP.NET Core download manager. You paste a URL, the server downloads the
file in the background, and a progress page streams live status (bytes, percent,
state) over Server-Sent Events. Downloads can be paused, resumed, and cancelled,
and partially downloaded files resume from where they left off via HTTP range
requests.

Files are routed automatically: anything containing `'.sXXeXX.'` in the file name lands under the TV-shows root (organised by show and season), everything
else under the movies root.

## Configuration

Settings live in `appsettings.json` under the `Downloads` section:

```json
{
  "Downloads": {
    "MoviesPath": "./downloads/Movies",
    "TvShowsPath": "./downloads/TvShows",
    "BasicAuth": {
      "example.com": {
        "Username": "example",
        "Password": "password"
      }
    }
  }
}
```

- **`MoviesPath`** / **`TvShowsPath`** — destination roots for downloaded files.
  In the Docker image these default to `/app/downloads/Movies` and
  `/app/downloads/TvShows`; mount a volume there to keep files on the host.
- **`BasicAuth`** — optional HTTP Basic credentials, keyed by host. The key is
  matched as a suffix, so an entry for `example.com` is used for requests to
  `example.com` and any subdomain such as `files.example.com`. Omit the section
  entirely if no sites need authentication.

## Running with Docker

The image is published to Docker Hub as
[`djelovic/minidl`](https://hub.docker.com/r/djelovic/minidl). It listens on
port **8080** inside the container.

### Directly

Map a host `appsettings.json` over the one in the image, and mount a host
directory for the downloads:

```bash
docker run -d \
  --name minidl \
  -p 8080:8080 \
  -v "$PWD/appsettings.json:/app/appsettings.json:ro" \
  -v "$PWD/downloads:/app/downloads" \
  djelovic/minidl:1.0.0.0
```

Then open <http://localhost:8080>.

> The bind-mounted `appsettings.json` lets you configure the app without
> rebuilding the image. Keep its `MoviesPath`/`TvShowsPath` pointing under
> `/app/downloads` (the mounted volume) so files persist on the host.

### With Docker Compose

Create a `docker-compose.yml`:

```yaml
services:
  minidl:
    image: djelovic/minidl:1.0.0.0
    container_name: minidl
    ports:
      - "8080:8080"
    volumes:
      - ./appsettings.json:/app/appsettings.json:ro
      - ./downloads:/app/downloads
    restart: unless-stopped
```

Then:

```bash
docker compose up -d
```

The app is available at <http://localhost:8080>. Stop it with
`docker compose down`.

## Building

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later.

### Run from source

```bash
dotnet run
```

This serves the app at <http://localhost:5000> (configured in
`Properties/launchSettings.json`).

### Publish a self-contained build

```bash
dotnet publish MiniDl.csproj -c Release -o ./publish
```

### Build the Docker image

```bash
docker build -t minidl:local .
```
