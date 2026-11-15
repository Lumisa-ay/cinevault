using Microsoft.AspNetCore.Authorization;
using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public sealed class ApiController(StateStore store, CrawlerService crawler, DownloadService downloads, IConfiguration config, TorrentService torrents) : ControllerBase
{
    [HttpGet("health")] public object Health() => new { application = "CineVault", version = "1.0", framework = ".NET / ASP.NET Core MVC", respectRobotsTxt = config.GetValue("Crawler:RespectRobotsTxt", true) };
    [HttpGet("media")] public Catalogue Media() => store.Catalogue;
    [HttpGet("settings")] public AppSettings Settings() => store.Settings;
    [HttpGet("defaults")] public AppSettings Defaults() => store.Defaults;
    [HttpPut("settings")] public AppSettings Settings(AppSettings settings) => store.SetSettings(settings);
    [HttpPost("directory/check")] public object CheckDirectory(DirectoryRequest request) => new { directory = store.CheckWritable(request.Directory) };
    [HttpPost("crawl")]
    public async Task<Catalogue> Crawl(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(85));
        return await crawler.Crawl(timeout.Token);
    }
    [HttpGet("downloads")] public DownloadJob[] Downloads() => store.Jobs();
    [HttpPost("downloads")] public DownloadJob Create(CreateDownload request)
    {
        var magnet = request.Url?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true || store.Catalogue.Items.FirstOrDefault(x => x.Id == request.MediaId)?.Resources.FirstOrDefault(x => x.Id == request.ResourceId)?.Protocol == "MAGNET";
        return magnet ? torrents.Create(request) : downloads.Create(request);
    }
    [HttpPost("downloads/{id}/{action}")] public Task<DownloadJob> Action(string id, string action, CancellationToken ct) => store.Job(id)?.Engine == "qbittorrent" ? torrents.Act(id, action, ct) : Task.FromResult(downloads.Act(id, action));
    [HttpDelete("downloads/finished")] public object ClearFinished() { downloads.ClearFinished(torrents.ActiveIds); return new { cleared = true }; }
}
