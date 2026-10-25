using Cognecto.Web.Data;
using Cognecto.Web.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cognecto.Web.Services;

/// <summary>
/// Saves an upload and its rows in one transaction. Rows go in through Postgres COPY rather than
/// EF change tracking, so a 20,000-row file costs a few MB of memory and well under a second.
/// </summary>
public class MigrationUploadWriter(AppDbContext db)
{
    public async Task<MigrationUpload> SaveAsync(
        AppUser uploader, string fileName, IReadOnlyList<MigrationRecord> records, CancellationToken cancellationToken)
    {
        var upload = new MigrationUpload
        {
            FileName = fileName,
            UploadedBy = uploader.UserId,
            DistrictId = uploader.DistrictId,
            UploadedAt = DateTime.UtcNow,
            RowCount = records.Count,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.MigrationUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(upload).State = EntityState.Detached;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var importer = await connection.BeginBinaryImportAsync(
            """
            copy public.migration_records (
                upload_id, uploaded_by, district_id, row_no, ac_no_name,
                existing_part_no, existing_part_name, existing_section, breakup_type, existing_electors,
                rationalized_part_no, rationalized_part_name, rationalized_section, rationalized_electors, action)
            from stdin (format binary)
            """, cancellationToken))
        {
            foreach (var r in records)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(upload.Id, NpgsqlDbType.Bigint, cancellationToken);
                await importer.WriteAsync(uploader.UserId, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(uploader.DistrictId, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.RowNo, NpgsqlDbType.Integer, cancellationToken);
                await importer.WriteAsync(r.AcNoName, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.ExistingPartNo, NpgsqlDbType.Integer, cancellationToken);
                await importer.WriteAsync(r.ExistingPartName, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.ExistingSection, NpgsqlDbType.Text, cancellationToken);
                if (r.BreakupType is null)
                    await importer.WriteNullAsync(cancellationToken);
                else
                    await importer.WriteAsync(r.BreakupType, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.ExistingElectors, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.RationalizedPartNo, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.RationalizedPartName, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.RationalizedSection, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(r.RationalizedElectors, NpgsqlDbType.Integer, cancellationToken);
                await importer.WriteAsync(r.Action, NpgsqlDbType.Text, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return upload;
    }
}
