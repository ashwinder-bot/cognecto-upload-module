using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cognecto.Web.Models;

[Table("app_users")]
public class AppUser
{
    [Key, Column("user_id")] public string UserId { get; set; } = "";
    [Column("full_name")] public string FullName { get; set; } = "";
    [Column("designation")] public string Designation { get; set; } = "";
}
