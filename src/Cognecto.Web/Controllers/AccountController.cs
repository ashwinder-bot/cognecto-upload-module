using System.Security.Claims;
using Cognecto.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cognecto.Web.Controllers;

// Demo sign-in: pick one of the seeded officers from app_users. No password.
// Swap for real authentication later; uploads only rely on the NameIdentifier claim.
public class AccountController(AppDbContext db) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View(await db.AppUsers.AsNoTracking().OrderBy(u => u.UserId).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string userId, string? returnUrl)
    {
        var user = await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
        if (user is null)
        {
            ModelState.AddModelError("", "Unknown user.");
            return await Login(returnUrl);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserId),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim("designation", user.Designation),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
