using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "Employee,HR,Manager")]
[Route("api")]
public sealed class RefillOperationsController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock,
    IConfiguration configuration,
    IHubContext<NotificationsHub>? hub = null,
    ILogger<RefillOperationsController>? logger = null) : ControllerBase
{
    [HttpGet("offices/{office}/inventory")]
    [ProducesResponseType<IReadOnlyList<SnackItemResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SnackItemResponse>>> Inventory(
        OfficeLocation office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken))
        {
            return Forbid();
        }

        var items = await database.SnackItems.AsNoTracking().Where(item => item.Office == office)
            .OrderBy(item => item.Name)
            .Select(item => new SnackItemResponse(
                item.Id, item.Office, item.Name, item.Unit, item.QuantityOnHand, item.ReorderThreshold,
                item.IsActive, item.IsClaimed, Convert.ToBase64String(item.RowVersion)))
            .ToArrayAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("offices/{office}/inventory/{itemId:guid}/movements")]
    [ProducesResponseType<IReadOnlyList<StockMovementResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StockMovementResponse>>> Movements(
        OfficeLocation office,
        Guid itemId,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken))
        {
            return Forbid();
        }

        var exists = await database.SnackItems.AnyAsync(item => item.Id == itemId && item.Office == office, cancellationToken);
        if (!exists)
        {
            return NotFound();
        }
        var movements = await database.StockMovements.AsNoTracking()
            .Where(movement => movement.SnackItemId == itemId)
            .OrderByDescending(movement => movement.OccurredAt)
            .Take(Math.Clamp(pageSize, 1, 100))
            .Select(movement => new StockMovementResponse(
                movement.Id, movement.SnackItemId, movement.Kind, movement.Quantity,
                movement.OccurredAt, movement.ActorEmployeeId, movement.RefillId,
                movement.UnitCost, movement.CostCurrency, movement.Note, movement.ReportedConsumedQuantity))
            .ToArrayAsync(cancellationToken);
        return Ok(movements);
    }

    [HttpGet("offices/{office}/refill-requests")]
    [ProducesResponseType<IReadOnlyList<OfficeRefillQueueItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OfficeRefillQueueItemResponse>>> OfficeRefillQueue(
        OfficeLocation office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken)) return Forbid();
        var refills = await database.Refills.AsNoTracking().Include(x => x.AssignedVolunteer)
            .Where(x => x.Office == office && (x.Status == RefillStatus.Open || x.Status == RefillStatus.Assigned))
            .ToArrayAsync(cancellationToken);
        var itemIds = refills.Where(x => x.SnackItemId != null).Select(x => x.SnackItemId!.Value).Distinct().ToArray();
        var items = await database.SnackItems.AsNoTracking().Where(x => itemIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var responses = new List<OfficeRefillQueueItemResponse>(refills.Length);
        foreach (var refill in refills.OrderBy(x => x.CreatedAt))
        {
            var attempt = refill.AssignedVolunteer is null ? null : await database.RefillAssignmentAttempts.AsNoTracking()
                .Where(x => x.RefillId == refill.Id && x.EmployeeId == refill.AssignedVolunteer.EmployeeId &&
                    (x.Outcome == RefillAttemptOutcome.Offered || x.Outcome == RefillAttemptOutcome.OfferAcknowledged || x.Outcome == RefillAttemptOutcome.Accepted))
                .OrderByDescending(x => x.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
            var state = attempt?.Outcome switch
            {
                RefillAttemptOutcome.Offered => RefillOfferState.Offered,
                RefillAttemptOutcome.OfferAcknowledged => RefillOfferState.Acknowledged,
                RefillAttemptOutcome.Accepted => RefillOfferState.Accepted,
                _ => RefillOfferState.None
            };
            responses.Add(new OfficeRefillQueueItemResponse(
                RefillResponse.From(refill),
                refill.SnackItemId is Guid id && items.TryGetValue(id, out var item) ? Convert.ToBase64String(item.RowVersion) : null,
                refill.AssignedVolunteer?.EmployeeId,
                state == RefillOfferState.None ? "Waiting for the next eligible employee offer." : "Offers rotate by completed refills; employees on leave and prior attempts today are skipped.",
                state == RefillOfferState.Offered && refill.AssignedVolunteer?.EmployeeId == employeeId && refill.AssignmentExpiresAt > clock.GetUtcNow(),
                attempt?.AssignmentExpiresAt,
                state));
        }
        return Ok(responses);
    }

    [HttpPost("offices/{office}/inventory/items")]
    [ProducesResponseType<SnackItemResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SnackItemResponse>> AddItem(
        OfficeLocation office,
        CreateSnackItemRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 30 || request.ReorderThreshold < 0)
        {
            return BadRequest(Problem("Provide an item name, unit, and non-negative reorder threshold."));
        }
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken))
        {
            return Forbid();
        }

        var normalizedName = request.Name.Trim();
        var existing = await database.SnackItems.SingleOrDefaultAsync(
            item => item.Office == office && item.Name == normalizedName, cancellationToken);
        if (existing is not null)
        {
            if (existing.QuantityOnHand > 0)
            {
                return Conflict(Problem("This item is already in stock. A refill request is only allowed when stock is empty."));
            }
            return Ok(ToResponse(existing));
        }

        var item = new SnackItem
        {
            Id = Guid.NewGuid(),
            Office = office,
            Name = normalizedName,
            Unit = request.Unit.Trim(),
            ReorderThreshold = request.ReorderThreshold,
            QuantityOnHand = 0,
            IsActive = true
        };
        database.SnackItems.Add(item);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(Problem("An item with this name was created at this office by another request."));
        }
        return Created($"/api/offices/{office}/inventory", ToResponse(item));
    }

    [HttpPost("offices/{office}/refill-requests")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillResponse>> OpenRefill(
        OfficeLocation office,
        OpenRefillRequest request,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken))
        {
            return Forbid();
        }
        var item = await database.SnackItems.SingleOrDefaultAsync(
            candidate => candidate.Id == request.SnackItemId && candidate.Office == office && candidate.IsActive,
            cancellationToken);
        if (item is null)
        {
            return NotFound(Problem("The item does not exist at this office."));
        }
        if (item.QuantityOnHand > 0)
        {
            return Conflict(Problem("This item is already in stock; refill is allowed only when stock is empty."));
        }
        if (item.IsClaimed || await database.Refills.AnyAsync(refill => refill.Office == office &&
                refill.SnackItemId == item.Id && (refill.Status == RefillStatus.Open || refill.Status == RefillStatus.Assigned),
                cancellationToken))
        {
            return Conflict(Problem("A refill request for this item is already active."));
        }

        var refill = new Refill(Guid.NewGuid(), office, item.Name, now)
        {
            RequesterEmployeeId = employeeId,
            SnackItemId = item.Id
        };
        database.Refills.Add(refill);
        await AddOfficeEventAsync(database, office, "RefillNeeded", $"{item.Name} needs refilling at {office}.",
            new { refillId = refill.Id, itemId = item.Id, office = office.ToString() }, now, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(Problem("The item was claimed by another request. Reload inventory and try again."));
        }
        return Created($"/api/me/refills/{refill.Id}", RefillResponse.From(refill));
    }

    [HttpPost("offices/{office}/refill-requests/{refillId:guid}/claim")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillResponse>> ClaimRefill(
        OfficeLocation office,
        Guid refillId,
        ClaimRefillRequest request,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office, cancellationToken)) return Forbid();
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var refill = await database.Refills.Include(x => x.AssignedVolunteer)
            .SingleOrDefaultAsync(x => x.Id == refillId && x.Office == office, cancellationToken);
        if (refill is null) return NotFound();
        if (refill.Status != RefillStatus.Assigned || refill.AssignedVolunteer?.EmployeeId != employeeId ||
            refill.AssignmentExpiresAt <= now || refill.SnackItemId is null)
            return Conflict(Problem("This refill is not currently offered to you. Wait for your personal offer."));
        if (!VersionMatches(refill.RowVersion, request.RefillRowVersion))
            return Conflict(Problem("This refill offer changed or was already accepted. Reload the office queue."));
        var item = await database.SnackItems.SingleOrDefaultAsync(x => x.Id == refill.SnackItemId && x.Office == office, cancellationToken);
        if (item is null || item.QuantityOnHand > 0 || !item.IsClaimed)
            return Conflict(Problem("This refill item is no longer available."));
        if (!VersionMatches(item.RowVersion, request.ItemRowVersion))
            return Conflict(Problem("This item changed. Reload the office queue and try again."));

        var attempt = await database.RefillAssignmentAttempts.Where(x => x.RefillId == refill.Id &&
                x.EmployeeId == employeeId && x.Outcome == RefillAttemptOutcome.Offered)
            .OrderByDescending(x => x.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        if (attempt is null || attempt.AssignmentExpiresAt <= now)
            return Conflict(Problem("This offer has expired. The next eligible employee will be offered the refill."));

        var officeConfig = await database.Offices.SingleAsync(x => x.Location == office, cancellationToken);
        var localDate = ToLocalDate(now, officeConfig.TimeZoneId);
        var monthlyBudget = await GetOrCreateBudgetAsync(database, officeConfig, localDate, now, cancellationToken);
        if (monthlyBudget.SpentAmount >= monthlyBudget.BudgetAmount)
        {
            return Conflict(Problem("This office has reached its monthly budget. Refill claiming will resume on the first day of next month."));
        }
        attempt.Outcome = RefillAttemptOutcome.OfferAcknowledged;
        attempt.AssignmentExpiresAt = now.AddMinutes(Math.Clamp(
            configuration.GetValue("SnackCounter:AssignmentTimeoutMinutes", 24 * 60), 1, 30 * 24 * 60));
        refill.AssignmentExpiresAt = attempt.AssignmentExpiresAt;
        refill.Version++;
        await AddEmployeeEventAsync(database, employeeId, "RefillClaimed",
            $"You accepted the refill offer for {item.Name} at {officeConfig.Name}. Confirm where you are working to continue.",
            new { refillId = refill.Id, itemId = item.Id, office = office.ToString(), expiresAt = refill.AssignmentExpiresAt },
            now, cancellationToken);
        await AddOfficeEventAsync(database, office, "RefillClaimed", $"{item.Name} was claimed for refilling.",
            new { refillId = refill.Id, itemId = item.Id, employeeId }, now, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("Another employee claimed this refill first. Reload the office queue."));
        }
        catch (DbUpdateException)
        {
            return Conflict(Problem("Another employee claimed this refill first. Reload the office queue."));
        }
        await NotifyOfficeQueueChangedAsync(office, refill.Id, cancellationToken);
        return Ok(RefillResponse.From(refill));
    }

    private async Task NotifyOfficeQueueChangedAsync(
        OfficeLocation office,
        Guid refillId,
        CancellationToken cancellationToken)
    {
        if (hub is null) return;
        try
        {
            await hub.Clients.Group(NotificationsHub.OfficeGroup(office.ToString()))
                .SendAsync("officeQueueChanged", new { office = office.ToString(), refillId }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "Immediate refill queue update failed for {Office}; durable notification will retry.", office);
        }
    }

    [HttpGet("me/refills")]
    [ProducesResponseType<IReadOnlyList<RefillResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RefillResponse>>> MyRefills(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var refills = await database.Refills.AsNoTracking()
            .Include(refill => refill.AssignedVolunteer)
            .Where(refill => refill.RequesterEmployeeId == employeeId ||
                EF.Property<string?>(refill, "AssignedVolunteerEmployeeId") == employeeId)
            .Where(refill => office == null || refill.Office == office)
            .OrderByDescending(refill => refill.CreatedAt)
            .Select(refill => refill.Copy())
            .ToArrayAsync(cancellationToken);
        return Ok(refills.Select(RefillResponse.From).ToArray());
    }

    [HttpPost("refills/{refillId:guid}/accept")]
    [ProducesResponseType<RefillActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillActionResponse>> AcceptRefill(
        Guid refillId,
        AcceptRefillRequest request,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var refill = await database.Refills.Include(item => item.AssignedVolunteer)
            .SingleOrDefaultAsync(item => item.Id == refillId, cancellationToken);
        if (refill is null)
        {
            return NotFound();
        }
        if (refill.Status != RefillStatus.Assigned || refill.AssignedVolunteer?.EmployeeId != employeeId ||
            refill.Version != request.ExpectedVersion)
        {
            return Conflict(Problem("This refill is not assigned to you or its version is stale."));
        }

        var item = refill.SnackItemId is null ? null : await database.SnackItems.SingleOrDefaultAsync(
            candidate => candidate.Id == refill.SnackItemId, cancellationToken);
        if (item is null)
        {
            return Conflict(Problem("The assigned item is no longer available."));
        }
        var localDate = await GetOfficeLocalDateAsync(database, refill.Office, now, cancellationToken);
        var attempt = await database.RefillAssignmentAttempts.Where(candidate =>
                candidate.RefillId == refill.Id && candidate.EmployeeId == employeeId &&
                (candidate.Outcome == RefillAttemptOutcome.Offered || candidate.Outcome == RefillAttemptOutcome.OfferAcknowledged))
            .OrderByDescending(candidate => candidate.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        if (attempt is null || attempt.AssignmentExpiresAt <= now)
        {
            return Conflict(Problem("This refill offer has expired. The next eligible employee will receive it."));
        }

        attempt.WorkingMode = request.WorkingMode;
        attempt.WorkingOffice = request.WorkingOffice;
        var onLeave = await database.EmployeeLeaves.AnyAsync(x => x.EmployeeId == employeeId && x.Office == refill.Office &&
            x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= localDate && x.EndDate >= localDate, cancellationToken);
        var skipReason = onLeave
            ? "You are on leave today. The refill will be offered to the next eligible employee."
            : request.WorkingMode == WorkingMode.WFH
            ? "You are working from home today. The refill will be offered to the next eligible employee."
            : request.WorkingOffice != refill.Office
                ? "You are working from another office today. The refill will be offered to the next eligible employee."
                : null;
        if (skipReason is not null)
        {
            attempt.Outcome = onLeave ? RefillAttemptOutcome.SkippedLeave : request.WorkingMode == WorkingMode.WFH
                ? RefillAttemptOutcome.SkippedWfh
                : RefillAttemptOutcome.WrongLocation;
            refill.Status = RefillStatus.Open;
            refill.AssignedVolunteer = null;
            refill.AssignmentExpiresAt = null;
            refill.Version++;
            item.IsClaimed = false;
            await AddOfficeEventAsync(database, refill.Office, "RefillOfferPassed", $"{item.Name} will be offered to another eligible employee.",
                new { refillId = refill.Id, itemId = item.Id, office = refill.Office.ToString() }, now, cancellationToken);
        }
        else
        {
            attempt.Outcome = RefillAttemptOutcome.Accepted;
            attempt.AssignmentExpiresAt = now.AddMinutes(Math.Clamp(
                configuration.GetValue("SnackCounter:AssignmentTimeoutMinutes", 24 * 60), 1, 30 * 24 * 60));
            refill.AssignmentExpiresAt = attempt.AssignmentExpiresAt;
            refill.Version++;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("The refill changed while you were accepting it. Reload and try again."));
        }
        return Ok(new RefillActionResponse(RefillResponse.From(refill), skipReason is null, skipReason));
    }

    [HttpPost("refills/{refillId:guid}/complete")]
    [RequestSizeLimit(5_000_000)]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillResponse>> CompleteRefill(
        Guid refillId,
        CompleteRefillRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Quantity <= 0 || request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Description))
        {
            return BadRequest(Problem("Quantity, purchase amount, and description are required."));
        }
        byte[]? receipt = null;
        if (!string.IsNullOrWhiteSpace(request.ReceiptImageBase64))
        {
            try
            {
                receipt = Convert.FromBase64String(request.ReceiptImageBase64);
            }
            catch (FormatException)
            {
                return BadRequest(Problem("ReceiptImageBase64 must be valid base64."));
            }
        }

        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var refill = await database.Refills.Include(item => item.AssignedVolunteer)
            .SingleOrDefaultAsync(item => item.Id == refillId, cancellationToken);
        if (refill is null)
        {
            return NotFound();
        }
        if (refill.Status != RefillStatus.Assigned || refill.AssignedVolunteer?.EmployeeId != employeeId ||
            refill.Version != request.ExpectedVersion)
        {
            return Conflict(Problem("This refill is not assigned to you or its version is stale."));
        }

        var item = refill.SnackItemId is null ? null : await database.SnackItems.SingleOrDefaultAsync(
            candidate => candidate.Id == refill.SnackItemId, cancellationToken);
        if (item is null || !item.IsClaimed)
        {
            return Conflict(Problem("The item assignment is no longer active."));
        }
        var acceptedAttempt = await database.RefillAssignmentAttempts.AsNoTracking().Where(attempt =>
                attempt.RefillId == refill.Id && attempt.EmployeeId == employeeId &&
                attempt.Outcome == RefillAttemptOutcome.Accepted)
            .OrderByDescending(attempt => attempt.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        if (acceptedAttempt is null || acceptedAttempt.AssignmentExpiresAt <= now ||
            acceptedAttempt.WorkingMode != WorkingMode.WFO || acceptedAttempt.WorkingOffice != refill.Office)
        {
            return Conflict(Problem("Confirm that you are working from this office today before recording the refill."));
        }
        var office = await database.Offices.SingleAsync(candidate => candidate.Location == refill.Office, cancellationToken);
        var purchaseLocalDate = await GetOfficeLocalDateAsync(database, refill.Office, now, cancellationToken);
        var monthlyBudget = await GetOrCreateBudgetAsync(database, office, purchaseLocalDate, now, cancellationToken);
        if (monthlyBudget.SpentAmount + request.Amount > monthlyBudget.BudgetAmount)
        {
            return Conflict(Problem("This purchase would exceed the office monthly budget."));
        }

        var previousSpend = monthlyBudget.SpentAmount;
        monthlyBudget.SpentAmount += request.Amount;
        item.QuantityOnHand += request.Quantity;
        item.IsClaimed = false;
        refill.PurchaseAmount = request.Amount;
        refill.PurchaseCurrency = office.BudgetCurrency;
        refill.PurchasedAt = now;
        refill.PurchaseDescription = request.Description.Trim();
        refill.ReceiptImage = receipt;
        refill.Status = RefillStatus.Purchased;
        refill.AssignmentExpiresAt = null;
        refill.Version++;
        database.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            SnackItem = item,
            SnackItemId = item.Id,
            Kind = StockMovementKind.Restock,
            Quantity = request.Quantity,
            OccurredAt = now,
            ActorEmployeeId = employeeId,
            RefillId = refill.Id,
            UnitCost = request.Amount / request.Quantity,
            CostCurrency = office.BudgetCurrency,
            Note = request.Description.Trim()
        });
        await AddOfficeEventAsync(database, refill.Office, "RefillCompleted", $"{item.Name} was restocked.",
            new { refillId = refill.Id, itemId = item.Id, quantity = request.Quantity, amount = request.Amount, currency = office.BudgetCurrency.ToString() }, now, cancellationToken);
        if (monthlyBudget.SpentAmount >= monthlyBudget.BudgetAmount)
        {
            var outstandingAssignments = await database.Refills.Include(candidate => candidate.AssignedVolunteer)
                .Where(candidate => candidate.Office == refill.Office && candidate.Status == RefillStatus.Assigned && candidate.Id != refill.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var outstanding in outstandingAssignments)
            {
                if (outstanding.SnackItemId is Guid outstandingItemId)
                {
                    var outstandingItem = await database.SnackItems.SingleOrDefaultAsync(
                        candidate => candidate.Id == outstandingItemId, cancellationToken);
                    if (outstandingItem is not null) outstandingItem.IsClaimed = false;
                }
                var priorAssignee = outstanding.AssignedVolunteer?.EmployeeId;
                outstanding.Status = RefillStatus.Open;
                outstanding.AssignedVolunteer = null;
                outstanding.AssignmentExpiresAt = null;
                outstanding.Version++;
                if (priorAssignee is not null)
                {
                    await AddEmployeeEventAsync(database, priorAssignee, "RefillAvailable",
                        $"{outstanding.Item} was returned to the {office.Name} queue because its monthly budget was reached.",
                        new { refillId = outstanding.Id, office = refill.Office.ToString(), budgetReached = true }, now, cancellationToken);
                }
                await AddOfficeEventAsync(database, refill.Office, "RefillAvailable",
                    $"{outstanding.Item} is back in the queue and can be claimed next month.",
                    new { refillId = outstanding.Id, office = refill.Office.ToString(), budgetReached = true }, now, cancellationToken);
            }
        }
        if (monthlyBudget.BudgetAmount > 0 && previousSpend < monthlyBudget.BudgetAmount * monthlyBudget.WarningThresholdPercent / 100m &&
            monthlyBudget.SpentAmount >= monthlyBudget.BudgetAmount * monthlyBudget.WarningThresholdPercent / 100m)
        {
            await AddOfficeEventWithKeyAsync(database, refill.Office, "BudgetThresholdReached",
                $"{office.Name} has used {monthlyBudget.SpentAmount} of its {monthlyBudget.BudgetAmount} {monthlyBudget.Currency} budget.",
                new { office = refill.Office.ToString(), year = purchaseLocalDate.Year, month = purchaseLocalDate.Month,
                    spent = monthlyBudget.SpentAmount, budget = monthlyBudget.BudgetAmount,
                    remaining = monthlyBudget.BudgetAmount - monthlyBudget.SpentAmount, currency = monthlyBudget.Currency.ToString(),
                    thresholdPercent = monthlyBudget.WarningThresholdPercent },
                $"budget-near:{refill.Office}:{purchaseLocalDate:yyyy-MM}", now, cancellationToken);
        }
        if (monthlyBudget.SpentAmount == monthlyBudget.BudgetAmount)
        {
            await AddOfficeEventWithKeyAsync(database, refill.Office, "BudgetMet",
                $"{office.Name} met its {purchaseLocalDate:MMMM yyyy} budget of {monthlyBudget.BudgetAmount} {monthlyBudget.Currency}.",
                new { office = refill.Office.ToString(), year = purchaseLocalDate.Year, month = purchaseLocalDate.Month,
                    spent = monthlyBudget.SpentAmount, budget = monthlyBudget.BudgetAmount, currency = monthlyBudget.Currency.ToString() },
                $"budget-met:{refill.Office}:{purchaseLocalDate:yyyy-MM}", now, cancellationToken);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("The item, refill, or budget changed. Reload the current state and retry."));
        }
        return Ok(RefillResponse.From(refill));
    }

    [HttpPost("refills/{refillId:guid}/deny")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillResponse>> DenyRefill(
        Guid refillId,
        DenyRefillRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 2_000)
        {
            return BadRequest(Problem("A denial reason of at most 2,000 characters is required."));
        }

        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var refill = await database.Refills.Include(item => item.AssignedVolunteer)
            .SingleOrDefaultAsync(item => item.Id == refillId, cancellationToken);
        if (refill is null)
        {
            return NotFound();
        }
        if (refill.Status != RefillStatus.Assigned || refill.AssignedVolunteer?.EmployeeId != employeeId ||
            refill.Version != request.ExpectedVersion)
        {
            return Conflict(Problem("This refill is not assigned to you or its version is stale."));
        }

        var attempt = await database.RefillAssignmentAttempts.Where(x => x.RefillId == refill.Id &&
                x.EmployeeId == employeeId && (x.Outcome == RefillAttemptOutcome.Offered ||
                    x.Outcome == RefillAttemptOutcome.OfferAcknowledged || x.Outcome == RefillAttemptOutcome.Accepted))
            .OrderByDescending(x => x.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        if (attempt is null) return Conflict(Problem("There is no active refill offer to pass on."));
        attempt.Outcome = RefillAttemptOutcome.Declined;
        refill.Status = RefillStatus.Open;
        refill.AssignedVolunteer = null;
        refill.AssignmentExpiresAt = null;
        refill.Version++;
        if (refill.SnackItemId is not null)
        {
            var item = await database.SnackItems.SingleOrDefaultAsync(candidate => candidate.Id == refill.SnackItemId, cancellationToken);
            if (item is not null) item.IsClaimed = false;
        }
        await AddOfficeEventAsync(database, refill.Office, "RefillOfferPassed", $"{refill.Item} will be offered to another eligible employee.",
            new { refillId = refill.Id, employeeId, reason = request.Reason.Trim() }, now, cancellationToken);
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(database, employeeId, now, cancellationToken);
        var personalPayload = FrontendApiSupport.Serialize(new { refillId, office = refill.Office.ToString(), reason = request.Reason.Trim() });
        var personalMessage = FrontendApiSupport.AddMessage(database, employeeChat, null, employeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification,
            $"You passed on the {refill.Item} refill offer. It will be offered to another employee.", personalPayload, now);
        FrontendApiSupport.AddNotificationAndOutbox(database, "RefillOfferPassed", $"refill:{refillId}:passed:{attempt.Id}",
            employeeId, null, false, personalMessage, personalPayload, now);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("The refill changed while you were denying it. Reload and try again."));
        }
        return Ok(RefillResponse.From(refill));
    }

    [Authorize(Roles = "HR,Manager")]
    [HttpPost("refills/{refillId:guid}/reassign-expired")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefillResponse>> ReassignExpired(Guid refillId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var refill = await database.Refills.Include(item => item.AssignedVolunteer)
            .SingleOrDefaultAsync(item => item.Id == refillId, cancellationToken);
        if (refill is null)
        {
            return NotFound();
        }
        if (refill.Status != RefillStatus.Assigned || refill.AssignmentExpiresAt > now || refill.AssignedVolunteer is null)
        {
            return Conflict(Problem("This refill does not have an expired assignment."));
        }

        var item = refill.SnackItemId is null ? null : await database.SnackItems.SingleOrDefaultAsync(
            candidate => candidate.Id == refill.SnackItemId, cancellationToken);
        if (item is null)
        {
            return Conflict(Problem("The assigned item is no longer available."));
        }
        var previous = refill.AssignedVolunteer.EmployeeId;
        var expiredAttempt = await database.RefillAssignmentAttempts.Where(candidate =>
                candidate.RefillId == refill.Id && candidate.EmployeeId == previous &&
                (candidate.Outcome == RefillAttemptOutcome.Offered || candidate.Outcome == RefillAttemptOutcome.OfferAcknowledged ||
                    candidate.Outcome == RefillAttemptOutcome.Accepted))
            .OrderByDescending(candidate => candidate.AttemptedAt).FirstOrDefaultAsync(cancellationToken);
        if (expiredAttempt is not null)
        {
            expiredAttempt.Outcome = RefillAttemptOutcome.Expired;
        }
        refill.AssignedVolunteer = null;
        refill.AssignmentExpiresAt = null;
        refill.Status = RefillStatus.Open;
        refill.Version++;
        item.IsClaimed = false;
        await AddOfficeEventAsync(database, refill.Office, "RefillAvailable", $"{item.Name} is available to claim again.",
            new { refillId = refill.Id, itemId = item.Id, office = refill.Office.ToString() }, now, cancellationToken);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("The refill changed while the timeout was being processed."));
        }
        return Ok(RefillResponse.From(refill));
    }

    private async Task AddEmployeeEventAsync(
        SnackCounterDbContext database,
        string employeeId,
        string eventType,
        string text,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(
            database, employeeId, now, cancellationToken);
        var payloadJson = FrontendApiSupport.Serialize(payload);
        var message = FrontendApiSupport.AddMessage(database, conversation, null, employeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification, text, payloadJson, now);
        FrontendApiSupport.AddNotificationAndOutbox(database, eventType, $"{eventType}:{message.Id:N}",
            employeeId, null, false, message, payloadJson, now);
    }

    private async Task AddOfficeEventAsync(
        SnackCounterDbContext database,
        OfficeLocation office,
        string eventType,
        string text,
        object payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var conversation = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(
            database, office, now, cancellationToken);
        var payloadJson = FrontendApiSupport.Serialize(payload);
        var message = FrontendApiSupport.AddMessage(database, conversation, null, null, office,
            MessageAudience.Office, ConversationMessageKind.Notification, text, payloadJson, now);
        FrontendApiSupport.AddNotificationAndOutbox(database, eventType, $"{eventType}:{message.Id:N}",
            null, office, false, message, payloadJson, now);
    }

    private async Task AddOfficeEventWithKeyAsync(
        SnackCounterDbContext database,
        OfficeLocation office,
        string eventType,
        string text,
        object payload,
        string deduplicationKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await database.Notifications.AnyAsync(x => x.DeduplicationKey == deduplicationKey, cancellationToken)) return;
        var conversation = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(database, office, now, cancellationToken);
        var payloadJson = FrontendApiSupport.Serialize(payload);
        var message = FrontendApiSupport.AddMessage(database, conversation, null, null, office,
            MessageAudience.Office, ConversationMessageKind.Notification, text, payloadJson, now);
        FrontendApiSupport.AddNotificationAndOutbox(database, eventType, deduplicationKey,
            null, office, false, message, payloadJson, now);
    }

    private async Task<MonthlyBudget> GetOrCreateBudgetAsync(
        SnackCounterDbContext database,
        Office office,
        DateOnly localDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var budget = await database.MonthlyBudgets.SingleOrDefaultAsync(candidate =>
            candidate.Office == office.Location && candidate.Year == localDate.Year && candidate.Month == localDate.Month,
            cancellationToken);
        if (budget is not null)
        {
            return budget;
        }

        budget = new MonthlyBudget
        {
            Office = office.Location,
            Year = localDate.Year,
            Month = localDate.Month,
            Currency = office.BudgetCurrency,
            BudgetAmount = office.MonthlyBudget,
            WarningThresholdPercent = office.WarningThresholdPercent,
            SnapshotAt = now
        };
        database.MonthlyBudgets.Add(budget);
        return budget;
    }

    private static async Task<DateOnly> GetOfficeLocalDateAsync(
        SnackCounterDbContext database,
        OfficeLocation office,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var officeConfig = await database.Offices.AsNoTracking()
            .SingleAsync(candidate => candidate.Location == office, cancellationToken);
        return ToLocalDate(now, officeConfig.TimeZoneId);
    }

    private static DateOnly ToLocalDate(DateTimeOffset now, string timeZoneId)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
    }

    private static bool VersionMatches(byte[] current, string supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(current, Convert.FromBase64String(supplied)); }
        catch (FormatException) { return false; }
    }

    private static SnackItemResponse ToResponse(SnackItem item) => new(
        item.Id, item.Office, item.Name, item.Unit, item.QuantityOnHand,
        item.ReorderThreshold, item.IsActive, item.IsClaimed, Convert.ToBase64String(item.RowVersion));
}

