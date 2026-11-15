using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public sealed class PageParser
{
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static string Match(string text, string pattern, string group = "v") => Matches(text, pattern).FirstOrDefault()?.Groups[group].Value ?? "";
    private static string Clean(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    private readonly string ratingPattern;
    private readonly string genrePattern;
    public PageParser(IConfiguration config)
    {
        ratingPattern = config["Crawler:RatingPattern"] ?? @"(?:豆瓣|douban)[\s\S]{0,120}?(?<rating>[0-9]\.[0-9])";
        genrePattern = config["Crawler:GenrePattern"] ?? @"(?:类型|genre)[^<]{0,20}[：:]\s*(?<genre>[^<\r\n]{1,60})";
    }
    private static string Text(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : "";
    }
    private static IEnumerable<JsonElement> Nodes(JsonElement obj)
    {
        if (obj.ValueKind == JsonValueKind.Array) { foreach (var child in obj.EnumerateArray()) foreach (var n in Nodes(child)) yield return n; }
        else if (obj.ValueKind == JsonValueKind.Object)
        {
            var type = obj.TryGetProperty("@type", out var t) ? t.ToString() : "";
            if (type.Contains("Movie", StringComparison.OrdinalIgnoreCase) || type.Contains("TVSeries", StringComparison.OrdinalIgnoreCase)) yield return obj;
            foreach (var p in obj.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) foreach (var n in Nodes(p.Value)) yield return n;
        }
    }
    public static string Quality(string text) => Regex.IsMatch(text, "2160|4k|uhd", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "4K" :
        Regex.IsMatch(text, "1080", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "1080P" :
        Regex.IsMatch(text, "720", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "720P" : "未知";
    public static IEnumerable<(string Url, string Title)> Anchors(string html, Uri page)
    {
        foreach (Match m in Matches(html, "<a\\b(?<attrs>[^>]*?href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*)>(?<text>[\\s\\S]*?)</a>"))
        {
            var raw = WebUtility.HtmlDecode(m.Groups["url"].Value).Trim();
            if (!Uri.TryCreate(page, raw, out var uri)) continue;
            var title = Clean(Match(m.Groups["attrs"].Value, "title\\s*=\\s*[\"'](?<v>[^\"']+)[\"']"));
            if (title.Length == 0) title = Clean(m.Groups["text"].Value);
            yield return (uri.AbsoluteUri, title);
        }
    }
    public ResourceLink[] Resources(string html, Uri page)
    {
        return Anchors(html, page).Where(a => a.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) || a.Url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(a.Url, @"\.(mkv|mp4|avi|mov|webm|m4v|torrent|zip|7z)(?:\?|$)|/download(?:/|\?)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            .DistinctBy(a => a.Url).Take(50).Select(a => new ResourceLink(Hash(a.Url), string.IsNullOrWhiteSpace(a.Title) ? Uri.UnescapeDataString(new Uri(a.Url).AbsolutePath.Split('/').LastOrDefault() ?? "资源链接") : a.Title,
                a.Url, Quality(a.Title + a.Url), a.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? "MAGNET" : a.Url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase) ? "ED2K" : "HTTP")).ToArray();
    }
    public MediaItem[] Parse(string html, Uri page, bool detail)
    {
        if (html.Length > 4 * 1024 * 1024) throw new InvalidDataException("页面超过 4 MB 限制。");
        var result = new List<MediaItem>();
        foreach (Match script in Matches(html, "<script\\b[^>]*type\\s*=\\s*[\"']application/ld\\+json[\"'][^>]*>(?<v>[\\s\\S]*?)</script>"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.Groups["v"].Value, new JsonDocumentOptions { MaxDepth = 40 });
                foreach (var node in Nodes(json.RootElement))
                {
                    var title = Clean(Text(node, "name")); if (title.Length == 0) continue;
                    var url = Text(node, "url");
                    if (!Uri.TryCreate(page, url.Length > 0 ? url : page.AbsoluteUri, out var source) || source.Host != page.Host) source = page;
                    var rating = 0d;
                    if (node.TryGetProperty("aggregateRating", out var aggregate) && aggregate.ValueKind == JsonValueKind.Object)
                    {
                        double.TryParse(Text(aggregate, "ratingValue"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rating);
                        if (double.TryParse(Text(aggregate, "bestRating"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0) rating = rating / max * 10;
                    }
                    var genres = node.TryGetProperty("genre", out var genre) ? genre.ValueKind == JsonValueKind.Array ? genre.EnumerateArray().Select(x => x.ToString()).ToArray() : genre.ToString().Split(['/', ',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                    var year = int.TryParse(Text(node, "datePublished").Split('-')[0], out var y) ? y : 0;
                    string? image = null;
                    if (node.TryGetProperty("image", out var im)) image = im.ValueKind == JsonValueKind.String ? im.GetString() : im.ValueKind == JsonValueKind.Object ? Text(im, "url") : im.ValueKind == JsonValueKind.Array ? im.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault() : null;
                    if (image is not null && (!Uri.TryCreate(page, image, out var imgUri) || imgUri.Scheme is not ("http" or "https"))) image = null;
                    else if (image is not null) image = new Uri(page, image).AbsoluteUri;
                    var resources = detail ? Resources(html, page) : [];
                    if (detail && Uri.TryCreate(page, Text(node, "contentUrl"), out var content) && content.AbsoluteUri != page.AbsoluteUri && content.Scheme is "http" or "https")
                        resources = resources.Append(new ResourceLink(Hash(content.AbsoluteUri), title, content.AbsoluteUri, Quality(content.AbsoluteUri), "HTTP")).DistinctBy(x => x.Url).ToArray();
                    result.Add(new(Hash(source.AbsoluteUri + title), title, Text(node, "alternateName"), year,
                        Text(node, "@type").Contains("TVSeries", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie", genres, Math.Clamp(double.IsFinite(rating) ? rating : 0, 0, 10), null,
                        Text(node, "duration"), image, Clean(Text(node, "description")), source.AbsoluteUri,
                        resources.Select(r => r.Quality).Where(q => q != "未知").Distinct().ToArray(), resources));
                }
            }
            catch (JsonException) { /* A malformed block must not prevent parsing other structured data. */ }
        }
        if (detail && result.Count == 0)
        {
            var title = Clean(Match(html, @"<h1\b[^>]*>(?<v>[\s\S]*?)</h1>"));
            if (title.Length == 0) title = Clean(Match(html, "<meta[^>]+property=[\"']og:title[\"'][^>]+content=[\"'](?<v>[^\"']+)[\"']"));
            if (title.Length == 0) return [];
            var r = Match(html, ratingPattern, "rating");
            double.TryParse(r, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rating);
            var genre = Clean(Match(html, genrePattern, "genre")).Split(['/', ',', '，', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var yearText = Match(Clean(html), @"(?<v>\b(?:19|20)[0-9]{2}\b)");
            var links = Resources(html, page);
            result.Add(new(Hash(page.AbsoluteUri + title), title, "", int.TryParse(yearText, out var year) ? year : 0,
                Regex.IsMatch(html, @"电视剧|TVSeries|美剧|日剧|韩剧", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ? "tv" : "movie",
                genre, Math.Clamp(rating, 0, 10), null, null, null, "", page.AbsoluteUri, links.Select(x => x.Quality).Where(x => x != "未知").Distinct().ToArray(), links));
        }
        return result.DistinctBy(x => x.Id).ToArray();
    }
}
