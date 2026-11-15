using System.Text.Json;
using CineVault.Models;

namespace CineVault.Services;

public sealed class StateStore
{
    private readonly object gate = new();
    private readonly string dataRoot;
    private readonly string defaultRoot;
    private readonly string[] allowedRoots;
    private readonly List<DownloadJob> jobs;
    private AppSettings settings;
    private Catalogue catalogue;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public StateStore(string dataRoot, IEnumerable<string>? allowedDownloadRoots = null, string? defaultDownloadRoot = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        System.IO.Directory.CreateDirectory(this.dataRoot);
        defaultRoot = Path.GetFullPath(defaultDownloadRoot ?? Path.Combine(this.dataRoot, "downloads"));
        allowedRoots = new[] { defaultRoot }.Concat(allowedDownloadRoots ?? []).Select(Path.GetFullPath).Distinct().ToArray();
        settings = Read<AppSettings>("settings.json") ?? new(defaultRoot);
        try { settings = ValidateSettings(settings); } catch { settings = new(defaultRoot); }
        catalogue = Read<Catalogue>("catalogue.json") ?? new([], null);
        jobs = Read<List<DownloadJob>>("tasks.json") ?? [];
        foreach (var j in jobs)
        {
            if (j.Engine == "http" && j.Status is "Downloading" or "Retrying") j.Status = "Queued";
            j.Speed = 0;
        }
        Write("tasks.json", jobs);
    }
    private T? Read<T>(string name)
    {
        var path = Path.Combine(dataRoot, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidDataException($"数据文件损坏，请从备份恢复：{name}", ex); }
    }
    private void Write<T>(string name, T value)
    {
        var path = Path.Combine(dataRoot, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public AppSettings Settings { get { lock (gate) return settings; } }
    public AppSettings Defaults => new(defaultRoot);
    public Catalogue Catalogue { get { lock (gate) return catalogue; } }
    public void SetCatalogue(Catalogue value) { lock (gate) { Write("catalogue.json", value); catalogue = value; } }
    public void MergeMedia(IEnumerable<MediaItem> items)
    {
        lock (gate)
        {
            var incoming = items.ToArray();
            if (incoming.Length == 0) return;
            var ids = incoming.Select(x => x.Id).ToHashSet();
            // Keep resolved download links when a search returns the same item again.
            incoming = incoming.Select(x => x.Resources.Length == 0 && catalogue.Items.FirstOrDefault(old => old.Id == x.Id) is { Resources.Length: > 0 } old
                ? x with { Resources = old.Resources, Qualities = old.Qualities } : x).ToArray();
            var next = new Catalogue(incoming.Concat(catalogue.Items.Where(x => !ids.Contains(x.Id))).Take(1000).ToArray(), DateTimeOffset.UtcNow);
            Write("catalogue.json", next); catalogue = next;
        }
    }
    public AppSettings ValidateSettings(AppSettings value)
    {
        if (value.MaxRetries is < 0 or > 5 || value.Concurrency is < 1 or > 4 || value.MinRating is < 0 or > 10 || !double.IsFinite(value.MinRating))
            throw new ArgumentException("评分范围为 0–10，并发数为 1–4，重试次数为 0–5。");
        return value with { Directory = ValidateDirectory(value.Directory) };
    }
    public AppSettings SetSettings(AppSettings value)
    {
        value = ValidateSettings(value);
        CheckWritable(value.Directory);
        lock (gate) { Write("settings.json", value); settings = value; return settings; }
    }
    public string ValidateDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("请输入服务电脑上的绝对目录路径。");
        if (value.StartsWith(@"\\") || value.StartsWith("//")) throw new ArgumentException("不允许网络共享目录。");
        var full = Path.GetFullPath(value);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!allowedRoots.Any(root => full.Equals(root, cmp) || full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, cmp)))
            throw new ArgumentException("目录不在允许的保存范围。请先在 appsettings.json 的 AllowedDownloadRoots 中配置下载根目录，然后重启服务。");
        // Reject symlinks and junctions in every existing ancestor, including the configured root.
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("保存路径不可经过符号链接或目录联接。");
        return full;
    }
    public string CheckWritable(string value)
    {
        var full = ValidateDirectory(value);
        System.IO.Directory.CreateDirectory(full);
        full = ValidateDirectory(full);
        var probe = Path.Combine(full, ".cinevault-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return full;
    }
    public DownloadJob[] Jobs() { lock (gate) return jobs.Select(x => x.Snapshot()).OrderByDescending(x => x.CreatedAt).ToArray(); }
    public DownloadJob? Job(string id) { lock (gate) return jobs.FirstOrDefault(x => x.Id == id)?.Snapshot(); }
    public DownloadJob Add(DownloadJob job)
    {
        lock (gate)
        {
            if (jobs.Any(x => x.Url == job.Url && x.Directory == job.Directory && x.Status is "Queued" or "Downloading" or "Paused" or "Retrying"))
                throw new ArgumentException("同一个目录中已经有该资源的活动任务。");
            jobs.Add(job.Snapshot()); Write("tasks.json", jobs); return job.Snapshot();
        }
    }
    public DownloadJob Change(string id, Action<DownloadJob> update, bool persist = true)
    {
        lock (gate)
        {
            var job = jobs.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("任务不存在。");
            update(job);
            if (persist) Write("tasks.json", jobs);
            return job.Snapshot();
        }
    }
    public void PersistJobs() { lock (gate) Write("tasks.json", jobs); }
    public bool TryStartDownload(string id)
    {
        lock (gate)
        {
            var job = jobs.FirstOrDefault(x => x.Id == id);
            if (job is null || job.Status is not ("Queued" or "Retrying") || jobs.Count(x => x.Status == "Downloading") >= settings.Concurrency) return false;
            job.Status = "Downloading"; job.Error = null; Write("tasks.json", jobs); return true;
        }
    }
    public void ClearFinished(ISet<string>? activeIds = null) { lock (gate) { jobs.RemoveAll(x => (x.Status is "Completed" or "Cancelled") && !(activeIds?.Contains(x.Id) ?? false)); Write("tasks.json", jobs); } }
    public string SafeFilePath(DownloadJob job, bool partial)
    {
        var directory = ValidateDirectory(job.Directory);
        if (string.IsNullOrWhiteSpace(job.FileName) || Path.GetFileName(job.FileName) != job.FileName || job.FileName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("文件名不合法。");
        var path = Path.Combine(directory, job.FileName + (partial ? ".part" : ""));
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("文件不能是符号链接。");
        return path;
    }
}
