namespace Sargudl.Services;

public class DownloadOptions
{
    public string MoviesPath { get; set; } = "";
    public string TvShowsPath { get; set; } = "";
    public Dictionary<string, BasicAuthCredentials> BasicAuth { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class BasicAuthCredentials
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
