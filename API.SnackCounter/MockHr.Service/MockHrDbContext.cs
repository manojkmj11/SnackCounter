using Microsoft.EntityFrameworkCore;

namespace MockHr.Service;

public sealed class MockHrDbContext(DbContextOptions<MockHrDbContext> options) : DbContext(options)
{
    public DbSet<ExternalClaim> Claims => Set<ExternalClaim>();
    public DbSet<WebhookOutbox> WebhookOutbox => Set<WebhookOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExternalClaim>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EmployeeId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Office).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(19, 4);
            entity.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2_000).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.DecisionReason).HasMaxLength(2_000);
            entity.Property(x => x.ReceiptBase64).HasColumnType("nvarchar(max)");
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.SubmittedAt });
        });
        modelBuilder.Entity<WebhookOutbox>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2_000);
            entity.HasIndex(x => new { x.DeliveredAt, x.AvailableAt, x.LockedUntil });
            entity.HasIndex(x => x.EventId).IsUnique();
            entity.HasOne<ExternalClaim>().WithMany().HasForeignKey(x => x.ExternalClaimId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class ExternalClaim
{
    public Guid Id { get; set; }
    public Guid SnackCounterClaimId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string Office { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ReceiptBase64 { get; set; }
    public string? ReceiptMediaType { get; set; }
    public string Status { get; set; } = "Pending";
    public string? DecisionReason { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WebhookOutbox
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid ExternalClaimId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
