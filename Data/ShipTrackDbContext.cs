using Microsoft.EntityFrameworkCore;
using ShipTrack.Api.Models;

namespace ShipTrack.Api.Data;

public class ShipTrackDbContext : DbContext
{
    public ShipTrackDbContext(DbContextOptions<ShipTrackDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRoleMapping> UserRoles => Set<UserRoleMapping>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<Rider> Riders => Set<Rider>();
    public DbSet<TrackingLog> TrackingLogs => Set<TrackingLog>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<CashSettlement> CashSettlements => Set<CashSettlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Unique User Email
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        // Many-to-Many User <-> Role via UserRoles
        modelBuilder.Entity<UserRoleMapping>()
            .HasKey(ur => new { ur.UserId, ur.RoleId });

        modelBuilder.Entity<UserRoleMapping>()
            .HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserRoleMapping>()
            .HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique Tracking Number
        modelBuilder.Entity<Shipment>()
            .HasIndex(s => s.TrackingNumber)
            .IsUnique();

        // Unique Invoice Number
        modelBuilder.Entity<Invoice>()
            .HasIndex(i => i.InvoiceNumber)
            .IsUnique();

        // 1-to-1 Relationship Shipment <-> Invoice
        modelBuilder.Entity<Shipment>()
            .HasOne(s => s.Invoice)
            .WithOne(i => i.Shipment)
            .HasForeignKey<Invoice>(i => i.ShipmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // 1-to-Many Relationship Rider -> Shipments
        modelBuilder.Entity<Shipment>()
            .HasOne(s => s.Rider)
            .WithMany(r => r.AssignedShipments)
            .HasForeignKey(s => s.RiderId)
            .OnDelete(DeleteBehavior.SetNull);

        // 1-to-Many Relationship Shipment -> TrackingLogs
        modelBuilder.Entity<TrackingLog>()
            .HasOne(t => t.Shipment)
            .WithMany(s => s.TrackingLogs)
            .HasForeignKey(t => t.ShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
