using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public sealed class MovieSearchService(IPublicWebClient web, IConfiguration config, StateStore store, SourceSettingsStore? sourceSettings = null)
{
    private readonly SemaphoreSlim gate = new(2, 2);
    private readonly SourceApiClient api = new(web);
    private SourceCredentials Credentials => sourceSettings?.Current ?? SourceCredentials.Environment(config);
    private string ImdbDirectory => config["Sources:ImdbDirectory"] ?? Path.Combine(config["DataDirectory"] ?? "App_Data", "imdb");
    private string? ImdbFile(string name) => new[] { Path.Combine(ImdbDirectory, name + ".gz"), Path.Combine(ImdbDirectory, name) }.FirstOrDefault(File.Exists);
    private bool ImdbReady => ImdbFile("title.basics.tsv") is not null && ImdbFile("title.ratings.tsv") is not null;
    private static string Encode(string s) => Uri.EscapeDataString(s);

    public MovieSource[] Sources(string query) => Sources(query, Credentials);
    private MovieSource[] Sources(string query, SourceCredentials credentials)
    {
        var q = Encode(query);
        var imdbReady = credentials.ImdbMode == "official" ? credentials.OfficialReady : credentials.ImdbMode == "omdb" ? credentials.OmdbApiKey.Length > 0 : ImdbReady;
        var imdbMessage = credentials.ImdbMode == "official" ? "IMDb 官方 API 按 tt 编号查询资料及评分；需 API Key、AWS 凭据与订阅标识。" : credentials.ImdbMode == "omdb" ? "通过 OMDb 第三方接口按片名或 IMDb 编号查询；不是 IMDb 官方 API。" :
            ImdbReady ? "本地数据集可检索；大文件查询可能需要数十秒。仅查询原文片名，不含中文译名表。" : "把官方 title.basics.tsv.gz 与 title.ratings.tsv.gz 放入 /data/imdb，或由管理员在来源配置中启用在线接口。";
        return [
            new("archive", "Internet Archive", "ready", "站内检索影片；打开资源详情后读取文件列表。评分为本站用户 5 分制折算，不是 IMDb。", "https://archive.org/search?query=" + q),
            new("tmdb", "TMDB", credentials.TmdbCredential.Length > 0 ? "ready" : "needs_key", credentials.TmdbCredential.Length > 0 ? "已配置官方 API，可查询电影、电视剧与 TMDB 评分。" : "请管理员进入「来源与密钥」保存 TMDB API Key 或 Read Access Token。", "https://www.themoviedb.org/search?query=" + q),
            new("douban", "豆瓣电影", "external", "网页检索入口；不会把未读取的豆瓣评分填入结果。", "https://www.douban.com/search?cat=1002&q=" + q),
            new("justwatch", "JustWatch", "external", "网页查询观看平台；官方 API 需要合作方令牌，当前未接入。", "https://www.justwatch.com/" + Region() + "/search?q=" + q),
            new("imdb", "IMDb", imdbReady ? "ready" : credentials.ImdbMode == "dataset" ? "needs_data" : "needs_key", imdbMessage, "https://www.imdb.com/find/?q=" + q + "&s=tt")
        ];
    }

    private string Region()
    {
        var value = config["Sources:JustWatchRegion"] ?? "jp";
        return Regex.IsMatch(value, "^[a-z]{2}$", RegexOptions.CultureInvariant) ? value : "jp";
    }

    public static MovieSearchRequest Validate(MovieSearchRequest request)
    {
        if (request is null || request.Query is null || request.Query.Length > 120 || request.Source is not ("all" or "archive" or "tmdb" or "douban" or "justwatch" or "imdb") ||
            request.Type is not ("" or "movie" or "tv") || request.Mode is not ("search" or "latest") || request.Mode == "latest" && (request.Source != "tmdb" || request.Type != "movie" || request.Query.Length > 0) || !double.IsFinite(request.MinRating) || request.MinRating is < 0 or > 10 || request.Page is < 1 or > 20)
            throw new ArgumentException("请输入不超过 120 字的片名、有效来源、电影/电视剧类型、0–10 评分及 1–20 页码。");
        return request with { Query = request.Query.Trim() };
    }

    public async Task<MovieSearchResponse> Search(MovieSearchRequest request, CancellationToken ct)
    {
        request = Validate(request);
        if (!await gate.WaitAsync(0, ct)) throw new ArgumentException("已有两项来源检索正在执行，请稍后重试。");
        try
        {
            // A settings save affects the next search, not the middle of an in-flight request.
            var credentials = Credentials;
            var tasks = Sources(request.Query, credentials).Where(s => request.Source == "all" || s.Id == request.Source).Select(async source =>
            {
                if (source.Status != "ready") return (Source: source, Items: Array.Empty<MediaItem>());
                if (source.Id == "imdb" && request.Query.Length == 0)
                    return (Source: source with { Status = "needs_query", Message = credentials.ImdbMode == "official" ? "IMDb 官方 API 需要 IMDb 编号，例如 tt0133093。" : "IMDb 检索需要片名；请输入英文或原文片名后重试。" }, Items: Array.Empty<MediaItem>());
                if (source.Id == "imdb" && credentials.ImdbMode == "official" && !Regex.IsMatch(request.Query, "^tt[0-9]{7,10}$", RegexOptions.CultureInvariant))
                    return (Source: source with { Status = "needs_query", Message = "当前官方 API 适配按编号查询，请输入 tt0133093 这样的 IMDb 编号；按片名检索可切换 OMDb 或本地数据集。" }, Items: Array.Empty<MediaItem>());
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(source.Id == "imdb" ? 65 : 22));
                    var items = source.Id switch
                    {
                        "archive" => await ArchiveSearch(request, timeout.Token),
                        "tmdb" => await TmdbSearch(request, credentials, timeout.Token),
                        "imdb" => credentials.ImdbMode switch { "official" => await OfficialImdbSearch(request, credentials, timeout.Token), "omdb" => await OmdbSearch(request, credentials, timeout.Token), _ => await ImdbSearch(request, timeout.Token) },
                        _ => []
                    };
                    items = items.Where(m => (request.Type.Length == 0 || m.Type == request.Type) &&
                        (m.Rating >= request.MinRating || request.IncludeUnrated && m.Rating == 0)).OrderByDescending(m => m.Rating).ToArray();
                    return (Source: source with { Status = "ok", Count = items.Length, Message = items.Length == 0 ? "本页没有满足条件的结果。可尝试英文原名、下一页或降低评分。" : source.Message }, Items: items);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { return (Source: source with { Status = "error", Message = "该来源检索超时；其他来源与已有资源保留。" }, Items: Array.Empty<MediaItem>()); }
                catch (InvalidDataException ex)
                { return (Source: source with { Status = "error", Message = ex.Message }, Items: Array.Empty<MediaItem>()); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException or FormatException or KeyNotFoundException)
                {
                    // Never return exception text: upstream URLs can contain API credentials.
                    var message = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ? "来源认证失败，请检查服务器上的 API Key。" :
                        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? "来源请求频率受限，请稍后重试。" :
                        "无法读取该来源；请检查服务器的 DNS、代理、证书或来源状态。其他来源仍可使用。";
                    return (Source: source with { Status = "error", Message = message }, Items: Array.Empty<MediaItem>());
                }
            });
            var responses = await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();
            var result = request.Mode == "latest" ? responses.SelectMany(x => x.Items).OrderByDescending(x => x.ReleaseDate, StringComparer.Ordinal).ToArray() : responses.SelectMany(x => x.Items).OrderByDescending(x => x.Rating).ToArray();
            store.MergeMedia(result);
            return new(result, responses.Select(x => x.Source).ToArray(), request.Page, request.Query);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonDocument> Json(string url, CancellationToken ct)
    {
        using var response = await web.SendAsync(PublicWebClient.ValidateUri(url), ct);
        response.EnsureSuccessStatusCode();
        const int max = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("来源响应超过 8 MB。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > max) throw new InvalidDataException("来源响应超过 8 MB。");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return JsonDocument.Parse(output.ToArray());
    }

    private static string Text(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value)) return "";
        if (value.ValueKind == JsonValueKind.Array) return string.Join(" / ", value.EnumerateArray().Select(x => x.ToString()));
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString();
    }
    private static double Number(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static int Year(string value) => value.Length >= 4 && int.TryParse(value[..4], out var n) && n is >= 1880 and <= 2100 ? n : 0;
    private static string Plain(string value) => WebUtility.HtmlDecode(Regex.Replace(value.Length > 20000 ? value[..20000] : value, "<[^>]*>", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).Trim();
    private static string[] Subjects(JsonElement item) => Text(item, "subject").Split([';', '/', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
    private static bool Identifier(string value) => value.Length is > 0 and <= 120 && Regex.IsMatch(value, "^[a-zA-Z0-9_][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant);

    private async Task<MediaItem[]> ArchiveSearch(MovieSearchRequest request, CancellationToken ct)
    {
        var q = "mediatype:movies AND " + (request.Type == "tv" ? "collection:television" : request.Type == "movie" ? "collection:feature_films" : "(collection:feature_films OR collection:television)");
        if (request.Query.Length > 0)
        {
            var literal = request.Query.Replace("\\", "\\\\").Replace("\"", "\\\"");
            q += " AND title:\"" + literal + "\"";
        }
        if (request.MinRating > 0)
        {
            var score = "avg_rating:[" + (request.MinRating / 2).ToString(CultureInfo.InvariantCulture) + " TO 5]";
            q += " AND " + (request.IncludeUnrated ? "(" + score + " OR -avg_rating:[* TO *])" : score);
        }
        var fields = new[] { "identifier", "title", "year", "date", "description", "subject", "collection", "avg_rating", "num_reviews" };
        var url = "https://archive.org/advancedsearch.php?q=" + Encode(q) + "&" + string.Join("&", fields.Select(f => "fl[]=" + f)) + "&sort[]=downloads+desc&rows=24&page=" + request.Page + "&output=json";
        using var doc = await Json(url, ct);
        if (doc.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException("来源查询失败。");
        var items = new List<MediaItem>();
        foreach (var item in doc.RootElement.GetProperty("response").GetProperty("docs").EnumerateArray())
        {
            var id = Text(item, "identifier"); if (!Identifier(id)) continue;
            var title = Plain(Text(item, "title")); if (title.Length == 0) continue;
            var isTv = request.Type == "tv" || Text(item, "collection").Split(" / ").Any(x => x is "television" or "classic_tv" or "tvarchive");
            var rating = Number(item, "avg_rating");
            items.Add(new("archive:" + id, title, title, Year(Text(item, "year")) is var year && year > 0 ? year : Year(Text(item, "date")),
                isTv ? "tv" : "movie", Subjects(item), rating is > 0 and <= 5 ? Math.Round(rating * 2, 1) : 0, null, null,
                "https://archive.org/services/img/" + Encode(id), Plain(Text(item, "description")), "https://archive.org/details/" + Encode(id), [], [],
                Source: "Internet Archive", RatingSource: "Internet Archive（5 分制折算至 10 分）", VoteCount: (int)Number(item, "num_reviews")));
        }
        return items.ToArray();
    }

    private async Task<MediaItem[]> TmdbSearch(MovieSearchRequest request, SourceCredentials credentials, CancellationToken ct)
    {
        var types = request.Type.Length > 0 ? new[] { request.Type } : new[] { "movie", "tv" };
        var responses = await Task.WhenAll(types.Select(async type =>
        {
            var path = request.Query.Length > 0 ? "search/" + type : "discover/" + type;
            var url = path + "?language=zh-CN&include_adult=false&page=" + request.Page;
            url += request.Query.Length > 0 ? "&query=" + Encode(request.Query) : "&sort_by=vote_average.desc&vote_count.gte=100&vote_average.gte=" + request.MinRating.ToString(CultureInfo.InvariantCulture);
            if (request.Mode == "latest")
            {
                var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                url = "discover/movie?language=zh-CN&include_adult=false&sort_by=primary_release_date.desc&primary_release_date.lte=" + today + "&page=" + request.Page + "&vote_average.gte=" + request.MinRating.ToString(CultureInfo.InvariantCulture);
            }
            using var doc = await api.Tmdb(url, credentials, ct);
            return doc.RootElement.GetProperty("results").EnumerateArray().Select(item =>
            {
                var id = Text(item, "id"); var title = Text(item, type == "movie" ? "title" : "name");
                var poster = Text(item, "poster_path");
                return new MediaItem("tmdb:" + type + ":" + id, title, Text(item, type == "movie" ? "original_title" : "original_name"), Year(Text(item, type == "movie" ? "release_date" : "first_air_date")), type,
                    item.TryGetProperty("genre_ids", out var genres) ? genres.EnumerateArray().Select(x => Genre(x.GetInt32())).ToArray() : [],
                    Math.Round(Math.Clamp(Number(item, "vote_average"), 0, 10), 1), null, null, poster.StartsWith('/') ? "https://image.tmdb.org/t/p/w500" + poster : null,
                    Text(item, "overview"), "https://www.themoviedb.org/" + type + "/" + Encode(id), [], [], Source: "TMDB", RatingSource: "TMDB", VoteCount: (int)Number(item, "vote_count"), ReleaseDate: Text(item, type == "movie" ? "release_date" : "first_air_date"));
            }).ToArray();
        }));
        return responses.SelectMany(x => x).ToArray();
    }

    private async Task<MediaItem[]> OfficialImdbSearch(MovieSearchRequest request, SourceCredentials credentials, CancellationToken ct)
    {
        if (request.Page > 1) return [];
        using var doc = await api.Imdb(request.Query, credentials, ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null) return [];
        var name = title.GetProperty("titleText").GetProperty("text").GetString() ?? request.Query;
        var summary = title.GetProperty("ratingsSummary"); var rating = Number(summary, "aggregateRating");
        return [new("imdb:" + request.Query, name, name, 0, "unknown", [], rating, rating > 0 ? rating : null, null, null,
            "IMDb 官方 API 提供的片名与评分；当前适配不查询年份与类别。", "https://www.imdb.com/title/" + Encode(request.Query) + "/", [], [],
            Source: "IMDb 官方 API", RatingSource: "IMDb", VoteCount: (int)Number(summary, "voteCount"))];
    }

    private async Task<MediaItem[]> OmdbSearch(MovieSearchRequest request, SourceCredentials credentials, CancellationToken ct)
    {
        var isId = Regex.IsMatch(request.Query, "^tt[0-9]{7,10}$", RegexOptions.CultureInvariant);
        string[] ids;
        if (isId) { if (request.Page > 1) return []; ids = [request.Query]; }
        else
        {
            var query = "s=" + Encode(request.Query) + "&page=" + request.Page + (request.Type.Length > 0 ? "&type=" + (request.Type == "tv" ? "series" : "movie") : "");
            using var results = await api.Omdb(query, credentials, ct);
            if (!results.RootElement.TryGetProperty("Search", out var search)) return [];
            ids = search.EnumerateArray().Select(x => Text(x, "imdbID")).Where(x => Regex.IsMatch(x, "^tt[0-9]{7,10}$", RegexOptions.CultureInvariant)).Distinct().Take(10).ToArray();
        }
        var items = new List<MediaItem>();
        // OMDb search summaries omit ratings: read each detail rather than inventing or guessing them.
        foreach (var id in ids)
        {
            using var detail = await api.Omdb("i=" + id + "&plot=short", credentials, ct);
            var item = detail.RootElement;
            if (Text(item, "Response") == "False" || Text(item, "Type") is not ("movie" or "series")) continue;
            var rating = Number(item, "imdbRating");
            var poster = Text(item, "Poster");
            if (!Uri.TryCreate(poster, UriKind.Absolute, out var image) || image.Scheme != "https") poster = "";
            var name = Text(item, "Title");
            var votes = int.TryParse(Text(item, "imdbVotes").Replace(",", ""), out var count) ? count : 0;
            items.Add(new("imdb:" + id, name, name, Year(Text(item, "Year")), Text(item, "Type") == "series" ? "tv" : "movie",
                Text(item, "Genre") is "N/A" or "" ? [] : Text(item, "Genre").Split(',', StringSplitOptions.TrimEntries), rating, rating > 0 ? rating : null,
                Text(item, "Runtime") == "N/A" ? null : Text(item, "Runtime"), poster.Length > 0 ? poster : null, Text(item, "Plot") == "N/A" ? "" : Text(item, "Plot"),
                "https://www.imdb.com/title/" + id + "/", [], [], Source: "OMDb（第三方）", RatingSource: "IMDb（由 OMDb 提供）", VoteCount: votes));
        }
        return items.ToArray();
    }

    public async Task<SourceConnectionResult> TestConnection(string provider, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var credentials = Credentials;
            if (provider == "tmdb")
            {
                using var doc = await api.Tmdb("configuration", credentials, timeout.Token);
                if (!doc.RootElement.TryGetProperty("images", out _)) throw new InvalidDataException("TMDB 未返回有效配置，请检查凭据。");
                using var movies = await api.Tmdb("search/movie?query=Interstellar&language=zh-CN&include_adult=false&page=1", credentials, timeout.Token);
                if (!movies.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                    throw new InvalidDataException("TMDB 认证已通过，但电影检索未返回测试影片，请检查检索接口访问情况。");
                return new(true, $"TMDB 认证与电影检索均已通过，测试查询返回 {results.GetArrayLength()} 项。请点击“开始检索”同步最新电影。");
            }
            else if (provider == "imdb" && credentials.ImdbMode == "official")
            {
                using var doc = await api.Imdb("tt0133093", credentials, timeout.Token);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("title", out var title) || title.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException("IMDb 未返回有效资料，请检查订阅权限。");
            }
            else if (provider == "imdb" && credentials.ImdbMode == "omdb")
            {
                using var doc = await api.Omdb("i=tt0133093", credentials, timeout.Token);
                if (Text(doc.RootElement, "imdbID") != "tt0133093") throw new InvalidDataException("OMDb 未返回有效资料，请检查密钥与额度。");
            }
            else if (provider == "imdb")
                return new(ImdbReady, ImdbReady ? "IMDb 两个本地数据集文件已就绪。" : "尚未找到 IMDb 两个数据集文件；也可选择在线 API 模式。");
            else throw new ArgumentException("未知来源。");
            return new(true, "连接测试成功，已保存的凭据可以访问所选接口。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { return new(false, ex.Message); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return new(false, "认证或订阅权限验证失败，请核对密钥、AWS 凭据及订阅标识。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "连接超时；凭据已保存，请检查服务器的代理、DNS 和来源状态。"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(false, "无法验证来源连接；请检查服务器的网络、证书、账户额度与来源状态。凭据已保存。"); }
    }

    private static string Genre(int id) => id switch { 28 => "动作", 12 => "冒险", 16 => "动画", 35 => "喜剧", 80 => "犯罪", 99 => "纪录", 18 => "剧情", 10751 => "家庭", 14 => "奇幻", 36 => "历史", 27 => "恐怖", 10402 => "音乐", 9648 => "悬疑", 10749 => "爱情", 878 => "科幻", 10770 => "电视电影", 53 => "惊悚", 10752 => "战争", 37 => "西部", 10759 => "动作冒险", 10762 => "儿童", 10763 => "新闻", 10764 => "真人秀", 10765 => "科幻奇幻", 10766 => "肥皂剧", 10767 => "脱口秀", 10768 => "战争政治", _ => "其他" };

    private static StreamReader OpenDataset(string path)
    {
        Stream stream = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) stream = new GZipStream(stream, CompressionMode.Decompress);
        return new StreamReader(stream);
    }

    private async Task<MediaItem[]> ImdbSearch(MovieSearchRequest request, CancellationToken ct)
    {
        if (request.Query.Length == 0) throw new InvalidDataException("IMDb 本地检索需要片名。");
        var candidates = new Dictionary<string, MediaItem>();
        using (var input = OpenDataset(ImdbFile("title.basics.tsv")!))
        {
            await input.ReadLineAsync(ct);
            while (await input.ReadLineAsync(ct) is { } line)
            {
                var fields = line.Split('\t');
                if (fields.Length < 9 || fields[4] != "0" || fields[1] is not ("movie" or "tvSeries" or "tvMiniSeries" or "tvMovie")) continue;
                var type = fields[1] is "tvSeries" or "tvMiniSeries" ? "tv" : "movie";
                if (request.Type.Length > 0 && type != request.Type || !fields[2].Contains(request.Query, StringComparison.OrdinalIgnoreCase) && !fields[3].Contains(request.Query, StringComparison.OrdinalIgnoreCase)) continue;
                candidates[fields[0]] = new("imdb:" + fields[0], fields[2], fields[3], Year(fields[5]), type,
                    fields[8] == "\\N" ? [] : fields[8].Split(','), 0, null, fields[7] == "\\N" ? null : fields[7] + " 分钟", null, "IMDb 官方非商业数据集。", "https://www.imdb.com/title/" + Encode(fields[0]) + "/", [], [], Source: "IMDb", RatingSource: "IMDb");
                // Fail explicitly rather than silently truncating and claiming a complete result.
                if (candidates.Count > 5000) throw new InvalidDataException("匹配过多，请输入更完整的片名。");
            }
        }
        using (var input = OpenDataset(ImdbFile("title.ratings.tsv")!))
        {
            await input.ReadLineAsync(ct);
            while (await input.ReadLineAsync(ct) is { } line)
            {
                var fields = line.Split('\t');
                if (fields.Length < 3 || !candidates.TryGetValue(fields[0], out var item)) continue;
                if (double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) && double.IsFinite(rating) && rating is > 0 and <= 10 && int.TryParse(fields[2], out var votes))
                    candidates[fields[0]] = item with { Rating = rating, Imdb = rating, VoteCount = votes };
            }
        }
        return candidates.Values.Where(m => m.Rating >= request.MinRating || request.IncludeUnrated && m.Rating == 0).OrderByDescending(m => m.Rating).Skip((request.Page - 1) * 24).Take(24).ToArray();
    }

    public async Task<MediaItem> ArchiveResources(string identifier, CancellationToken ct)
    {
        if (!Identifier(identifier)) throw new ArgumentException("影片标识不合法。");
        var item = store.Catalogue.Items.FirstOrDefault(x => x.Id == "archive:" + identifier) ?? throw new KeyNotFoundException("请先检索此影片。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        using var doc = await Json("https://archive.org/metadata/" + Encode(identifier), timeout.Token);
        var root = doc.RootElement;
        if (!root.TryGetProperty("metadata", out var metadata) || Text(metadata, "identifier") != identifier) throw new InvalidDataException("影片已不可用。");
        if (Text(root, "is_dark").Equals("true", StringComparison.OrdinalIgnoreCase) || Text(metadata, "access-restricted-item").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该影片文件受访问限制，无法加入下载队列。");
        var links = new List<ResourceLink>();
        if (root.TryGetProperty("files", out var files)) foreach (var file in files.EnumerateArray())
        {
            var name = Text(file, "name"); var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".mp4" or ".mkv" or ".ogv" or ".webm" or ".avi" or ".mov" or ".m4v") || name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(x => x is ".." or "." or "") ||
                Text(file, "private") is "true" or "1") continue;
            var url = "https://archive.org/download/" + Encode(identifier) + "/" + string.Join("/", name.Split('/').Select(Encode));
            links.Add(new("archive-file:" + name, name, url, Text(file, "format") is { Length: > 0 } format ? format : ext[1..].ToUpperInvariant(), "HTTPS", long.TryParse(Text(file, "size"), out var size) ? Math.Round(size / 1048576d, 1).ToString(CultureInfo.InvariantCulture) + " MB" : null));
            if (links.Count >= 50) break;
        }
        var license = Text(metadata, "licenseurl");
        var updated = item with { Resources = links.ToArray(), Qualities = links.Select(x => x.Quality).Distinct().ToArray(),
            Description = Plain(Text(metadata, "description")) + (license.Length > 0 ? "\n来源标注许可：" + license : "\n来源未标注明确许可；请在原始页面核对文件使用条件。") };
        store.MergeMedia([updated]); return updated;
    }
}
