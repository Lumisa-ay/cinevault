namespace CineVault.Models;

public sealed record MovieSearchRequest(string Query = "", string Source = "all", string Type = "",
    double MinRating = 0, int Page = 1, bool IncludeUnrated = false, string Mode = "search");
public sealed record MovieSource(string Id, string Name, string Status, string Message, string Url, int Count = 0);
public sealed record MovieSearchResponse(MediaItem[] Items, MovieSource[] Sources, int Page, string Query);
