using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class OfficeVolunteerEligibility
{
    public OfficeLocation Office { get; set; }
    public string EmployeeId { get; set; } = string.Empty;
    public Volunteer Volunteer { get; set; } = null!;
    public DateOnly? ActiveFrom { get; set; }
    public DateOnly? ActiveUntil { get; set; }
}

public sealed class SnackCounterDbContext(DbContextOptions<SnackCounterDbContext> options) : DbContext(options)
{
    public DbSet<Refill> Refills => Set<Refill>();
    public DbSet<Volunteer> Volunteers => Set<Volunteer>();
    public DbSet<OfficeVolunteerEligibility> EmployeeOffices => Set<OfficeVolunteerEligibility>();
    public DbSet<Office> Offices => Set<Office>();
    public DbSet<AppRole> Roles => Set<AppRole>();
    public DbSet<EmployeeRole> EmployeeRoles => Set<EmployeeRole>();
    public DbSet<SnackItem> SnackItems => Set<SnackItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<EmployeeLeave> EmployeeLeaves => Set<EmployeeLeave>();
    public DbSet<RefillRotation> RefillRotations => Set<RefillRotation>();
    public DbSet<RefillAssignmentAttempt> RefillAssignmentAttempts => Set<RefillAssignmentAttempt>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ReimbursementClaim> ReimbursementClaims => Set<ReimbursementClaim>();
    public DbSet<MonthlyBudget> MonthlyBudgets => Set<MonthlyBudget>();
    public DbSet<MonthlyReport> MonthlyReports => Set<MonthlyReport>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedHrWebhook> ProcessedHrWebhooks => Set<ProcessedHrWebhook>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SnackCounterDbContext).Assembly);
}
