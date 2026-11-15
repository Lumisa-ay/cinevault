namespace CineVault.Models;

public sealed record AppSettings(string Directory, bool AutoRetry = true, int MaxRetries = 3,
    int Concurrency = 2, double MinRating = 8, bool Organize = true);
public sealed record ResourceLink(string Id, string Name, string Url, string Quality, string Protocol, string? Size = null);
public sealed record MediaItem(string Id, string Title, string OriginalTitle, int Year, string Type,
    string[] Genres, double Rating, double? Imdb, string? Duration, string? Poster,
    string Description, string SourceUrl, string[] Qualities, ResourceLink[] Resources, bool IsDemo = false,
    string? Source = null, string? RatingSource = null, int? VoteCount = null, string? ReleaseDate = null);
public sealed record Catalogue(MediaItem[] Items, DateTimeOffset? UpdatedAt, string[]? Warnings = null);
public sealed record CreateDownload(string? MediaId, string? ResourceId, string? Directory,
    string? Title, string? Url);
public sealed record DirectoryRequest(string Directory);

public sealed class DownloadJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? MediaId { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Quality { get; set; } = "HTTP";
    public string FileName { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public long Speed { get; set; }
    public int Attempts { get; set; }
    public int MaxRetries { get; set; }
    public bool AutoRetry { get; set; }
    public string? Error { get; set; }
    public string? Validator { get; set; }
    public string Engine { get; set; } = "http";
    public string? InfoHash { get; set; }
    public bool RemoteSubmitted { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDemo => false;
    public DownloadJob Snapshot() => (DownloadJob)MemberwiseClone();
}
