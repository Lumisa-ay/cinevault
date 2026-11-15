using System.Net;
using System.Text;
using System.Text.Json;
using CineVault.Controllers;
using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class TorrentChecks
{
    public static async Task Run(string root, Action<bool,string> check)
    {
        var dir = Path.Combine(root, "bt-checks"); Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Sources:TmdbApiKey"] = "tmdb-fixture" }).Build();
        var settings = new SourceSettingsStore(dir, config, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys"))));
        settings.SaveBt(new() { ResourceIndexUrl = "http://prowlarr:9696/", ResourceIndexKey = "index-fixture-key", QbitUrl = "http://qbittorrent:8080/", QbitUserName = "admin", QbitPassword = "fixture password", QbitSaveRoot = "/downloads" });
        check(settings.View().HasResourceIndexKey && settings.View().HasQbitPassword && !JsonSerializer.Serialize(settings.View()).Contains("index-fixture-key"), "BT credentials are configured and excluded from public view models");
        var ciphertext = File.ReadAllText(Path.Combine(dir, "source-settings.protected"));
        check(!ciphertext.Contains("fixture password") && !ciphertext.Contains("index-fixture-key"), "index and downloader secrets use existing encrypted storage");
        var rejected = false; try { SourceSettingsStore.ServiceUrl("http://169.254.169.254/"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "service configuration rejects cloud metadata targets");
        rejected = false; try { SourceSettingsStore.ServiceUrl("http://user:secret@example.com/"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "service configuration cannot embed credentials in URLs");
        var movie = new MediaItem("tmdb:movie:1", "Fixture Movie", "Fixture Movie", 2025, "movie", [], 8.5, null, null, null, "", "https://www.themoviedb.org/movie/1", [], []);
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        const string magnet = "magnet:?xt=urn:btih:" + hash;
        var best = TorrentReleaseRules.Candidate(movie, "Fixture.Movie.2025.2160p.CHS.mkv", magnet, 10, 1073741824L, "Fixture index");
        var wrong = TorrentReleaseRules.Candidate(movie, "Fixture.Movie.2024.2160p.CHS.mkv", "magnet:?xt=urn:btih:" + new string('b',40), 999, 1, "Fixture index");
        var lower = TorrentReleaseRules.Candidate(movie, "Fixture.Movie.2025.1080p.CHS.mkv", "magnet:?xt=urn:btih:" + new string('c',40), 10, 1, "Fixture index");
        check(best.Is4K && best.HasChineseSubtitles && best.MatchesMovie && best.Rank > lower.Rank && lower.Rank > wrong.Rank, "matching title and year outrank wrong releases; 4K Chinese subtitles rank first within correct releases");
        var audio = TorrentReleaseRules.Candidate(movie, "Fixture.Movie.2025.2160p.Mandarin.mkv", magnet, 2, 1, "Fixture index");
        check(!audio.HasChineseSubtitles, "Mandarin audio is not treated as Chinese subtitle evidence");
        var it = movie with { Title = "It", OriginalTitle = "It" };
        check(!TorrentReleaseRules.Candidate(it, "Titanic.2025.2160p.CHS", magnet, 1, 1, "Fixture").MatchesMovie, "short movie titles do not match unrelated release names by substring");
        check(TorrentReleaseRules.ParseMagnet("magnet:?xt=urn:btih:"+new string('A',32)).Hash == new string('0',40), "base32 BTIH hashes normalize to canonical hexadecimal");
        var clean = TorrentReleaseRules.ParseMagnet(magnet + "&ws=http%3A%2F%2F169.254.169.254&tr=http%3A%2F%2F127.0.0.1%2Fannounce").Magnet;
        check(!clean.Contains("169.254") && !clean.Contains("127.0.0.1"), "untrusted webseed and private tracker targets are not forwarded");
        using var handler = new ResourceFixtureHandler(); var gateway = new ResourceGateway(settings, handler);
        var found = await gateway.Search(movie, CancellationToken.None);
        check(found.Length == 1 && found[0].Is4K && found[0].HasChineseSubtitles && handler.SawIndexKey, "Prowlarr queries its configured indexes using a header key and returns ranked magnet candidates");
        await gateway.Add(magnet, "/downloads/Movies", CancellationToken.None);
        check(handler.LastForm.ContainsKey("urls") && handler.LastForm["savepath"] == "/downloads/Movies" && handler.LoginCount == 1, "qBittorrent authenticates and receives the magnet plus explicit save directory");
        var status = await gateway.Status([hash], CancellationToken.None);
        check(status.Length == 1 && status[0].State == "downloading" && status[0].Downloaded == 50, "qBittorrent progress is read from its real API schema");
        await gateway.Control(hash, "cancel", CancellationToken.None);
        check(handler.LastForm["deleteFiles"] == "false", "cancelling a BT task preserves downloaded files");
        check((await gateway.Test("indexer", CancellationToken.None)).Success && (await gateway.Test("qbittorrent", CancellationToken.None)).Success, "index and downloader connection tests validate the configured services");
        var store = new StateStore(Path.Combine(dir, "state")); store.SetCatalogue(new([movie], DateTimeOffset.UtcNow));
        store.SetSettings(store.Settings with { Concurrency = 1, AutoRetry = false });
        var fixture = new TorrentFixtureGateway();
        var bt = new TorrentService(store, settings, fixture, NullLogger<TorrentService>.Instance);
        var resources = await bt.Find(movie.Id, CancellationToken.None);
        check(resources.Item.Resources[0].Protocol == "MAGNET" && resources.Item.Resources[0].Quality.Contains("名称标注"), "magnet search persists resources while identifying unverified subtitle labels");
        var job = bt.Create(new(movie.Id, resources.Candidates[0].Id, null, null, null));
        check(job.Engine == "qbittorrent" && job.Directory == "/downloads/Movies" && job.InfoHash == hash && job.Status == "Queued", "BT creation persists a queued task and maps the configured root directory");
        var restored = new StateStore(Path.Combine(dir, "state"));
        check(restored.Job(job.Id)!.Engine == "qbittorrent" && restored.Job(job.Id)!.InfoHash == hash, "BT task engine and hash survive restart");
        var http = new DownloadService(store, new FixtureWeb(), NullLogger<DownloadService>.Instance);
        var queuedHttp = http.Create(new(null,null,null,"HTTP fixture","https://example.org/ok.mp4"));
        await bt.StartAsync(CancellationToken.None);
        async Task Wait(string id, string state)
        { var end = DateTime.UtcNow.AddSeconds(15); while(DateTime.UtcNow < end) { if(store.Job(id)?.Status == state) return; await Task.Delay(50); } throw new Exception("BT fixture timeout waiting for "+state+": "+store.Job(id)?.Error); }
        await Wait(job.Id,"Downloading"); await http.StartAsync(CancellationToken.None); await Task.Delay(400);
        check(store.Job(queuedHttp.Id)!.Status == "Queued" && fixture.Adds == 1, "HTTP and BT jobs share the configured concurrency limit");
        fixture.State = "uploading"; await Wait(job.Id,"Completed"); await Wait(queuedHttp.Id,"Completed");
        check(store.Job(job.Id)!.TotalBytes == 100 && store.Job(job.Id)!.BytesReceived == 100, "completed BT status reflects downloader-reported bytes");
        var cancel = bt.Create(new(null,null,null,"Cancel fixture","magnet:?xt=urn:btih:"+new string('d',40)));
        await Wait(cancel.Id,"Completed");
        var paused = bt.Create(new(null,null,null,"Paused fixture","magnet:?xt=urn:btih:"+new string('e',40)));
        fixture.State="downloading"; await Wait(paused.Id,"Downloading");
        await bt.Act(paused.Id,"pause",CancellationToken.None);
        check(store.Job(paused.Id)!.Status == "Paused" && fixture.Actions.Last()=="pause", "BT pause is sent to the downloader and reflected locally");
        await bt.Act(paused.Id,"cancel",CancellationToken.None); http.ClearFinished(bt.ActiveIds);
        check(store.Job(paused.Id) is null && fixture.Actions.Last()=="cancel", "cancelled BT records can be cleared after remote cancellation");
        fixture.FailAdd=true;
        var invalid=bt.Create(new(null,null,null,"Invalid response fixture","magnet:?xt=urn:btih:"+new string('f',40)));
        await Wait(invalid.Id,"Failed");
        fixture.FailAdd=false;fixture.State="uploading";await bt.Act(invalid.Id,"retry",CancellationToken.None);await Wait(invalid.Id,"Completed");
        check(store.Job(invalid.Id)!.Status=="Completed", "invalid downloader data produces a recoverable task failure without stopping the queue");
        await bt.StopAsync(CancellationToken.None); await http.StopAsync(CancellationToken.None); bt.Dispose(); http.Dispose();
        var latest = new MovieSearchService(new LatestFixtureWeb(), config, store, settings);
        var latestResult = await latest.Search(new(Source:"tmdb",Type:"movie",Mode:"latest"),CancellationToken.None);
        check(latestResult.Items.Select(x=>x.ReleaseDate).SequenceEqual(new[]{"2025-11-02","2025-10-01"}), "latest movie synchronization orders by full release date rather than rating");
        check(store.Catalogue.Items.Any(x=>x.Id==latestResult.Items[0].Id), "latest movie information is merged into the persisted catalogue");
        var controller = new TorrentController(new TorrentService(store,settings,fixture,NullLogger<TorrentService>.Instance), latest, settings);
        fixture.ReturnSubtitles=false;
        var response=await controller.Sync(new(Limit:1),CancellationToken.None);
        using var responseJson=JsonDocument.Parse(JsonSerializer.Serialize(response,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        check(responseJson.RootElement.GetProperty("outcomes")[0].GetProperty("status").GetString()=="Skipped", "batch mode skips releases without explicit 4K Chinese subtitle evidence");
        fixture.ReturnSubtitles=true;fixture.SearchHash=new string('9',40);
        var accepted=await controller.Sync(new(Limit:1),CancellationToken.None);
        using var acceptedJson=JsonDocument.Parse(JsonSerializer.Serialize(accepted,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var outcome=acceptedJson.RootElement.GetProperty("outcomes")[0];
        check(outcome.GetProperty("status").GetString()=="Queued" && store.Job(outcome.GetProperty("taskId").GetString()!)?.InfoHash==fixture.SearchHash, "batch mode queues the best matching 4K Chinese release as a real persisted BT task");
        var oversized=false;try{await controller.Sync(new(Limit:4),CancellationToken.None);}catch(ArgumentException){oversized=true;}
        check(oversized,"batch download request cannot exceed the three-movie limit");
    }
}

sealed class ResourceFixtureHandler : HttpMessageHandler
{
    public bool SawIndexKey, HasTorrent; public int LoginCount; public Dictionary<string,string> LastForm=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path=request.RequestUri!.AbsolutePath; string body;
        if(path.EndsWith("/auth/login")) { LoginCount++; body="Ok."; }
        else if(path.EndsWith("/torrents/add")||path.EndsWith("/torrents/delete"))
        { LastForm=(await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1].Replace('+',' '))); HasTorrent=path.EndsWith("/add"); body="Ok."; }
        else if(path.EndsWith("/torrents/info")) body=HasTorrent?"[{\"hash\":\"0123456789abcdef0123456789abcdef01234567\",\"name\":\"Fixture\",\"state\":\"downloading\",\"downloaded\":50,\"size\":100,\"dlspeed\":10,\"save_path\":\"/downloads/Movies\"}]":"[]";
        else if(path.EndsWith("/app/version")) body="v5.0.0";
        else if(path.EndsWith("/indexer")) body="[{\"enable\":true}]";
        else { SawIndexKey=request.Headers.Contains("X-Api-Key"); body="[{\"title\":\"Fixture.Movie.2025.2160p.CHS\",\"infoHash\":\"0123456789abcdef0123456789abcdef01234567\",\"seeders\":10,\"size\":1024,\"indexer\":\"Fixture\",\"protocol\":\"torrent\"}]"; }
        return new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    }
}
sealed class TorrentFixtureGateway : IResourceGateway
{
    public int Adds; public string State="downloading", SearchHash="0123456789abcdef0123456789abcdef01234567"; public bool ReturnSubtitles=true, FailAdd; public List<string> Actions=[]; private readonly List<string> hashes=[];
    public Task<TorrentCandidate[]> Search(MediaItem movie,CancellationToken ct) => Task.FromResult(new[]{TorrentReleaseRules.Candidate(movie,movie.OriginalTitle+"."+movie.Year+".2160p."+(ReturnSubtitles?"CHS":"Mandarin"),"magnet:?xt=urn:btih:"+SearchHash,10,100,"Fixture")});
    public Task Add(string magnet,string directory,CancellationToken ct){ if(FailAdd)throw new InvalidDataException("Fixture malformed downloader response");Adds++; hashes.Add(TorrentReleaseRules.ParseMagnet(magnet).Hash); return Task.CompletedTask; }
    public Task<TorrentSnapshot[]> Status(string[] selected,CancellationToken ct)=>Task.FromResult(hashes.Where(selected.Contains).Select(hash=>new TorrentSnapshot(hash,"Fixture",State,State=="uploading"?100:50,100,10,"/downloads/Movies")).ToArray());
    public Task Control(string hash,string action,CancellationToken ct){Actions.Add(action);if(action=="cancel")hashes.Remove(hash);return Task.CompletedTask;}
    public Task<SourceConnectionResult> Test(string provider,CancellationToken ct)=>Task.FromResult(new SourceConnectionResult(true,"Fixture"));
}
sealed class LatestFixtureWeb : IPublicWebClient
{
    public Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken ct,long? range=null,string? validator=null)
    {
        if(!uri.Query.Contains("sort_by=primary_release_date.desc")||!uri.Query.Contains("primary_release_date.lte="))throw new Exception("Missing latest release filters");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"results\":[{\"id\":91,\"title\":\"Older Fixture\",\"original_title\":\"Older Fixture\",\"release_date\":\"2025-10-01\",\"vote_average\":9.9,\"vote_count\":100,\"genre_ids\":[]},{\"id\":92,\"title\":\"New Fixture\",\"original_title\":\"New Fixture\",\"release_date\":\"2025-11-02\",\"vote_average\":6.0,\"vote_count\":100,\"genre_ids\":[]}]}",Encoding.UTF8,"application/json")});
    }
}
