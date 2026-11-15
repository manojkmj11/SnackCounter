using System.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using API.SnackCounter.Controllers;
using SnackCounter.Application;
using SnackCounter.Domain;

namespace SnackCounter.Infrastructure;

public sealed class SnackCounterAutomationWorker(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock,
    ICurrencyConverter currencyConverter,
    IExternalHrClaimClient externalHr,
    IConfiguration configuration,
    ILogger<SnackCounterAutomationWorker> logger,
    IHubContext<NotificationsHub>? hub = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AssignmentLock = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOutboxAsync(stoppingToken);
                await ReleaseExpiredClaimsAsync(stoppingToken);
                await OfferOpenRefillsAsync(stoppingToken);
                await SendLeavePromptsAsync(stoppingToken);
                await PublishMonthEndReportsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Snack counter automation cycle failed."); }

            try { await Task.Delay(PollInterval, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public Task RunRefillOfferCycleAsync(CancellationToken cancellationToken = default) =>
        OfferOpenRefillsAsync(cancellationToken);

    public Task RunExpiredClaimCycleAsync(CancellationToken cancellationToken = default) =>
        ReleaseExpiredClaimsAsync(cancellationToken);

    private async Task DispatchOutboxAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 100 && !cancellationToken.IsCancellationRequested; i++)
        {
            var now = clock.GetUtcNow();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidateId = await db.OutboxMessages.AsNoTracking()
                .Where(x => x.ProcessedAt == null && x.AvailableAt <= now && (x.LockedUntil == null || x.LockedUntil <= now))
                .OrderBy(x => x.AvailableAt).ThenBy(x => x.OccurredAt)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            if (candidateId is null) return;

            var lockUntil = now + AssignmentLock;
            int claimed;
            if (db.Database.IsRelational())
            {
                claimed = await db.OutboxMessages.Where(x => x.Id == candidateId && x.ProcessedAt == null &&
                        (x.LockedUntil == null || x.LockedUntil <= now))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LockedUntil, lockUntil), cancellationToken);
            }
            else
            {
                var row = await db.OutboxMessages.SingleAsync(x => x.Id == candidateId, cancellationToken);
                if (row.ProcessedAt is not null || row.LockedUntil > now) continue;
                row.LockedUntil = lockUntil;
                await db.SaveChangesAsync(cancellationToken);
                claimed = 1;
            }
            if (claimed == 0) continue;

            await using var work = await contextFactory.CreateDbContextAsync(cancellationToken);
            var outbox = await work.OutboxMessages.SingleOrDefaultAsync(x => x.Id == candidateId, cancellationToken);
            if (outbox is null || outbox.ProcessedAt is not null) continue;
            try
            {
                if (outbox.EventType == "ExternalHrClaimSubmit" && configuration.GetValue<bool>("MockHr:Enabled"))
                {
                    using var document = System.Text.Json.JsonDocument.Parse(outbox.PayloadJson);
                    var claimId = document.RootElement.GetProperty("claimId").GetGuid();
                    await externalHr.SubmitClaimAsync(claimId, cancellationToken);
                }
                var notifications = await work.Notifications.AsNoTracking()
                    .Where(x => x.ConversationMessageId == outbox.EventId)
                    .Select(x => new HubNotification(x.Id, x.EventType, x.RecipientEmployeeId, x.Office,
                        x.IsHrAudience, x.ConversationMessageId, x.PayloadJson, x.CreatedAt, x.ReadAt))
                    .ToArrayAsync(cancellationToken);
                foreach (var notification in notifications)
                    await SendToAudienceAsync(notification, cancellationToken);

                var deliveredAt = clock.GetUtcNow();
                outbox.ProcessedAt = deliveredAt;
                outbox.LockedUntil = null;
                outbox.LastError = null;
                foreach (var notification in work.Notifications.Where(x => x.ConversationMessageId == outbox.EventId))
                {
                    notification.DeliveryState = NotificationDeliveryState.Sent;
                    notification.DeliveryAttempts++;
                    notification.DeliveredAt = deliveredAt;
                }
                await work.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox event {EventId} could not be delivered.", outbox.EventId);
                outbox.AttemptCount++;
                outbox.LastError = exception.Message.Length > 2_000 ? exception.Message[..2_000] : exception.Message;
                outbox.LockedUntil = null;
                outbox.AvailableAt = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(outbox.AttemptCount, 8))));
                await work.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private Task SendToAudienceAsync(HubNotification notification, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(notification.RecipientEmployeeId))
            return hub.Clients.Group(NotificationsHub.EmployeeGroup(notification.RecipientEmployeeId))
                .SendAsync("notification", notification, cancellationToken);
        if (notification.IsHrAudience)
            return hub.Clients.Group(NotificationsHub.HrGroup).SendAsync("notification", notification, cancellationToken);
        if (notification.Office is not null)
            return hub.Clients.Group(NotificationsHub.OfficeGroup(notification.Office.Value.ToString()))
                .SendAsync("notification", notification, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task OfferOpenRefillsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await db.Offices.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Location).ToArrayAsync(cancellationToken);
        foreach (var office in offices)
        {
            var localToday = LocalDate(now, office.TimeZoneId);
            var monthStart = new DateOnly(localToday.Year, localToday.Month, 1);
            var priorMonth = monthStart.AddMonths(-1);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId);
            var priorStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(priorMonth.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var currentStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(monthStart.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
            var usage = await db.StockMovements.AsNoTracking()
                .Where(x => x.SnackItem.Office == office.Location && x.Kind == StockMovementKind.Consumption &&
                    x.OccurredAt >= priorStart && x.OccurredAt < currentStart)
                .GroupBy(x => x.SnackItemId)
                .Select(group => new { ItemId = group.Key, Quantity = group.Sum(x => x.ReportedConsumedQuantity ?? -x.Quantity) })
                .ToDictionaryAsync(x => x.ItemId, x => x.Quantity, cancellationToken);
            var open = await db.Refills.AsNoTracking()
                .Where(x => x.Office == office.Location && x.Status == RefillStatus.Open && x.SnackItemId != null)
                .Select(x => new { x.Id, x.SnackItemId, x.CreatedAt }).ToArrayAsync(cancellationToken);
            var ordered = open.OrderByDescending(x => x.SnackItemId is Guid itemId && usage.TryGetValue(itemId, out var quantity) ? quantity : 0m)
                .ThenBy(x => x.CreatedAt).ToArray();
            foreach (var refill in ordered)
            {
                try { await OfferNextEmployeeAsync(refill.Id, now, cancellationToken); }
                catch (DbUpdateConcurrencyException) { /* Another worker or employee advanced this refill. */ }
                catch (DbUpdateException exception)
                {
                    logger.LogWarning(exception, "Could not create the next employee offer for refill {RefillId}.", refill.Id);
                }
            }
        }
    }

    private async Task OfferNextEmployeeAsync(Guid refillId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var refill = await db.Refills.Include(x => x.AssignedVolunteer)
            .SingleOrDefaultAsync(x => x.Id == refillId, cancellationToken);
        if (refill is null || refill.Status != RefillStatus.Open || refill.AssignedVolunteer is not null || refill.SnackItemId is null) return;
        var item = await db.SnackItems.SingleOrDefaultAsync(x => x.Id == refill.SnackItemId && x.Office == refill.Office, cancellationToken);
        if (item is null || !item.IsActive || item.QuantityOnHand > 0 || item.IsClaimed) return;
        var office = await db.Offices.SingleAsync(x => x.Location == refill.Office, cancellationToken);
        var localDate = LocalDate(now, office.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId));
        var thisMonth = new DateOnly(localDate.Year, localDate.Month, 1);
        var configuredBudget = await db.MonthlyBudgets.AsNoTracking().Where(x => x.Office == office.Location &&
                x.Year == localDate.Year && x.Month == localDate.Month)
            .Select(x => new { x.BudgetAmount, x.SpentAmount }).SingleOrDefaultAsync(cancellationToken);
        if ((configuredBudget?.SpentAmount ?? 0m) >= (configuredBudget?.BudgetAmount ?? office.MonthlyBudget)) return;

        var employees = await db.Volunteers.Where(x => x.IsActive &&
                db.EmployeeRoles.Any(role => role.EmployeeId == x.EmployeeId && role.RoleName == "Employee") &&
                db.EmployeeOffices.Any(roster => roster.EmployeeId == x.EmployeeId && roster.Office == refill.Office &&
                    (roster.ActiveFrom == null || roster.ActiveFrom <= localDate) &&
                    (roster.ActiveUntil == null || roster.ActiveUntil >= localDate)))
            .OrderBy(x => x.EmployeeId).ToArrayAsync(cancellationToken);
        if (employees.Length == 0) return;
        var employeeIds = employees.Select(x => x.EmployeeId).ToArray();
        var leavesToday = await db.EmployeeLeaves.AsNoTracking().Where(x => employeeIds.Contains(x.EmployeeId) &&
                x.Office == refill.Office && x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= localDate && x.EndDate >= localDate)
            .Select(x => x.EmployeeId).ToArrayAsync(cancellationToken);
        var unavailableToday = await db.RefillAssignmentAttempts.AsNoTracking().Where(x => employeeIds.Contains(x.EmployeeId) &&
                x.LocalDate == localDate && (x.Outcome == RefillAttemptOutcome.SkippedLeave ||
                    x.Outcome == RefillAttemptOutcome.SkippedWfh || x.Outcome == RefillAttemptOutcome.WrongLocation))
            .Select(x => x.EmployeeId).Distinct().ToArrayAsync(cancellationToken);
        var triedThisRefillToday = await db.RefillAssignmentAttempts.AsNoTracking().Where(x =>
                x.RefillId == refill.Id && x.LocalDate == localDate)
            .Select(x => x.EmployeeId).ToArrayAsync(cancellationToken);
        var currentlyAssigned = await db.Refills.AsNoTracking().Where(x => x.Status == RefillStatus.Assigned &&
                x.AssignedVolunteer != null && x.Id != refill.Id)
            .Select(x => EF.Property<string?>(x, "AssignedVolunteerEmployeeId"))
            .Where(x => x != null).ToArrayAsync(cancellationToken);
        var blocked = leavesToday.Concat(unavailableToday).Concat(triedThisRefillToday).Concat(currentlyAssigned!).ToHashSet(StringComparer.Ordinal);
        var available = employees.Where(x => !blocked.Contains(x.EmployeeId)).ToArray();
        if (available.Length == 0) return;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId);
        var monthStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(thisMonth.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var nextMonthUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(thisMonth.AddMonths(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var completedCounts = await db.Refills.AsNoTracking().Where(x => x.Office == refill.Office &&
                x.Status == RefillStatus.Purchased && x.PurchasedAt >= monthStartUtc && x.PurchasedAt < nextMonthUtc &&
                x.AssignedVolunteer != null)
            .GroupBy(x => EF.Property<string?>(x, "AssignedVolunteerEmployeeId"))
            .Select(group => new { EmployeeId = group.Key!, Count = group.Count() })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Count, cancellationToken);
        var offerCounts = await db.RefillAssignmentAttempts.AsNoTracking().Where(x =>
                x.LocalDate >= thisMonth && x.LocalDate < thisMonth.AddMonths(1) && employeeIds.Contains(x.EmployeeId) &&
                db.Refills.Any(r => r.Id == x.RefillId && r.Office == refill.Office) &&
                x.Outcome != RefillAttemptOutcome.SkippedLeave && x.Outcome != RefillAttemptOutcome.SkippedWfh &&
                x.Outcome != RefillAttemptOutcome.WrongLocation && x.Outcome != RefillAttemptOutcome.NoRosterAvailable)
            .GroupBy(x => x.EmployeeId)
            .Select(group => new { EmployeeId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Count, cancellationToken);
        var volunteer = available.OrderBy(x => completedCounts.TryGetValue(x.EmployeeId, out var done) ? done : 0)
            .ThenBy(x => offerCounts.TryGetValue(x.EmployeeId, out var offered) ? offered : 0)
            .ThenBy(x => x.EmployeeId, StringComparer.Ordinal).First();

        var offerMinutes = Math.Clamp(configuration.GetValue("SnackCounter:OfferTimeoutMinutes", 30), 1, 24 * 60);
        var expiresAt = now.AddMinutes(offerMinutes);
        var attemptNumber = (await db.RefillAssignmentAttempts.Where(x => x.RefillId == refill.Id)
            .Select(x => (int?)x.AttemptNumber).MaxAsync(cancellationToken) ?? 0) + 1;
        refill.AssignedVolunteer = volunteer;
        refill.Status = RefillStatus.Assigned;
        refill.AssignmentExpiresAt = expiresAt;
        refill.Version++;
        item.IsClaimed = true;
        var attempt = new RefillAssignmentAttempt
        {
            Id = Guid.NewGuid(), RefillId = refill.Id, EmployeeId = volunteer.EmployeeId, LocalDate = localDate,
            AttemptNumber = attemptNumber, Outcome = RefillAttemptOutcome.Offered,
            AttemptedAt = now, AssignmentExpiresAt = expiresAt
        };
        db.RefillAssignmentAttempts.Add(attempt);
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, volunteer.EmployeeId, now, cancellationToken);
        var personalPayload = FrontendApiSupport.Serialize(new
        {
            refillId = refill.Id, itemId = item.Id, office = office.Location.ToString(),
            expiresAt, offerMinutes
        });
        var personalMessage = FrontendApiSupport.AddMessage(db, employeeChat, null, volunteer.EmployeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification,
            $"You have the next refill offer for {item.Name} at {office.Name}. Accept it and confirm your work location within {offerMinutes} minutes.",
            personalPayload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "RefillOffered", $"refill:{refill.Id}:offer:{attempt.Id}",
            volunteer.EmployeeId, null, false, personalMessage, personalPayload, now);
        var officeChat = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(db, office.Location, now, cancellationToken);
        var officePayload = FrontendApiSupport.Serialize(new
        {
            refillId = refill.Id, itemId = item.Id, office = office.Location.ToString(),
            suggestedEmployeeId = volunteer.EmployeeId, expiresAt, state = RefillOfferState.Offered.ToString()
        });
        await AddEventAsync(db, officeChat, "RefillOffered",
            $"{item.Name} at {office.Name} is temporarily offered to {volunteer.DisplayName} until {localNow.AddMinutes(offerMinutes):HH:mm} local time.",
            officePayload, $"refill:{refill.Id}:office-offer:{attempt.Id}", null, office.Location, false, now);
        await db.SaveChangesAsync(cancellationToken);
        if (tx is not null) await tx.CommitAsync(cancellationToken);
        if (hub is not null)
        {
            try
            {
                await hub.Clients.Group(NotificationsHub.OfficeGroup(office.Location.ToString()))
                    .SendAsync("officeQueueChanged", new { office = office.Location.ToString(), refillId = refill.Id }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Queue update signal failed for refill {RefillId}.", refill.Id);
            }
        }
    }

    private async Task ReleaseExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var expired = await db.Refills.AsNoTracking().Where(x => x.Status == RefillStatus.Assigned &&
                x.AssignmentExpiresAt != null && x.AssignmentExpiresAt <= now)
            .OrderBy(x => x.AssignmentExpiresAt).Select(x => x.Id).Take(100).ToArrayAsync(cancellationToken);
        foreach (var id in expired)
        {
            try { await ReleaseExpiredClaimAsync(id, now, cancellationToken); }
            catch (DbUpdateConcurrencyException) { /* A concurrent employee or worker already advanced it. */ }
            catch (DbUpdateException exception) { logger.LogWarning(exception, "Could not release expired refill claim {RefillId}.", id); }
        }

    }

    private async Task ReleaseExpiredClaimAsync(Guid refillId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        var refill = await db.Refills.Include(x => x.AssignedVolunteer).SingleOrDefaultAsync(x => x.Id == refillId, cancellationToken);
        if (refill?.Status != RefillStatus.Assigned || refill.AssignmentExpiresAt > now || refill.AssignedVolunteer is null) return;
        var previousEmployeeId = refill.AssignedVolunteer.EmployeeId;
        var item = refill.SnackItemId is null ? null : await db.SnackItems.SingleOrDefaultAsync(x => x.Id == refill.SnackItemId, cancellationToken);
        if (item is null) return;
        var office = await db.Offices.SingleAsync(x => x.Location == refill.Office, cancellationToken);
        var expiredAttempt = await db.RefillAssignmentAttempts.Where(x => x.RefillId == refill.Id &&
                x.EmployeeId == previousEmployeeId && (x.Outcome == RefillAttemptOutcome.Offered ||
                    x.Outcome == RefillAttemptOutcome.OfferAcknowledged || x.Outcome == RefillAttemptOutcome.Accepted))
            .OrderByDescending(x => x.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        var wasPendingOffer = expiredAttempt?.Outcome == RefillAttemptOutcome.Offered;
        if (expiredAttempt is not null) expiredAttempt.Outcome = RefillAttemptOutcome.Expired;
        refill.AssignedVolunteer = null; refill.AssignmentExpiresAt = null; refill.Status = RefillStatus.Open; refill.Version++;
        item.IsClaimed = false;
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, previousEmployeeId, now, cancellationToken);
        var personalPayload = FrontendApiSupport.Serialize(new { refillId = refill.Id, itemId = item.Id, office = office.Location.ToString() });
        var personalMessage = FrontendApiSupport.AddMessage(db, employeeChat, null, previousEmployeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification,
            wasPendingOffer
                ? $"Your {item.Name} refill offer expired. The item is being offered to the next eligible employee."
                : $"Your {item.Name} refill task timed out. The item is being offered to the next eligible employee.", personalPayload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, wasPendingOffer ? "RefillOfferExpired" : "RefillAssignmentExpired",
            $"refill:{refill.Id}:expired:{refill.Version}",
            previousEmployeeId, null, false, personalMessage, personalPayload, now);
        var chat = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(db, office.Location, now, cancellationToken);
        var payload = FrontendApiSupport.Serialize(new { refillId = refill.Id, itemId = item.Id, office = office.Location.ToString() });
        await AddEventAsync(db, chat, "RefillReoffered", $"{item.Name} is being offered to the next eligible employee in {office.Name}.",
            payload, $"refill-available:{refill.Id}:{refill.Version}", null, office.Location, false, now);
        await db.SaveChangesAsync(cancellationToken);
        if (tx is not null) await tx.CommitAsync(cancellationToken);
    }

    private async Task SendLeavePromptsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var employees = await db.Volunteers.AsNoTracking().Where(x => x.IsActive).ToArrayAsync(cancellationToken);
        foreach (var employee in employees)
        {
            var office = await db.Offices.AsNoTracking().SingleOrDefaultAsync(x => x.Location == employee.HomeOffice, cancellationToken);
            if (office is null) continue;
            var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId));
            var localDate = DateOnly.FromDateTime(localNow.DateTime);
            if (localDate.Day != DateTime.DaysInMonth(localDate.Year, localDate.Month) || localNow.Hour < 17) continue;
            var next = localDate.AddMonths(1);
            var key = $"leave-prompt:{employee.EmployeeId}:{next:yyyy-MM}";
            if (await db.Notifications.AnyAsync(x => x.DeduplicationKey == key, cancellationToken)) continue;
            var chat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, employee.EmployeeId, now, cancellationToken);
            var payload = FrontendApiSupport.Serialize(new { month = next.ToString("yyyy-MM"), office = office.Location.ToString() });
            var msg = FrontendApiSupport.AddMessage(db, chat, null, employee.EmployeeId, null, MessageAudience.Employee,
                ConversationMessageKind.Notification,
                $"Please share your planned leave for {next:MMMM yyyy}, or confirm that you have none planned.", payload, now);
            FrontendApiSupport.AddNotificationAndOutbox(db, "PlannedLeavePrompt", key, employee.EmployeeId, null, false, msg, payload, now);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
    }

    private async Task PublishMonthEndReportsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await db.Offices.AsNoTracking().Where(x => x.IsActive).ToArrayAsync(cancellationToken);
        if (offices.Length == 0) return;
        var periods = offices.Select(x => PreviousClosedMonth(now, x.TimeZoneId)).ToArray();
        var month = periods.Min();
        if (periods.Any(x => x != month)) return; // Wait until every office has closed the same period.
        foreach (var office in offices)
        {
            var exists = await db.MonthlyReports.AnyAsync(x => x.Year == month.Year && x.Month == month.Month && x.Office == office.Location, cancellationToken);
            if (exists) continue;
            var (start, end) = PeriodUtc(month, office.TimeZoneId);
            var spent = await db.Refills.AsNoTracking().Where(x => x.Office == office.Location && x.PurchasedAt >= start && x.PurchasedAt < end)
                .SumAsync(x => x.PurchaseAmount ?? 0m, cancellationToken);
            var budget = await db.MonthlyBudgets.AsNoTracking().Where(x => x.Office == office.Location && x.Year == month.Year && x.Month == month.Month)
                .Select(x => (decimal?)x.BudgetAmount).SingleOrDefaultAsync(cancellationToken) ?? office.MonthlyBudget;
            var movements = await db.StockMovements.AsNoTracking().Where(x => x.SnackItem.Office == office.Location &&
                    x.Kind == StockMovementKind.Consumption && x.OccurredAt >= start && x.OccurredAt < end)
                .Select(x => new { Item = x.SnackItem.Name, x.Quantity, x.ReportedConsumedQuantity }).ToArrayAsync(cancellationToken);
            var topItems = movements.GroupBy(x => x.Item)
                .Select(group => new
                {
                    item = group.Key,
                    quantity = group.Sum(x => x.ReportedConsumedQuantity ?? -x.Quantity),
                    observedQuantity = group.Where(x => x.ReportedConsumedQuantity != null).Sum(x => x.ReportedConsumedQuantity ?? 0m),
                    estimatedQuantity = group.Where(x => x.ReportedConsumedQuantity == null).Sum(x => -x.Quantity)
                })
                .OrderByDescending(x => x.quantity).Take(10).ToArray();
            var payload = FrontendApiSupport.Serialize(new { month = month.ToString("yyyy-MM"), office = office.Location,
                currency = office.BudgetCurrency, budget, spent, remaining = budget - spent, topItems, asOf = end });
            db.MonthlyReports.Add(new MonthlyReport { Id = Guid.NewGuid(), Kind = MonthlyReportKind.Office,
                Office = office.Location, Year = month.Year, Month = month.Month, AsOf = end, PayloadJson = payload, GeneratedAt = now });
            var chat = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(db, office.Location, now, cancellationToken);
            await AddEventAsync(db, chat, "MonthlyReportReady", $"{office.Name} monthly report for {month:MMMM yyyy} is ready.",
                payload, $"report:{office.Location}:{month:yyyy-MM}", null, office.Location, false, now);
            if (budget > 0 && spent >= budget * office.WarningThresholdPercent / 100m)
            {
                await AddEventAsync(db, chat, "BudgetThresholdReached",
                    $"{office.Name} used {spent} of its {budget} {office.BudgetCurrency} budget in {month:MMMM yyyy}.",
                    payload, $"budget-near:{office.Location}:{month:yyyy-MM}", null, office.Location, false, now);
            }
            var budgetEvent = spent == budget ? "BudgetMet" : spent > budget ? "BudgetExceeded" : "BudgetUnusedAtClose";
            await AddEventAsync(db, chat, budgetEvent, spent >= budget
                    ? $"{office.Name} spent {spent} {office.BudgetCurrency} against its {month:MMMM yyyy} budget of {budget} {office.BudgetCurrency}."
                    : $"{office.Name} ended {month:MMMM yyyy} with {budget - spent} {office.BudgetCurrency} unused.",
                payload, budgetEvent switch
                {
                    "BudgetMet" => $"budget-met:{office.Location}:{month:yyyy-MM}",
                    "BudgetExceeded" => $"budget-exceeded:{office.Location}:{month:yyyy-MM}",
                    _ => $"budget-unused:{office.Location}:{month:yyyy-MM}"
                }, null, office.Location, false, now);
            await AddEventAsync(db, chat, "MonthlyConsumptionRanked", $"Top consumed items for {office.Name} in {month:MMMM yyyy} are ready.",
                payload, $"consumption:{office.Location}:{month:yyyy-MM}", null, office.Location, false, now);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }

        await using var check = await contextFactory.CreateDbContextAsync(cancellationToken);
        var allOfficeReports = await check.MonthlyReports.AsNoTracking().Where(x => x.Year == month.Year && x.Month == month.Month && x.Office != null)
            .Select(x => new { x.Office, x.PayloadJson }).ToArrayAsync(cancellationToken);
        if (allOfficeReports.Length != offices.Length) return;
        var existingCombined = await check.MonthlyReports.SingleOrDefaultAsync(x =>
            x.Year == month.Year && x.Month == month.Month && x.Office == null, cancellationToken);
        if (existingCombined?.ReportingCurrency is not null) return;
        var totalsByCurrency = new Dictionary<Currency, decimal>();
        decimal totalInNok = 0m;
        foreach (var office in offices)
        {
            var (start, end) = PeriodUtc(month, office.TimeZoneId);
            var purchases = await check.Refills.AsNoTracking().Where(x => x.Office == office.Location && x.PurchasedAt >= start && x.PurchasedAt < end)
                .Where(x => x.PurchaseAmount != null && x.PurchaseCurrency != null && x.PurchasedAt != null)
                .Select(x => new { Amount = x.PurchaseAmount!.Value, Currency = x.PurchaseCurrency!.Value, PurchasedAt = x.PurchasedAt!.Value })
                .ToArrayAsync(cancellationToken);
            totalsByCurrency[office.BudgetCurrency] = purchases.Sum(x => x.Amount);
            totalInNok += purchases.Sum(x => currencyConverter.Convert(x.Amount, x.Currency, Currency.NOK,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.PurchasedAt, TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId)).DateTime)));
        }
        var combined = FrontendApiSupport.Serialize(new
        {
            month = month.ToString("yyyy-MM"),
            offices = allOfficeReports.Select(x => new { office = x.Office, report = x.PayloadJson }),
            totalsByCurrency,
            reportingCurrency = Currency.NOK,
            totalInReportingCurrency = totalInNok,
            exchangeRatePolicy = "Configured static demonstration FX table; converted per purchase and rounded to 2 decimals.",
            asOf = now
        });
        var hrChat = await FrontendApiSupport.GetOrCreateHrConversationAsync(check, now, cancellationToken);
        if (existingCombined is null)
        {
            check.MonthlyReports.Add(new MonthlyReport { Id = Guid.NewGuid(), Kind = MonthlyReportKind.Combined,
                Year = month.Year, Month = month.Month, AsOf = now, ReportingCurrency = Currency.NOK,
                PayloadJson = combined, GeneratedAt = now });
            await AddEventAsync(check, hrChat, "MonthlyReportReady", $"Combined monthly report for {month:MMMM yyyy} is ready.",
                combined, $"report:combined:{month:yyyy-MM}", null, null, true, now);
        }
        else
        {
            existingCombined.ReportingCurrency = Currency.NOK;
            existingCombined.PayloadJson = combined;
            existingCombined.AsOf = now;
            var key = $"report:combined:{month:yyyy-MM}";
            var existingNotification = await check.Notifications.SingleOrDefaultAsync(x => x.DeduplicationKey == key, cancellationToken);
            if (existingNotification is not null)
            {
                existingNotification.PayloadJson = combined;
                if (existingNotification.ConversationMessageId is Guid messageId)
                {
                    var message = await check.ConversationMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
                    if (message is not null) message.PayloadJson = combined;
                }
            }
        }
        try { await check.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { check.ChangeTracker.Clear(); }
    }

    private async Task AddEventAsync(SnackCounterDbContext db, Conversation chat, string eventType, string text,
        string payload, string key, string? recipient, OfficeLocation? office, bool hr, DateTimeOffset now)
    {
        if (await db.Notifications.AnyAsync(x => x.DeduplicationKey == key)) return;
        var message = FrontendApiSupport.AddMessage(db, chat, null, recipient, office,
            hr ? MessageAudience.Hr : office is null ? MessageAudience.Employee : MessageAudience.Office,
            ConversationMessageKind.Report, text, payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, eventType, key, recipient, office, hr, message, payload, now);
    }

    private static DateOnly LocalDate(DateTimeOffset now, string zoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).DateTime);

    private static DateOnly PreviousClosedMonth(DateTimeOffset now, string zoneId)
    {
        var local = LocalDate(now, zoneId);
        return new DateOnly(local.Year, local.Month, 1).AddMonths(-1);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) PeriodUtc(DateOnly month, string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var start = TimeZoneInfo.ConvertTimeToUtc(month.ToDateTime(TimeOnly.MinValue), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), zone);
        return (new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero));
    }
}
