using System.Net;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public static class TorrentReleaseRules
{
    public static (string Hash, string Magnet) ParseMagnet(string value)
    {
        if (value.Length > 8192 || !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("需要有效的磁力链接。");
        var fields = value[8..].Split('&').Select(x => x.Split('=', 2)).Select(x => (Key: x[0].ToLowerInvariant(), Value: Uri.UnescapeDataString(x.Length > 1 ? x[1] : ""))).ToArray();
        var xt = fields.FirstOrDefault(x => x.Key == "xt" && x.Value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)).Value;
        var hash = xt is null ? "" : xt[9..];
        if (Regex.IsMatch(hash, "^[A-Za-z2-7]{32}$"))
        {
            var bytes = new List<byte>(); int bits = 0, buffer = 0;
            foreach (var c in hash.ToUpperInvariant())
            { buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); } }
            hash = Convert.ToHexString(bytes.ToArray());
        }
        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$")) throw new ArgumentException("暂仅支持包含有效 BTIH 哈希的磁力链接。");
        hash = hash.ToLowerInvariant(); var clean = "magnet:?xt=urn:btih:" + hash;
        var name = fields.FirstOrDefault(x => x.Key == "dn").Value;
        if (!string.IsNullOrEmpty(name)) clean += "&dn=" + Uri.EscapeDataString(name[..Math.Min(name.Length, 240)]);
        foreach (var tracker in fields.Where(x => x.Key == "tr").Take(10))
        {
            if (!Uri.TryCreate(tracker.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "udp") || uri.UserInfo.Length > 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !PublicWebClient.IsPublicAddress(ip)) continue;
            clean += "&tr=" + Uri.EscapeDataString(uri.AbsoluteUri);
        }
        return (hash, clean);
    }
    public static TorrentCandidate Candidate(MediaItem movie, string title, string magnet, int seeders, long size, string indexer)
    {
        title = title[..Math.Min(title.Length, 512)]; indexer = indexer[..Math.Min(indexer.Length, 120)];
        var (hash, clean) = ParseMagnet(magnet);
        var is4K = Regex.IsMatch(title, @"(?<![a-z0-9])(?:2160p|4k)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var chinese = Regex.IsMatch(title, @"中文字幕|简体字幕|繁体字幕|简中|繁中|中字|(?<![a-z])(?:chs|cht)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool MatchTitle(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 240) return false;
            var words = Regex.Matches(name, @"[\p{L}\p{N}]+").Select(x => Regex.Escape(x.Value)).ToArray();
            return words.Length > 0 && Regex.IsMatch(title, @"(?<!\p{L})" + string.Join(@"[^\p{L}\p{N}]*", words) + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        var matches = movie.Year > 0 && Regex.IsMatch(title, @"(?<!\d)" + movie.Year + @"(?!\d)") && (MatchTitle(movie.Title) || MatchTitle(movie.OriginalTitle));
        seeders = Math.Max(0, seeders); size = Math.Max(0, size);
        var rank = (matches ? 10000 : 0) + (is4K && chinese ? 4000 : 0) + (is4K ? 1000 : 0) + (chinese ? 500 : 0) + Math.Min(seeders, 100);
        return new("bt:" + hash, title, clean, hash, is4K, chinese, matches, seeders, size, indexer, rank);
    }
}
