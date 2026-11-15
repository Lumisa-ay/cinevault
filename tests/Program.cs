using System.Net;
using System.Net.Http.Headers;
using CineVault.Models;
using CineVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

var checks = 0;
void Check(bool pass, string label) { if (!pass) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); checks++; }
void Reject(Action fn, string label) { try { fn(); } catch (ArgumentException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
var config = new ConfigurationBuilder().Build();
var parser = new PageParser(config);
var fixture = """
<html><script type="application/ld+json">{"@context":"https://schema.org","@type":"Movie","name":"Fixture Movie","datePublished":"2024-01-02","aggregateRating":{"ratingValue":"4.5","bestRating":"5"},"genre":["科幻","冒险"],"description":"Fixture only"}</script><a href="https://cdn.example.org/film.2160p.mkv?token=abc&amp;x=1">4K REMUX</a><a href="https://cdn.example.org/film.1080p.mp4">1080p</a></html>
""";
var items = parser.Parse(fixture, new Uri("https://example.org/movie/1"), true);
Check(items.Length == 1 && items[0].Rating == 9 && items[0].Imdb is null && items[0].Genres.Length == 2, "structured movie, normalized score, no invented IMDb");
Check(items[0].Resources.Length == 2 && items[0].Resources[0].Url.Contains("&x=1") && items[0].Qualities.Contains("4K"), "decoded resource links and quality classification");
Check(parser.Parse("<html>JavaScript required</html>", new Uri("https://example.org/"), false).Length == 0, "no invented catalogue for a script-only page");
var fallback = parser.Parse("<h1>测试剧集</h1><p>电视剧 豆瓣 8.7 类型:剧情/悬疑</p><a href='magnet:?xt=urn:btih:abc'>资源</a>",new Uri("https://example.org/tv/1"),true);
Check(fallback[0].Type == "tv" && fallback[0].Rating == 8.7 && fallback[0].Resources[0].Protocol == "MAGNET", "HTML fallback with explicit rating and magnet classification");
Check(!RobotsPolicy.Evaluate("User-agent: *\nDisallow: /\n", new Uri("https://example.org/")).Allowed, "site-wide robots restriction is reported for the requested page");
Check(RobotsPolicy.Evaluate("User-agent: Other\nDisallow: /\nUser-agent: *\nDisallow: /private", new Uri("https://example.org/")).Allowed, "unrelated user-agent does not restrict this crawler");
Check(RobotsPolicy.Evaluate("User-agent: *\nDisallow: /\nAllow: /public", new Uri("https://example.org/public/movie")).Allowed, "more-specific allow overrides the root disallow");
Check(RobotsPolicy.Evaluate("User-agent: *\nDisallow: /\nAllow: /", new Uri("https://example.org/movie")).Allowed, "allow wins when allow and disallow have equal specificity");
Check(RobotsPolicy.Evaluate("User-agent: *\nDisallow: /\nUser-agent: cinevault\nAllow: /", new Uri("https://example.org/movie")).Allowed, "case-insensitive exact product group overrides wildcard group");
Check(RobotsPolicy.Evaluate("User-agent: CineVault\nAllow: /\nUser-agent: Other\nDisallow: /", new Uri("https://example.org/movie")).Allowed, "allow directives terminate an agent group before the next agent");
Check(RobotsPolicy.Evaluate("User-agent: *\nAllow: /\nUser-agent: CineVaultOther\nDisallow: /", new Uri("https://example.org/movie")).Allowed, "a different product containing this product name does not match");
Check(!RobotsPolicy.Evaluate("User-agent: CineVault\nAllow: /\nUser-agent: CineVault\nDisallow: /private", new Uri("https://example.org/private/file")).Allowed, "repeated product groups combine their rules");
Check(!RobotsPolicy.Evaluate("User-agent: *\nDisallow: /private/*.json$", new Uri("https://example.org/private/file.json")).Allowed &&
    RobotsPolicy.Evaluate("User-agent: *\nDisallow: /private/*.json$", new Uri("https://example.org/private/file.json?download=1")).Allowed, "wildcards and end anchors use the full path and query");
Check(!RobotsPolicy.Evaluate("User-agent: *\nDisallow: /%62ar\nDisallow: /电影", new Uri("https://example.org/bar")).Allowed &&
    !RobotsPolicy.Evaluate("User-agent: *\nDisallow: /电影", new Uri("https://example.org/%E7%94%B5%E5%BD%B1/1")).Allowed, "unreserved escapes and UTF-8 paths compare consistently");
Check(RobotsPolicy.Evaluate("\uFEFFDisallow: /\rUser-agent: *\rDisallow: \r# comment", new Uri("https://example.org/")).Allowed, "empty rules, BOM, CR line endings and rules outside groups do not block pages");
Reject(()=>PublicWebClient.ValidateUri("http://127.0.0.1/private"),"loopback URL rejected");
Reject(()=>PublicWebClient.ValidateUri("http://169.254.169.254/metadata"),"link-local URL rejected");
Reject(()=>PublicWebClient.ValidateUri("file:///C:/secret"),"non-HTTP URL rejected");
Check(!PublicWebClient.IsPublicAddress(IPAddress.Parse("::ffff:10.0.0.1")) && !PublicWebClient.IsPublicAddress(IPAddress.Parse("fc00::1")),"IPv4 mapped and IPv6 private addresses rejected");
var root=Path.Combine(Path.GetTempPath(),"cinevault-checks-"+Guid.NewGuid().ToString("N"));
var store=new StateStore(root);
Reject(()=>store.ValidateDirectory(Path.GetFullPath(Path.Combine(root,"downloads","..","escape"))),"directory traversal outside root rejected");
Reject(()=>store.SetSettings(store.Settings with {Concurrency=0}),"invalid queue concurrency rejected");
var crawlRoot = new StateStore(Path.Combine(root, "crawler"));
var crawlConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["Crawler:BaseUrl"]="https://example.org/", ["Crawler:ListPaths:0"]="/public", ["Crawler:ListPaths:1"]="/private",
    ["Crawler:DelayMilliseconds"]="500"
}).Build();
var existing = new Catalogue(items, DateTimeOffset.UtcNow); crawlRoot.SetCatalogue(existing);
async Task<SourceAccessException> ExpectSourceFailure(CrawlFixtureWeb source, string label)
{
    try { await new CrawlerService(source,parser,crawlConfig,crawlRoot).Crawl(CancellationToken.None); }
    catch (SourceAccessException ex) { return ex; }
    throw new Exception("FAIL: "+label);
}
var deniedWeb = new CrawlFixtureWeb(new() { ["/robots.txt"]=(HttpStatusCode.OK,"User-agent: *\nDisallow: /\n") });
var denied = await ExpectSourceFailure(deniedWeb,"robots block");
Check(denied.Code=="robots_disallowed" && denied.Rule=="Disallow: /" && denied.Agent=="*" && denied.ResponseStatus==403, "blocked crawl explains the matching rule without reporting a generic failure");
Check(deniedWeb.Requested.SequenceEqual(new[]{"/robots.txt"}) && ReferenceEquals(crawlRoot.Catalogue, existing), "blocked crawl does not fetch blocked content or replace the catalogue");
var allowedWeb = new CrawlFixtureWeb(new() {
    ["/robots.txt"]=(HttpStatusCode.OK,"User-agent: *\nDisallow: /\nAllow: /public"),
    ["/public"]=(HttpStatusCode.OK,fixture+"<a href='/private/movie/2'>private detail</a>")
});
var collected = await new CrawlerService(allowedWeb,parser,crawlConfig,crawlRoot).Crawl(CancellationToken.None);
Check(collected.Items.Length==1 && !allowedWeb.Requested.Any(x=>x.StartsWith("/private")), "allowed list pages are read while blocked lists and detail pages are skipped");
var tlsWeb = new CrawlFixtureWeb(new()) { Failure=new HttpRequestException(HttpRequestError.SecureConnectionError,"fixture",new System.Security.Authentication.AuthenticationException("untrusted fixture")) };
Check((await ExpectSourceFailure(tlsWeb,"TLS")).Code=="source_tls_error", "TLS failure is distinct from a robots restriction");
var reservedWeb = new CrawlFixtureWeb(new()) { Failure=new HttpRequestException("wrapped fixture",new PublicAddressRejectedException("example.org")) };
Check((await ExpectSourceFailure(reservedWeb,"reserved DNS address")).Code=="source_address_blocked", "reserved DNS addresses are distinguished from source access rules");
var dnsWeb = new CrawlFixtureWeb(new()) { Failure=new HttpRequestException(HttpRequestError.NameResolutionError,"fixture DNS") };
Check((await ExpectSourceFailure(dnsWeb,"DNS")).Code=="source_dns_error", "DNS lookup failures have a distinct diagnostic");
var forbiddenWeb = new CrawlFixtureWeb(new() { ["/robots.txt"]=(HttpStatusCode.Forbidden,"forbidden") });
Check((await ExpectSourceFailure(forbiddenWeb,"HTTP 403")).Code=="source_access_denied", "HTTP access denial is distinct from robots rules");
var unavailableWeb = new CrawlFixtureWeb(new() { ["/robots.txt"]=(HttpStatusCode.ServiceUnavailable,"unavailable") });
Check((await ExpectSourceFailure(unavailableWeb,"HTTP 503")).Code=="source_http_error" && unavailableWeb.Requested.Count==1, "unreachable rules stop the crawl without claiming a robots ban");
var timeoutWeb = new CrawlFixtureWeb(new()) { Failure=new TaskCanceledException("fixture timeout") };
Check((await ExpectSourceFailure(timeoutWeb,"timeout")).Code=="source_timeout", "source timeout has its own diagnostic code");

var ignoredConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["Crawler:BaseUrl"]="https://example.org/", ["Crawler:ListPaths:0"]="/public",
    ["Crawler:RespectRobotsTxt"]="false", ["Crawler:DelayMilliseconds"]="500"
}).Build();
var ignoredRobotsWeb = new CrawlFixtureWeb(new() {
    ["/robots.txt"]=(HttpStatusCode.OK,"User-agent: *\nDisallow: /"),
    ["/public"]=(HttpStatusCode.OK,fixture)
});
var ignoredResult = await new CrawlerService(ignoredRobotsWeb,parser,ignoredConfig,crawlRoot).Crawl(CancellationToken.None);
Check(ignoredResult.Items.Length==1 && !ignoredRobotsWeb.Requested.Contains("/robots.txt"), "explicit robots opt-out requests content directly without fetching robots.txt");
var parkedWeb = new CrawlFixtureWeb(new() {
    ["/public"]=(HttpStatusCode.OK,"<html><title>Redirecting...</title><script>fetch('https://router.parklogic.com/')</script></html>")
});
var beforeParked = crawlRoot.Catalogue;
SourceAccessException? parkedFailure = null;
try { await new CrawlerService(parkedWeb,parser,ignoredConfig,crawlRoot).Crawl(CancellationToken.None); } catch (SourceAccessException ex) { parkedFailure=ex; }
Check(parkedFailure?.Code=="source_domain_parked" && ReferenceEquals(beforeParked,crawlRoot.Catalogue), "domain parking pages get a specific diagnostic and preserve the catalogue");
Check(parkedWeb.Requested.SequenceEqual(new[]{"/public"}), "parking scripts are not executed or treated as movie resources");