public sealed record SnackItemResponse(
    Guid Id,
    OfficeLocation Office,
    string Name,
    string Unit,
    decimal QuantityOnHand,
    decimal ReorderThreshold,
    bool IsActive,
    bool IsClaimed,
    string RowVersion);
public sealed record StockMovementResponse(
    Guid Id,
    Guid SnackItemId,
    StockMovementKind Kind,
    decimal Quantity,
    DateTimeOffset OccurredAt,
    string? ActorEmployeeId,
    Guid? RefillId,
    decimal? UnitCost,
    Currency? CostCurrency,
    string? Note,
    decimal? ReportedConsumedQuantity);
public sealed record CreateSnackItemRequest(string Name, string Unit, decimal ReorderThreshold);
public sealed record OpenRefillRequest(Guid SnackItemId);
public sealed record AcceptRefillRequest(long ExpectedVersion, WorkingMode WorkingMode, OfficeLocation WorkingOffice);
public sealed record RefillActionResponse(RefillResponse Refill, bool Accepted, string? SkipReason);
public sealed record CompleteRefillRequest(
    long ExpectedVersion,
    decimal Quantity,
    decimal Amount,
    string Description,
    string? ReceiptImageBase64);
public sealed record DenyRefillRequest(long ExpectedVersion, string Reason);
public sealed record ClaimRefillRequest(string RefillRowVersion, string ItemRowVersion);
public sealed record OfficeRefillQueueItemResponse(RefillResponse Refill, string? ItemRowVersion,
    string? SuggestedEmployeeId, string SuggestionReason, bool CanAcceptOffer, DateTimeOffset? OfferExpiresAt, RefillOfferState OfferState);
