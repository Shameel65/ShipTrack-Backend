using System.ComponentModel.DataAnnotations;

namespace ShipTrack.Api.Models;

public class Rider
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? PlateNumber { get; set; } = "LEE-9214";

    [MaxLength(50)]
    public string VehicleType { get; set; } = "Motorcycle";

    public double CurrentLatitude { get; set; }
    public double CurrentLongitude { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime LastLocationUpdate { get; set; } = DateTime.UtcNow;

    public List<Shipment> AssignedShipments { get; set; } = new();
}
