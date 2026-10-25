using System.ComponentModel.DataAnnotations.Schema;

namespace Cognecto.Web.Models;

[Table("migration_uploads")]
public class MigrationUpload
{
    [Column("id")] public long Id { get; set; }
    [Column("file_name")] public string FileName { get; set; } = "";
    [Column("uploaded_by")] public string UploadedBy { get; set; } = "";
    [Column("district_id")] public string DistrictId { get; set; } = "";
    [Column("uploaded_at")] public DateTime UploadedAt { get; set; }
    [Column("row_count")] public int RowCount { get; set; }

    [ForeignKey(nameof(UploadedBy))] public AppUser? Uploader { get; set; }
    [ForeignKey(nameof(MigrationRecord.UploadId))] public List<MigrationRecord> Records { get; set; } = [];
}
