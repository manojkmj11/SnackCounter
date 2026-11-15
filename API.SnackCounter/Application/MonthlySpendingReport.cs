using SnackCounter.Domain;

namespace SnackCounter.Application;

public sealed record OfficeMonthlySpend(
    OfficeLocation Office,
    Currency Currency,
    decimal Spent,
    decimal Budget)
{
    public decimal Remaining => Budget - Spent;
}

public sealed record PurchaseBudgetAssessment(
    OfficeLocation Office,
    DateOnly Month,
    Currency Currency,
    decimal SpentToDate,
    decimal ProposedAmount,
    decimal RemainingBeforePurchase,
    bool IsWithinBudget);

public sealed record MonthlySpendingReport(
    DateOnly Month,
    IReadOnlyList<OfficeMonthlySpend> Offices,
    IReadOnlyDictionary<Currency, decimal> TotalsByCurrency,
    decimal TotalInReportingCurrency,
    Currency ReportingCurrency);

public sealed class MonthlySpendingReportService
{
    private readonly IRefillStore _store;
    private readonly ICurrencyConverter _currencyConverter;

    public MonthlySpendingReportService(IRefillStore store, ICurrencyConverter currencyConverter)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currencyConverter = currencyConverter ?? throw new ArgumentNullException(nameof(currencyConverter));
    }

    public MonthlySpendingReport GetReport(DateOnly month, Currency reportingCurrency)
    {
        var purchases = _store.GetPurchasesInMonth(month);
        var offices = Enum.GetValues<OfficeLocation>()
            .Select(office => new OfficeMonthlySpend(
                office,
                OfficeFinance.BudgetCurrency(office),
                purchases.Where(refill => refill.Office == office).Sum(refill => refill.PurchaseAmount ?? 0m),
                OfficeFinance.MonthlyBudget(office)))
            .ToArray();
        var totals = offices
            .GroupBy(office => office.Currency)
            .ToDictionary(group => group.Key, group => group.Sum(office => office.Spent));
        // The assignment does not specify an FX accounting date, so convert each receipt at its purchase date.
        var reportingTotal = purchases.Sum(refill => _currencyConverter.Convert(
            refill.PurchaseAmount ?? 0m,
            refill.PurchaseCurrency!.Value,
            reportingCurrency,
            DateOnly.FromDateTime(refill.PurchasedAt!.Value.UtcDateTime)));

        return new MonthlySpendingReport(month, offices, totals, reportingTotal, reportingCurrency);
    }

    public PurchaseBudgetAssessment CheckPurchaseBudget(OfficeLocation office, DateOnly month, decimal proposedAmount)
    {
        if (proposedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proposedAmount), "Proposed amount must be greater than zero.");
        }

        var currency = OfficeFinance.BudgetCurrency(office);
        var spent = _store.GetPurchasesInMonth(month)
            .Where(refill => refill.Office == office)
            .Sum(refill => refill.PurchaseAmount ?? 0m);
        var remaining = OfficeFinance.MonthlyBudget(office) - spent;
        return new PurchaseBudgetAssessment(
            office,
            month,
            currency,
            spent,
            proposedAmount,
            remaining,
            proposedAmount <= remaining);
    }
}
