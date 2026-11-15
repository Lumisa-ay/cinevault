using System.Security.Claims;
using CineVault.Models;
using CineVault.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[Authorize]
[Route("account")]
public sealed class AccountController(LocalUserStore users) : Controller
{
    [AllowAnonymous, HttpGet("login")]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        return View(new LoginInput { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : "/" });
    }
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginInput input)
    {
        Response.Headers.CacheControl = "no-store";
        if (ModelState.IsValid && users.Authenticate(input.UserName, input.Password) is { } user)
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName),
                new Claim(ClaimTypes.Role, user.Role), new Claim("display_name", user.DisplayName), new Claim("security_stamp", user.SecurityStamp) };
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return LocalRedirect(Url.IsLocalUrl(input.ReturnUrl) ? input.ReturnUrl! : "/");
        }
        ModelState.AddModelError("", "用户名或密码错误。连续失败后请稍后再试。");
        input.Password = "";
        return View(input);
    }
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    { await HttpContext.SignOutAsync(); return RedirectToAction(nameof(Login)); }

    [HttpGet("password")]
    public IActionResult Password() => View(new ChangePasswordInput());

    [HttpPost("password")]
    public async Task<IActionResult> Password(ChangePasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        try
        {
            if (!users.ChangePassword(User.FindFirstValue(ClaimTypes.NameIdentifier)!, input.CurrentPassword, input.NewPassword))
            { ModelState.AddModelError("", "当前密码不正确。"); return View(input); }
        }
        catch (ArgumentException ex) { ModelState.AddModelError("", ex.Message); return View(input); }
        await HttpContext.SignOutAsync();
        return Redirect("/account/login?changed=1");
    }
    [Authorize(Roles = "Admin"), HttpGet("users")]
    public IActionResult Users() => View(users.All());

    [Authorize(Roles = "Admin"), HttpPost("users")]
    public IActionResult CreateUser(CreateUserInput input)
    {
        if (!ModelState.IsValid) TempData["Error"] = "请完整填写用户信息，密码需要 12–128 个字符。";
        else
        {
            try { users.Create(input.UserName, input.DisplayName, input.Password, input.Role); TempData["Message"] = "用户已创建，可以登录。"; }
            catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        }
        return RedirectToAction(nameof(Users));
    }
    [Authorize(Roles = "Admin"), HttpPost("users/{id}/password")]
    public IActionResult ResetPassword(string id, string password)
    {
        try
        {
            if (id == User.FindFirstValue(ClaimTypes.NameIdentifier)) throw new ArgumentException("请通过“修改密码”更改自己的密码。");
            users.ResetPassword(id, password); TempData["Message"] = "密码已重置，该用户的旧登录已失效。";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Users));
    }
    [Authorize(Roles = "Admin"), HttpPost("users/{id}/status")]
    public IActionResult SetStatus(string id, bool disabled)
    {
        try { users.SetDisabled(id, disabled, User.FindFirstValue(ClaimTypes.NameIdentifier)!); TempData["Message"] = disabled ? "账户已停用，旧登录已失效。" : "账户已启用。"; }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Users));
    }
}
