using System.ComponentModel.DataAnnotations;

namespace ShipTrack.Api.Models;

public enum UserRole
{
    Customer,
    Rider,
    Admin
}

public class User
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(150)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Customer;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserRoleMapping> UserRoles { get; set; } = new List<UserRoleMapping>();
}
