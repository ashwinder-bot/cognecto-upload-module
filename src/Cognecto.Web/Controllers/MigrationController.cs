using System.Security.Claims;
using Cognecto.Web.Data;
using Cognecto.Web.Models;
using Cognecto.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cognecto.Web.Controllers;

[Authorize]
public class MigrationController(AppDbContext db, MigrationCsvParser parser) : Controller
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxErrorsShown = 50;

    public async Task<IActionResult> Index() => View(await RecentUploads());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            ModelState.AddModelError("", "Choose a CSV file to upload.");
        else if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError("", "Only .csv files are accepted. In Excel use File > Save As > CSV UTF-8.");
        else if (file.Length > MaxFileBytes)
            ModelState.AddModelError("", "The file is larger than 5 MB.");

        if (!ModelState.IsValid)
            return View(nameof(Index), await RecentUploads());

        MigrationCsvResult result;
        await using (var stream = file!.OpenReadStream())
            result = parser.Parse(stream);

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors.Take(MaxErrorsShown))
                ModelState.AddModelError("", error);
            if (result.Errors.Count > MaxErrorsShown)
                ModelState.AddModelError("", $"…and {result.Errors.Count - MaxErrorsShown} more.");
            return View(nameof(Index), await RecentUploads());
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        foreach (var record in result.Records)
            record.UploadedBy = userId;

        var upload = new MigrationUpload
        {
            FileName = Path.GetFileName(file.FileName),
            UploadedBy = userId,
            UploadedAt = DateTime.UtcNow,
            RowCount = result.Records.Count,
            Records = result.Records,
        };
        db.MigrationUploads.Add(upload);
        await db.SaveChangesAsync(); // upload + all rows in one transaction

        TempData["Success"] = $"Saved {upload.RowCount} rows from {upload.FileName}.";
        return RedirectToAction(nameof(Details), new { id = upload.Id });
    }

    public async Task<IActionResult> Details(long id)
    {
        var upload = await db.MigrationUploads
            .AsNoTracking()
            .Include(u => u.Uploader)
            .Include(u => u.Records.OrderBy(r => r.RowNo))
            .FirstOrDefaultAsync(u => u.Id == id);

        return upload is null ? NotFound() : View(upload);
    }

    private Task<List<MigrationUpload>> RecentUploads() =>
        db.MigrationUploads
            .AsNoTracking()
            .Include(u => u.Uploader)
            .OrderByDescending(u => u.UploadedAt)
            .Take(50)
            .ToListAsync();
}
