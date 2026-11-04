using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShipTrack.Api.Data;
using ShipTrack.Api.Models;

namespace ShipTrack.Api.Hubs;

public class TrackingHub : Hub
{
    private readonly ShipTrackDbContext _db;

    public TrackingHub(ShipTrackDbContext db)
    {
        _db = db;
    }

    // Customer or Rider joins a private group dedicated to this Tracking Number
    public async Task JoinTrackingGroup(string trackingNumber)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, trackingNumber);
    }

    // Rider sends GPS location update -> Broadcasts immediately to all customers tracking this parcel
    public async Task SendLocationUpdate(string trackingNumber, double latitude, double longitude)
    {
        // 1. Broadcast immediately to anyone listening to this specific tracking number
        await Clients.Group(trackingNumber).SendAsync("ReceiveLocationUpdate", new
        {
            trackingNumber,
            latitude,
            longitude,
            timestamp = DateTime.UtcNow
        });

        // 2. Also broadcast globally so active maps stay live
        await Clients.All.SendAsync("GlobalLocationUpdate", new
        {
            trackingNumber,
            latitude,
            longitude,
            timestamp = DateTime.UtcNow
        });

        try
        {
            var shipment = await _db.Shipments
                .Include(s => s.Rider)
                .FirstOrDefaultAsync(s => s.TrackingNumber == trackingNumber);

            if (shipment != null)
            {
                _db.TrackingLogs.Add(new TrackingLog
                {
                    Id = Guid.NewGuid(),
                    ShipmentId = shipment.Id,
                    TrackingNumber = shipment.TrackingNumber,
                    Latitude = latitude,
                    Longitude = longitude,
                    StatusText = "Rider moving towards delivery address",
                    RecordedAt = DateTime.UtcNow
                });

                if (shipment.Rider != null)
                {
                    shipment.Rider.CurrentLatitude = latitude;
                    shipment.Rider.CurrentLongitude = longitude;
                    shipment.Rider.LastLocationUpdate = DateTime.UtcNow;
                }

                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TRACKING HUB] Tracking history record notice: {ex.Message}");
        }
    }

    // Update status (e.g. Delivered, Picked Up)
    public async Task UpdateDeliveryStatus(string trackingNumber, string statusText)
    {
        await Clients.Group(trackingNumber).SendAsync("ReceiveStatusUpdate", new
        {
            trackingNumber,
            status = statusText,
            timestamp = DateTime.UtcNow
        });

        await Clients.All.SendAsync("GlobalStatusUpdate", new
        {
            trackingNumber,
            status = statusText,
            timestamp = DateTime.UtcNow
        });
    }
}
