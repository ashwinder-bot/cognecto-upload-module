using System.Security.Claims;
using Cognecto.Web.Data;
using Cognecto.Web.Models;
using Cognecto.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cognecto.Web.Controllers;

[Authorize]
public class MigrationController(
    AppDbContext db,
    MigrationCsvParser parser,
    MigrationUploadWriter writer,
    UploadGate gate,
    IOptions<UploadOptions> options) : Controller
{
    private const int MaxErrorsShown = 50;

    public async Task<IActionResult> Index()
    {
        var user = await CurrentUser();
        return user is null ? Forbid() : await IndexView(user);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        // The uploader and their district come from app_users, never from the form,
        // so a file is always filed under the district of the officer who sent it.
        var user = await CurrentUser();
        if (user is null)
            return Forbid();

        if (file is null || file.Length == 0)
            ModelState.AddModelError("", "Choose a CSV file to upload.");
        else if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("", "Only .csv files are accepted. In Excel use File > Save As > CSV UTF-8.");
        else if (file.Length > options.Value.MaxFileBytes)
            ModelState.AddModelError("", $"The file is larger than {options.Value.MaxFileMegabytes} MB.");

        if (!ModelState.IsValid)
            return await IndexView(user);

        MigrationUpload upload;
        using (await gate.EnterAsync(HttpContext.RequestAborted))
        {
            MigrationCsvResult result;
            await using (var stream = file!.OpenReadStream())
                result = parser.Parse(stream);

            if (result.Errors.Count > 0)
            {
                foreach (var error in result.Errors.Take(MaxErrorsShown))
                    ModelState.AddModelError("", error);
                if (result.Errors.Count > MaxErrorsShown)
                    ModelState.AddModelError("", $"…and {result.Errors.Count - MaxErrorsShown} more.");
                return await IndexView(user);
            }

            upload = await writer.SaveAsync(user, Path.GetFileName(file.FileName), result.Records, HttpContext.RequestAborted);
        }

        TempData["Success"] = $"Saved {upload.RowCount} rows from {upload.FileName}.";
        return RedirectToAction(nameof(Details), new { id = upload.Id });
    }

    public async Task<IActionResult> Details(long id)
    {
        var user = await CurrentUser();
        if (user is null)
            return Forbid();

        var upload = await db.MigrationUploads
            .AsNoTracking()
            .Include(u => u.Uploader)
            .Include(u => u.Records.OrderBy(r => r.RowNo))
            .FirstOrDefaultAsync(u => u.Id == id && u.DistrictId == user.DistrictId);

        return upload is null ? NotFound() : View(upload);
    }

    // Read on every request rather than trusting the cookie, so a changed district takes effect immediately.
    private Task<AppUser?> CurrentUser()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return db.AppUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId);
    }

    // Officers only see their own district's uploads; filtering across districts is for the main authority.
    private async Task<IActionResult> IndexView(AppUser user)
    {
        ViewData["DistrictId"] = user.DistrictId;
        ViewData["MaxFileMegabytes"] = options.Value.MaxFileMegabytes;
        return View(nameof(Index), await db.MigrationUploads
            .AsNoTracking()
            .Include(u => u.Uploader)
            .Where(u => u.DistrictId == user.DistrictId)
            .OrderByDescending(u => u.UploadedAt)
            .Take(50)
            .ToListAsync());
    }
}
