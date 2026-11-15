using System.Collections.Concurrent;
using CineVault.Models;

namespace CineVault.Services;

public sealed class TorrentService(StateStore store, SourceSettingsStore settings, IResourceGateway gateway, ILogger<TorrentService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> active = new();
    public ISet<string> ActiveIds => active.Keys.ToHashSet();
    public async Task<TorrentSearchResult> Find(string mediaId, CancellationToken ct)
    {
        var movie = store.Catalogue.Items.FirstOrDefault(x => x.Id == mediaId) ?? throw new KeyNotFoundException("请先同步或检索该电影。");
        var candidates = await gateway.Search(movie, ct);
        var links = candidates.Select(x => new ResourceLink(x.Id, x.Title, x.Magnet,
            (x.Is4K ? "4K" : "其他画质") + (x.HasChineseSubtitles ? " · 中字（名称标注）" : " · 字幕未知") + (!x.MatchesMovie ? " · 片名/年份待核对" : ""), "MAGNET", Math.Round(x.Size / 1073741824d, 2) + " GB")).ToArray();
        var updated = movie with { Resources = movie.Resources.Where(x => x.Protocol != "MAGNET").Concat(links).ToArray(), Qualities = movie.Qualities.Concat(candidates.Any(x => x.Is4K) ? new[] { "4K" } : Array.Empty<string>()).Distinct().ToArray() };
        store.MergeMedia([updated]);
        return new(updated, candidates, ["画质与字幕根据资源名称识别，尚未检查实际文件；做种数来自索引记录，可能不准确或过期，请核对原片名和年份。"]);
    }
    public DownloadJob Create(CreateDownload request)
    {
        var c = settings.Current;
        if (c.QbitUrl.Length == 0 || c.QbitPassword.Length == 0) throw new ArgumentException("请管理员先配置 qBittorrent Web UI，才能下载磁力资源。");
        var title = request.Title; var magnet = request.Url; var quality = "磁力链接"; var type = "movie";
        if (request.MediaId is { Length: > 0 })
        {
            var movie = store.Catalogue.Items.FirstOrDefault(x => x.Id == request.MediaId) ?? throw new KeyNotFoundException("影片不存在。");
            var resource = movie.Resources.FirstOrDefault(x => x.Id == request.ResourceId && x.Protocol == "MAGNET") ?? throw new ArgumentException("请先检索并选择磁力资源。");
            title = movie.Title; magnet = resource.Url; quality = resource.Quality; type = movie.Type;
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120) throw new ArgumentException("下载名称需要 1–120 个字符。");
        var parsed = TorrentReleaseRules.ParseMagnet(magnet ?? "");
        if (store.Jobs().Any(x => x.Engine == "qbittorrent" && x.InfoHash == parsed.Hash && x.Status != "Cancelled")) throw new ArgumentException("该磁力资源已在任务列表中。");
        var localDirectory = store.ValidateDirectory(request.Directory ?? store.Settings.Directory);
        if (store.Settings.Organize) localDirectory = Path.Combine(localDirectory, type == "tv" ? "Series" : "Movies");
        var relative = Path.GetRelativePath(store.Defaults.Directory, localDirectory).Replace('\\', '/');
        if (relative.Split('/').Any(x => x == "..") || Path.IsPathFullyQualified(relative)) throw new ArgumentException("BT 保存目录必须位于默认下载根目录内。");
        var remote = c.QbitSaveRoot.TrimEnd('/') + (relative == "." ? "" : "/" + relative);
        if (remote.Length == 0) remote = "/";
        return store.Add(new DownloadJob { Engine = "qbittorrent", InfoHash = parsed.Hash, MediaId = request.MediaId, Title = title.Trim(), Url = parsed.Magnet,
            Directory = remote, Quality = quality, AutoRetry = store.Settings.AutoRetry, MaxRetries = store.Settings.MaxRetries });
    }
    public async Task<DownloadJob> Act(string id, string action, CancellationToken ct)
    {
        var job = store.Job(id) ?? throw new KeyNotFoundException("任务不存在。");
        var next = action switch { "pause" when job.Status is "Queued" or "Downloading" or "Retrying" => "Paused", "resume" when job.Status == "Paused" => "Queued", "retry" when job.Status is "Failed" or "Cancelled" => "Queued", "cancel" when job.Status is not ("Completed" or "Cancelled") => "Cancelled", _ => throw new ArgumentException("当前状态不支持此操作。") };
        if (job.RemoteSubmitted && job.InfoHash is { } hash) await gateway.Control(hash, action, ct);
        return store.Change(id, x => { x.Status = next; x.Speed = 0; x.Error = null; x.RetryAt = null; if (action == "cancel") x.RemoteSubmitted = false; if (action == "retry") x.Attempts = 0; });
    }
    private async Task Submit(DownloadJob job, CancellationToken ct)
    {
        if (!active.TryAdd(job.Id, 0)) return;
        try
        {
            if (!store.TryStartDownload(job.Id)) return;
            await gateway.Add(job.Url, job.Directory, ct);
            var current = store.Change(job.Id, x => { x.RemoteSubmitted = true; if (x.Status is "Queued" or "Retrying") x.Status = "Downloading"; x.Error = null; });
            if (current.Status is "Cancelled" or "Paused") await gateway.Control(job.InfoHash!, current.Status == "Cancelled" ? "cancel" : "pause", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or ArgumentException or KeyNotFoundException or OperationCanceledException)
        {
            if (store.Job(job.Id) is null) return;
            store.Change(job.Id, x =>
            {
                if (x.Status is "Cancelled" or "Paused") return;
                var retry = ex is not ArgumentException && x.AutoRetry && x.Attempts < x.MaxRetries;
                x.Status = retry ? "Retrying" : "Failed"; x.Speed = 0; x.Error = ex is ArgumentException ? ex.Message : "提交磁力任务失败，请检查下载器连接与保存目录。";
                if (retry) { x.Attempts++; x.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, x.Attempts) * 3)); }
            });
        }
        finally { active.TryRemove(job.Id, out _); }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var job in store.Jobs().Where(x => x.Engine == "qbittorrent" && !x.RemoteSubmitted && (x.Status == "Queued" || x.Status == "Retrying" && x.RetryAt <= DateTimeOffset.UtcNow)))
                { if (store.Jobs().Count(x => x.Status == "Downloading") >= store.Settings.Concurrency) break; await Submit(job, ct); }
                var jobs = store.Jobs().Where(x => x.Engine == "qbittorrent" && x.RemoteSubmitted && x.Status is "Queued" or "Downloading" or "Paused").ToArray();
                if (jobs.Length > 0)
                {
                    try
                    {
                        var states = await gateway.Status(jobs.Select(x => x.InfoHash!).ToArray(), ct);
                        foreach (var missing in jobs.Where(x => x.CreatedAt < DateTimeOffset.UtcNow.AddSeconds(-30) && !states.Any(s => s.Hash.Equals(x.InfoHash, StringComparison.OrdinalIgnoreCase))))
                            if (store.Job(missing.Id) is not null) store.Change(missing.Id, x => { if (x.Status != "Cancelled") { x.Status = "Failed"; x.RemoteSubmitted = false; x.Error = "下载器中找不到该任务，可能已被外部移除；可手动重试。"; } });
                        foreach (var state in states)
                        {
                            var job = jobs.FirstOrDefault(x => string.Equals(x.InfoHash, state.Hash, StringComparison.OrdinalIgnoreCase));
                            if (job is null || store.Job(job.Id) is null) continue;
                            store.Change(job.Id, x =>
                            {
                                if (x.Status == "Cancelled") return;
                                x.BytesReceived = state.Downloaded; x.TotalBytes = state.Size > 0 ? state.Size : null; x.Speed = Math.Max(0, state.Speed);
                                x.Status = state.State switch { "error" or "missingFiles" => "Failed", "pausedDL" or "stoppedDL" => "Paused", "queuedDL" => "Queued", "uploading" or "stalledUP" or "forcedUP" or "pausedUP" or "stoppedUP" or "queuedUP" or "checkingUP" => "Completed", _ => "Downloading" };
                                if (x.Status == "Completed" && x.TotalBytes is > 0) { x.BytesReceived = x.TotalBytes.Value; x.Speed = 0; }
                                x.Error = x.Status == "Failed" ? "下载器报告文件或磁盘错误，请检查 qBittorrent。" : state.State == "stalledDL" ? "等待可连接的做种者。" : null;
                            });
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or OperationCanceledException)
                    {
                        foreach (var job in jobs.Where(x => store.Job(x.Id) is not null)) store.Change(job.Id, x => { if (ex is ArgumentException) x.Status = "Failed"; x.Speed = 0; x.Error = "下载器暂时无法连接；任务记录保留，请检查 Web UI 账户和网络。"; });
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or HttpRequestException)
            { logger.LogWarning("BT task refresh failed: {Type}", ex.GetType().Name); await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        }
    }
}