await SearchChecks.Run(root, Check);
await SourceSettingsChecks.Run(root, Check);
await TorrentChecks.Run(root, Check);

var web=new FixtureWeb();
using var loggerFactory=LoggerFactory.Create(b=>b.AddSimpleConsole());
var service=new DownloadService(store,web,loggerFactory.CreateLogger<DownloadService>());
store.SetSettings(store.Settings with {Concurrency=1,Organize=false,AutoRetry=true,MaxRetries=1});
await service.StartAsync(CancellationToken.None);
async Task<DownloadJob> Wait(string id, string state, int maxMs=12000)
{
 var deadline=DateTime.UtcNow.AddMilliseconds(maxMs);
 while(DateTime.UtcNow<deadline){var j=store.Job(id)!;if(j.Status==state)return j;await Task.Delay(50);}
 throw new Exception($"Timed out waiting for {state}; actual={store.Job(id)?.Status} {store.Job(id)?.Error}");
}
var ok=service.Create(new(null,null,null,"Fixture success","https://example.org/ok.mp4"));
await Wait(ok.Id,"Completed");
Check(File.ReadAllBytes(store.SafeFilePath(ok,false)).SequenceEqual(web.Payload),"download writes exact fixture bytes into selected directory");
var retry=service.Create(new(null,null,null,"Fixture retry","https://example.org/retry.mp4"));
var recovered=await Wait(retry.Id,"Completed");
Check(recovered.Attempts==1 && web.RetryCalls==2,"transient failure automatically retries and completes");
var fail=service.Create(new(null,null,null,"Fixture failed","https://example.org/fail.mp4"));
await Wait(fail.Id,"Failed");
Check(store.Job(fail.Id)!.Attempts==1,"retry ceiling ends in a failed state");
web.AllowFailed=true;service.Act(fail.Id,"retry");await Wait(fail.Id,"Completed");
Check(File.Exists(store.SafeFilePath(fail,false)),"manual retry recovers a failed task");
var slow=service.Create(new(null,null,null,"Fixture slow","https://example.org/slow.mp4"));
await Wait(slow.Id,"Downloading");await Task.Delay(800);service.Act(slow.Id,"pause");await Task.Delay(400);
Check(store.Job(slow.Id)!.Status=="Paused" && !File.Exists(store.SafeFilePath(slow,false)),"pause preserves partial file without marking complete");
service.Act(slow.Id,"resume");await Wait(slow.Id,"Completed",15000);
Check(File.ReadAllBytes(store.SafeFilePath(slow,false)).SequenceEqual(web.Payload),"resume produces exact bytes using validated range");
var html=service.Create(new(null,null,null,"Fixture invalid","https://example.org/page.mp4"));await Wait(html.Id,"Failed");
Check(!File.Exists(store.SafeFilePath(html,false)),"HTML response is never marked as a downloaded movie");
var cancelled=service.Create(new(null,null,null,"Fixture cancel","https://example.org/slow.mp4"));
await Wait(cancelled.Id,"Downloading");service.Act(cancelled.Id,"cancel");service.ClearFinished();await Task.Delay(500);
service.ClearFinished();
Check(store.Job(cancelled.Id) is null,"cancel and immediate history cleanup do not interrupt the queue worker");
var afterCleanup=service.Create(new(null,null,null,"Fixture after cleanup","https://example.org/ok.mp4"));await Wait(afterCleanup.Id,"Completed");
Check(File.Exists(store.SafeFilePath(afterCleanup,false)),"queue remains usable after cancellation and cleanup");
await service.StopAsync(CancellationToken.None);service.Dispose();
var restored=new StateStore(root);
Check(restored.Jobs().Length==store.Jobs().Length && restored.Job(afterCleanup.Id)!.Status=="Completed","queue records persist and restore");
Check(!restored.Jobs().Any(x=>x.IsDemo),"real backend never accepts demonstration jobs");
Console.WriteLine($"All {checks} checks passed. Fixture directory: {root}");

