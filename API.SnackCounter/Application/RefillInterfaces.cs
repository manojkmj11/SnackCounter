using SnackCounter.Domain;

namespace SnackCounter.Application;

public interface IRefillStore
{
    void Create(Refill refill);
    Refill? Get(Guid refillId);
    IReadOnlyList<Refill> List(OfficeLocation? office = null, RefillStatus? status = null);
    IReadOnlyList<Refill> GetExpiredAssignments(DateTimeOffset now);
    IReadOnlyList<Refill> GetPurchasesInMonth(DateOnly month);
    bool TryAssign(Guid refillId, long expectedVersion, Volunteer volunteer, DateTimeOffset expiresAt);
    bool TryReassignExpired(Guid refillId, long expectedVersion, DateTimeOffset now, Volunteer nextVolunteer, TimeSpan assignmentTimeout);
    bool TryCompletePurchase(Guid refillId, long expectedVersion, DateTimeOffset purchasedAt, decimal amount, string description, byte[] receiptImage);
    bool MarkClaimSubmitted(Guid refillId, long expectedVersion, string hrClaimId);
    bool TryUpdateClaimStatus(Guid refillId, long expectedVersion, ReimbursementStatus status);
}

public interface IVolunteerDirectory
{
    Volunteer? GetVolunteer(string employeeId);
    IReadOnlyList<Volunteer> GetEligibleVolunteers(OfficeLocation office);
}

public interface IVolunteerSelector
{
    Volunteer? GetNextEligibleVolunteer(OfficeLocation office, string previousEmployeeId);
}

public interface ICurrencyConverter
{
    decimal Convert(decimal amount, Currency from, Currency to, DateOnly date);
}

public interface IChatTool
{
    void NotifyRefillCreated(Refill refill);
    void NotifyVolunteerAssigned(Refill refill, Volunteer volunteer);
    void NotifyVolunteerHandoff(Refill refill, Volunteer previous, Volunteer next);
    // Claim notification is retried after partial failures; implementations should deduplicate by refill ID.
    void NotifyClaimSubmitted(Refill refill);
}

public interface IHrSystem
{
    string SubmitClaim(HrReimbursementClaim claim);
}
