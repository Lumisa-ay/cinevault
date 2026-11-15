using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineVault.Models;

namespace CineVault.Services;

public sealed class SourceApiClient(IPublicWebClient web)
{
    public async Task<JsonDocument> Tmdb(string path, SourceCredentials credentials, CancellationToken ct)
    {
        var url = "https://api.themoviedb.org/3/" + path;
        if (credentials.TmdbCredential.Length == 0) throw new ArgumentException("请先保存 TMDB 密钥。");
        if (credentials.TmdbType == "bearer")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", credentials.TmdbCredential);
            using var response = await web.SendApiAsync(request, ct);
            return await ReadJson(response, ct);
        }
        url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(credentials.TmdbCredential);
        using var keyResponse = await web.SendAsync(new Uri(url), ct);
        return await ReadJson(keyResponse, ct);
    }

    public async Task<JsonDocument> Omdb(string query, SourceCredentials credentials, CancellationToken ct)
    {
        if (credentials.OmdbApiKey.Length == 0) throw new ArgumentException("请先保存 OMDb API Key。");
        var url = "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(credentials.OmdbApiKey) + "&r=json&" + query;
        using var response = await web.SendAsync(new Uri(url), ct);
        var doc = await ReadJson(response, ct);
        if (doc.RootElement.TryGetProperty("Response", out var valid) && valid.GetString() == "False")
        {
            var error = doc.RootElement.TryGetProperty("Error", out var e) ? e.GetString() ?? "" : "";
            if (error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return doc;
            doc.Dispose();
            throw new InvalidDataException(error.Contains("key", StringComparison.OrdinalIgnoreCase) ? "OMDb 密钥无效或尚未激活，请核对密钥签发平台。" : "OMDb 拒绝查询，请检查账户额度与服务状态。");
        }
        return doc;
    }

    public async Task<JsonDocument> Imdb(string id, SourceCredentials credentials, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^tt[0-9]{7,10}$", RegexOptions.CultureInvariant)) throw new ArgumentException("IMDb 官方 API 当前按编号查询，请输入 tt0133093 这样的 IMDb 编号。");
        if (!credentials.OfficialReady) throw new ArgumentException("IMDb 官方 API 需要 API Key、AWS 凭据以及 Data Set / Revision / Asset 三个订阅标识。");
        var body = JsonSerializer.Serialize(new { query = "{ title(id: \"" + id + "\") { titleText { text } ratingsSummary { aggregateRating voteCount } } }" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-fulfill.dataexchange.us-east-1.amazonaws.com/v1") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-api-key", credentials.ImdbApiKey);
        request.Headers.Add("x-amzn-dataexchange-data-set-id", credentials.DataSetId);
        request.Headers.Add("x-amzn-dataexchange-revision-id", credentials.RevisionId);
        request.Headers.Add("x-amzn-dataexchange-asset-id", credentials.AssetId);
        AwsV4.Sign(request, body, credentials.AwsAccessKeyId, credentials.AwsSecretAccessKey, credentials.AwsSessionToken, "us-east-1", "dataexchange", DateTimeOffset.UtcNow);
        using var response = await web.SendApiAsync(request, ct);
        var doc = await ReadJson(response, ct);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        { doc.Dispose(); throw new InvalidDataException("IMDb API 返回查询错误，请核对订阅权限、产品字段与凭据。"); }
        return doc;
    }

    public static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        const int max = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("来源响应超过 8 MB。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > max) throw new InvalidDataException("来源响应超过 8 MB。");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return JsonDocument.Parse(output.ToArray());
    }
}

public static class AwsV4
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static string Hash(string value) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    public static void Sign(HttpRequestMessage request, string body, string accessKey, string secretKey, string sessionToken, string region, string service, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        request.Headers.Host = uri.Host;
        request.Headers.Add("x-amz-date", timestamp);
        if (sessionToken.Length > 0) request.Headers.Add("x-amz-security-token", sessionToken);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in request.Headers) headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value).Trim();
        if (request.Content?.Headers.ContentType is { } contentType) headers["content-type"] = contentType.ToString();
        var signed = string.Join(";", headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(x => x.Key + ":" + Regex.Replace(x.Value, @"\s+", " ") + "\n"));
        var query = string.Join("&", uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
            .Select(x => (Key: Uri.EscapeDataString(Uri.UnescapeDataString(x[0])), Value: Uri.EscapeDataString(Uri.UnescapeDataString(x.Length > 1 ? x[1] : ""))))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        var canonical = request.Method.Method + "\n" + uri.AbsolutePath + "\n" + query + "\n" + canonicalHeaders + "\n" + signed + "\n" + Hash(body);
        var scope = date + "/" + region + "/" + service + "/aws4_request";
        var toSign = "AWS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Hash(canonical);
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
        request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256 Credential=" + accessKey + "/" + scope + ", SignedHeaders=" + signed + ", Signature=" + Hex(Hmac(key, toSign)));
    }
}
