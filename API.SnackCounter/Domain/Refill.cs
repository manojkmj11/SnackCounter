namespace SnackCounter.Domain;

public enum OfficeLocation
{
    Oslo,
    Bengaluru
}

public enum Currency
{
    NOK,
    INR
}

public enum RefillStatus
{
    Open,
    Assigned,
    Purchased,
    ClaimSubmitted,
    ClaimRejected,
    Approved,
    Reimbursed,
    Denied
}

public enum ReimbursementStatus
{
    Approved,
    Rejected,
    Reimbursed
}

public static class OfficeFinance
{
    public static Currency BudgetCurrency(OfficeLocation office) => office switch
    {
        OfficeLocation.Oslo => Currency.NOK,
        OfficeLocation.Bengaluru => Currency.INR,
        _ => throw new ArgumentOutOfRangeException(nameof(office))
    };

    public static decimal MonthlyBudget(OfficeLocation office) => office switch
    {
        OfficeLocation.Oslo => 6_000m,
        OfficeLocation.Bengaluru => 15_000m,
        _ => throw new ArgumentOutOfRangeException(nameof(office))
    };
}

public sealed record Volunteer(string EmployeeId, OfficeLocation HomeOffice, Currency PayrollCurrency)
{
    public string DisplayName { get; set; } = EmployeeId;
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Refill
{
    public Guid Id { get; set; }
    public OfficeLocation Office { get; set; }
    public string? RequesterEmployeeId { get; set; }
    public Guid? SnackItemId { get; set; }
    public string Item { get; set; } = string.Empty;
    public RefillStatus Status { get; set; }
    public Volunteer? AssignedVolunteer { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AssignmentExpiresAt { get; set; }
    public decimal? PurchaseAmount { get; set; }
    public Currency? PurchaseCurrency { get; set; }
    public DateTimeOffset? PurchasedAt { get; set; }
    public string? PurchaseDescription { get; set; }
    public byte[]? ReceiptImage { get; set; }
    public string? HrClaimId { get; set; }
    public long Version { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public Refill() { }

    public Refill(Guid id, OfficeLocation office, string item, DateTimeOffset createdAt)
    {
        Id = id;
        Office = office;
        Item = item;
        CreatedAt = createdAt;
        Status = RefillStatus.Open;
    }

    public Refill Copy() => new()
    {
        Id = Id,
        Office = Office,
        RequesterEmployeeId = RequesterEmployeeId,
        SnackItemId = SnackItemId,
        Item = Item,
        Status = Status,
        AssignedVolunteer = AssignedVolunteer,
        CreatedAt = CreatedAt,
        AssignmentExpiresAt = AssignmentExpiresAt,
        PurchaseAmount = PurchaseAmount,
        PurchaseCurrency = PurchaseCurrency,
        PurchasedAt = PurchasedAt,
        PurchaseDescription = PurchaseDescription,
        ReceiptImage = ReceiptImage?.ToArray(),
        HrClaimId = HrClaimId,
        Version = Version,
        RowVersion = RowVersion.ToArray()
    };
}

public sealed record HrReimbursementClaim(
    Guid RefillId,
    string EmployeeId,
    decimal Amount,
    Currency Currency,
    string Description,
    byte[] ReceiptImage);
