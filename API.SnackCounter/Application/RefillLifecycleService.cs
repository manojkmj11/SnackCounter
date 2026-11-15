using SnackCounter.Domain;

namespace SnackCounter.Application;

public sealed class RefillLifecycleService
{
    private readonly IRefillStore _store;
    private readonly IVolunteerSelector _volunteers;
    private readonly IChatTool _chat;
    private readonly IHrSystem _hrSystem;
    private readonly ICurrencyConverter _currencyConverter;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _assignmentTimeout;

    public RefillLifecycleService(
        IRefillStore store,
        IVolunteerSelector volunteers,
        IChatTool chat,
        IHrSystem hrSystem,
        TimeProvider clock,
        TimeSpan assignmentTimeout,
        ICurrencyConverter currencyConverter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(volunteers);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(hrSystem);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(currencyConverter);
        if (assignmentTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(assignmentTimeout));
        }

        _store = store;
        _volunteers = volunteers;
        _chat = chat;
        _hrSystem = hrSystem;
        _currencyConverter = currencyConverter;
        _clock = clock;
        _assignmentTimeout = assignmentTimeout;
    }

    public Refill CreateRefill(OfficeLocation office, string item)
    {
        if (!Enum.IsDefined(office))
        {
            throw new ArgumentOutOfRangeException(nameof(office));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        var refill = new Refill(Guid.NewGuid(), office, item.Trim(), _clock.GetUtcNow());
        _store.Create(refill);
        _chat.NotifyRefillCreated(refill.Copy());
        return refill.Copy();
    }

    public bool Commit(Guid refillId, Volunteer volunteer, long? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(volunteer);
        var now = _clock.GetUtcNow();
        var current = _store.Get(refillId);
        if (current is null)
        {
            return false;
        }
        if (expectedVersion is not null && current.Version != expectedVersion)
        {
            return false;
        }
        if (!_store.TryAssign(refillId, current.Version, volunteer, now + _assignmentTimeout))
        {
            return false;
        }

        var refill = GetRequired(refillId);
        _chat.NotifyVolunteerAssigned(refill, volunteer);
        return true;
    }

    public bool AssignNextVolunteer(Guid refillId, long? expectedVersion = null)
    {
        var refill = _store.Get(refillId);
        if (refill is null || refill.Status != RefillStatus.Open ||
            (expectedVersion is not null && refill.Version != expectedVersion))
        {
            return false;
        }

        var next = _volunteers.GetNextEligibleVolunteer(refill.Office, string.Empty);
        return next is not null && Commit(refillId, next);
    }

    public int ReassignExpiredRefills()
    {
        var now = _clock.GetUtcNow();
        var reassignedCount = 0;
        foreach (var expired in _store.GetExpiredAssignments(now))
        {
            var previous = expired.AssignedVolunteer;
            if (previous is null)
            {
                continue;
            }

            var next = _volunteers.GetNextEligibleVolunteer(expired.Office, previous.EmployeeId);
            if (next is null || next.EmployeeId == previous.EmployeeId)
            {
                continue;
            }

            if (_store.TryReassignExpired(expired.Id, expired.Version, now, next, _assignmentTimeout))
            {
                reassignedCount++;
                _chat.NotifyVolunteerHandoff(GetRequired(expired.Id), previous, next);
            }
        }
        return reassignedCount;
    }

    public void CompletePurchase(
        Guid refillId,
        string employeeId,
        decimal amount,
        string description,
        byte[] receiptImage,
        long? expectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Purchase amount must be greater than zero.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(receiptImage);
        if (receiptImage.Length == 0)
        {
            throw new ArgumentException("A receipt image is required.", nameof(receiptImage));
        }

        var refill = GetRequired(refillId);
        if (refill.AssignedVolunteer?.EmployeeId != employeeId)
        {
            throw new InvalidOperationException("Only the currently assigned volunteer can submit this purchase.");
        }

        if (refill.Status == RefillStatus.Assigned)
        {
            if (expectedVersion is not null && refill.Version != expectedVersion)
            {
                throw new InvalidOperationException("The refill changed; reload it and retry with the latest version.");
            }
            var currentVersion = refill.Version;
            // The assignment gives each office a budget currency but no purchase-currency input,
            // so purchases are recorded in the office's budget currency.
            var recorded = _store.TryCompletePurchase(
                refillId,
                currentVersion,
                _clock.GetUtcNow(),
                amount,
                description.Trim(),
                receiptImage.ToArray());
            refill = GetRequired(refillId);
            if (!recorded && refill.Status == RefillStatus.Assigned && refill.Version == currentVersion)
            {
                throw new InvalidOperationException("The purchase would exceed the office monthly budget.");
            }
        }

        if ((refill.Status != RefillStatus.Purchased && refill.Status != RefillStatus.ClaimSubmitted) ||
            refill.PurchaseAmount != amount ||
            refill.PurchaseDescription != description.Trim() ||
            refill.ReceiptImage is null ||
            !refill.ReceiptImage.SequenceEqual(receiptImage))
        {
            throw new InvalidOperationException("The refill must be assigned or have the same purchase recorded for retry.");
        }

        // A retry after HR accepted the claim must not create another claim.
        if (refill.Status == RefillStatus.ClaimSubmitted)
        {
            _chat.NotifyClaimSubmitted(refill);
            return;
        }

        var buyer = refill.AssignedVolunteer!;
        var purchaseDate = DateOnly.FromDateTime(refill.PurchasedAt!.Value.UtcDateTime);
        var reimbursementAmount = _currencyConverter.Convert(
            refill.PurchaseAmount.Value,
            refill.PurchaseCurrency!.Value,
            buyer.PayrollCurrency,
            purchaseDate);
        var claim = new HrReimbursementClaim(
            refill.Id,
            buyer.EmployeeId,
            reimbursementAmount,
            buyer.PayrollCurrency,
            refill.PurchaseDescription,
            refill.ReceiptImage.ToArray());
        var claimId = _hrSystem.SubmitClaim(claim);
        if (!_store.MarkClaimSubmitted(refillId, refill.Version, claimId))
        {
            var latest = GetRequired(refillId);
            if (latest.Status != RefillStatus.ClaimSubmitted || latest.HrClaimId != claimId)
            {
                throw new InvalidOperationException("The purchase was recorded, but the reimbursement claim could not be recorded.");
            }
        }

        _chat.NotifyClaimSubmitted(GetRequired(refillId));
    }

    public void UpdateClaimStatus(Guid refillId, ReimbursementStatus status, long? expectedVersion = null)
    {
        var current = _store.Get(refillId);
        if (current is null || (expectedVersion is not null && current.Version != expectedVersion) ||
            !_store.TryUpdateClaimStatus(refillId, current.Version, status))
        {
            throw new InvalidOperationException($"The reimbursement status cannot transition to {status}.");
        }
    }

    private Refill GetRequired(Guid refillId) =>
        _store.Get(refillId) ?? throw new KeyNotFoundException($"Refill '{refillId}' was not found.");
}