sealed class FixtureWeb : IPublicWebClient
{
 public byte[] Payload {get;}=Enumerable.Range(0,100000).Select(x=>(byte)(x%251)).ToArray();
 public int RetryCalls; public bool AllowFailed;
 public Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken ct,long? range=null,string? validator=null)
 {
  if(uri.AbsolutePath=="/retry.mp4"&&Interlocked.Increment(ref RetryCalls)==1||uri.AbsolutePath=="/fail.mp4"&&!AllowFailed)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("temporary fixture failure")});
  var offset=range.GetValueOrDefault();var r=new HttpResponseMessage(offset>0?HttpStatusCode.PartialContent:HttpStatusCode.OK);
  r.Headers.ETag=new EntityTagHeaderValue("\"fixture-v1\"");
  r.Content=uri.AbsolutePath=="/slow.mp4"?new StreamContent(new SlowStream(Payload[(int)offset..])):new ByteArrayContent(Payload[(int)offset..]);
  r.Content.Headers.ContentLength=Payload.Length-offset;
  r.Content.Headers.ContentType=new MediaTypeHeaderValue(uri.AbsolutePath=="/page.mp4"?"text/html":"video/mp4");
  if(offset>0)r.Content.Headers.ContentRange=new ContentRangeHeaderValue(offset,Payload.Length-1,Payload.Length);
  return Task.FromResult(r);
 }
}
sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
{
 public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
 {await Task.Delay(250,ct);return await base.ReadAsync(buffer[..Math.Min(10000,buffer.Length)],ct);}
}

sealed class CrawlFixtureWeb(Dictionary<string,(HttpStatusCode Status,string Body)> responses) : IPublicWebClient
{
    public List<string> Requested { get; } = [];
    public Exception? Failure { get; set; }
    public Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken ct,long? range=null,string? validator=null)
    {
        Requested.Add(uri.AbsolutePath);
        if (Failure is not null) return Task.FromException<HttpResponseMessage>(Failure);
        if (!responses.TryGetValue(uri.AbsolutePath,out var value)) throw new Exception("Unexpected crawler request: "+uri.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(value.Status) { Content=new StringContent(value.Body) });
    }
}
