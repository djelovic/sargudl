namespace MiniDl.Services;

public class DownloadOptions {
	public required DownloadPaths Paths { get; init; }
	// Extensions routed to Paths.Movies. Omit from config to use DefaultVideoExtensions.
	public List<string>? VideoExtensions { get; init; }
	public Dictionary<string, BasicAuthCredentials> BasicAuth { get; } = new(StringComparer.OrdinalIgnoreCase);

	public static readonly string[] DefaultVideoExtensions = [
		".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".flv", ".webm",
		".mpg", ".mpeg", ".m2ts", ".ts", ".vob", ".ogv", ".divx", ".3gp", ".rm", ".rmvb", ".asf",
	];

	// Case-insensitive set of the configured (or default) extensions, each normalised
	// to a leading dot so config entries may be written as "mkv" or ".mkv".
	public HashSet<string> ResolveVideoExtensions() {
		var extensions = VideoExtensions is { Count: > 0 } configured ? configured : (IEnumerable<string>)DefaultVideoExtensions;
		return extensions
			.Where(e => !string.IsNullOrWhiteSpace(e))
			.Select(e => {
				var trimmed = e.Trim();
				return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
			})
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}
}

public class DownloadPaths {
	public required string Movies { get; init; }
	public required string Shows { get; init; }
	public required string Other { get; init; }
}

public class BasicAuthCredentials {
	public required string Username { get; init; }
	public required string Password { get; init; }
}
