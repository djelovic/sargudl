namespace Sargudl.Services;

public class DownloadOptions {
	public required string MoviesPath { get; init; }
	public required string TvShowsPath { get; init; }
	public Dictionary<string, BasicAuthCredentials> BasicAuth { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public class BasicAuthCredentials {
	public required string Username { get; init; }
	public required string Password { get; init; }
}
