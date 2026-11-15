namespace CineVault.Models;

public sealed record TorrentCandidate(string Id, string Title, string Magnet, string InfoHash, bool Is4K,
    bool HasChineseSubtitles, bool MatchesMovie, int Seeders, long Size, string Indexer, int Rank);
public sealed record TorrentSearchResult(MediaItem Item, TorrentCandidate[] Candidates, string[] Warnings);
public sealed record TorrentSnapshot(string Hash, string Name, string State, long Downloaded, long Size, long Speed, string SavePath);
public sealed record LatestDownloadRequest(int Page = 1, double MinRating = 0, int Limit = 3);
public sealed record LatestDownloadOutcome(string MediaId, string Title, string Status, string Message, string? TaskId = null);
