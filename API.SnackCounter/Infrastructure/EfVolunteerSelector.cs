using Microsoft.EntityFrameworkCore;
using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class EfVolunteerSelector : IVolunteerSelector
{
    private readonly Lazy<RoundRobinVolunteerSelector> _selector;

    public EfVolunteerSelector(IDbContextFactory<SnackCounterDbContext> contextFactory)
    {
        _selector = new Lazy<RoundRobinVolunteerSelector>(
            () => LoadSelector(contextFactory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Volunteer? GetNextEligibleVolunteer(OfficeLocation office, string previousEmployeeId) =>
        _selector.Value.GetNextEligibleVolunteer(office, previousEmployeeId);

    private static RoundRobinVolunteerSelector LoadSelector(IDbContextFactory<SnackCounterDbContext> contextFactory)
    {
        using var database = contextFactory.CreateDbContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var roster = database.EmployeeOffices
            .AsNoTracking()
            .Include(entry => entry.Volunteer)
            .Where(entry => entry.Volunteer.IsActive)
            .Where(entry => entry.ActiveFrom == null || entry.ActiveFrom <= today)
            .Where(entry => entry.ActiveUntil == null || entry.ActiveUntil >= today)
            .OrderBy(entry => entry.EmployeeId)
            .ToArray()
            .GroupBy(entry => entry.Office)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Volunteer>)group.Select(entry => entry.Volunteer).ToArray());
        return new RoundRobinVolunteerSelector(roster);
    }
}
