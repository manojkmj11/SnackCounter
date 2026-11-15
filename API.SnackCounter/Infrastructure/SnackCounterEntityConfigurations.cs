using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class OfficeConfiguration : IEntityTypeConfiguration<Office>
{
    public void Configure(EntityTypeBuilder<Office> entity)
    {
        entity.ToTable("Offices");
        entity.HasKey(office => office.Location);
        entity.Property(office => office.Name).HasMaxLength(100).IsRequired();
        entity.Property(office => office.TimeZoneId).HasMaxLength(100).IsRequired();
        entity.Property(office => office.MonthlyBudget).HasPrecision(19, 4);
        entity.Property(office => office.WarningThresholdPercent).HasPrecision(5, 2);
        entity.Property(office => office.RowVersion).IsRowVersion();
        entity.HasData(
            new Office
            {
                Location = OfficeLocation.Oslo,
                Name = "Oslo",
                TimeZoneId = "Europe/Oslo",
                BudgetCurrency = Currency.NOK,
                MonthlyBudget = 6_000m,
                WarningThresholdPercent = 80m,
                IsActive = true
            },
            new Office
            {
                Location = OfficeLocation.Bengaluru,
                Name = "Bengaluru",
                TimeZoneId = "Asia/Kolkata",
                BudgetCurrency = Currency.INR,
                MonthlyBudget = 15_000m,
                WarningThresholdPercent = 80m,
                IsActive = true
            });
    }
}

public sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    public void Configure(EntityTypeBuilder<AppRole> entity)
    {
        entity.ToTable("Roles");
        entity.HasKey(role => role.Name);
        entity.Property(role => role.Name).HasMaxLength(50);
        entity.Property(role => role.Description).HasMaxLength(200).IsRequired();
        entity.HasData(
            new AppRole { Name = "Employee", Description = "Employee snack counter access" },
            new AppRole { Name = "HR", Description = "HR reimbursement queue and decisions" },
            new AppRole { Name = "Manager", Description = "Combined management reports" });
    }
}

