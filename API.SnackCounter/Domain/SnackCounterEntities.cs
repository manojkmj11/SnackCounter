namespace SnackCounter.Domain;

public enum WorkingMode
{
    WFO,
    WFH
}

public enum StockMovementKind
{
    Restock,
    Consumption,
    Adjustment,
    Waste
}

public enum EmployeeLeaveStatus
{
    Planned,
    Confirmed,
    Cancelled
}

public enum RefillAttemptOutcome
{
    Offered,
    Accepted,
    SkippedLeave,
    SkippedWfh,
    WrongLocation,
    Expired,
    Declined,
    NoRosterAvailable,
    OfferAcknowledged
}

public enum RefillOfferState
{
    None,
    Offered,
    Acknowledged,
    Accepted
}

public enum ConversationKind
{
    Employee,
    Office,
    Hr
}

public enum MessageAudience
{
    Employee,
    Office,
    Hr
}

public enum ConversationMessageKind
{
    Text,
    Notification,
    Report
}

public enum NotificationDeliveryState
{
    Pending,
    Sent,
    Failed
}

public enum ReimbursementClaimStatus
{
    Submitted,
    Processing,
    Approved,
    Denied,
    Reimbursed
}

public enum MonthlyReportKind
{
    Office,
    Combined
}

public sealed class Office
{
    public OfficeLocation Location { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public Currency BudgetCurrency { get; set; }
    public decimal MonthlyBudget { get; set; }
    public decimal WarningThresholdPercent { get; set; } = 80m;
    public bool IsActive { get; set; } = true;
    public byte[] RowVersion { get; set; } = [];
}

public sealed class AppRole
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class EmployeeRole
{
    public string EmployeeId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

public sealed class SnackItem
{
    public Guid Id { get; set; }
    public OfficeLocation Office { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = "item";
    public decimal QuantityOnHand { get; set; }
    public decimal ReorderThreshold { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsClaimed { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class StockMovement
{
    public Guid Id { get; set; }
    public Guid SnackItemId { get; set; }
    public SnackItem SnackItem { get; set; } = null!;
    public StockMovementKind Kind { get; set; }
    public decimal Quantity { get; set; }
    public decimal? ReportedConsumedQuantity { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? ActorEmployeeId { get; set; }
    public Guid? RefillId { get; set; }
    public decimal? UnitCost { get; set; }
    public Currency? CostCurrency { get; set; }
    public string? Note { get; set; }
}

public sealed class EmployeeLeave
{
    public Guid Id { get; set; }
    public string EmployeeId { get; set; } = string.Empty;
    public OfficeLocation Office { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public EmployeeLeaveStatus Status { get; set; } = EmployeeLeaveStatus.Planned;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class RefillRotation
{
    public OfficeLocation Office { get; set; }
    public string? LastEmployeeId { get; set; }
    public DateOnly? LastAttemptedLocalDate { get; set; }
    public DateTimeOffset? LastSelectedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class RefillAssignmentAttempt
{
    public Guid Id { get; set; }
    public Guid RefillId { get; set; }
    public string EmployeeId { get; set; } = string.Empty;
    public DateOnly LocalDate { get; set; }
    public int AttemptNumber { get; set; }
    public RefillAttemptOutcome Outcome { get; set; }
    public WorkingMode? WorkingMode { get; set; }
    public OfficeLocation? WorkingOffice { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public DateTimeOffset? AssignmentExpiresAt { get; set; }
}

public sealed class ReimbursementClaim
{
    public Guid Id { get; set; }
    public string EmployeeId { get; set; } = string.Empty;
    public OfficeLocation Office { get; set; }
    public Guid PurchaseId { get; set; }
    public int SubmissionAttempt { get; set; } = 1;
    public Guid? PreviousClaimId { get; set; }
    public Guid? RefillId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Currency Currency { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ReceiptStorageKey { get; set; }
    public string? ReceiptMediaType { get; set; }
    public long? ReceiptSizeBytes { get; set; }
    public string? ReceiptSha256 { get; set; }
    public byte[]? ReceiptContent { get; set; }
    public ReimbursementClaimStatus Status { get; set; } = ReimbursementClaimStatus.Submitted;
    public string? ProcessingByEmployeeId { get; set; }
    public DateTimeOffset? ProcessingAt { get; set; }
    public DateTimeOffset? ProcessingLeaseExpiresAt { get; set; }
    public string? DecisionReason { get; set; }
    public Guid? ExternalHrClaimId { get; set; }
    public DateTimeOffset? ExternalHrSubmittedAt { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ReimbursedAt { get; set; }
    public decimal? PayrollAmount { get; set; }
    public Currency? PayrollCurrency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public DateTimeOffset? ExchangeRateAt { get; set; }
    public string? ExchangeRateSource { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public ConversationKind Kind { get; set; }
    public string? EmployeeId { get; set; }
    public OfficeLocation? Office { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public ICollection<ConversationMessage> Messages { get; set; } = new List<ConversationMessage>();
}

public sealed class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public string? SenderEmployeeId { get; set; }
    public string? RecipientEmployeeId { get; set; }
    public MessageAudience Audience { get; set; }
    public OfficeLocation? Office { get; set; }
    public ConversationMessageKind Kind { get; set; }
    public string? Text { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; }
    public string? RecipientEmployeeId { get; set; }
    public OfficeLocation? Office { get; set; }
    public bool IsHrAudience { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public NotificationDeliveryState DeliveryState { get; set; } = NotificationDeliveryState.Pending;
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class MonthlyBudget
{
    public OfficeLocation Office { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public Currency Currency { get; set; }
    public decimal BudgetAmount { get; set; }
    public decimal SpentAmount { get; set; }
    public decimal WarningThresholdPercent { get; set; }
    public DateTimeOffset SnapshotAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class MonthlyReport
{
    public Guid Id { get; set; }
    public MonthlyReportKind Kind { get; set; }
    public OfficeLocation? Office { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public Currency? ReportingCurrency { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class AuditRecord
{
    public long Id { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? ChangeSummaryJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class ProcessedHrWebhook
{
    public Guid EventId { get; set; }
    public Guid ClaimId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
