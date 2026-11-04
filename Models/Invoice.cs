using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShipTrack.Api.Models;

public enum PaymentMethod
{
    CashOnDelivery,
    StripeOnline
}

public enum PaymentStatus
{
    Pending,
    Collected,
    Reconciled
}

public class Invoice
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ShipmentId { get; set; }
    public Shipment? Shipment { get; set; }

    [Required]
    [MaxLength(30)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Column(TypeName = "decimal(10,2)")]
    public decimal BaseCharge { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal WeightCharge { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal TotalAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;

    [MaxLength(100)]
    public string? StripePaymentId { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
}
