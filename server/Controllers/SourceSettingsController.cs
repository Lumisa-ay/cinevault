using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[Authorize(Roles = "Admin"), Route("admin/sources"), RequestSizeLimit(65536)]
public sealed class SourceSettingsController(SourceSettingsStore settings, MovieSearchService search, IResourceGateway gateway) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(settings.View());

    [HttpPost("tmdb")]
    public IActionResult Tmdb(TmdbSettingsInput input)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "表单不完整，请重新填写。"; return RedirectToAction(nameof(Index)); }
        try { settings.SaveTmdb(input); TempData["Message"] = input.ClearCredential ? "TMDB 密钥已清除，在线检索已停用。" : "TMDB 配置已加密保存，立即生效。可点击测试连接。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TempData["Error"] = "配置未能写入，请检查数据目录权限。原配置保留。"; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("imdb")]
    public IActionResult Imdb(ImdbSettingsInput input)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "表单不完整，请重新填写。"; return RedirectToAction(nameof(Index)); }
        try { settings.SaveImdb(input); TempData["Message"] = "IMDb 接入方式与凭据已加密保存，立即生效。可点击测试连接。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TempData["Error"] = "配置未能写入，请检查数据目录权限。原配置保留。"; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("test/{provider}")]
    public async Task<IActionResult> Test(string provider, CancellationToken ct)
    {
        var result = provider is "indexer" or "qbittorrent" ? await gateway.Test(provider, ct) : await search.TestConnection(provider, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("bt")]
    public IActionResult Bt(BtSettingsInput input)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "请完整填写 BT 配置。"; return RedirectToAction(nameof(Index)); }
        try { settings.SaveBt(input); TempData["Message"] = "下载器配置已加密保存，立即生效。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TempData["Error"] = "配置无法写入，原配置保留。"; }
        return RedirectToAction(nameof(Index));
    }
}
