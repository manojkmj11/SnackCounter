using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "Employee,HR,Manager")]
[Route("api/offices")]
public sealed class OfficesController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OfficeResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OfficeResponse>>> List(CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await database.Offices.AsNoTracking().OrderBy(office => office.Location)
            .Select(office => new OfficeResponse(
                office.Location, office.Name, office.TimeZoneId, office.BudgetCurrency,
                office.MonthlyBudget, office.WarningThresholdPercent))
            .ToArrayAsync(cancellationToken);
        return Ok(offices);
    }

    [HttpGet("{office}/monthly-overview")]
    [ProducesResponseType<MonthlyPantryOverviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MonthlyPantryOverviewResponse>> MonthlyOverview(
        OfficeLocation office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken))
        {
            return Forbid();
        }

        var officeConfig = await database.Offices.AsNoTracking()
            .SingleOrDefaultAsync(location => location.Location == office && location.IsActive, cancellationToken);
        if (officeConfig is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(officeConfig.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var month = new DateOnly(localNow.Year, localNow.Month, 1);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(month.ToDateTime(TimeOnly.MinValue), timeZone), TimeSpan.Zero);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), timeZone), TimeSpan.Zero);

        var budget = await database.MonthlyBudgets.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Office == office && entry.Year == month.Year && entry.Month == month.Month,
                cancellationToken);
        var budgetAmount = budget?.BudgetAmount ?? officeConfig.MonthlyBudget;
        var spent = budget?.SpentAmount ?? await database.Refills.AsNoTracking()
            .Where(refill => refill.Office == office && refill.PurchasedAt >= start && refill.PurchasedAt < end)
            .SumAsync(refill => refill.PurchaseAmount ?? 0m, cancellationToken);

        var refillMovements = await database.StockMovements.AsNoTracking()
            .Where(movement => movement.SnackItem.Office == office && movement.Kind == StockMovementKind.Restock &&
                movement.OccurredAt >= start && movement.OccurredAt < end)
            .Select(movement => new { Item = movement.SnackItem.Name, movement.Quantity })
            .ToArrayAsync(cancellationToken);
        var topRefilledItems = refillMovements.GroupBy(movement => movement.Item)
            .Select(group => new TopRefilledItemResponse(group.Key, group.Count(), group.Sum(movement => movement.Quantity)))
            .OrderByDescending(item => item.RefillCount)
            .ThenByDescending(item => item.QuantityAdded)
            .ThenBy(item => item.Item)
            .Take(3)
            .ToArray();

        return Ok(new MonthlyPantryOverviewResponse(
            office, month, officeConfig.BudgetCurrency, budgetAmount, spent, Math.Max(0m, budgetAmount - spent),
            spent >= budgetAmount, topRefilledItems));
    }
}

public sealed record TopRefilledItemResponse(string Item, int RefillCount, decimal QuantityAdded);
public sealed record MonthlyPantryOverviewResponse(
    OfficeLocation Office,
    DateOnly Month,
    Currency Currency,
    decimal Budget,
    decimal Spent,
    decimal Remaining,
    bool BudgetReached,
    IReadOnlyList<TopRefilledItemResponse> TopRefilledItems);
