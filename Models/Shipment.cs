using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShipTrack.Api.Models;

public enum ShipmentStatus
{
    Booked,
    PickedUp,
    InTransit,
    OutForDelivery,
    Delivered,
    Cancelled,
    FailedAttempt
}

public class Shipment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(20)]
    public string TrackingNumber { get; set; } = string.Empty;

    // Sender Info
    [Required]
    [MaxLength(100)]
    public string SenderName { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string SenderPhone { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? SenderEmail { get; set; }

    [Required]
    [MaxLength(250)]
    public string PickupAddress { get; set; } = string.Empty;

    public double PickupLatitude { get; set; }
    public double PickupLongitude { get; set; }

    // Receiver Info
    [Required]
    [MaxLength(100)]
    public string ReceiverName { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string ReceiverPhone { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ReceiverEmail { get; set; }

    [Required]
    [MaxLength(250)]
    public string DeliveryAddress { get; set; } = string.Empty;

    public double DeliveryLatitude { get; set; }
    public double DeliveryLongitude { get; set; }

    // Parcel Specs
    [Column(TypeName = "decimal(10,2)")]
    public decimal WeightKg { get; set; }

    public ShipmentStatus Status { get; set; } = ShipmentStatus.Booked;

    public Guid? RiderId { get; set; }
    public Rider? Rider { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PickedUpAt { get; set; }
    public DateTime? OutForDeliveryAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    [MaxLength(10)]
    public string? DeliveryPin { get; set; }

    public string? ProofOfDeliverySignature { get; set; }

    public int? CustomerRating { get; set; }

    [MaxLength(500)]
    public string? CustomerReview { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? RiderEarnings { get; set; }

    // Navigation
    public Invoice? Invoice { get; set; }
    public List<TrackingLog> TrackingLogs { get; set; } = new();
}
