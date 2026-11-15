using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public sealed class DownloadService(StateStore store, IPublicWebClient web, ILogger<DownloadService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    public DownloadJob Create(CreateDownload request)
    {
        var settings = store.Settings;
        var title = request.Title; var url = request.Url; var quality = "HTTP"; var type = "movie";
        if (!string.IsNullOrEmpty(request.MediaId))
        {
            var item = store.Catalogue.Items.FirstOrDefault(x => x.Id == request.MediaId) ?? throw new ArgumentException("内容已不存在，请刷新资源。");
            var resource = item.Resources.FirstOrDefault(x => x.Id == request.ResourceId) ?? throw new ArgumentException("资源已不存在，请重新选择。");
            if (resource.Protocol is not ("HTTP" or "HTTPS")) throw new ArgumentException("磁力资源需要通过 BT 下载器提交。");
            title = item.Title; url = resource.Url; quality = resource.Quality; type = item.Type;
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120) throw new ArgumentException("资源名称长度应为 1–120 个字符。");
        var uri = PublicWebClient.ValidateUri(url ?? "");
        var directory = store.ValidateDirectory(request.Directory ?? settings.Directory);
        if (settings.Organize) directory = Path.Combine(directory, type == "tv" ? "Series" : "Movies");
        directory = store.CheckWritable(directory);
        var job = new DownloadJob { Title = title.Trim(), MediaId = request.MediaId, Url = uri.AbsoluteUri, Quality = quality,
            Directory = directory, AutoRetry = settings.AutoRetry, MaxRetries = settings.MaxRetries };
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (!new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".zip", ".7z", ".torrent", ".txt", ".pdf" }.Contains(extension)) extension = ".bin";
        var safeTitle = Regex.Replace(title, @"[^\p{L}\p{N} _.-]", "_", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim(' ', '.');
        if (safeTitle.Length > 60) safeTitle = safeTitle[..60];
        // Start with a stable prefix to avoid reserved device names on Windows; include a unique ID to avoid overwrites.
        job.FileName = "CV_" + safeTitle + "_" + job.Id[..8] + extension;
        return store.Add(job);
    }
    public void ClearFinished(ISet<string>? otherActive = null) => store.ClearFinished(active.Keys.Concat(otherActive ?? new HashSet<string>()).ToHashSet());
    public DownloadJob Act(string id, string action)
    {
        var result = store.Change(id, j =>
        {
            switch (action)
            {
                case "pause" when j.Status is "Queued" or "Downloading" or "Retrying": j.Status = "Paused"; break;
                case "resume" when j.Status == "Paused": j.Status = "Queued"; j.Error = null; break;
                case "retry" when j.Status is "Failed" or "Cancelled": j.Status = "Queued"; j.Attempts = 0; j.Error = null; j.RetryAt = null; break;
                case "cancel" when j.Status is not ("Completed" or "Cancelled"): j.Status = "Cancelled"; j.Error = null; break;
                default: throw new ArgumentException("当前任务状态不支持此操作。");
            }
            j.Speed = 0;
        });
        if (action is "pause" or "cancel" && active.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        return result;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                workers.RemoveAll(t => t.IsCompleted);
                foreach (var j in store.Jobs().Where(j => j.Engine == "http" && j.Status == "Retrying" && j.RetryAt <= DateTimeOffset.UtcNow))
                    store.Change(j.Id, x => { if (x.Status == "Retrying") { x.Status = "Queued"; x.Error = null; } });
                foreach (var job in store.Jobs().Where(j => j.Engine == "http" && j.Status == "Queued").OrderBy(j => j.CreatedAt))
                {
                    if (active.Count >= store.Settings.Concurrency) break;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    if (!active.TryAdd(job.Id, cts)) { cts.Dispose(); continue; }
                    workers.Add(Task.Run(async () =>
                    {
                        try { await RunOne(job.Id, cts.Token, stoppingToken); }
                        finally { active.TryRemove(job.Id, out _); cts.Dispose(); }
                    }, CancellationToken.None));
                }
                await Task.Delay(300, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var cts in active.Values) cts.Cancel();
            await Task.WhenAll(workers);
            store.PersistJobs();
        }
    }
    private async Task RunOne(string id, CancellationToken ct, CancellationToken stoppingToken)
    {
        if (!store.TryStartDownload(id)) return;
        var start = store.Job(id)!;
        if (start.Status != "Downloading") return;
        try
        {
            ct.ThrowIfCancellationRequested();
            var partialPath = store.SafeFilePath(start, true);
            var targetPath = store.SafeFilePath(start, false);
            var offset = File.Exists(partialPath) && !string.IsNullOrEmpty(start.Validator) ? new FileInfo(partialPath).Length : 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await web.SendAsync(PublicWebClient.ValidateUri(start.Url), timeout.Token, offset, start.Validator);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is "text/html" or "application/xhtml+xml" or "application/json") throw new InvalidDataException("链接返回网页或 JSON，而不是下载文件。请检查资源地址。");
            var isPartial = response.StatusCode == HttpStatusCode.PartialContent;
            if (isPartial && (offset == 0 || response.Content.Headers.ContentRange?.From != offset)) throw new InvalidDataException("服务器返回的断点范围不匹配，无法安全续传。");
            if (!isPartial) offset = 0;
            var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength.HasValue ? offset + response.Content.Headers.ContentLength.Value : null);
            var validator = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : response.Content.Headers.LastModified?.ToString("R");
            store.Change(id, j => { j.BytesReceived = offset; j.TotalBytes = total; j.Validator = validator; });
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var file = new FileStream(partialPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; var downloaded = offset; var measured = offset; var watch = Stopwatch.StartNew();
                while (true)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token); downloaded += count;
                    if (total.HasValue && downloaded > total) throw new InvalidDataException("返回文件超过声明长度。");
                    if (watch.ElapsedMilliseconds >= 600)
                    {
                        var speed = (long)((downloaded - measured) / watch.Elapsed.TotalSeconds); measured = downloaded; watch.Restart();
                        store.Change(id, j => { j.BytesReceived = downloaded; j.Speed = speed; });
                    }
                }
                await file.FlushAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (total.HasValue && downloaded != total) throw new InvalidDataException("文件传输未完整结束，将尝试重试。");
                store.Change(id, j => { j.BytesReceived = downloaded; j.TotalBytes ??= downloaded; });
            }
            // Validate again immediately before the final move; never overwrite another completed file.
            partialPath = store.SafeFilePath(start, true); targetPath = store.SafeFilePath(start, false);
            store.Change(id, j =>
            {
                if (j.Status != "Downloading") return;
                ct.ThrowIfCancellationRequested();
                File.Move(partialPath, targetPath, false);
                j.Status = "Completed"; j.Error = null; j.Speed = 0;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            store.Change(id, j => { if (j.Status == "Downloading") j.Status = stoppingToken.IsCancellationRequested ? "Queued" : "Paused"; j.Speed = 0; });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            logger.LogWarning("Download {Id} failed: {Message}", id, ex.Message);
            store.Change(id, j =>
            {
                if (j.Status != "Downloading") return;
                j.Error = ex is OperationCanceledException ? "连接或传输超过 30 秒未响应。" : ex.Message;
                j.Speed = 0;
                if (j.AutoRetry && j.Attempts < j.MaxRetries)
                {
                    j.Attempts++; j.Status = "Retrying";
                    j.RetryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, j.Attempts)));
                }
                else j.Status = "Failed";
            });
        }
    }
}
