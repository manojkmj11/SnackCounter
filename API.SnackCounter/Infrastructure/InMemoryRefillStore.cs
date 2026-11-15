using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class InMemoryRefillStore : IRefillStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Refill> _refills = new();

    public void Create(Refill refill)
    {
        ArgumentNullException.ThrowIfNull(refill);
        lock (_gate)
        {
            if (!_refills.TryAdd(refill.Id, refill.Copy()))
            {
                throw new InvalidOperationException($"Refill '{refill.Id}' already exists.");
            }
        }
    }

    public Refill? Get(Guid refillId)
    {
        lock (_gate)
        {
            return _refills.TryGetValue(refillId, out var refill) ? refill.Copy() : null;
        }
    }

    public IReadOnlyList<Refill> List(OfficeLocation? office = null, RefillStatus? status = null)
    {
        lock (_gate)
        {
            return _refills.Values
                .Where(refill => office is null || refill.Office == office)
                .Where(refill => status is null || refill.Status == status)
                .OrderBy(refill => refill.CreatedAt)
                .Select(refill => refill.Copy())
                .ToArray();
        }
    }


    public IReadOnlyList<Refill> GetExpiredAssignments(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _refills.Values
                .Where(refill => refill.Status == RefillStatus.Assigned && refill.AssignmentExpiresAt <= now)
                .Select(refill => refill.Copy())
                .ToArray();
        }
    }

    public IReadOnlyList<Refill> GetPurchasesInMonth(DateOnly month)
    {
        lock (_gate)
        {
            return _refills.Values
                .Where(refill => refill.PurchasedAt is DateTimeOffset purchasedAt &&
                    purchasedAt.UtcDateTime.Year == month.Year &&
                    purchasedAt.UtcDateTime.Month == month.Month)
                .Select(refill => refill.Copy())
                .ToArray();
        }
    }

    public bool TryAssign(Guid refillId, long expectedVersion, Volunteer volunteer, DateTimeOffset expiresAt)
    {
        lock (_gate)
        {
            if (!_refills.TryGetValue(refillId, out var refill) || refill.Version != expectedVersion || refill.Status != RefillStatus.Open)
            {
                return false;
            }

            refill.AssignedVolunteer = volunteer;
            refill.AssignmentExpiresAt = expiresAt;
            refill.Status = RefillStatus.Assigned;
            refill.Version++;
            return true;
        }
    }

    public bool TryReassignExpired(Guid refillId, long expectedVersion, DateTimeOffset now, Volunteer nextVolunteer, TimeSpan assignmentTimeout)
    {
        lock (_gate)
        {
            if (!_refills.TryGetValue(refillId, out var refill) ||
                refill.Status != RefillStatus.Assigned ||
                refill.Version != expectedVersion ||
                refill.AssignmentExpiresAt > now ||
                refill.AssignedVolunteer is null ||
                refill.AssignedVolunteer.EmployeeId == nextVolunteer.EmployeeId)
            {
                return false;
            }

            refill.AssignedVolunteer = nextVolunteer;
            refill.AssignmentExpiresAt = now + assignmentTimeout;
            refill.Version++;
            return true;
        }
    }

    public bool TryCompletePurchase(Guid refillId, long expectedVersion, DateTimeOffset purchasedAt, decimal amount, string description, byte[] receiptImage)
    {
        lock (_gate)
        {
            if (!_refills.TryGetValue(refillId, out var refill) || refill.Version != expectedVersion || refill.Status != RefillStatus.Assigned)
            {
                return false;
            }

            // The assignment gives monthly budgets but no office time zones; UTC is the stable demo boundary.
            var utcPurchaseDate = purchasedAt.UtcDateTime;
            var monthlySpend = _refills.Values
                .Where(item => item.Office == refill.Office &&
                    item.PurchasedAt is DateTimeOffset recordedAt &&
                    recordedAt.UtcDateTime.Year == utcPurchaseDate.Year &&
                    recordedAt.UtcDateTime.Month == utcPurchaseDate.Month)
                .Sum(item => item.PurchaseAmount ?? 0m);
            // The earlier budget assessment is informational; repeat it inside the atomic write to catch concurrent purchases.
            if (monthlySpend + amount > OfficeFinance.MonthlyBudget(refill.Office))
            {
                return false;
            }

            refill.PurchaseAmount = amount;
            refill.PurchaseCurrency = OfficeFinance.BudgetCurrency(refill.Office);
            refill.PurchasedAt = purchasedAt;
            refill.PurchaseDescription = description;
            refill.ReceiptImage = receiptImage.ToArray();
            refill.Status = RefillStatus.Purchased;
            refill.Version++;
            return true;
        }
    }

    public bool MarkClaimSubmitted(Guid refillId, long expectedVersion, string hrClaimId)
    {
        lock (_gate)
        {
            if (!_refills.TryGetValue(refillId, out var refill) || refill.Version != expectedVersion || refill.Status != RefillStatus.Purchased)
            {
                return false;
            }

            refill.HrClaimId = hrClaimId;
            refill.Status = RefillStatus.ClaimSubmitted;
            refill.Version++;
            return true;
        }
    }

    public bool TryUpdateClaimStatus(Guid refillId, long expectedVersion, ReimbursementStatus status)
    {
        lock (_gate)
        {
            if (!_refills.TryGetValue(refillId, out var refill) || refill.Version != expectedVersion)
            {
                return false;
            }

            var nextStatus = (refill.Status, status) switch
            {
                (RefillStatus.ClaimSubmitted, ReimbursementStatus.Approved) => RefillStatus.Approved,
                (RefillStatus.ClaimSubmitted, ReimbursementStatus.Rejected) => RefillStatus.ClaimRejected,
                (RefillStatus.Approved, ReimbursementStatus.Reimbursed) => RefillStatus.Reimbursed,
                _ => (RefillStatus?)null
            };
            if (nextStatus is null)
            {
                return false;
            }

            refill.Status = nextStatus.Value;
            refill.Version++;
            return true;
        }
    }
}
