using System.Text.Json;
using CineVault.Models;

namespace CineVault.Services;

public interface IResourceGateway
{
    Task<TorrentCandidate[]> Search(MediaItem movie, CancellationToken ct);
    Task Add(string magnet, string directory, CancellationToken ct);
    Task<TorrentSnapshot[]> Status(string[] hashes, CancellationToken ct);
    Task Control(string hash, string action, CancellationToken ct);
    Task<SourceConnectionResult> Test(string provider, CancellationToken ct);
}
public sealed class ResourceGateway(SourceSettingsStore settings, HttpMessageHandler? fixtureHandler = null) : IResourceGateway
{
    private HttpClient Client(string url)
    {
        var root = SourceSettingsStore.ServiceUrl(url);
        if (root.Length == 0) throw new ArgumentException("请管理员先在“来源与密钥”配置资源索引与 qBittorrent。");
        return new HttpClient(fixtureHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, fixtureHandler is null) { BaseAddress = new Uri(root), Timeout = TimeSpan.FromSeconds(15) };
    }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : "";
    private static long Number(JsonElement item, string key) => long.TryParse(Text(item, key), out var n) ? n : 0;
    private static async Task<JsonDocument> Get(HttpClient client, string path, CancellationToken ct)
    { using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct); return await SourceApiClient.ReadJson(response, ct); }
    public async Task<TorrentCandidate[]> Search(MediaItem movie, CancellationToken ct)
    {
        var c = settings.Current;
        if (c.ResourceIndexKey.Length == 0) throw new ArgumentException("尚未配置 Prowlarr API Key。请先添加你可使用的资源索引。");
        using var client = Client(c.ResourceIndexUrl); client.DefaultRequestHeaders.Add("X-Api-Key", c.ResourceIndexKey);
        var queries = new[] { movie.OriginalTitle, movie.Title }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(2);
        var candidates = new List<TorrentCandidate>();
        foreach (var query in queries)
        {
            using var doc = await Get(client, "api/v1/search?query=" + Uri.EscapeDataString(query + (movie.Year > 0 ? " " + movie.Year : "")) + "&type=search", ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("资源索引未返回有效结果。");
            foreach (var row in doc.RootElement.EnumerateArray().Take(200))
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (Text(row, "protocol").Equals("usenet", StringComparison.OrdinalIgnoreCase)) continue;
                var title = Text(row, "title"); var magnet = Text(row, "magnetUrl"); var hash = Text(row, "infoHash");
                if (magnet.Length == 0 && hash.Length > 0) magnet = "magnet:?xt=urn:btih:" + hash + "&dn=" + Uri.EscapeDataString(title);
                try { candidates.Add(TorrentReleaseRules.Candidate(movie, title, magnet, (int)Math.Clamp(Number(row, "seeders"), 0, int.MaxValue), Number(row, "size"), Text(row, "indexer"))); }
                catch (ArgumentException) { }
            }
            if (candidates.Any(x => x.MatchesMovie)) break;
        }
        return candidates.DistinctBy(x => x.InfoHash).OrderByDescending(x => x.Rank).Take(30).ToArray();
    }
    private async Task<HttpClient> Qbit(CancellationToken ct)
    {
        var c = settings.Current;
        if (c.QbitUserName.Length == 0 || c.QbitPassword.Length == 0) throw new ArgumentException("请先配置 qBittorrent 的 Web UI 用户名和密码。");
        var client = Client(c.QbitUrl); client.DefaultRequestHeaders.Referrer = client.BaseAddress;
        try
        {
            using var response = await client.PostAsync("api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = c.QbitUserName, ["password"] = c.QbitPassword }), ct);
            response.EnsureSuccessStatusCode();
            if ((await response.Content.ReadAsStringAsync(ct)).Trim() != "Ok.") throw new ArgumentException("qBittorrent 登录失败，请核对 Web UI 账户。");
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task Post(HttpClient client, string action, Dictionary<string,string> values, CancellationToken ct)
    { using var response = await client.PostAsync("api/v2/torrents/" + action, new FormUrlEncodedContent(values), ct); response.EnsureSuccessStatusCode(); }
    public async Task Add(string magnet, string directory, CancellationToken ct)
    {
        var parsed = TorrentReleaseRules.ParseMagnet(magnet);
        using var client = await Qbit(ct);
        using var existing = await Get(client, "api/v2/torrents/info?hashes=" + parsed.Hash, ct);
        if (existing.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("下载器未返回有效任务列表。");
        foreach (var old in existing.RootElement.EnumerateArray())
        {
            if (!Text(old, "hash").Equals(parsed.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            var oldPath = Text(old, "save_path").Replace('\\', '/').TrimEnd('/');
            if (!oldPath.Equals(directory.Replace('\\', '/').TrimEnd('/'), StringComparison.Ordinal))
                throw new ArgumentException("下载器已有该资源，保存位置不同；请先在 qBittorrent 核对目录。");
            return;
        }
        using var response = await client.PostAsync("api/v2/torrents/add", new FormUrlEncodedContent(new Dictionary<string,string> { ["urls"] = parsed.Magnet, ["savepath"] = directory, ["autoTMM"] = "false" }), ct);
        response.EnsureSuccessStatusCode();
        if ((await response.Content.ReadAsStringAsync(ct)).Trim() != "Ok.") throw new InvalidDataException("qBittorrent 未接受该磁力链接。");
    }
    public async Task<TorrentSnapshot[]> Status(string[] hashes, CancellationToken ct)
    {
        if (hashes.Length == 0) return [];
        using var client = await Qbit(ct); var output = new List<TorrentSnapshot>();
        foreach (var batch in hashes.Chunk(40))
        {
            using var doc = await Get(client, "api/v2/torrents/info?hashes=" + Uri.EscapeDataString(string.Join("|", batch)), ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("下载器未返回有效任务列表。");
            foreach (var item in doc.RootElement.EnumerateArray()) output.Add(new(Text(item, "hash"), Text(item, "name"), Text(item, "state"), item.TryGetProperty("completed", out _) ? Number(item, "completed") : Number(item, "downloaded"), Number(item, "size"), Number(item, "dlspeed"), Text(item, "save_path")));
        }
        return output.ToArray();
    }
    public async Task Control(string hash, string action, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{40}$")) throw new ArgumentException("任务哈希不合法。");
        using var client = await Qbit(ct);
        if (action == "cancel") { await Post(client, "delete", new() { ["hashes"] = hash, ["deleteFiles"] = "false" }, ct); return; }
        var endpoint = action == "pause" ? "stop" : "start";
        try { await Post(client, endpoint, new() { ["hashes"] = hash }, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { await Post(client, action == "pause" ? "pause" : "resume", new() { ["hashes"] = hash }, ct); }
    }
    public async Task<SourceConnectionResult> Test(string provider, CancellationToken ct)
    {
        try
        {
            if (provider == "qbittorrent") { using var client = await Qbit(ct); using var response = await client.GetAsync("api/v2/app/version", ct); response.EnsureSuccessStatusCode(); }
            else if (provider == "indexer")
            {
                var c = settings.Current;
                if (c.ResourceIndexKey.Length == 0) return new(false, "请先保存 Prowlarr API Key。");
                using var client = Client(c.ResourceIndexUrl); client.DefaultRequestHeaders.Add("X-Api-Key", c.ResourceIndexKey);
                using var doc = await Get(client, "api/v1/indexer", ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || !doc.RootElement.EnumerateArray().Any(x => Text(x, "enable").Equals("true", StringComparison.OrdinalIgnoreCase)))
                    return new(false, "Prowlarr 已连接，但没有启用的资源索引，请先在 Prowlarr 中添加可用索引。");
            }
            else return new(false, "未知服务。");
            return new(true, "服务连接测试成功。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException or ArgumentException)
        { return new(false, "服务连接失败，请核对地址、凭据、索引状态和 Docker 网络。凭据已保留。"); }
    }
}
