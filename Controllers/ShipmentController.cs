using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using ShipTrack.Api.Data;
using ShipTrack.Api.Hubs;
using ShipTrack.Api.Models;
using ShipTrack.Api.Services;

namespace ShipTrack.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShipmentController : ControllerBase
{
    private readonly ShipTrackDbContext _db;
    private readonly IHubContext<TrackingHub> _hub;
    private readonly IEmailService _emailService;

    public ShipmentController(ShipTrackDbContext db, IHubContext<TrackingHub> hub, IEmailService emailService)
    {
        _db = db;
        _hub = hub;
        _emailService = emailService;
    }

    private static string? ToIsoUtc(DateTime? dt)
    {
        if (!dt.HasValue) return null;
        return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc).ToString("o");
    }

    private static string ToIsoUtc(DateTime dt)
    {
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");
    }

    public record BookShipmentRequest(
        string SenderName,
        string SenderPhone,
        string PickupAddress,
        double PickupLatitude,
        double PickupLongitude,
        string ReceiverName,
        string ReceiverPhone,
        string DeliveryAddress,
        double DeliveryLatitude,
        double DeliveryLongitude,
        decimal WeightKg,
        PaymentMethod PaymentMethod,
        string? StripePaymentId = null,
        string? ReceiverEmail = null,
        string? SenderEmail = null
    );

    [HttpPost("book")]
    public async Task<IActionResult> BookShipment([FromBody] BookShipmentRequest request)
    {
        var randomNum = Random.Shared.Next(10000, 99999);
        var trackingNumber = $"TRK-{randomNum}";
        var deliveryPin = Random.Shared.Next(1000, 9999).ToString();

        var baseCharge = 200.00m;
        var weightCharge = Math.Max(0, request.WeightKg * 100.00m);
        var totalAmount = baseCharge + weightCharge;

        var shipment = new Shipment
        {
            TrackingNumber = trackingNumber,
            SenderName = request.SenderName,
            SenderPhone = request.SenderPhone,
            SenderEmail = request.SenderEmail,
            PickupAddress = request.PickupAddress,
            PickupLatitude = request.PickupLatitude != 0 ? request.PickupLatitude : 31.5204,
            PickupLongitude = request.PickupLongitude != 0 ? request.PickupLongitude : 74.3587,
            ReceiverName = request.ReceiverName,
            ReceiverPhone = request.ReceiverPhone,
            ReceiverEmail = request.ReceiverEmail,
            DeliveryAddress = request.DeliveryAddress,
            DeliveryLatitude = request.DeliveryLatitude != 0 ? request.DeliveryLatitude : 31.4697,
            DeliveryLongitude = request.DeliveryLongitude != 0 ? request.DeliveryLongitude : 74.4084,
            WeightKg = request.WeightKg,
            Status = ShipmentStatus.Booked,
            CreatedAt = DateTime.UtcNow,
            DeliveryPin = deliveryPin,
            RiderEarnings = 80.00m
        };

        var isStripe = request.PaymentMethod == PaymentMethod.StripeOnline;
        var stripeId = isStripe
            ? (!string.IsNullOrWhiteSpace(request.StripePaymentId) ? request.StripePaymentId : $"pi_test_{Random.Shared.Next(10000000, 99999999)}")
            : null;

        var invoice = new Invoice
        {
            InvoiceNumber = $"INV-{randomNum}",
            BaseCharge = baseCharge,
            WeightCharge = weightCharge,
            TotalAmount = totalAmount,
            PaymentMethod = request.PaymentMethod,
            PaymentStatus = isStripe ? PaymentStatus.Collected : PaymentStatus.Pending,
            StripePaymentId = stripeId,
            PaidAt = isStripe ? DateTime.UtcNow : null,
            Shipment = shipment
        };

        shipment.Invoice = invoice;

        shipment.TrackingLogs.Add(new TrackingLog
        {
            TrackingNumber = shipment.TrackingNumber,
            Latitude = shipment.PickupLatitude,
            Longitude = shipment.PickupLongitude,
            StatusText = isStripe ? "Shipment Booked (Paid Online via Stripe)" : "Shipment Booked successfully",
            RecordedAt = DateTime.UtcNow
        });

        _db.Shipments.Add(shipment);
        await _db.SaveChangesAsync();

        // Automated Brevo Dispatch for Consignment Confirmation
        var targetEmail = request.ReceiverEmail ?? request.SenderEmail;
        if (!string.IsNullOrWhiteSpace(targetEmail))
        {
            _ = Task.Run(() => _emailService.SendShipmentBookedEmailAsync(
                targetEmail,
                request.ReceiverName,
                trackingNumber,
                deliveryPin,
                totalAmount,
                request.PaymentMethod.ToString()
            ));
        }

        return Ok(new
        {
            message = "Shipment booked successfully",
            trackingNumber = shipment.TrackingNumber,
            deliveryPin = shipment.DeliveryPin,
            invoiceNumber = invoice.InvoiceNumber,
            totalAmount = invoice.TotalAmount,
            baseCharge = invoice.BaseCharge,
            weightCharge = invoice.WeightCharge,
            paymentMethod = invoice.PaymentMethod.ToString(),
            paymentStatus = invoice.PaymentStatus.ToString(),
            stripePaymentId = invoice.StripePaymentId,
            status = shipment.Status.ToString(),
            createdAt = ToIsoUtc(shipment.CreatedAt)
        });
    }

    [HttpGet("{trackingNumber}")]
    public async Task<IActionResult> GetTrackingDetails(string trackingNumber)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Invoice)
            .Include(s => s.Rider)
            .Include(s => s.TrackingLogs.OrderByDescending(t => t.RecordedAt))
            .FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);

        if (shipment == null)
            return NotFound(new { message = $"Shipment with tracking number {trackingNumber} not found." });

        return Ok(new
        {
            trackingNumber = shipment.TrackingNumber,
            status = shipment.Status.ToString(), // Return friendly string name!
            senderName = shipment.SenderName,
            senderPhone = shipment.SenderPhone,
            pickupAddress = shipment.PickupAddress,
            pickupCoordinates = new { lat = shipment.PickupLatitude, lng = shipment.PickupLongitude },
            receiverName = shipment.ReceiverName,
            receiverPhone = shipment.ReceiverPhone,
            deliveryAddress = shipment.DeliveryAddress,
            deliveryCoordinates = new { lat = shipment.DeliveryLatitude, lng = shipment.DeliveryLongitude },
            weightKg = shipment.WeightKg,
            deliveryPin = shipment.DeliveryPin,
            proofOfDeliverySignature = shipment.ProofOfDeliverySignature,
            customerRating = shipment.CustomerRating,
            customerReview = shipment.CustomerReview,
            riderEarnings = shipment.RiderEarnings,
            createdAt = ToIsoUtc(shipment.CreatedAt),
            timeline = new
            {
                bookedAt = ToIsoUtc(shipment.CreatedAt),
                pickedUpAt = ToIsoUtc(shipment.PickedUpAt),
                outForDeliveryAt = ToIsoUtc(shipment.OutForDeliveryAt),
                deliveredAt = ToIsoUtc(shipment.DeliveredAt)
            },
            invoice = new
            {
                invoiceNumber = shipment.Invoice?.InvoiceNumber,
                totalAmount = shipment.Invoice?.TotalAmount,
                paymentMethod = shipment.Invoice?.PaymentMethod.ToString(),
                paymentStatus = shipment.Invoice?.PaymentStatus.ToString(),
                stripePaymentId = shipment.Invoice?.StripePaymentId
            },
            rider = shipment.Rider != null ? new
            {
                name = shipment.Rider.FullName,
                phone = shipment.Rider.Phone,
                vehicle = shipment.Rider.VehicleType,
                plateNumber = shipment.Rider.PlateNumber,
                currentLat = shipment.Rider.CurrentLatitude,
                currentLng = shipment.Rider.CurrentLongitude
            } : null,
            trackingHistory = shipment.TrackingLogs.OrderByDescending(t => t.RecordedAt).Select(t => new
            {
                trackingNumber = !string.IsNullOrEmpty(t.TrackingNumber) ? t.TrackingNumber : shipment.TrackingNumber,
                latitude = t.Latitude,
                longitude = t.Longitude,
                status = t.StatusText,
                timestamp = ToIsoUtc(t.RecordedAt)
            })
        });
    }

    // Returns all active shipments for Rider console
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveShipments()
    {
        var shipments = await _db.Shipments
            .Include(s => s.Invoice)
            .Where(s => s.Status != ShipmentStatus.Delivered && s.Status != ShipmentStatus.Cancelled)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var result = shipments.Select(s => new
        {
            s.TrackingNumber,
            s.SenderName,
            s.SenderPhone,
            s.PickupAddress,
            s.ReceiverName,
            s.ReceiverPhone,
            s.DeliveryAddress,
            Status = s.Status.ToString(),
            s.WeightKg,
            s.DeliveryPin,
            TotalAmount = s.Invoice != null ? s.Invoice.TotalAmount : 0,
            PaymentMethod = s.Invoice != null ? s.Invoice.PaymentMethod.ToString() : "COD",
            PaymentStatus = s.Invoice != null ? s.Invoice.PaymentStatus.ToString() : "Pending",
            StripePaymentId = s.Invoice != null ? s.Invoice.StripePaymentId : null,
            CreatedAt = ToIsoUtc(s.CreatedAt)
        });

        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllShipments()
    {
        var shipments = await _db.Shipments
            .Include(s => s.Invoice)
            .Include(s => s.Rider)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var result = shipments.Select(s => new
        {
            s.TrackingNumber,
            s.SenderName,
            s.SenderPhone,
            s.PickupAddress,
            s.ReceiverName,
            s.ReceiverPhone,
            s.DeliveryAddress,
            Status = s.Status.ToString(), // Fixed: Return string instead of int (0, 1, 2)
            s.WeightKg,
            s.DeliveryPin,
            s.ProofOfDeliverySignature,
            s.CustomerRating,
            s.CustomerReview,
            s.RiderEarnings,
            TotalAmount = s.Invoice != null ? s.Invoice.TotalAmount : 0,
            PaymentMethod = s.Invoice != null ? s.Invoice.PaymentMethod.ToString() : "COD",
            PaymentStatus = s.Invoice != null ? s.Invoice.PaymentStatus.ToString() : "N/A",
            StripePaymentId = s.Invoice != null ? s.Invoice.StripePaymentId : null,
            RiderName = s.Rider != null ? s.Rider.FullName : "Unassigned",
            CreatedAt = ToIsoUtc(s.CreatedAt),
            PickedUpAt = ToIsoUtc(s.PickedUpAt),
            OutForDeliveryAt = ToIsoUtc(s.OutForDeliveryAt),
            DeliveredAt = ToIsoUtc(s.DeliveredAt)
        });

        return Ok(result);
    }

    public record UpdateStatusRequest(
        ShipmentStatus Status, 
        string? StatusNote, 
        string? DeliveryPin = null, 
        string? SignatureBase64 = null,
        int? Rating = null,
        string? Review = null
    );

    [HttpPost("{trackingNumber}/status")]
    public async Task<IActionResult> UpdateStatus(string trackingNumber, [FromBody] UpdateStatusRequest req)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Invoice)
            .Include(s => s.Rider)
            .FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);

        if (shipment == null)
            return NotFound(new { message = "Shipment not found" });

        // Anti-theft verification: Recipient Handover PIN check on delivery
        if (req.Status == ShipmentStatus.Delivered)
        {
            if (!string.IsNullOrWhiteSpace(shipment.DeliveryPin))
            {
                if (string.IsNullOrWhiteSpace(req.DeliveryPin) || 
                    !string.Equals(req.DeliveryPin.Trim(), shipment.DeliveryPin.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new 
                    { 
                        message = "Invalid Recipient Delivery PIN! Please ask the customer for their 4-digit handover verification code." 
                    });
                }
            }

            // Save e-POD Digital Signature
            if (!string.IsNullOrWhiteSpace(req.SignatureBase64))
            {
                shipment.ProofOfDeliverySignature = req.SignatureBase64;
            }

            // Save customer rating if provided upon delivery
            if (req.Rating.HasValue && req.Rating.Value >= 1 && req.Rating.Value <= 5)
            {
                shipment.CustomerRating = req.Rating.Value;
                shipment.CustomerReview = req.Review;
            }

            shipment.RiderEarnings = 80.00m;
        }

        shipment.Status = req.Status;

        if (req.Status == ShipmentStatus.PickedUp)
        {
            shipment.PickedUpAt = DateTime.UtcNow;
        }
        else if (req.Status == ShipmentStatus.OutForDelivery || req.Status == ShipmentStatus.InTransit)
        {
            if (shipment.PickedUpAt == null) shipment.PickedUpAt = DateTime.UtcNow;
            shipment.OutForDeliveryAt = DateTime.UtcNow;
        }
        else if (req.Status == ShipmentStatus.Delivered)
        {
            if (shipment.PickedUpAt == null) shipment.PickedUpAt = DateTime.UtcNow;
            if (shipment.OutForDeliveryAt == null) shipment.OutForDeliveryAt = DateTime.UtcNow;
            shipment.DeliveredAt = DateTime.UtcNow;
            if (shipment.Invoice != null)
            {
                shipment.Invoice.PaymentStatus = PaymentStatus.Collected;
                shipment.Invoice.PaidAt = DateTime.UtcNow;
            }
        }

        _db.TrackingLogs.Add(new TrackingLog
        {
            Id = Guid.NewGuid(),
            ShipmentId = shipment.Id,
            TrackingNumber = shipment.TrackingNumber,
            Latitude = shipment.DeliveryLatitude,
            Longitude = shipment.DeliveryLongitude,
            StatusText = req.StatusNote ?? $"Status updated to {req.Status}",
            RecordedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        // Automated Out-for-Delivery Brevo Email Dispatch
        if (req.Status == ShipmentStatus.OutForDelivery && !string.IsNullOrWhiteSpace(shipment.ReceiverEmail))
        {
            var riderInfo = shipment.Rider != null ? shipment.Rider.FullName : "Assigned Fleet Courier";
            var vehicleInfo = shipment.Rider != null ? $"{shipment.Rider.VehicleType} [{shipment.Rider.PlateNumber}]" : "Motorcycle";

            _ = Task.Run(() => _emailService.SendOutForDeliveryEmailAsync(
                shipment.ReceiverEmail,
                shipment.ReceiverName,
                shipment.TrackingNumber,
                riderInfo,
                vehicleInfo,
                shipment.DeliveryPin ?? "9009"
            ));
        }

        // Broadcast to customer live map via SignalR
        await _hub.Clients.Group(trackingNumber).SendAsync("ReceiveStatusUpdate", new
        {
            trackingNumber,
            status = shipment.Status.ToString(),
            timestamp = ToIsoUtc(DateTime.UtcNow),
            signature = shipment.ProofOfDeliverySignature
        });

        return Ok(new
        {
            message = "Status updated successfully",
            status = shipment.Status.ToString(),
            deliveredAt = ToIsoUtc(shipment.DeliveredAt),
            hasSignature = !string.IsNullOrEmpty(shipment.ProofOfDeliverySignature)
        });
    }

    // Re-queue Failed Attempt Consignment for Re-attempt
    [HttpPost("{trackingNumber}/re-queue")]
    public async Task<IActionResult> ReQueueShipment(string trackingNumber)
    {
        var shipment = await _db.Shipments.FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);
        if (shipment == null) return NotFound(new { message = "Shipment not found" });

        shipment.Status = ShipmentStatus.PickedUp; // Reset back to hub for next-day dispatch

        _db.TrackingLogs.Add(new TrackingLog
        {
            Id = Guid.NewGuid(),
            ShipmentId = shipment.Id,
            TrackingNumber = shipment.TrackingNumber,
            Latitude = shipment.PickupLatitude,
            Longitude = shipment.PickupLongitude,
            StatusText = "Consignment re-queued by Operations Hub for delivery re-attempt",
            RecordedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        await _hub.Clients.Group(trackingNumber).SendAsync("ReceiveStatusUpdate", new
        {
            trackingNumber,
            status = shipment.Status.ToString(),
            timestamp = ToIsoUtc(DateTime.UtcNow)
        });

        return Ok(new { message = $"Consignment {trackingNumber} successfully re-queued for re-attempt!", status = shipment.Status.ToString() });
    }

    // Return to Origin (RTO)
    [HttpPost("{trackingNumber}/rto")]
    public async Task<IActionResult> ReturnToOrigin(string trackingNumber)
    {
        var shipment = await _db.Shipments.FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);
        if (shipment == null) return NotFound(new { message = "Shipment not found" });

        shipment.Status = ShipmentStatus.Cancelled;

        _db.TrackingLogs.Add(new TrackingLog
        {
            Id = Guid.NewGuid(),
            ShipmentId = shipment.Id,
            TrackingNumber = shipment.TrackingNumber,
            Latitude = shipment.PickupLatitude,
            Longitude = shipment.PickupLongitude,
            StatusText = "Return to Origin (RTO): Consignment marked for return to sender warehouse",
            RecordedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        await _hub.Clients.Group(trackingNumber).SendAsync("ReceiveStatusUpdate", new
        {
            trackingNumber,
            status = shipment.Status.ToString(),
            timestamp = ToIsoUtc(DateTime.UtcNow)
        });

        return Ok(new { message = $"Consignment {trackingNumber} marked as Return to Origin (RTO).", status = shipment.Status.ToString() });
    }

    public record RateShipmentRequest(int Rating, string? Review);

    [HttpPost("{trackingNumber}/rate")]
    public async Task<IActionResult> RateShipment(string trackingNumber, [FromBody] RateShipmentRequest req)
    {
        var shipment = await _db.Shipments.FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);
        if (shipment == null)
            return NotFound(new { message = "Shipment not found" });

        if (req.Rating < 1 || req.Rating > 5)
            return BadRequest(new { message = "Rating must be between 1 and 5 stars" });

        shipment.CustomerRating = req.Rating;
        shipment.CustomerReview = req.Review;
        await _db.SaveChangesAsync();

        return Ok(new 
        { 
            message = "Thank you for your rating!", 
            rating = shipment.CustomerRating, 
            review = shipment.CustomerReview 
        });
    }

    [HttpGet("rider/wallet")]
    public async Task<IActionResult> GetRiderWallet()
    {
        var deliveredShipments = await _db.Shipments
            .Include(s => s.Invoice)
            .Where(s => s.Status == ShipmentStatus.Delivered)
            .ToListAsync();

        var totalCompleted = deliveredShipments.Count;
        var totalCodCash = deliveredShipments
            .Where(s => s.Invoice != null && s.Invoice.PaymentMethod == PaymentMethod.CashOnDelivery)
            .Sum(s => s.Invoice!.TotalAmount);

        var approvedDeposits = await _db.CashSettlements
            .Where(s => s.Status == "Approved")
            .SumAsync(s => (decimal?)s.Amount) ?? 0m;

        var pendingSettlement = await _db.CashSettlements
            .OrderByDescending(s => s.RequestedAt)
            .FirstOrDefaultAsync(s => s.Status == "Pending");

        var lastRejectedSettlement = await _db.CashSettlements
            .OrderByDescending(s => s.ApprovedAt ?? s.RequestedAt)
            .FirstOrDefaultAsync(s => s.Status == "Rejected");

        var hasPendingDeposit = pendingSettlement != null;
        var unsettledCash = Math.Max(0m, totalCodCash - approvedDeposits);
        var totalEarnings = deliveredShipments.Sum(s => s.RiderEarnings ?? 80.00m);
        var activeCount = await _db.Shipments.CountAsync(s => s.Status != ShipmentStatus.Delivered && s.Status != ShipmentStatus.Cancelled);

        return Ok(new
        {
            totalCompleted,
            totalCodCash,
            totalEarnings,
            unsettledCash,
            approvedDeposits,
            hasPendingDeposit,
            pendingSettlement = pendingSettlement != null ? new
            {
                pendingSettlement.Id,
                pendingSettlement.DepositSlipNumber,
                pendingSettlement.Amount,
                pendingSettlement.RiderName,
                RequestedAt = ToIsoUtc(pendingSettlement.RequestedAt)
            } : null,
            lastRejectedDeposit = (pendingSettlement == null && lastRejectedSettlement != null) ? new
            {
                lastRejectedSettlement.Id,
                lastRejectedSettlement.DepositSlipNumber,
                lastRejectedSettlement.Amount,
                RejectedAt = ToIsoUtc(lastRejectedSettlement.ApprovedAt ?? lastRejectedSettlement.RequestedAt)
            } : null,
            activeDeliveries = activeCount,
            recentDeliveries = deliveredShipments.OrderByDescending(s => s.DeliveredAt).Take(5).Select(s => new
            {
                s.TrackingNumber,
                s.ReceiverName,
                Amount = s.Invoice?.TotalAmount ?? 0,
                DeliveredAt = ToIsoUtc(s.DeliveredAt),
                HasSignature = !string.IsNullOrEmpty(s.ProofOfDeliverySignature),
                Rating = s.CustomerRating
            })
        });
    }

    public record SettleCashRequest(decimal Amount, string? HubNotes, string? RiderName);

    [HttpPost("rider/settle-cash")]
    public async Task<IActionResult> SettleCash([FromBody] SettleCashRequest req)
    {
        // Guard against duplicate pending deposits
        var existingPending = await _db.CashSettlements
            .FirstOrDefaultAsync(s => s.Status == "Pending");

        if (existingPending != null)
        {
            return BadRequest(new { 
                message = $"Deposit slip {existingPending.DepositSlipNumber} (Rs. {existingPending.Amount}) is currently pending Hub Cashier approval. You cannot submit again until it is approved or rejected." 
            });
        }

        // Find delivered COD shipments that make up this cash deposit
        var deliveredCod = await _db.Shipments
            .Include(x => x.Invoice)
            .Where(x => x.Status == ShipmentStatus.Delivered && x.Invoice != null && x.Invoice.PaymentMethod == PaymentMethod.CashOnDelivery)
            .OrderByDescending(x => x.DeliveredAt)
            .ToListAsync();

        var linkedTrk = new List<string>();
        var exactSingleMatch = deliveredCod.FirstOrDefault(x => (x.Invoice?.TotalAmount ?? 0) == req.Amount);
        if (exactSingleMatch != null)
        {
            linkedTrk.Add(exactSingleMatch.TrackingNumber);
        }
        else
        {
            decimal sum = 0m;
            foreach (var sh in deliveredCod)
            {
                var amt = sh.Invoice?.TotalAmount ?? 0;
                if (amt <= 0) continue;
                if (sum + amt <= req.Amount || linkedTrk.Count == 0)
                {
                    linkedTrk.Add(sh.TrackingNumber);
                    sum += amt;
                    if (sum >= req.Amount) break;
                }
            }
        }

        var slip = $"DEP-{Random.Shared.Next(100000, 999999)}";
        var settlement = new CashSettlement
        {
            DepositSlipNumber = slip,
            RiderName = !string.IsNullOrWhiteSpace(req.RiderName) ? req.RiderName : "Tariq Mehmood (Fleet Rider)",
            Amount = req.Amount,
            Notes = req.HubNotes ?? "Cash handover to Hub Cashier",
            Status = "Pending",
            RequestedAt = DateTime.UtcNow,
            TrackingNumbers = string.Join(",", linkedTrk)
        };

        _db.CashSettlements.Add(settlement);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Cash deposit submitted for Hub Reconciliation!",
            settledAmount = req.Amount,
            settledAt = ToIsoUtc(DateTime.UtcNow),
            depositSlip = slip,
            status = "Pending",
            trackingNumbers = settlement.TrackingNumbers
        });
    }

    [HttpGet("settlements/{id}/breakdown")]
    public async Task<IActionResult> GetSettlementBreakdown(Guid id)
    {
        var s = await _db.CashSettlements.FindAsync(id);
        if (s == null) return NotFound(new { message = "Settlement not found" });

        // Retrieve delivered COD consignments
        var allDelivered = await _db.Shipments
            .Include(x => x.Invoice)
            .Include(x => x.Rider)
            .Where(x => x.Status == ShipmentStatus.Delivered && x.Invoice != null && x.Invoice.PaymentMethod == PaymentMethod.CashOnDelivery)
            .OrderByDescending(x => x.DeliveredAt)
            .ToListAsync();

        var matched = new List<Shipment>();

        // 1. If explicit TrackingNumbers are recorded in settlement, load ONLY those exact consignments
        if (!string.IsNullOrWhiteSpace(s.TrackingNumbers))
        {
            var trkSet = s.TrackingNumbers.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            matched = allDelivered.Where(x => trkSet.Contains(x.TrackingNumber, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        // 2. Fallback: match strictly by deposit amount and requested time window
        if (!matched.Any())
        {
            var candidatePool = allDelivered
                .Where(x => x.DeliveredAt == null || x.DeliveredAt <= s.RequestedAt.AddMinutes(15))
                .ToList();

            var exactMatch = candidatePool.FirstOrDefault(x => (x.Invoice?.TotalAmount ?? 0) == s.Amount);
            if (exactMatch != null)
            {
                matched.Add(exactMatch);
            }
            else
            {
                decimal currentSum = 0m;
                foreach (var sh in candidatePool)
                {
                    var amt = sh.Invoice?.TotalAmount ?? 0;
                    if (amt <= 0) continue;
                    if (currentSum + amt <= s.Amount || matched.Count == 0)
                    {
                        matched.Add(sh);
                        currentSum += amt;
                        if (currentSum >= s.Amount) break;
                    }
                }
            }
        }

        var consignments = matched.Select(c => new
        {
            c.TrackingNumber,
            c.SenderName,
            c.SenderPhone,
            c.PickupAddress,
            c.ReceiverName,
            c.ReceiverPhone,
            c.DeliveryAddress,
            c.WeightKg,
            DeliveredAt = ToIsoUtc(c.DeliveredAt),
            CodAmount = c.Invoice?.TotalAmount ?? 0,
            PaymentMethod = c.Invoice?.PaymentMethod.ToString() ?? "CashOnDelivery",
            DeliveryPin = c.DeliveryPin ?? "****",
            HasSignature = !string.IsNullOrEmpty(c.ProofOfDeliverySignature),
            SignaturePreview = c.ProofOfDeliverySignature,
            CustomerRating = c.CustomerRating,
            CustomerReview = c.CustomerReview,
            RiderName = c.Rider?.FullName ?? s.RiderName,
            Vehicle = c.Rider?.VehicleType ?? "Honda CD 70",
            PlateNumber = c.Rider?.PlateNumber ?? "LEE-9214"
        }).ToList();

        return Ok(new
        {
            settlement = new
            {
                s.Id,
                s.DepositSlipNumber,
                s.RiderName,
                s.Amount,
                s.Notes,
                s.Status,
                s.TrackingNumbers,
                RequestedAt = ToIsoUtc(s.RequestedAt),
                ApprovedAt = ToIsoUtc(s.ApprovedAt),
                s.ApprovedBy
            },
            totalParcels = consignments.Count,
            totalCodAmount = consignments.Sum(c => c.CodAmount),
            consignments
        });
    }

    [HttpGet("settlements")]
    public async Task<IActionResult> GetSettlements()
    {
        var list = await _db.CashSettlements
            .OrderByDescending(s => s.RequestedAt)
            .Select(s => new
            {
                s.Id,
                s.DepositSlipNumber,
                s.RiderName,
                s.Amount,
                s.Notes,
                s.Status,
                RequestedAt = ToIsoUtc(s.RequestedAt),
                ApprovedAt = ToIsoUtc(s.ApprovedAt),
                s.ApprovedBy
            })
            .ToListAsync();

        return Ok(list);
    }

    [HttpPost("settlements/{id}/approve")]
    public async Task<IActionResult> ApproveSettlement(Guid id)
    {
        var s = await _db.CashSettlements.FindAsync(id);
        if (s == null) return NotFound(new { message = "Settlement not found" });

        s.Status = "Approved";
        s.ApprovedAt = DateTime.UtcNow;
        s.ApprovedBy = "Central Logistics Manager";
        await _db.SaveChangesAsync();

        return Ok(new { message = "Cash deposit verified and approved!", status = "Approved" });
    }

    [HttpPost("settlements/{id}/reject")]
    public async Task<IActionResult> RejectSettlement(Guid id)
    {
        var s = await _db.CashSettlements.FindAsync(id);
        if (s == null) return NotFound(new { message = "Settlement not found" });

        s.Status = "Rejected";
        s.ApprovedAt = DateTime.UtcNow;
        s.ApprovedBy = "Central Logistics Manager";
        await _db.SaveChangesAsync();

        return Ok(new { message = "Cash deposit rejected.", status = "Rejected" });
    }
}
