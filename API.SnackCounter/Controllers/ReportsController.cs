using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Application;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "HR,Manager")]
[Route("api/reports")]
public sealed class ReportsController(
    MonthlySpendingReportService reports,
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock,
    ICurrencyConverter currencyConverter) : ControllerBase
{
    [HttpGet("snapshot")]
    [ProducesResponseType<MonthlyReportSnapshot>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MonthlyReportSnapshot>> Snapshot(
        [FromQuery] string month,
        [FromQuery] DateTimeOffset? asOf,
        CancellationToken cancellationToken,
        [FromQuery] Currency currency = Currency.NOK)
    {
        if (!TryParseMonth(month, out var parsedMonth))
            return BadRequest(new ProblemDetails { Title = "Invalid month.", Detail = "Use the yyyy-MM format." });
        if (!Enum.IsDefined(currency))
            return BadRequest(new ProblemDetails { Title = "Invalid reporting currency.", Detail = "Choose NOK or INR." });
        var timestamp = asOf ?? clock.GetUtcNow();
        if (timestamp > clock.GetUtcNow()) return BadRequest(new ProblemDetails { Title = "asOf cannot be in the future." });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await db.Offices.AsNoTracking().OrderBy(x => x.Location).ToArrayAsync(cancellationToken);
        var results = new List<OfficeReportSnapshot>();
        decimal totalInReportingCurrency = 0m;
        foreach (var office in offices)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId);
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(parsedMonth.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(parsedMonth.AddMonths(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            if (timestamp < start) continue;
            var purchases = await db.Refills.AsNoTracking().Where(x => x.Office == office.Location &&
                    x.PurchasedAt >= start && x.PurchasedAt < end && x.PurchasedAt <= timestamp)
                .Where(x => x.PurchaseAmount != null && x.PurchaseCurrency != null && x.PurchasedAt != null)
                .Select(x => new { Amount = x.PurchaseAmount!.Value, PurchaseCurrency = x.PurchaseCurrency!.Value, PurchasedAt = x.PurchasedAt!.Value })
                .ToArrayAsync(cancellationToken);
            var spend = purchases.Sum(x => x.Amount);
            totalInReportingCurrency += purchases.Sum(x => currencyConverter.Convert(x.Amount, x.PurchaseCurrency,
                currency, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.PurchasedAt, zone).DateTime)));
            var budget = await db.MonthlyBudgets.AsNoTracking().Where(x => x.Office == office.Location &&
                    x.Year == parsedMonth.Year && x.Month == parsedMonth.Month)
                .Select(x => (decimal?)x.BudgetAmount).SingleOrDefaultAsync(cancellationToken) ?? office.MonthlyBudget;
            var movements = await db.StockMovements.AsNoTracking().Where(x => x.SnackItem.Office == office.Location &&
                    x.Kind == StockMovementKind.Consumption && x.OccurredAt >= start && x.OccurredAt < end && x.OccurredAt <= timestamp)
                .Select(x => new { Item = x.SnackItem.Name, x.Quantity, x.ReportedConsumedQuantity })
                .ToArrayAsync(cancellationToken);
            var consumption = movements.GroupBy(x => x.Item)
                .Select(group => new ItemConsumption(group.Key,
                    group.Sum(x => x.ReportedConsumedQuantity ?? -x.Quantity),
                    group.Where(x => x.ReportedConsumedQuantity != null).Sum(x => x.ReportedConsumedQuantity ?? 0m),
                    group.Where(x => x.ReportedConsumedQuantity == null).Sum(x => -x.Quantity)))
                .OrderByDescending(x => x.Quantity).Take(10).ToArray();
            results.Add(new OfficeReportSnapshot(office.Location, office.BudgetCurrency, budget, spend,
                budget - spend, consumption));
        }

        return Ok(new MonthlyReportSnapshot(parsedMonth, timestamp, results,
            results.GroupBy(x => x.Currency).ToDictionary(x => x.Key, x => x.Sum(row => row.Spent)),
            currency, totalInReportingCurrency, "Configured demo FX rates; converted at each purchase date; rounded to 2 decimals."));
    }

    [HttpGet("monthly")]
    [ProducesResponseType<MonthlySpendingReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<MonthlySpendingReport> Monthly([FromQuery] string month, [FromQuery] Currency currency)
    {
        if (!TryParseMonth(month, out var parsedMonth))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid month.",
                Detail = "Use the yyyy-MM format, for example 2026-11."
            });
        }

        return Ok(reports.GetReport(parsedMonth, currency));
    }

    [HttpGet("budget-check")]
    [ProducesResponseType<PurchaseBudgetAssessment>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<PurchaseBudgetAssessment> CheckBudget(
        [FromQuery] OfficeLocation office,
        [FromQuery] string month,
        [FromQuery] decimal amount)
    {
        if (!TryParseMonth(month, out var parsedMonth))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid month.",
                Detail = "Use the yyyy-MM format, for example 2026-11."
            });
        }

        try
        {
            return Ok(reports.CheckPurchaseBudget(office, parsedMonth, amount));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid amount.", Detail = exception.Message });
        }
    }

    private static bool TryParseMonth(string value, out DateOnly month)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            month = new DateOnly(parsed.Year, parsed.Month, 1);
            return true;
        }

        month = default;
        return false;
    }
}

public sealed record ItemConsumption(string Item, decimal Quantity, decimal ObservedQuantity = 0m, decimal EstimatedQuantity = 0m);
public sealed record OfficeReportSnapshot(OfficeLocation Office, Currency Currency, decimal Budget, decimal Spent,
    decimal Remaining, IReadOnlyList<ItemConsumption> MostConsumedItems);
public sealed record MonthlyReportSnapshot(DateOnly Month, DateTimeOffset AsOf,
    IReadOnlyList<OfficeReportSnapshot> Offices, IReadOnlyDictionary<Currency, decimal> TotalsByCurrency,
    Currency ReportingCurrency, decimal TotalInReportingCurrency, string ExchangeRatePolicy);
