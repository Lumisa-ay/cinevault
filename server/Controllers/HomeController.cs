using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CineVault.Controllers;

[Authorize]
public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
