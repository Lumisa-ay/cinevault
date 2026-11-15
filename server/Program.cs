using System.Net;
using System.Security.Claims;
using CineVault.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;

if (args.Contains("--healthcheck"))
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var response = await client.GetAsync("http://127.0.0.1:8080/healthz");
        Environment.Exit(response.IsSuccessStatusCode ? 0 : 1);
    }
    catch { Environment.Exit(1); }
    return;
}
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["HTTP_PORTS"]))
    builder.WebHost.UseUrls("http://localhost:5188");
var dataRoot = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
Directory.CreateDirectory(dataRoot);
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "CineVault.Antiforgery"; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddDataProtection().SetApplicationName("CineVault.Local").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")));
builder.Services.AddSingleton(sp => new LocalUserStore(dataRoot, builder.Configuration));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/account/login";
    o.Cookie.Name = "CineVault.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = async c =>
    {
        if (c.Request.Path.StartsWithSegments("/api"))
        { c.Response.StatusCode = 401; await c.Response.WriteAsJsonAsync(new { error = "登录已过期，请重新登录。" }); }
        else c.Response.Redirect(c.RedirectUri);
    };
    o.Events.OnRedirectToAccessDenied = async c =>
    { c.Response.StatusCode = 403; await c.Response.WriteAsJsonAsync(new { error = "当前账户没有此操作的权限。" }); };
    o.Events.OnValidatePrincipal = async c =>
    {
        var principal = c.Principal;
        if (!c.HttpContext.RequestServices.GetRequiredService<LocalUserStore>().SessionValid(
                principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                principal?.FindFirstValue("security_stamp") ?? "", principal?.FindFirstValue(ClaimTypes.Role) ?? ""))
        { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton(sp => new SourceSettingsStore(dataRoot, builder.Configuration, sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(sp => new StateStore(dataRoot,
    builder.Configuration.GetSection("AllowedDownloadRoots").Get<string[]>(), builder.Configuration["DownloadDirectory"]));
builder.Services.AddSingleton<IPublicWebClient, PublicWebClient>();
builder.Services.AddSingleton<PageParser>();
builder.Services.AddSingleton<CrawlerService>();
builder.Services.AddSingleton<MovieSearchService>();
builder.Services.AddSingleton<IResourceGateway, ResourceGateway>();
builder.Services.AddSingleton<TorrentService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentService>());
builder.Services.AddSingleton<DownloadService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadService>());
var app = builder.Build();
app.Services.GetRequiredService<LocalUserStore>();
var publishedUi = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var sourceUi = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "dist"));
var uiRoot = File.Exists(Path.Combine(publishedUi, "index.html")) ? publishedUi : sourceUi;
if (!File.Exists(Path.Combine(uiRoot, "index.html"))) throw new InvalidOperationException("缺少 dist 前端文件。");
app.Use(async (context, next) =>
{
    context.Items["UiRoot"] = uiRoot;
    if (builder.Configuration.GetValue("LocalOnly", true) && context.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'; object-src 'none'; base-uri 'self'; form-action 'self'";
    if (!context.Request.Path.Equals("/styles.css") && !context.Request.Path.Equals("/account.css"))
        context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !origin.Equals($"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "请从当前工作台发起操作。" }); return; }
    }
    try { await next(); }
    catch (SourceAccessException ex)
    {
        if (context.Response.HasStarted) throw;
        app.Logger.LogWarning("Source access stopped: {Code} at {Host} ({Rule})", ex.Code, ex.Url.Host, ex.Rule ?? "-");
        context.Response.StatusCode = ex.ResponseStatus;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message, code = ex.Code, hint = ex.Hint,
            url = ex.Url.AbsoluteUri, robotsUrl = ex.Code == "robots_disallowed" ? ex.RobotsUrl : null, rule = ex.Rule, agent = ex.Agent });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException or KeyNotFoundException or OperationCanceledException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is KeyNotFoundException ? 404 : ex is HttpRequestException ? 502 : ex is OperationCanceledException ? 504 : 400;
        await context.Response.WriteAsJsonAsync(new { error = ex is OperationCanceledException ? "请求超时或已取消；原有数据已保留。" : ex.Message });
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is null && context.Request.Path is var p && p != "/styles.css" && p != "/account.css" && context.User.Identity?.IsAuthenticated != true)
    {
        if (p.StartsWithSegments("/api")) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "请先登录。" }); }
        else await context.ChallengeAsync();
        return;
    }
    if (context.Request.Path.Equals("/index.html")) { context.Response.Redirect("/"); return; }
    await next();
});
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uiRoot) });
app.MapGet("/healthz", () => Results.Json(new { status = "ok" })).AllowAnonymous();
app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
public partial class Program { }
