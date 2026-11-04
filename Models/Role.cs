using System.ComponentModel.DataAnnotations;

namespace ShipTrack.Api.Models;

public class Role
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(250)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserRoleMapping> UserRoles { get; set; } = new List<UserRoleMapping>();
}
