using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Cognecto.Web.Models;

namespace Cognecto.Web.Controllers;

public class HomeController : Controller
{
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    // Shown for 4xx responses that have no body of their own, e.g. an upload that does not exist.
    // id is the status code, from UseStatusCodePagesWithReExecute("/Home/Status/{0}").
    public IActionResult Status(int id)
    {
        Response.StatusCode = id;
        ViewData["Title"] = id == 404 ? "Not found" : $"Error {id}";
        return View(id);
    }
}
