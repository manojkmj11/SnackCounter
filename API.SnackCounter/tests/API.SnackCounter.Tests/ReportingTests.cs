using API.SnackCounter.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Application;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Tests;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class ReportingTests
{
    private static readonly DateTimeOffset ReportClock = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task MonthlySnapshot_ReportsBothOfficesWithBudgetsSpendAndSeparateCurrencyTotals()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        AddPurchase(db, OfficeLocation.Oslo, "Oslo purchase", Utc(2026, 10, 10, 12), 5_400m);
        AddPurchase(db, OfficeLocation.Bengaluru, "Bengaluru purchase", Utc(2026, 10, 10, 12), 12_250m);
        db.MonthlyBudgets.AddRange(
            Budget(OfficeLocation.Oslo, 2026, 10, Currency.NOK, 6_200m),
            Budget(OfficeLocation.Bengaluru, 2026, 10, Currency.INR, 15_500m));
        await db.SaveChangesAsync();

        var result = await fixture.Controller.Snapshot("2026-10", null, CancellationToken.None);
        var snapshot = OkValue(result);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Month, Is.EqualTo(new DateOnly(2026, 10, 1)));
            Assert.That(snapshot.Offices, Has.Count.EqualTo(2));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo).Currency, Is.EqualTo(Currency.NOK));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo).Budget, Is.EqualTo(6_200m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo).Spent, Is.EqualTo(5_400m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo).Remaining, Is.EqualTo(800m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Currency, Is.EqualTo(Currency.INR));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Budget, Is.EqualTo(15_500m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Spent, Is.EqualTo(12_250m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Remaining, Is.EqualTo(3_250m));
            Assert.That(snapshot.TotalsByCurrency[Currency.NOK], Is.EqualTo(5_400m));
            Assert.That(snapshot.TotalsByCurrency[Currency.INR], Is.EqualTo(12_250m));
            Assert.That(snapshot.ReportingCurrency, Is.EqualTo(Currency.NOK));
            Assert.That(snapshot.TotalInReportingCurrency, Is.EqualTo(6_625m));
        });
    }

    [Test]
    public async Task OnDemandSnapshot_UsesAsOfCutoffAndRanksMostConsumedItems()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var coffee = Item(OfficeLocation.Oslo, "Coffee");
        var tea = Item(OfficeLocation.Oslo, "Tea");
        db.SnackItems.AddRange(coffee, tea);
        AddPurchase(db, OfficeLocation.Oslo, "Before cutoff", Utc(2026, 11, 5, 9), 500m);
        AddPurchase(db, OfficeLocation.Oslo, "After cutoff", Utc(2026, 11, 20, 9), 900m);
        db.StockMovements.AddRange(
            Consumption(coffee, 22m, Utc(2026, 11, 4, 12)),
            Consumption(coffee, 5m, Utc(2026, 11, 10, 12), 8m),
            Consumption(tea, 18m, Utc(2026, 11, 8, 12)),
            Consumption(tea, 100m, Utc(2026, 11, 20, 12)));
        await db.SaveChangesAsync();

        var asOf = Utc(2026, 11, 12, 0);
        var result = await fixture.Controller.Snapshot("2026-11", asOf, CancellationToken.None);
        var snapshot = OkValue(result);
        var oslo = snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.AsOf, Is.EqualTo(asOf));
            Assert.That(oslo.Spent, Is.EqualTo(500m));
            Assert.That(oslo.MostConsumedItems.Select(x => x.Item), Is.EqualTo(new[] { "Coffee", "Tea" }));
            Assert.That(oslo.MostConsumedItems.Select(x => x.Quantity), Is.EqualTo(new[] { 30m, 18m }));
            Assert.That(oslo.MostConsumedItems[0].ObservedQuantity, Is.EqualTo(8m));
            Assert.That(oslo.MostConsumedItems[0].EstimatedQuantity, Is.EqualTo(22m));
        });
    }

    [Test]
    public async Task MonthlySnapshot_UsesEachOfficeLocalMonthBoundaries()
    {
        var fixture = await CreateFixtureAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        AddPurchase(db, OfficeLocation.Oslo, "Oslo October", Utc(2026, 10, 31, 22, 30), 100m);
        AddPurchase(db, OfficeLocation.Oslo, "Oslo November", Utc(2026, 10, 31, 23, 30), 200m);
        AddPurchase(db, OfficeLocation.Bengaluru, "Bengaluru October", Utc(2026, 10, 31, 18), 300m);
        AddPurchase(db, OfficeLocation.Bengaluru, "Bengaluru November", Utc(2026, 10, 31, 18, 45), 400m);
        await db.SaveChangesAsync();

        var snapshot = OkValue(await fixture.Controller.Snapshot("2026-10", null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Oslo).Spent, Is.EqualTo(100m));
            Assert.That(snapshot.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Spent, Is.EqualTo(300m));
        });
    }

    [Test]
    public async Task SnapshotRejectsInvalidMonthAndFutureAsOfTimestamp()
    {
        var fixture = await CreateFixtureAsync();

        var invalidMonth = await fixture.Controller.Snapshot("October 2026", null, CancellationToken.None);
        var future = await fixture.Controller.Snapshot("2026-11", ReportClock.AddMinutes(1), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(invalidMonth.Result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(future.Result, Is.TypeOf<BadRequestObjectResult>());
        });
    }

    [Test]
    public async Task OnDemandSnapshotForMonthNotStarted_ReturnsNoOfficeTotals()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Controller.Snapshot("2026-12", null, CancellationToken.None);

        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
        Assert.That(OkValue(result).Offices, Is.Empty);
    }

    private static async Task<ReportingFixture> CreateFixtureAsync()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"reporting-{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var converter = new TableCurrencyConverter(new Dictionary<(Currency, Currency), decimal>
        {
            [(Currency.NOK, Currency.INR)] = 10m,
            [(Currency.INR, Currency.NOK)] = 0.1m
        });
        var reports = new MonthlySpendingReportService(new InMemoryRefillStore(), converter);
        var controller = new ReportsController(reports, factory, new FixedTimeProvider(ReportClock), converter);
        return new ReportingFixture(factory, controller);
    }

    private static MonthlyReportSnapshot OkValue(ActionResult<MonthlyReportSnapshot> result)
    {
        Assert.That(result.Result, Is.TypeOf<OkObjectResult>());
        return (MonthlyReportSnapshot)((OkObjectResult)result.Result!).Value!;
    }

    private static MonthlyBudget Budget(OfficeLocation office, int year, int month, Currency currency, decimal amount) =>
        new() { Office = office, Year = year, Month = month, Currency = currency,
            BudgetAmount = amount, SpentAmount = 0m, WarningThresholdPercent = 80m, SnapshotAt = ReportClock };

    private static SnackItem Item(OfficeLocation office, string name) => new()
    {
        Id = Guid.NewGuid(), Office = office, Name = name, Unit = "pack", QuantityOnHand = 1m,
        ReorderThreshold = 1m, IsActive = true
    };

    private static StockMovement Consumption(SnackItem item, decimal quantity, DateTimeOffset at, decimal? observed = null) => new()
    {
        Id = Guid.NewGuid(), SnackItemId = item.Id, SnackItem = item, Kind = StockMovementKind.Consumption,
        Quantity = -quantity, ReportedConsumedQuantity = observed, OccurredAt = at, Note = "Reported consumption"
    };

    private static void AddPurchase(SnackCounterDbContext db, OfficeLocation office, string description,
        DateTimeOffset purchasedAt, decimal amount)
    {
        var refill = new Refill(Guid.NewGuid(), office, description, purchasedAt)
        {
            Status = RefillStatus.Purchased,
            PurchaseAmount = amount,
            PurchaseCurrency = OfficeFinance.BudgetCurrency(office),
            PurchasedAt = purchasedAt,
            PurchaseDescription = description
        };
        db.Refills.Add(refill);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private sealed record ReportingFixture(TestDbContextFactory Factory, ReportsController Controller);

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
