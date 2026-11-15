using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class EfRefillStore(IDbContextFactory<SnackCounterDbContext> contextFactory)
    : IRefillStore, IVolunteerDirectory
{
    private static readonly ConcurrentDictionary<(OfficeLocation Office, int Year, int Month), object> BudgetLocks = new();

    public void Create(Refill refill)
    {
        using var database = contextFactory.CreateDbContext();
        database.Refills.Add(refill.Copy());
        database.SaveChanges();
    }

    public Refill? Get(Guid refillId)
    {
        using var database = contextFactory.CreateDbContext();
        return database.Refills
            .AsNoTracking()
            .Include(refill => refill.AssignedVolunteer)
            .SingleOrDefault(refill => refill.Id == refillId)
            ?.Copy();
    }

    public IReadOnlyList<Refill> List(OfficeLocation? office = null, RefillStatus? status = null)
    {
        using var database = contextFactory.CreateDbContext();
        var query = database.Refills
            .AsNoTracking()
            .Include(refill => refill.AssignedVolunteer)
            .AsQueryable();
        if (office is not null)
        {
            query = query.Where(refill => refill.Office == office);
        }
        if (status is not null)
        {
            query = query.Where(refill => refill.Status == status);
        }

        return query
            .OrderByDescending(refill => refill.CreatedAt)
            .Select(refill => refill.Copy())
            .ToArray();
    }

    public Volunteer? GetVolunteer(string employeeId)
    {
        using var database = contextFactory.CreateDbContext();
        return database.Volunteers.AsNoTracking().SingleOrDefault(volunteer => volunteer.EmployeeId == employeeId);
    }

    public IReadOnlyList<Volunteer> GetEligibleVolunteers(OfficeLocation office)
    {
        using var database = contextFactory.CreateDbContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return database.EmployeeOffices
            .AsNoTracking()
            .Where(entry => entry.Office == office)
            .Where(entry => entry.Volunteer.IsActive)
            .Where(entry => entry.ActiveFrom == null || entry.ActiveFrom <= today)
            .Where(entry => entry.ActiveUntil == null || entry.ActiveUntil >= today)
            .OrderBy(entry => entry.EmployeeId)
            .Select(entry => entry.Volunteer)
            .ToArray();
    }

    public IReadOnlyList<Refill> GetExpiredAssignments(DateTimeOffset now)
    {
        using var database = contextFactory.CreateDbContext();
        return database.Refills
            .AsNoTracking()
            .Include(refill => refill.AssignedVolunteer)
            .Where(refill => refill.Status == RefillStatus.Assigned && refill.AssignmentExpiresAt <= now)
            .OrderBy(refill => refill.AssignmentExpiresAt)
            .Select(refill => refill.Copy())
            .ToArray();
    }

    public IReadOnlyList<Refill> GetPurchasesInMonth(DateOnly month)
    {
        var (start, end) = UtcMonthBounds(month);
        using var database = contextFactory.CreateDbContext();
        return database.Refills
            .AsNoTracking()
            .Include(refill => refill.AssignedVolunteer)
            .Where(refill => refill.PurchasedAt >= start && refill.PurchasedAt < end)
            .OrderBy(refill => refill.PurchasedAt)
            .Select(refill => refill.Copy())
            .ToArray();
    }

    public bool TryAssign(Guid refillId, long expectedVersion, Volunteer volunteer, DateTimeOffset expiresAt)
    {
        using var database = contextFactory.CreateDbContext();
        var refill = database.Refills.SingleOrDefault(item => item.Id == refillId);
        var employee = database.Volunteers.SingleOrDefault(item => item.EmployeeId == volunteer.EmployeeId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (refill is null || employee is null || refill.Version != expectedVersion || refill.Status != RefillStatus.Open ||
            !database.EmployeeOffices.Any(entry => entry.Office == refill.Office && entry.EmployeeId == employee.EmployeeId &&
                (entry.ActiveFrom == null || entry.ActiveFrom <= today) && (entry.ActiveUntil == null || entry.ActiveUntil >= today)) ||
            !employee.IsActive)
        {
            return false;
        }

        refill.AssignedVolunteer = employee;
        refill.AssignmentExpiresAt = expiresAt;
        refill.Status = RefillStatus.Assigned;
        refill.Version++;
        return SaveWithConcurrencyCheck(database);
    }

    public bool TryReassignExpired(
        Guid refillId,
        long expectedVersion,
        DateTimeOffset now,
        Volunteer nextVolunteer,
        TimeSpan assignmentTimeout)
    {
        using var database = contextFactory.CreateDbContext();
        var refill = database.Refills
            .Include(item => item.AssignedVolunteer)
            .SingleOrDefault(item => item.Id == refillId);
        var employee = database.Volunteers.SingleOrDefault(item => item.EmployeeId == nextVolunteer.EmployeeId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (refill is null || employee is null || refill.Version != expectedVersion ||
            refill.Status != RefillStatus.Assigned || refill.AssignmentExpiresAt > now ||
            refill.AssignedVolunteer is null || refill.AssignedVolunteer.EmployeeId == nextVolunteer.EmployeeId ||
            !database.EmployeeOffices.Any(entry => entry.Office == refill.Office && entry.EmployeeId == employee.EmployeeId &&
                (entry.ActiveFrom == null || entry.ActiveFrom <= today) && (entry.ActiveUntil == null || entry.ActiveUntil >= today)) ||
            !employee.IsActive)
        {
            return false;
        }

        refill.AssignedVolunteer = employee;
        refill.AssignmentExpiresAt = now + assignmentTimeout;
        refill.Version++;
        return SaveWithConcurrencyCheck(database);
    }

    public bool TryCompletePurchase(
        Guid refillId,
        long expectedVersion,
        DateTimeOffset purchasedAt,
        decimal amount,
        string description,
        byte[] receiptImage)
    {
        var utcPurchaseDate = purchasedAt.UtcDateTime;
        var budgetKey = (Office: default(OfficeLocation), Year: utcPurchaseDate.Year, Month: utcPurchaseDate.Month);
        using (var lookup = contextFactory.CreateDbContext())
        {
            var office = lookup.Refills.AsNoTracking()
                .Where(refill => refill.Id == refillId)
                .Select(refill => (OfficeLocation?)refill.Office)
                .SingleOrDefault();
            if (office is null)
            {
                return false;
            }
            budgetKey = (office.Value, utcPurchaseDate.Year, utcPurchaseDate.Month);
        }

        // EF InMemory has no cross-row transaction; this process-wide lock keeps the aggregate budget guard atomic in this demo host.
        lock (BudgetLocks.GetOrAdd(budgetKey, _ => new object()))
        {
            using var database = contextFactory.CreateDbContext();
            var refill = database.Refills
                .Include(item => item.AssignedVolunteer)
                .SingleOrDefault(item => item.Id == refillId);
            if (refill is null || refill.Version != expectedVersion || refill.Status != RefillStatus.Assigned)
            {
                return false;
            }

            var (monthStart, monthEnd) = UtcMonthBounds(DateOnly.FromDateTime(utcPurchaseDate));
            var monthlySpend = database.Refills
                .Where(item => item.Office == refill.Office &&
                    item.PurchasedAt >= monthStart && item.PurchasedAt < monthEnd)
                .Sum(item => (decimal?)item.PurchaseAmount) ?? 0m;
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
            return SaveWithConcurrencyCheck(database);
        }
    }

    public bool MarkClaimSubmitted(Guid refillId, long expectedVersion, string hrClaimId)
    {
        using var database = contextFactory.CreateDbContext();
        var refill = database.Refills.SingleOrDefault(item => item.Id == refillId);
        if (refill is null || refill.Version != expectedVersion || refill.Status != RefillStatus.Purchased)
        {
            return false;
        }

        refill.HrClaimId = hrClaimId;
        refill.Status = RefillStatus.ClaimSubmitted;
        refill.Version++;
        return SaveWithConcurrencyCheck(database);
    }

    public bool TryUpdateClaimStatus(Guid refillId, long expectedVersion, ReimbursementStatus status)
    {
        using var database = contextFactory.CreateDbContext();
        var refill = database.Refills.SingleOrDefault(item => item.Id == refillId);
        if (refill is null || refill.Version != expectedVersion)
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
        return SaveWithConcurrencyCheck(database);
    }

    private static bool SaveWithConcurrencyCheck(SnackCounterDbContext database)
    {
        try
        {
            database.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) UtcMonthBounds(DateOnly month)
    {
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }
}
