using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace CineVault.Services;

public interface IPublicWebClient
{
    Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, long? range = null, string? validator = null);
    Task<HttpResponseMessage> SendApiAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new NotSupportedException("此网络客户端不支持认证 API 请求。");
}
public sealed class PublicAddressRejectedException(string host)
    : HttpRequestException("域名 " + host + " 解析到内网或保留地址，已拒绝访问。");

public sealed class PublicWebClient : IPublicWebClient, IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient? sourceClient;
    private readonly HashSet<string> proxyHosts = new(StringComparer.OrdinalIgnoreCase);
    public PublicWebClient(IConfiguration? config = null)
    {
        var handler = NewHandler();
        handler.UseProxy = false;
        handler.ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0)
                throw new HttpRequestException(HttpRequestError.NameResolutionError, "域名没有可用的解析地址。");
            if (addresses.Any(ip => !IsPublicAddress(ip)))
                throw new PublicAddressRejectedException(context.DnsEndPoint.Host);
            Exception? last = null;
            foreach (var ip in addresses)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, true); }
                catch (Exception ex) { socket.Dispose(); last = ex; ct.ThrowIfCancellationRequested(); }
            }
            throw new HttpRequestException("无法连接资源服务器。", last);
        };
        client = NewClient(handler, "CineVault/1.0 (+local personal resource manager)");
        var proxyUrl = config?["Network:SourceProxy"];
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https") || proxy.UserInfo.Length > 0)
                throw new ArgumentException("Network:SourceProxy 需要不含账户信息的 HTTP/HTTPS 代理地址。");
            foreach (var host in config!.GetSection("Network:ProxyHosts").Get<string[]>() ?? [])
            {
                if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
                    throw new ArgumentException("Network:ProxyHosts 需要明确列出的完整域名。");
                proxyHosts.Add(host);
            }
            if (proxyHosts.Count == 0) throw new ArgumentException("配置源站代理时必须同时指定 Network:ProxyHosts。");
            var sourceHandler = NewHandler();
            sourceHandler.UseProxy = true;
            sourceHandler.Proxy = new WebProxy(proxy);
            sourceClient = NewClient(sourceHandler, config["Network:SourceUserAgent"] ??
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36");
            sourceClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            sourceClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        }
    }
    private static HttpClient NewClient(SocketsHttpHandler handler, string userAgent)
    {
        var value = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        value.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return value;
    }
    private static SocketsHttpHandler NewHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var bundle = Path.Combine(AppContext.BaseDirectory, "Certificates", "chain.pem");
        if (File.Exists(bundle))
        {
            // These are cross-signed intermediates, not new trusted roots.
            // The platform still verifies signatures, names, validity and the existing system trust anchor.
            var policy = new X509ChainPolicy { RevocationMode = X509RevocationMode.NoCheck };
            policy.ExtraStore.ImportFromPemFile(bundle);
            handler.SslOptions.CertificateChainPolicy = policy;
        }
        return handler;
    }
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] is 0 or 168 || b[1] == 88 && b[2] == 99) || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) || b[0] == 203 && b[1] == 0 && b[2] == 113);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0d && b[3] == 0xb8 || b[2] is 0 or 2)) &&
            !(b[0] == 0x20 && b[1] == 0x02);
    }
    public static Uri ValidateUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port is not (80 or 443))
            throw new ArgumentException("仅支持不含账户信息的 HTTP / HTTPS 地址及标准端口。");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsPublicAddress(ip))
            throw new ArgumentException("不允许访问本机、内网或保留地址。");
        return uri;
    }
    public async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, long? range = null, string? validator = null)
    {
        for (var i = 0; i < 6; i++)
        {
            uri = ValidateUri(uri.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (range > 0 && !string.IsNullOrEmpty(validator))
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(range, null);
                request.Headers.TryAddWithoutValidation("If-Range", validator);
            }
            var transport = sourceClient is not null && proxyHosts.Contains(uri.IdnHost) ? sourceClient : client;
            var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("重定向缺少目标地址。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new HttpRequestException("资源重定向次数过多。");
    }
    public async Task<HttpResponseMessage> SendApiAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = ValidateUri(request.RequestUri?.AbsoluteUri ?? "");
        if (uri.Scheme != "https") throw new ArgumentException("认证 API 仅支持 HTTPS。");
        var transport = sourceClient is not null && proxyHosts.Contains(uri.IdnHost) ? sourceClient : client;
        var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // Credentials and AWS signatures are never replayed to a redirect target.
        if ((int)response.StatusCode is >= 300 and < 400)
        { response.Dispose(); throw new HttpRequestException("认证 API 返回重定向，已停止请求。"); }
        return response;
    }
    public void Dispose() { client.Dispose(); sourceClient?.Dispose(); }
}
