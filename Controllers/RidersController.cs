using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShipTrack.Api.Data;
using ShipTrack.Api.Models;

namespace ShipTrack.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RidersController : ControllerBase
{
    private readonly ShipTrackDbContext _db;

    public RidersController(ShipTrackDbContext db)
    {
        _db = db;
    }

    // 1. Get all fleet riders
    [HttpGet]
    public async Task<IActionResult> GetAllRiders()
    {
        var riders = await _db.Riders
            .OrderBy(r => r.FullName)
            .Select(r => new
            {
                r.Id,
                r.FullName,
                r.Email,
                r.Phone,
                r.VehicleType,
                r.PlateNumber,
                r.IsActive,
                r.CurrentLatitude,
                r.CurrentLongitude,
                r.LastLocationUpdate,
                ActiveShipmentsCount = _db.Shipments.Count(s => s.RiderId == r.Id && s.Status != ShipmentStatus.Delivered && s.Status != ShipmentStatus.Cancelled)
            })
            .ToListAsync();

        return Ok(riders);
    }

    // 2. Get Rider profile by email or user name
    [HttpGet("profile")]
    public async Task<IActionResult> GetRiderProfile([FromQuery] string? email, [FromQuery] string? name)
    {
        Rider? rider = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            rider = await _db.Riders.FirstOrDefaultAsync(r => r.Email != null && r.Email.ToLower() == email.Trim().ToLower());
        }

        if (rider == null && !string.IsNullOrWhiteSpace(name))
        {
            rider = await _db.Riders.FirstOrDefaultAsync(r => r.FullName.ToLower() == name.Trim().ToLower());
        }

        if (rider == null)
        {
            // Fallback: pick the first rider or create default
            rider = await _db.Riders.FirstOrDefaultAsync();
        }

        if (rider == null)
        {
            return NotFound(new { message = "No rider profile found." });
        }

        return Ok(new
        {
            rider.Id,
            rider.FullName,
            rider.Email,
            rider.Phone,
            rider.VehicleType,
            rider.PlateNumber,
            rider.IsActive
        });
    }

    public record CreateRiderRequest(
        string FullName,
        string? Email,
        string Phone,
        string VehicleType,
        string PlateNumber
    );

    // 3. Add / Register New Fleet Rider
    [HttpPost]
    public async Task<IActionResult> CreateRider([FromBody] CreateRiderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FullName) || string.IsNullOrWhiteSpace(req.Phone))
        {
            return BadRequest(new { message = "Rider Full Name and Phone Number are required." });
        }

        var rider = new Rider
        {
            Id = Guid.NewGuid(),
            FullName = req.FullName.Trim(),
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            Phone = req.Phone.Trim(),
            VehicleType = string.IsNullOrWhiteSpace(req.VehicleType) ? "Honda CD 70" : req.VehicleType.Trim(),
            PlateNumber = string.IsNullOrWhiteSpace(req.PlateNumber) ? "LEE-9214" : req.PlateNumber.Trim(),
            IsActive = true,
            CurrentLatitude = 31.5204,
            CurrentLongitude = 74.3587,
            LastLocationUpdate = DateTime.UtcNow
        };

        _db.Riders.Add(rider);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = $"Fleet Rider '{rider.FullName}' successfully registered!",
            rider
        });
    }

    public record UpdateRiderRequest(
        string FullName,
        string? Email,
        string Phone,
        string VehicleType,
        string PlateNumber,
        bool IsActive
    );

    // 4. Update Rider & Bike Details
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateRider(Guid id, [FromBody] UpdateRiderRequest req)
    {
        var rider = await _db.Riders.FindAsync(id);
        if (rider == null) return NotFound(new { message = "Rider not found." });

        rider.FullName = req.FullName.Trim();
        rider.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        rider.Phone = req.Phone.Trim();
        rider.VehicleType = req.VehicleType.Trim();
        rider.PlateNumber = req.PlateNumber.Trim();
        rider.IsActive = req.IsActive;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Rider & vehicle details updated successfully!",
            rider
        });
    }

    public record AssignShipmentRequest(string TrackingNumber, Guid RiderId);

    // 5. Assign Consignment to Rider
    [HttpPost("assign")]
    public async Task<IActionResult> AssignShipment([FromBody] AssignShipmentRequest req)
    {
        var shipment = await _db.Shipments
            .Include(s => s.Rider)
            .FirstOrDefaultAsync(s => s.TrackingNumber == req.TrackingNumber);

        if (shipment == null) return NotFound(new { message = "Shipment not found." });

        if (shipment.Status == ShipmentStatus.Delivered)
        {
            return BadRequest(new { message = "Cannot assign or change courier: Consignment is already delivered and closed!" });
        }

        if (shipment.Status == ShipmentStatus.Cancelled)
        {
            return BadRequest(new { message = "Cannot assign courier: Consignment is cancelled." });
        }

        var rider = await _db.Riders.FindAsync(req.RiderId);
        if (rider == null) return NotFound(new { message = "Rider not found." });

        shipment.RiderId = rider.Id;
        shipment.Rider = rider;

        _db.TrackingLogs.Add(new TrackingLog
        {
            Id = Guid.NewGuid(),
            ShipmentId = shipment.Id,
            TrackingNumber = shipment.TrackingNumber,
            Latitude = shipment.PickupLatitude,
            Longitude = shipment.PickupLongitude,
            StatusText = $"Assigned to Courier: {rider.FullName} ({rider.VehicleType} [{rider.PlateNumber}])",
            RecordedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = $"Consignment {shipment.TrackingNumber} assigned to {rider.FullName}!",
            rider = new
            {
                rider.FullName,
                rider.Phone,
                rider.VehicleType,
                rider.PlateNumber
            }
        });
    }
}
