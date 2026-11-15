using SnackCounter.Application;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using API.SnackCounter.Controllers;

namespace API.SnackCounter.Tests;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class SnackCounterRulesTests
{
    [Test]
    public async Task ConcurrentEmployees_CannotBothClaimTheSameRefill()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"concurrent-refill-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var firstEmployee = new Volunteer("emp-1", OfficeLocation.Oslo, Currency.NOK);
        var secondEmployee = new Volunteer("emp-2", OfficeLocation.Oslo, Currency.NOK);
        var itemId = Guid.NewGuid();
        var refillId = Guid.NewGuid();
        string refillVersion;
        string itemVersion;
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.AddRange(firstEmployee, secondEmployee);
            setup.SnackItems.Add(new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Oslo, Name = "Coffee", Unit = "pack",
                QuantityOnHand = 0, ReorderThreshold = 1, IsClaimed = true, RowVersion = [1, 2, 3, 4, 5, 6, 7, 8]
            });
            var refill = new Refill(refillId, OfficeLocation.Oslo, "Coffee", clock.GetUtcNow())
            {
                SnackItemId = itemId, Status = RefillStatus.Assigned, AssignedVolunteer = firstEmployee,
                AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(30),
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            };
            setup.Refills.Add(refill);
            setup.RefillAssignmentAttempts.Add(new RefillAssignmentAttempt
            {
                Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = firstEmployee.EmployeeId,
                LocalDate = new DateOnly(2026, 11, 14), AttemptNumber = 1, Outcome = RefillAttemptOutcome.Offered,
                AttemptedAt = clock.GetUtcNow(), AssignmentExpiresAt = refill.AssignmentExpiresAt
            });
            await setup.SaveChangesAsync();
            var refillSnapshot = await setup.Refills.AsNoTracking().SingleAsync(x => x.Id == refillId);
            var item = await setup.SnackItems.AsNoTracking().SingleAsync(x => x.Id == itemId);
            refillVersion = Convert.ToBase64String(refillSnapshot.RowVersion);
            itemVersion = Convert.ToBase64String(item.RowVersion);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:AssignmentTimeoutMinutes"] = "30"
        }).Build();
        var firstController = new RefillOperationsController(factory, clock, configuration);
        var secondController = new RefillOperationsController(factory, clock, configuration);
        SetIdentity(firstController, firstEmployee.EmployeeId, "Employee");
        SetIdentity(secondController, secondEmployee.EmployeeId, "Employee");

        var results = await Task.WhenAll(
            firstController.ClaimRefill(OfficeLocation.Oslo, refillId,
                new ClaimRefillRequest(refillVersion, itemVersion), CancellationToken.None),
            secondController.ClaimRefill(OfficeLocation.Oslo, refillId,
                new ClaimRefillRequest(refillVersion, itemVersion), CancellationToken.None));

        Assert.That(results.Count(result => result.Result is OkObjectResult), Is.EqualTo(1));
        Assert.That(results.Count(result => result.Result is ConflictObjectResult), Is.EqualTo(1));
        await using var verify = await factory.CreateDbContextAsync();
        var claimed = await verify.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        var itemAfterClaim = await verify.SnackItems.SingleAsync(x => x.Id == itemId);
        Assert.Multiple(() =>
        {
            Assert.That(claimed.Status, Is.EqualTo(RefillStatus.Assigned));
            Assert.That(claimed.AssignedVolunteer!.EmployeeId, Is.EqualTo(firstEmployee.EmployeeId));
            Assert.That(itemAfterClaim.IsClaimed, Is.True);
        });
    }

    [Test]
    public async Task ExpiredOffer_IsHandedToNextEmployee_AndDailyRosterExhaustionWaitsUntilTomorrow()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"expired-claim-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var refillId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var first = new Volunteer("emp-1", OfficeLocation.Bengaluru, Currency.INR);
        var next = new Volunteer("emp-2", OfficeLocation.Bengaluru, Currency.INR);
        var visitorWithoutOfficeAccess = new Volunteer("emp-0", OfficeLocation.Oslo, Currency.NOK);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.AddRange(first, next, visitorWithoutOfficeAccess);
            setup.EmployeeRoles.AddRange(
                new EmployeeRole { EmployeeId = first.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = next.EmployeeId, RoleName = "Employee" },
                new EmployeeRole { EmployeeId = visitorWithoutOfficeAccess.EmployeeId, RoleName = "Employee" });
            setup.EmployeeOffices.AddRange(
                new OfficeVolunteerEligibility { EmployeeId = first.EmployeeId, Office = OfficeLocation.Bengaluru },
                new OfficeVolunteerEligibility { EmployeeId = next.EmployeeId, Office = OfficeLocation.Bengaluru });
            var item = new SnackItem
            {
                Id = itemId, Office = OfficeLocation.Bengaluru, Name = "Tea", Unit = "box",
                QuantityOnHand = 0, ReorderThreshold = 1, IsClaimed = true, RowVersion = [1, 2, 3, 4, 5, 6, 7, 8]
            };
            var refill = new Refill(refillId, OfficeLocation.Bengaluru, "Tea", clock.GetUtcNow().AddHours(-2))
            {
                SnackItemId = itemId, Status = RefillStatus.Assigned, AssignedVolunteer = first,
                AssignmentExpiresAt = clock.GetUtcNow().AddMinutes(-1), Version = 7,
                RowVersion = [8, 7, 6, 5, 4, 3, 2, 1]
            };
            setup.SnackItems.Add(item);
            setup.Refills.Add(refill);
            setup.RefillAssignmentAttempts.Add(new RefillAssignmentAttempt
            {
                Id = Guid.NewGuid(), RefillId = refillId, EmployeeId = first.EmployeeId,
                LocalDate = new DateOnly(2026, 11, 14), AttemptNumber = 1, Outcome = RefillAttemptOutcome.Offered,
                AttemptedAt = clock.GetUtcNow().AddHours(-2), AssignmentExpiresAt = refill.AssignmentExpiresAt
            });
            await setup.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SnackCounter:AssignmentTimeoutMinutes"] = "30",
            ["SnackCounter:OfferTimeoutMinutes"] = "30"
        }).Build();
        var worker = new SnackCounterAutomationWorker(factory, clock, CurrencyConverter(), new TestExternalHrClient(),
            configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<SnackCounterAutomationWorker>.Instance);
        await worker.RunExpiredClaimCycleAsync();
        await worker.RunRefillOfferCycleAsync();

        await using (var verify = await factory.CreateDbContextAsync())
        {
            var refill = await verify.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
            Assert.That(refill.AssignedVolunteer!.EmployeeId, Is.EqualTo(next.EmployeeId));
            Assert.That(await verify.RefillAssignmentAttempts.AnyAsync(x => x.RefillId == refillId &&
                x.EmployeeId == first.EmployeeId && x.Outcome == RefillAttemptOutcome.Expired), Is.True);
            Assert.That((await verify.SnackItems.SingleAsync(x => x.Id == itemId)).IsClaimed, Is.True);
        }

        clock.Advance(TimeSpan.FromMinutes(31));
        await worker.RunExpiredClaimCycleAsync();
        await worker.RunRefillOfferCycleAsync();
        await using (var verify = await factory.CreateDbContextAsync())
        {
            var refill = await verify.Refills.SingleAsync(x => x.Id == refillId);
            Assert.That(refill.Status, Is.EqualTo(RefillStatus.Open), "each roster member has already received an offer today");
            Assert.That(refill.AssignedVolunteer, Is.Null);
        }

        clock.Advance(TimeSpan.FromHours(24));
        await worker.RunRefillOfferCycleAsync();
        await using var nextDay = await factory.CreateDbContextAsync();
        var offeredTomorrow = await nextDay.Refills.Include(x => x.AssignedVolunteer).SingleAsync(x => x.Id == refillId);
        Assert.That(offeredTomorrow.AssignedVolunteer!.EmployeeId, Is.EqualTo(first.EmployeeId));
    }

    [Test]
    public void BudgetsAndMonthlyTotals_AreCorrectAndRemainSeparatedByCurrency()
    {
        var store = new InMemoryRefillStore();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var service = CreateService(store, clock, new FixedNextVolunteerSelector());

        Buy(service, OfficeLocation.Oslo, "oslo-1", Currency.NOK, 5_300m);
        Buy(service, OfficeLocation.Bengaluru, "blr-1", Currency.INR, 14_300m);

        var reports = new MonthlySpendingReportService(store, CurrencyConverter());
        var osloRemaining = reports.CheckPurchaseBudget(OfficeLocation.Oslo,
            new DateOnly(2026, 11, 1), 700m);
        var osloOverBudget = reports.CheckPurchaseBudget(OfficeLocation.Oslo,
            new DateOnly(2026, 11, 1), 701m);
        var bengaluruRemaining = reports.CheckPurchaseBudget(OfficeLocation.Bengaluru,
            new DateOnly(2026, 11, 1), 700m);
        Assert.Multiple(() =>
        {
            Assert.That(osloRemaining.IsWithinBudget, Is.True);
            Assert.That(osloRemaining.RemainingBeforePurchase, Is.EqualTo(700m));
            Assert.That(osloOverBudget.IsWithinBudget, Is.False);
            Assert.That(bengaluruRemaining.Currency, Is.EqualTo(Currency.INR));
            Assert.That(bengaluruRemaining.RemainingBeforePurchase, Is.EqualTo(700m));
        });

        var report = reports.GetReport(
            new DateOnly(2026, 11, 1), Currency.NOK);
        Assert.Multiple(() =>
        {
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Oslo).Budget, Is.EqualTo(6_000m));
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Oslo).Spent, Is.EqualTo(5_300m));
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Oslo).Remaining, Is.EqualTo(700m));
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Budget, Is.EqualTo(15_000m));
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Spent, Is.EqualTo(14_300m));
            Assert.That(report.Offices.Single(x => x.Office == OfficeLocation.Bengaluru).Remaining, Is.EqualTo(700m));
            Assert.That(report.TotalsByCurrency[Currency.NOK], Is.EqualTo(5_300m));
            Assert.That(report.TotalsByCurrency[Currency.INR], Is.EqualTo(14_300m));
            Assert.That(report.TotalInReportingCurrency, Is.EqualTo(6_730m));
        });

        var overBudgetRefill = service.CreateRefill(OfficeLocation.Oslo, "Extra coffee");
        Assert.That(service.Commit(overBudgetRefill.Id,
            new Volunteer("oslo-2", OfficeLocation.Oslo, Currency.NOK)), Is.True);
        Assert.Throws<InvalidOperationException>(() => service.CompletePurchase(
            overBudgetRefill.Id, "oslo-2", 701m, "Exceeds remaining budget", [1]));
        Assert.That(store.Get(overBudgetRefill.Id)!.Status, Is.EqualTo(RefillStatus.Assigned));
    }

    [Test]
    public void RefillLifecycle_RecordsPurchaseAndMovesThroughHrApprovalToReimbursement()
    {
        var store = new InMemoryRefillStore();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
        var chat = new InMemoryChatTool();
        var hr = new InMemoryHrSystem();
        var service = new RefillLifecycleService(store, new FixedNextVolunteerSelector(), chat, hr,
            clock, TimeSpan.FromHours(24), CurrencyConverter());
        var volunteer = new Volunteer("oslo-volunteer", OfficeLocation.Oslo, Currency.NOK);

        // 1. An empty item is recorded and enters the shared open queue.
        var refill = service.CreateRefill(OfficeLocation.Oslo, "Coffee");
        Assert.That(refill.Status, Is.EqualTo(RefillStatus.Open));

        // 2. One volunteer commits; a second commit cannot take the same refill.
        Assert.That(service.Commit(refill.Id, volunteer), Is.True);
        Assert.That(service.Commit(refill.Id, new Volunteer("other", OfficeLocation.Oslo, Currency.NOK)), Is.False);
        Assert.That(store.Get(refill.Id)!.AssignedVolunteer!.EmployeeId, Is.EqualTo(volunteer.EmployeeId));

        // 3. The assignee records the purchase. The office budget books 250 NOK and HR receives its claim.
        service.CompletePurchase(refill.Id, volunteer.EmployeeId, 250m, "Coffee restock", [1, 2, 3]);
        var submitted = store.Get(refill.Id)!;
        Assert.Multiple(() =>
        {
            Assert.That(submitted.Status, Is.EqualTo(RefillStatus.ClaimSubmitted));
            Assert.That(submitted.PurchaseCurrency, Is.EqualTo(Currency.NOK));
            Assert.That(submitted.PurchaseAmount, Is.EqualTo(250m));
            Assert.That(hr.Claims, Has.Count.EqualTo(1));
            Assert.That(hr.Claims[0].Currency, Is.EqualTo(Currency.NOK));
        });

        // 4. HR approval changes the claim state, then reimbursement closes the lifecycle.
        service.UpdateClaimStatus(refill.Id, ReimbursementStatus.Approved);
        Assert.That(store.Get(refill.Id)!.Status, Is.EqualTo(RefillStatus.Approved));
        service.UpdateClaimStatus(refill.Id, ReimbursementStatus.Reimbursed);
        Assert.That(store.Get(refill.Id)!.Status, Is.EqualTo(RefillStatus.Reimbursed));
        Assert.That(new MonthlySpendingReportService(store, CurrencyConverter())
            .GetReport(new DateOnly(2026, 11, 1), Currency.NOK).TotalsByCurrency[Currency.NOK], Is.EqualTo(250m));
    }

    private static void Buy(RefillLifecycleService service, OfficeLocation office, string employeeId,
        Currency currency, decimal amount)
    {
        var refill = service.CreateRefill(office, "Monthly stock");
        Assert.That(service.Commit(refill.Id, new Volunteer(employeeId, office, currency)), Is.True);
        service.CompletePurchase(refill.Id, employeeId, amount, "Monthly restock", [1]);
    }

    private static RefillLifecycleService CreateService(IRefillStore store, ManualClock clock,
        IVolunteerSelector selector, TimeSpan? timeout = null) => new(
        store, selector, new InMemoryChatTool(), new InMemoryHrSystem(), clock,
        timeout ?? TimeSpan.FromHours(24), CurrencyConverter());

    private static void SetIdentity(ControllerBase controller, string employeeId, string role)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, employeeId), new Claim(ClaimTypes.Role, role)], "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static TableCurrencyConverter CurrencyConverter() => new(new Dictionary<(Currency, Currency), decimal>
    {
        [(Currency.NOK, Currency.INR)] = 10m,
        [(Currency.INR, Currency.NOK)] = 0.1m
    });

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestExternalHrClient : IExternalHrClaimClient
    {
        public Task SubmitClaimAsync(Guid claimId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedNextVolunteerSelector(Volunteer? next = null) : IVolunteerSelector
    {
        public Volunteer? GetNextEligibleVolunteer(OfficeLocation office, string previousEmployeeId) =>
            next ?? new Volunteer("replacement", office, OfficeFinance.BudgetCurrency(office));
    }

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }
}