public sealed class EmployeeRoleConfiguration : IEntityTypeConfiguration<EmployeeRole>
{
    public void Configure(EntityTypeBuilder<EmployeeRole> entity)
    {
        entity.ToTable("EmployeeRoles");
        entity.HasKey(employeeRole => new { employeeRole.EmployeeId, employeeRole.RoleName });
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(employeeRole => employeeRole.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<AppRole>().WithMany().HasForeignKey(employeeRole => employeeRole.RoleName)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(employeeRole => employeeRole.RoleName);
    }
}

public sealed class VolunteerConfiguration : IEntityTypeConfiguration<Volunteer>
{
    public void Configure(EntityTypeBuilder<Volunteer> entity)
    {
        entity.ToTable("Employees");
        entity.HasKey(employee => employee.EmployeeId);
        entity.Property(employee => employee.DisplayName).HasMaxLength(200).IsRequired();
        entity.Property(employee => employee.Email).HasMaxLength(320);
        entity.Property(employee => employee.IsActive).HasDefaultValue(true);
        entity.HasOne<Office>().WithMany()
            .HasForeignKey(employee => employee.HomeOffice)
            .HasPrincipalKey(office => office.Location)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(employee => employee.Email).IsUnique().HasFilter("[Email] IS NOT NULL");
    }
}

public sealed class EmployeeOfficeConfiguration : IEntityTypeConfiguration<OfficeVolunteerEligibility>
{
    public void Configure(EntityTypeBuilder<OfficeVolunteerEligibility> entity)
    {
        entity.ToTable("EmployeeOffices");
        entity.HasKey(entry => new { entry.Office, entry.EmployeeId });
        entity.HasOne(entry => entry.Volunteer).WithMany()
            .HasForeignKey(entry => entry.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany()
            .HasForeignKey(entry => entry.Office)
            .HasPrincipalKey(office => office.Location)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(entry => entry.EmployeeId);
    }
}

public sealed class RefillConfiguration : IEntityTypeConfiguration<Refill>
{
    public void Configure(EntityTypeBuilder<Refill> entity)
    {
        entity.HasKey(refill => refill.Id);
        entity.Property(refill => refill.PurchaseAmount).HasPrecision(19, 4);
        entity.Property(refill => refill.Version).IsConcurrencyToken();
        entity.Property(refill => refill.RowVersion).IsRowVersion();
        entity.HasOne(refill => refill.AssignedVolunteer).WithMany()
            .HasForeignKey("AssignedVolunteerEmployeeId")
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany()
            .HasForeignKey(refill => refill.Office)
            .HasPrincipalKey(office => office.Location)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Volunteer>().WithMany()
            .HasForeignKey(refill => refill.RequesterEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SnackItem>().WithMany()
            .HasForeignKey(refill => refill.SnackItemId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(refill => new { refill.Office, refill.PurchasedAt });
        entity.HasIndex(refill => new { refill.Office, refill.SnackItemId })
            .IsUnique().HasFilter("[SnackItemId] IS NOT NULL AND [Status] IN (0, 1)");
    }
}

public sealed class SnackItemConfiguration : IEntityTypeConfiguration<SnackItem>
{
    public void Configure(EntityTypeBuilder<SnackItem> entity)
    {
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Name).HasMaxLength(150).IsRequired();
        entity.Property(item => item.Unit).HasMaxLength(30).IsRequired();
        entity.Property(item => item.QuantityOnHand).HasPrecision(19, 3);
        entity.Property(item => item.ReorderThreshold).HasPrecision(19, 3);
        entity.Property(item => item.RowVersion).IsRowVersion();
        entity.HasOne<Office>().WithMany().HasForeignKey(item => item.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(item => new { item.Office, item.Name }).IsUnique();
    }
}

public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> entity)
    {
        entity.HasKey(movement => movement.Id);
        entity.Property(movement => movement.Quantity).HasPrecision(19, 3);
        entity.Property(movement => movement.ReportedConsumedQuantity).HasPrecision(19, 3);
        entity.Property(movement => movement.UnitCost).HasPrecision(19, 4);
        entity.Property(movement => movement.Note).HasMaxLength(500);
        entity.HasOne(movement => movement.SnackItem).WithMany()
            .HasForeignKey(movement => movement.SnackItemId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(movement => movement.ActorEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Refill>().WithMany().HasForeignKey(movement => movement.RefillId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(movement => new { movement.SnackItemId, movement.OccurredAt });
    }
}

public sealed class EmployeeLeaveConfiguration : IEntityTypeConfiguration<EmployeeLeave>
{
    public void Configure(EntityTypeBuilder<EmployeeLeave> entity)
    {
        entity.HasKey(leave => leave.Id);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(leave => leave.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(leave => leave.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(leave => new { leave.EmployeeId, leave.StartDate, leave.EndDate });
    }
}

public sealed class RefillRotationConfiguration : IEntityTypeConfiguration<RefillRotation>
{
    public void Configure(EntityTypeBuilder<RefillRotation> entity)
    {
        entity.HasKey(rotation => rotation.Office);
        entity.Property(rotation => rotation.RowVersion).IsRowVersion();
        entity.HasOne<Office>().WithMany().HasForeignKey(rotation => rotation.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(rotation => rotation.LastEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RefillAssignmentAttemptConfiguration : IEntityTypeConfiguration<RefillAssignmentAttempt>
{
    public void Configure(EntityTypeBuilder<RefillAssignmentAttempt> entity)
    {
        entity.HasKey(attempt => attempt.Id);
        entity.HasOne<Refill>().WithMany().HasForeignKey(attempt => attempt.RefillId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(attempt => attempt.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(attempt => attempt.WorkingOffice)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(attempt => new { attempt.RefillId, attempt.EmployeeId, attempt.LocalDate }).IsUnique();
        entity.HasIndex(attempt => new { attempt.RefillId, attempt.LocalDate, attempt.AttemptNumber }).IsUnique();
    }
}

public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> entity)
    {
        entity.HasKey(conversation => conversation.Id);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(conversation => conversation.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(conversation => conversation.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(conversation => conversation.EmployeeId)
            .IsUnique().HasFilter("[Kind] = 0 AND [EmployeeId] IS NOT NULL");
        entity.HasIndex(conversation => conversation.Office)
            .IsUnique().HasFilter("[Kind] = 1 AND [Office] IS NOT NULL");
        entity.HasIndex(conversation => conversation.Kind)
            .IsUnique().HasFilter("[Kind] = 2");
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_Conversations_Audience",
            "([Kind] = 0 AND [EmployeeId] IS NOT NULL AND [Office] IS NULL) OR " +
            "([Kind] = 1 AND [EmployeeId] IS NULL AND [Office] IS NOT NULL) OR " +
            "([Kind] = 2 AND [EmployeeId] IS NULL AND [Office] IS NULL)"));
    }
}

public sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> entity)
    {
        entity.HasKey(message => message.Id);
        entity.Property(message => message.Text).HasMaxLength(8_000);
        entity.Property(message => message.PayloadJson).HasColumnType("nvarchar(max)");
        entity.HasOne(message => message.Conversation).WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(message => message.SenderEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(message => message.RecipientEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(message => message.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(message => new { message.ConversationId, message.CreatedAt, message.Id });
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_ConversationMessages_Audience",
            "([Audience] = 0 AND [RecipientEmployeeId] IS NOT NULL AND [Office] IS NULL) OR " +
            "([Audience] = 1 AND [RecipientEmployeeId] IS NULL AND [Office] IS NOT NULL) OR " +
            "([Audience] = 2 AND [RecipientEmployeeId] IS NULL AND [Office] IS NULL)"));
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> entity)
    {
        entity.HasKey(notification => notification.Id);
        entity.Property(notification => notification.EventType).HasMaxLength(160).IsRequired();
        entity.Property(notification => notification.DeduplicationKey).HasMaxLength(200).IsRequired();
        entity.Property(notification => notification.PayloadJson).HasColumnType("nvarchar(max)");
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(notification => notification.RecipientEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(notification => notification.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ConversationMessage>().WithMany().HasForeignKey(notification => notification.ConversationMessageId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(notification => new { notification.RecipientEmployeeId, notification.CreatedAt });
        entity.HasIndex(notification => new { notification.Office, notification.CreatedAt });
        entity.HasIndex(notification => notification.DeduplicationKey).IsUnique();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_Notifications_Audience",
            "([RecipientEmployeeId] IS NOT NULL AND [Office] IS NULL AND [IsHrAudience] = 0) OR " +
            "([RecipientEmployeeId] IS NULL AND [Office] IS NOT NULL AND [IsHrAudience] = 0) OR " +
            "([RecipientEmployeeId] IS NULL AND [Office] IS NULL AND [IsHrAudience] = 1)"));
    }
}

public sealed class ReimbursementClaimConfiguration : IEntityTypeConfiguration<ReimbursementClaim>
{
    public void Configure(EntityTypeBuilder<ReimbursementClaim> entity)
    {
        entity.HasKey(claim => claim.Id);
        entity.Property(claim => claim.IdempotencyKey).HasMaxLength(128).IsRequired();
          entity.Property(claim => claim.PurchaseId).HasDefaultValueSql("NEWID()");
        entity.Property(claim => claim.Amount).HasPrecision(19, 4);
        entity.Property(claim => claim.PayrollAmount).HasPrecision(19, 4);
        entity.Property(claim => claim.ExchangeRate).HasPrecision(19, 8);
        entity.Property(claim => claim.Description).HasMaxLength(2_000).IsRequired();
        entity.Property(claim => claim.ReceiptStorageKey).HasMaxLength(500);
        entity.Property(claim => claim.ReceiptMediaType).HasMaxLength(100);
        entity.Property(claim => claim.ReceiptSha256).HasMaxLength(64);
        entity.Property(claim => claim.DecisionReason).HasMaxLength(2_000);
        entity.HasIndex(claim => claim.ExternalHrClaimId).IsUnique().HasFilter("[ExternalHrClaimId] IS NOT NULL");
        entity.Property(claim => claim.ExchangeRateSource).HasMaxLength(200);
        entity.Property(claim => claim.RowVersion).IsRowVersion();
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(claim => claim.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Volunteer>().WithMany().HasForeignKey(claim => claim.ProcessingByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Office>().WithMany().HasForeignKey(claim => claim.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ReimbursementClaim>().WithMany().HasForeignKey(claim => claim.PreviousClaimId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Refill>().WithMany().HasForeignKey(claim => claim.RefillId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(claim => new { claim.EmployeeId, claim.PurchaseId, claim.SubmissionAttempt }).IsUnique();
        entity.HasIndex(claim => new { claim.Status, claim.SubmittedAt });
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_ReimbursementClaims_SubmissionAttempt", "[SubmissionAttempt] >= 1"));
    }
}

public sealed class ProcessedHrWebhookConfiguration : IEntityTypeConfiguration<ProcessedHrWebhook>
{
    public void Configure(EntityTypeBuilder<ProcessedHrWebhook> entity)
    {
        entity.HasKey(x => x.EventId);
        entity.HasOne<ReimbursementClaim>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.ClaimId, x.ReceivedAt });
    }
}

public sealed class MonthlyBudgetConfiguration : IEntityTypeConfiguration<MonthlyBudget>
{
    public void Configure(EntityTypeBuilder<MonthlyBudget> entity)
    {
        entity.HasKey(budget => new { budget.Office, budget.Year, budget.Month });
        entity.Property(budget => budget.BudgetAmount).HasPrecision(19, 4);
        entity.Property(budget => budget.SpentAmount).HasPrecision(19, 4);
        entity.Property(budget => budget.WarningThresholdPercent).HasPrecision(5, 2);
        entity.Property(budget => budget.RowVersion).IsRowVersion();
        entity.HasOne<Office>().WithMany().HasForeignKey(budget => budget.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_MonthlyBudgets_YearMonth",
            "[Year] >= 2000 AND [Month] BETWEEN 1 AND 12 AND [BudgetAmount] >= 0 AND [SpentAmount] >= 0"));
    }
}

public sealed class MonthlyReportConfiguration : IEntityTypeConfiguration<MonthlyReport>
{
    public void Configure(EntityTypeBuilder<MonthlyReport> entity)
    {
        entity.HasKey(report => report.Id);
        entity.Property(report => report.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.HasOne<Office>().WithMany().HasForeignKey(report => report.Office)
            .HasPrincipalKey(office => office.Location).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(report => new { report.Year, report.Month, report.Office })
            .IsUnique().HasFilter("[Office] IS NOT NULL");
        entity.HasIndex(report => new { report.Year, report.Month })
            .IsUnique().HasFilter("[Office] IS NULL");
    }
}

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> entity)
    {
        entity.HasKey(record => record.Id);
        entity.Property(record => record.ActorId).HasMaxLength(450).IsRequired();
        entity.Property(record => record.Action).HasMaxLength(160).IsRequired();
        entity.Property(record => record.EntityType).HasMaxLength(160).IsRequired();
        entity.Property(record => record.EntityId).HasMaxLength(450).IsRequired();
        entity.Property(record => record.ChangeSummaryJson).HasColumnType("nvarchar(max)");
        entity.HasIndex(record => new { record.EntityType, record.EntityId, record.OccurredAt });
        entity.HasIndex(record => new { record.ActorId, record.OccurredAt });
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> entity)
    {
        entity.HasKey(message => message.Id);
        entity.Property(message => message.EventType).HasMaxLength(160).IsRequired();
        entity.Property(message => message.PayloadJson).HasColumnType("nvarchar(max)").IsRequired();
        entity.Property(message => message.LastError).HasMaxLength(2_000);
        entity.HasIndex(message => message.EventId).IsUnique();
        entity.HasIndex(message => new { message.ProcessedAt, message.AvailableAt, message.LockedUntil });
    }
}
