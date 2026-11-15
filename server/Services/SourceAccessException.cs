using System.Net;
using System.Security.Authentication;

namespace CineVault.Services;

public sealed class SourceAccessException(string code, string message, Uri url, string hint, int responseStatus = 502,
    string? rule = null, string? agent = null, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public Uri Url { get; } = url;
    public string Hint { get; } = hint;
    public int ResponseStatus { get; } = responseStatus;
    public string? Rule { get; } = rule;
    public string? Agent { get; } = agent;
    public string RobotsUrl => new Uri(Url, "/robots.txt").AbsoluteUri;

    public static SourceAccessException RobotsDenied(Uri url, RobotsDecision decision) =>
        new("robots_disallowed", $"源站当前的 robots.txt 不允许自动访问 {url.AbsolutePath}（User-agent: {decision.Agent}；{decision.Rule}）。",
            url, "本次已停止抓取，原有资源已保留。可以查看源站规则，或在下载任务中手动添加可用的 HTTP/HTTPS 直链。", 403, decision.Rule, decision.Agent);

    public static SourceAccessException Transport(Uri url, HttpRequestException ex)
    {
        var stage = url.AbsolutePath == "/robots.txt" ? "源站访问规则" : "源站页面";
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
            if (cause is PublicAddressRejectedException)
                return new("source_address_blocked", "源站域名在容器中解析到了内网或保留地址，连接已被安全检查拦截。",
                    url, "请检查 Docker DNS 或代理的 Fake-IP 模式，让源站解析为真实公网地址后重试。此错误不等同于源站 robots.txt 禁止抓取；原有资源已保留。", inner: ex);
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return new("source_dns_error", "容器无法解析源站域名。",
                url, "请检查 Docker 的 DNS 配置、网络及源站域名是否有效；原有资源已保留。", inner: ex);
        if (ex.StatusCode is { } status)
        {
            var code = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "source_access_denied" :
                (int)status == 429 ? "source_rate_limited" : "source_http_error";
            return new(code, $"{stage}返回 HTTP {(int)status}（{status}）。",
                url, (int)status == 429 ? "请稍后重试，原有资源已保留。" : "请检查源站状态与访问权限，原有资源已保留。",
                (int)status == 429 ? 429 : 502, inner: ex);
        }
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
            if (cause is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
                return new("source_tls_error", $"{stage}的 HTTPS 证书链无法通过容器信任校验。",
                    url, "请检查源站证书是否完整；如使用企业代理，请配置可信的代理根证书。此情况不代表 robots.txt 禁止抓取，原有数据已保留。", inner: ex);
        return new("source_network_error", $"无法连接{stage}。",
            url, "请检查容器的网络、DNS 及源站可用性；本次未读取内容，原有资源已保留。", inner: ex);
    }
}
