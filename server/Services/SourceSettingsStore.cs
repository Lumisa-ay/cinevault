using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using CineVault.Models;

namespace CineVault.Services;

public sealed class SourceSettingsStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IConfiguration config;
    private readonly IDataProtector protector;
    private SourceCredentials saved;
    public SourceSettingsStore(string dataRoot, IConfiguration config, IDataProtectionProvider protection)
    {
        this.config = config;
        Directory.CreateDirectory(dataRoot);
        path = Path.Combine(dataRoot, "source-settings.protected");
        protector = protection.CreateProtector("CineVault.SourceSettings.v1");
        saved = File.Exists(path) ? JsonSerializer.Deserialize<SourceCredentials>(protector.Unprotect(File.ReadAllText(path)))
            ?? throw new InvalidDataException("来源配置无法读取，请恢复配置与加密密钥的备份。") : new();
    }
    public SourceCredentials Current
    {
        get
        {
            lock (gate)
            {
                var result = saved.Snapshot();
                if (!result.TmdbOverride) { result.TmdbType = "api_key"; result.TmdbCredential = SourceCredentials.Environment(config).TmdbCredential; }
                return result;
            }
        }
    }
    public SourceConfigurationView View()
    {
        var c = Current;
        return new(c.TmdbType, c.TmdbCredential.Length > 0, !c.TmdbOverride && c.TmdbCredential.Length > 0,
            c.ImdbMode, c.ImdbApiKey.Length > 0, c.OmdbApiKey.Length > 0, c.AwsAccessKeyId.Length > 0, c.AwsSecretAccessKey.Length > 0,
            c.AwsSessionToken.Length > 0, c.DataSetId, c.RevisionId, c.AssetId, c.OfficialReady,
            c.ResourceIndexUrl, c.ResourceIndexKey.Length > 0, c.QbitUrl, c.QbitUserName, c.QbitPassword.Length > 0, c.QbitSaveRoot);
    }
    private static string Secret(string? input, string previous, string label)
    {
        if (string.IsNullOrWhiteSpace(input)) return previous;
        var value = input.Trim();
        if (value.Length > 8192 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            throw new ArgumentException(label + " 格式不正确，请只填写凭据本身，不含空格或换行。");
        return value;
    }
    private static string Id(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("订阅标识仅支持 128 字以内的字母、数字、短横线或下划线。");
        return value;
    }
    private void Persist(SourceCredentials next)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(next)));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(20 * (attempt + 1)); }
            }
            saved = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void SaveTmdb(TmdbSettingsInput input)
    {
        if (input.CredentialType is not ("api_key" or "bearer")) throw new ArgumentException("请选择 TMDB API Key 或 Read Access Token。");
        lock (gate)
        {
            var next = saved.Snapshot(); var previous = Current;
            var raw = input.Credential?.Trim();
            if (input.CredentialType == "bearer" && raw?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) raw = raw[7..].Trim();
            if (!input.ClearCredential && input.CredentialType != previous.TmdbType && previous.TmdbCredential.Length > 0 && string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("更换 TMDB 凭据类型时，请同时填写新凭据。");
            next.TmdbOverride = true; next.TmdbType = input.CredentialType;
            next.TmdbCredential = input.ClearCredential ? "" : Secret(raw, previous.TmdbCredential, "TMDB 密钥");
            Persist(next);
        }
    }
    public void SaveImdb(ImdbSettingsInput input)
    {
        if (input.Mode is not ("dataset" or "official" or "omdb")) throw new ArgumentException("请选择有效的 IMDb 接入方式。");
        lock (gate)
        {
            var next = saved.Snapshot(); next.ImdbMode = input.Mode;
            if (input.ClearCredentials)
            { next.ImdbApiKey = next.OmdbApiKey = next.AwsAccessKeyId = next.AwsSecretAccessKey = next.AwsSessionToken = ""; }
            else
            {
                next.ImdbApiKey = Secret(input.ApiKey, next.ImdbApiKey, "IMDb API Key");
                next.OmdbApiKey = Secret(input.OmdbApiKey, next.OmdbApiKey, "OMDb API Key");
                next.AwsAccessKeyId = Secret(input.AwsAccessKeyId, next.AwsAccessKeyId, "AWS Access Key ID");
                next.AwsSecretAccessKey = Secret(input.AwsSecretAccessKey, next.AwsSecretAccessKey, "AWS Secret Access Key");
                next.AwsSessionToken = input.ClearSessionToken ? "" : Secret(input.AwsSessionToken, next.AwsSessionToken, "AWS Session Token");
            }
            next.DataSetId = Id(input.DataSetId); next.RevisionId = Id(input.RevisionId); next.AssetId = Id(input.AssetId);
            Persist(next);
        }
    }
    public static string ServiceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Length > 512 || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Port is 2375 or 2376 || uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("服务地址需要不含账户、查询参数的 HTTP/HTTPS 地址。");
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && (ip.IsIPv6LinkLocal || ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
            throw new ArgumentException("服务地址不可使用链路本地或云元数据地址。");
        return uri.AbsoluteUri;
    }
    public void SaveBt(BtSettingsInput input)
    {
        var indexUrl = input.ResourceIndexUrl is null ? null : ServiceUrl(input.ResourceIndexUrl); var qbitUrl = ServiceUrl(input.QbitUrl);
        var root = input.QbitSaveRoot?.Trim().Replace('\\', '/') ?? "";
        if (root.Length is 0 or > 500 || !(root.StartsWith('/') || System.Text.RegularExpressions.Regex.IsMatch(root, "^[A-Za-z]:/")) || root.Split('/').Any(x => x is "." or "..") || root.Any(char.IsControl))
            throw new ArgumentException("请填写 BT 下载器的绝对保存根目录，路径不能包含 . 或 ..。");
        var user = input.QbitUserName?.Trim() ?? "";
        if (user.Length > 120 || user.Any(char.IsControl)) throw new ArgumentException("下载器用户名格式不正确。");
        if (input.QbitPassword?.Length > 512 || input.QbitPassword?.Any(char.IsControl) == true) throw new ArgumentException("下载器密码格式不正确。");
        lock (gate)
        {
            var next = saved.Snapshot(); if (indexUrl is not null) next.ResourceIndexUrl = indexUrl; next.QbitUrl = qbitUrl; next.QbitUserName = user; next.QbitSaveRoot = root;
            next.ResourceIndexKey = input.ClearResourceIndexKey ? "" : Secret(input.ResourceIndexKey, next.ResourceIndexKey, "Prowlarr API Key");
            next.QbitPassword = input.ClearQbitPassword ? "" : string.IsNullOrEmpty(input.QbitPassword) ? next.QbitPassword : input.QbitPassword;
            Persist(next);
        }
    }
}
