using System.Text;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public sealed class CrawlerService(IPublicWebClient web, PageParser parser, IConfiguration config, StateStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<string> Html(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await web.SendAsync(uri, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("页面超过 4 MB 限制。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16384]; int n;
            while ((n = await input.ReadAsync(buffer, timeout.Token)) > 0)
            { if (output.Length + n > 4 * 1024 * 1024) throw new InvalidDataException("页面超过 4 MB 限制。"); await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token); }
            var encoding = Encoding.UTF8;
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            if (!string.IsNullOrEmpty(charset)) try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
            var html = encoding.GetString(output.ToArray());
            if (html.Contains("router.parklogic.com/", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(html, @"<title\b[^>]*>\s*Redirecting\.{0,3}\s*</title>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                throw new SourceAccessException("source_domain_parked", "当前源站返回域名停放/广告跳转页面，没有影视目录。",
                    uri, "已经直接请求页面；跳过 robots.txt 也无法从此页面获得电影或剧集。请确认该网站当前仍在使用的域名。", 422);
            return html;
        }
        catch (HttpRequestException ex) { throw SourceAccessException.Transport(uri, ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SourceAccessException("source_timeout", $"读取{(uri.AbsolutePath == "/robots.txt" ? "源站访问规则" : "源站页面")}超过 15 秒未响应。",
                uri, "请稍后重试，并检查容器网络与源站状态；原有资源已保留。", 504, inner: ex);
        }
    }
    public async Task<Catalogue> Crawl(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new ArgumentException("已有抓取任务在执行，请稍后刷新。");
        try
        {
            var root = PublicWebClient.ValidateUri(config["Crawler:BaseUrl"] ?? "https://www.gying.net/");
            var pattern = config["Crawler:DetailPathPattern"] ?? @"/(?:mv|movie|tv|vod|detail|play|series)[/._-]";
            var delay = Math.Clamp(config.GetValue("Crawler:DelayMilliseconds", 1200), 500, 10000);
            var max = Math.Clamp(config.GetValue("Crawler:MaxDetailPages", 15), 1, 30);
            var paths = config.GetSection("Crawler:ListPaths").Get<string[]>() ?? ["/"];
            var items = new List<MediaItem>(); var details = new HashSet<string>(); var warnings = new List<string>();
            var robots = "";
            var respectRobots = config.GetValue("Crawler:RespectRobotsTxt", true);
            if (respectRobots)
            {
                try { robots = await Html(new Uri(root, "/robots.txt"), ct); }
                catch (SourceAccessException ex) when (ex.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }) { }
            }
            var lists = paths.Take(4).Select(path => new Uri(root, path)).ToArray();
            if (lists.Any(url => url.Scheme != root.Scheme || url.Authority != root.Authority))
                throw new ArgumentException("列表页必须属于配置的源站。");
            var allowed = lists.Select(url => (Url: url, Decision: RobotsPolicy.Evaluate(robots, url))).ToArray();
            if (allowed.Length > 0 && allowed.All(x => !x.Decision.Allowed))
                throw SourceAccessException.RobotsDenied(allowed[0].Url, allowed[0].Decision);
            foreach (var list in allowed)
            {
                if (!list.Decision.Allowed) { warnings.Add($"按源站规则跳过列表页：{list.Url.AbsolutePath}（{list.Decision.Rule}）"); continue; }
                var url = list.Url;
                var html = await Html(url, ct);
                items.AddRange(parser.Parse(html, url, false));
                foreach (var a in PageParser.Anchors(html, url))
                    if (Uri.TryCreate(a.Url, UriKind.Absolute, out var d) && d.Scheme == root.Scheme && d.Authority == root.Authority && Regex.IsMatch(d.AbsolutePath, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) details.Add(d.AbsoluteUri);
                foreach (var item in items.Where(x => x.SourceUrl != url.AbsoluteUri)) details.Add(item.SourceUrl);
                await Task.Delay(delay, ct);
            }
            foreach (var d in details.Take(max))
            {
                var uri = new Uri(d);
                var decision = RobotsPolicy.Evaluate(robots, uri);
                if (!decision.Allowed) { warnings.Add($"按源站规则跳过详情页：{uri.AbsolutePath}（{decision.Rule}）"); continue; }
                try
                {
                    var parsed = parser.Parse(await Html(uri, ct), uri, true);
                    foreach (var item in parsed) { items.RemoveAll(x => x.Id == item.Id); items.Add(item); }
                }
                catch (Exception ex) when (ex is SourceAccessException or InvalidDataException or OperationCanceledException)
                { ct.ThrowIfCancellationRequested(); warnings.Add($"详情页读取失败：{uri.AbsolutePath}（{ex.Message}）"); }
                await Task.Delay(delay, ct);
            }
            if (items.Count == 0) throw new SourceAccessException("source_parse_empty", "页面已读取，但未解析到电影或剧集。",
                root, "源站可能需要 JavaScript、访问验证或已调整页面结构。请检查解析配置；原有资源已保留。", 422);
            if (items.All(x => x.Rating == 0)) warnings.Add("源站未提供可识别评分；可将最低评分调为不限以查看内容。");
            var result = new Catalogue(items.DistinctBy(x => x.Id).OrderByDescending(x => x.Rating).ToArray(), DateTimeOffset.UtcNow, warnings.Distinct().Take(10).ToArray());
            store.SetCatalogue(result); return result;
        }
        finally { gate.Release(); }
    }
}
