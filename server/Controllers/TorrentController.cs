using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[ApiController, Authorize, Route("api")]
public sealed class TorrentController(TorrentService torrents, MovieSearchService search, SourceSettingsStore settings) : ControllerBase
{
    private static readonly SemaphoreSlim BatchGate = new(1, 1);
    [HttpPost("media/{id}/magnets")]
    public async Task<TorrentSearchResult> Find(string id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { return await torrents.Find(id, timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException or InvalidDataException or InvalidOperationException)
        { throw new ArgumentException("资源引擎查询失败，请检查 Knaben 网络连接或稍后重试。"); }
    }
    [HttpPost("sync/latest/download")]
    public async Task<object> Sync(LatestDownloadRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 or > 3) throw new ArgumentException("每次批量下载最多检查 3 部电影。");
        var c = settings.Current;
        if (c.QbitUrl.Length == 0 || c.QbitPassword.Length == 0)
            throw new ArgumentException("请先在“来源与密钥”配置 qBittorrent。");
        if (!await BatchGate.WaitAsync(0, ct)) throw new ArgumentException("已有批量匹配任务正在执行，请稍后重试。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(150));
            var catalogue = await search.Search(new(Source: "tmdb", Type: "movie", MinRating: request.MinRating, Page: request.Page, Mode: "latest"), timeout.Token);
            var outcomes = new List<LatestDownloadOutcome>();
            var updated = new Dictionary<string,MediaItem>();
            foreach (var movie in catalogue.Items.Take(request.Limit))
            {
                try
                {
                    var found = await torrents.Find(movie.Id, timeout.Token);
                    updated[movie.Id] = found.Item;
                    var best = found.Candidates.FirstOrDefault(x => x.MatchesMovie && x.Is4K && x.HasChineseSubtitles && x.Seeders > 0);
                    if (best is null) { outcomes.Add(new(movie.Id, movie.Title, "Skipped", "没有找到片名/年份匹配、4K 中字且有做种者的资源。")); continue; }
                    var job = torrents.Create(new(movie.Id, best.Id, null, null, null));
                    outcomes.Add(new(movie.Id, movie.Title, "Queued", "已加入 BT 下载队列；字幕依据资源名称标注。", job.Id));
                }
                catch (Exception ex) when (ex is ArgumentException or HttpRequestException or IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
                { outcomes.Add(new(movie.Id, movie.Title, "Failed", "资源查询或建任务失败，请检查索引、重复任务及下载器配置。")); }
            }
            return new { catalogue = catalogue with { Items = catalogue.Items.Select(x => updated.GetValueOrDefault(x.Id, x)).ToArray() }, outcomes };
        }
        finally { BatchGate.Release(); }
    }
}
