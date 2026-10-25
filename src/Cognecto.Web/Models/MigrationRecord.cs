using System.ComponentModel.DataAnnotations.Schema;

namespace Cognecto.Web.Models;

/// <summary>One line of the "Format - Migration table" sheet.</summary>
[Table("migration_records")]
public class MigrationRecord
{
    [Column("id")] public long Id { get; set; }
    [Column("upload_id")] public long UploadId { get; set; }
    [Column("uploaded_by")] public string UploadedBy { get; set; } = "";
    [Column("district_id")] public string DistrictId { get; set; } = "";
    [Column("row_no")] public int RowNo { get; set; }
    [Column("ac_no_name")] public string AcNoName { get; set; } = "";

    // Existing Position
    [Column("existing_part_no")] public int ExistingPartNo { get; set; }
    [Column("existing_part_name")] public string ExistingPartName { get; set; } = "";
    [Column("existing_section")] public string ExistingSection { get; set; } = "";
    [Column("breakup_type")] public string? BreakupType { get; set; }
    [Column("existing_electors")] public string ExistingElectors { get; set; } = "";

    // Rationalization - Activity Identification
    [Column("rationalized_part_no")] public string RationalizedPartNo { get; set; } = "";
    [Column("rationalized_part_name")] public string RationalizedPartName { get; set; } = "";
    [Column("rationalized_section")] public string RationalizedSection { get; set; } = "";
    [Column("rationalized_electors")] public int RationalizedElectors { get; set; }
    [Column("action")] public string Action { get; set; } = "";
}
