using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "Employee,HR,Manager")]
[Route("api")]
public sealed class PeopleOperationsController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("me/leaves")]
    public async Task<ActionResult<IReadOnlyList<LeaveResponse>>> MyLeaves(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return Ok(await db.EmployeeLeaves.AsNoTracking().Where(x => x.EmployeeId == employeeId && (office == null || x.Office == office))
            .OrderBy(x => x.StartDate).Select(x => new LeaveResponse(x.Id, x.Office, x.StartDate, x.EndDate, x.Status))
            .ToArrayAsync(cancellationToken));
    }

    [HttpPost("me/leaves")]
    public async Task<ActionResult<LeaveResponse>> CreateLeave(CreateLeaveRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.StartDate < today || request.EndDate < request.StartDate)
            return BadRequest(new ProblemDetails { Title = "Invalid leave dates.", Detail = "Leave must be a valid future date range." });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(db, employeeId, request.Office, cancellationToken)) return Forbid();
        var overlaps = await db.EmployeeLeaves.AnyAsync(x => x.EmployeeId == employeeId && x.Office == request.Office &&
            x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= request.EndDate && x.EndDate >= request.StartDate, cancellationToken);
        if (overlaps) return Conflict(new ProblemDetails { Title = "Leave dates overlap an existing entry." });
        var leave = new EmployeeLeave { Id = Guid.NewGuid(), EmployeeId = employeeId, Office = request.Office,
            StartDate = request.StartDate, EndDate = request.EndDate, Status = EmployeeLeaveStatus.Planned, SubmittedAt = clock.GetUtcNow() };
        db.EmployeeLeaves.Add(leave);
        await db.SaveChangesAsync(cancellationToken);
        var response = new LeaveResponse(leave.Id, leave.Office, leave.StartDate, leave.EndDate, leave.Status);
        return CreatedAtAction(nameof(MyLeaves), response);
    }

    [HttpPut("me/leaves/{id:guid}")]
    public async Task<ActionResult<LeaveResponse>> UpdateLeave(Guid id, UpdateLeaveRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        if (request.EndDate < request.StartDate || request.StartDate < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            return BadRequest(new ProblemDetails { Title = "Invalid leave dates." });
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var leave = await db.EmployeeLeaves.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.Status == EmployeeLeaveStatus.Cancelled) return Conflict(new ProblemDetails { Title = "Cancelled leave cannot be edited." });
        if (!await FrontendApiSupport.HasOfficeAccessAsync(db, employeeId, leave.Office, cancellationToken)) return Forbid();
        var overlap = await db.EmployeeLeaves.AnyAsync(x => x.Id != id && x.EmployeeId == employeeId && x.Office == leave.Office &&
            x.Status != EmployeeLeaveStatus.Cancelled && x.StartDate <= request.EndDate && x.EndDate >= request.StartDate, cancellationToken);
        if (overlap) return Conflict(new ProblemDetails { Title = "Leave dates overlap an existing entry." });
        leave.StartDate = request.StartDate; leave.EndDate = request.EndDate; leave.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new LeaveResponse(leave.Id, leave.Office, leave.StartDate, leave.EndDate, leave.Status));
    }

    [HttpDelete("me/leaves/{id:guid}")]
    public async Task<IActionResult> CancelLeave(Guid id, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var leave = await db.EmployeeLeaves.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.StartDate < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)) return Conflict(new ProblemDetails { Title = "Past leave cannot be cancelled." });
        leave.Status = EmployeeLeaveStatus.Cancelled; leave.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("me/purchases")]
    public async Task<ActionResult<IReadOnlyList<EmployeePurchaseResponse>>> MyPurchases(CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var purchases = await db.Refills.AsNoTracking()
            .Where(purchase => EF.Property<string?>(purchase, "AssignedVolunteerEmployeeId") == employeeId &&
                purchase.PurchasedAt != null && purchase.PurchaseAmount != null && purchase.PurchaseCurrency != null)
            .OrderByDescending(purchase => purchase.PurchasedAt)
            .Select(purchase => new EmployeePurchaseResponse(
                purchase.Id, purchase.Office, purchase.Item, purchase.PurchaseAmount!.Value,
                purchase.PurchaseCurrency!.Value, purchase.PurchasedAt!.Value,
                purchase.PurchaseDescription ?? purchase.Item, null, null, null, null))
            .ToArrayAsync(cancellationToken);
        if (purchases.Length == 0) return Ok(purchases);

        var purchaseIds = purchases.Select(purchase => purchase.PurchaseId).ToArray();
        var claims = await db.ReimbursementClaims.AsNoTracking()
            .Where(claim => claim.EmployeeId == employeeId &&
                (purchaseIds.Contains(claim.PurchaseId) || claim.RefillId != null && purchaseIds.Contains(claim.RefillId.Value)))
            .OrderByDescending(claim => claim.SubmissionAttempt).ThenByDescending(claim => claim.SubmittedAt)
            .ToArrayAsync(cancellationToken);
        var latestClaimByPurchase = claims.GroupBy(claim => claim.RefillId ?? claim.PurchaseId)
            .ToDictionary(group => group.Key, group => group.First());
        return Ok(purchases.Select(purchase => latestClaimByPurchase.TryGetValue(purchase.PurchaseId, out var claim)
            ? purchase with { ClaimId = claim.Id, ClaimStatus = claim.Status, SubmissionAttempt = claim.SubmissionAttempt, DecisionReason = claim.DecisionReason }
            : purchase).ToArray());
    }

    [HttpGet("me/claims")]
    public async Task<ActionResult<IReadOnlyList<ClaimResponse>>> MyClaims(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return Ok(await db.ReimbursementClaims.AsNoTracking().Where(x => x.EmployeeId == employeeId && (office == null || x.Office == office))
            .OrderByDescending(x => x.SubmittedAt).Select(x => ClaimResponse.From(x)).ToArrayAsync(cancellationToken));
    }

    [HttpPost("me/claims")]
    public async Task<ActionResult<ClaimResponse>> SubmitClaim(CreateClaimRequest request, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        if (request.Amount <= 0 || request.PurchaseId == Guid.Empty || string.IsNullOrWhiteSpace(request.Description) ||
            !string.Equals(request.IdempotencyKey, request.PurchaseId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ProblemDetails { Title = "Claim details are incomplete.", Detail = "A valid purchase ID must be used as the idempotency key, along with amount and description." });
        byte[]? receipt = null;
        if (!string.IsNullOrWhiteSpace(request.ReceiptBase64))
        {
            try { receipt = Convert.FromBase64String(request.ReceiptBase64); }
            catch (FormatException) { return BadRequest(new ProblemDetails { Title = "Receipt must be valid base64." }); }
            if (receipt.Length > 5 * 1024 * 1024) return BadRequest(new ProblemDetails { Title = "Receipt exceeds 5 MB." });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var purchase = await db.Refills.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PurchaseId &&
            x.PurchasedAt != null && x.PurchaseAmount != null && x.PurchaseCurrency != null &&
            EF.Property<string?>(x, "AssignedVolunteerEmployeeId") == employeeId, cancellationToken);
        if (purchase is null) return NotFound(new ProblemDetails { Title = "Purchase not found.", Detail = "This completed purchase is not available to your account." });
        if (request.RefillId is not null && request.RefillId != purchase.Id)
            return BadRequest(new ProblemDetails { Title = "Purchase details do not match." });
        if (request.Amount != purchase.PurchaseAmount || request.Currency != purchase.PurchaseCurrency)
            return BadRequest(new ProblemDetails { Title = "Claim amount does not match the purchase.", Detail = "Submit the amount and currency recorded for this purchase." });
        if (!await FrontendApiSupport.HasOfficeAccessAsync(db, employeeId, purchase.Office, cancellationToken)) return Forbid();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var prior = await db.ReimbursementClaims.Where(x => x.EmployeeId == employeeId &&
                (x.PurchaseId == request.PurchaseId || x.RefillId == request.PurchaseId))
            .OrderByDescending(x => x.SubmissionAttempt).ThenByDescending(x => x.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var attempt = 1;
        Guid? previousClaimId = null;
        if (prior is not null)
        {
            if (prior.Status != ReimbursementClaimStatus.Denied)
            {
                return Ok(ClaimResponse.From(prior));
            }
            attempt = prior.SubmissionAttempt + 1;
            previousClaimId = prior.Id;
        }
        var now = clock.GetUtcNow();
        var claim = new ReimbursementClaim { Id = Guid.NewGuid(), EmployeeId = employeeId, Office = purchase.Office,
            PurchaseId = purchase.Id, SubmissionAttempt = attempt, PreviousClaimId = previousClaimId,
            RefillId = purchase.Id, IdempotencyKey = purchase.Id.ToString("D"), Amount = request.Amount, Currency = request.Currency,
            Description = request.Description.Trim(), SubmittedAt = now, ReceiptContent = receipt,
            ReceiptMediaType = receipt is null ? null : request.ReceiptMediaType ?? "application/octet-stream", ReceiptSizeBytes = receipt?.LongLength,
            ReceiptSha256 = receipt is null ? null : Convert.ToHexString(SHA256.HashData(receipt)) };
        db.ReimbursementClaims.Add(claim);
        var conversation = await FrontendApiSupport.GetOrCreateHrConversationAsync(db, now, cancellationToken);
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, office = claim.Office, amount = claim.Amount, currency = claim.Currency, employeeId });
        var message = FrontendApiSupport.AddMessage(db, conversation, employeeId, null, null, MessageAudience.Hr,
            ConversationMessageKind.Notification, $"New reimbursement claim submitted ({claim.Amount} {claim.Currency}).", payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimSubmitted", $"claim:{claim.Id}:submitted", null, null, true, message, payload, now);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(), EventId = Guid.NewGuid(), EventType = "ExternalHrClaimSubmit",
            PayloadJson = FrontendApiSupport.Serialize(new { claimId = claim.Id }),
            OccurredAt = now, AvailableAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            return Conflict(new ProblemDetails { Title = "Claim already exists.", Detail = "A claim for this purchase was submitted at the same time. Refresh your claims before retrying." });
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return CreatedAtAction(nameof(MyClaims), ClaimResponse.From(claim));
    }

    [HttpGet("me/claims/{id:guid}")]
    public async Task<ActionResult<ClaimResponse>> MyClaim(Guid id, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId, cancellationToken);
        return claim is null ? NotFound() : Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpGet("hr/claims")]
    public async Task<ActionResult<IReadOnlyList<ClaimResponse>>> HrClaims([FromQuery] ReimbursementClaimStatus? status, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ReimbursementClaims.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        return Ok(await query.OrderBy(x => x.SubmittedAt).Select(x => ClaimResponse.From(x)).ToArrayAsync(cancellationToken));
    }

    [Authorize(Roles = "HR")]
    [HttpGet("hr/claims/{id:guid}")]
    public async Task<ActionResult<ClaimResponse>> HrClaim(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return claim is null ? NotFound() : Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/claim")]
    public async Task<ActionResult<ClaimResponse>> ClaimForProcessing(Guid id, ClaimForProcessingRequest request, CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("MockHr:Enabled"))
            return Conflict(new ProblemDetails { Title = "Claim processing is external.", Detail = "Review this claim in the local Mock HR service; its signed webhook updates SnackCounter." });
        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status == ReimbursementClaimStatus.Processing && claim.ProcessingLeaseExpiresAt > now && claim.ProcessingByEmployeeId != actor)
            return Conflict(new ProblemDetails { Title = "Another HR user is processing this claim." });
        if (claim.Status is not (ReimbursementClaimStatus.Submitted or ReimbursementClaimStatus.Processing))
            return Conflict(new ProblemDetails { Title = "This claim is already decided." });
        claim.Status = ReimbursementClaimStatus.Processing; claim.ProcessingByEmployeeId = actor; claim.ProcessingAt = now;
        claim.ProcessingLeaseExpiresAt = now.AddMinutes(15);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Another HR user claimed this reimbursement." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/decision")]
    public async Task<ActionResult<ClaimResponse>> DecideClaim(Guid id, DecideClaimRequest request, CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("MockHr:Enabled"))
            return Conflict(new ProblemDetails { Title = "Claim decisions are external.", Detail = "Approve or deny this claim in the local Mock HR service. SnackCounter will receive the result by webhook." });
        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status != ReimbursementClaimStatus.Processing || claim.ProcessingByEmployeeId != actor || claim.ProcessingLeaseExpiresAt <= now)
            return Conflict(new ProblemDetails { Title = "Claim must be actively claimed by this HR user." });
        claim.Status = request.Approve ? ReimbursementClaimStatus.Approved : ReimbursementClaimStatus.Denied;
        claim.DecisionReason = request.Reason?.Trim(); claim.DecidedAt = now; claim.ProcessingLeaseExpiresAt = null;
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, claim.EmployeeId, now, cancellationToken);
        var text = request.Approve ? $"Your reimbursement claim was approved ({claim.Amount} {claim.Currency})." : $"Your reimbursement claim was denied. {claim.DecisionReason}";
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, status = claim.Status, amount = claim.Amount, currency = claim.Currency, reason = claim.DecisionReason });
        var message = FrontendApiSupport.AddMessage(db, employeeChat, actor, claim.EmployeeId, null, MessageAudience.Employee,
            ConversationMessageKind.Notification, text, payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimDecided", $"claim:{claim.Id}:decision", claim.EmployeeId, null, false, message, payload, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Claim was updated by another HR user." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR")]
    [HttpPost("hr/claims/{id:guid}/reimburse")]
    public async Task<ActionResult<ClaimResponse>> ReimburseClaim(Guid id, ReimburseClaimRequest request, CancellationToken cancellationToken)
    {
        var actor = FrontendApiSupport.EmployeeId(User); var now = clock.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (claim is null) return NotFound();
        if (!VersionMatches(claim.RowVersion, request.RowVersion)) return Conflict(new ProblemDetails { Title = "Claim changed.", Detail = "Reload it and try again." });
        if (claim.Status != ReimbursementClaimStatus.Approved) return Conflict(new ProblemDetails { Title = "Only an approved claim can be reimbursed." });
        if (request.PayrollAmount <= 0) return BadRequest(new ProblemDetails { Title = "Payroll amount must be positive." });
        if ((claim.Currency != request.PayrollCurrency && (request.ExchangeRate is null || request.ExchangeRate <= 0 || string.IsNullOrWhiteSpace(request.ExchangeRateSource))) ||
            (request.ExchangeRate is not null && request.ExchangeRate <= 0))
            return BadRequest(new ProblemDetails { Title = "Currency conversion requires a positive rate and source." });
        claim.Status = ReimbursementClaimStatus.Reimbursed; claim.ReimbursedAt = now;
        claim.PayrollAmount = request.PayrollAmount; claim.PayrollCurrency = request.PayrollCurrency;
        claim.ExchangeRate = request.ExchangeRate; claim.ExchangeRateAt = request.ExchangeRate is null ? null : request.ExchangeRateAt ?? now;
        claim.ExchangeRateSource = request.ExchangeRate is null ? null : request.ExchangeRateSource;
        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, claim.EmployeeId, now, cancellationToken);
        var payload = FrontendApiSupport.Serialize(new { claimId = claim.Id, status = claim.Status, payrollAmount = claim.PayrollAmount, payrollCurrency = claim.PayrollCurrency });
        var message = FrontendApiSupport.AddMessage(db, employeeChat, actor, claim.EmployeeId, null, MessageAudience.Employee,
            ConversationMessageKind.Notification, $"Your claim was reimbursed: {claim.PayrollAmount} {claim.PayrollCurrency}.", payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimReimbursed", $"claim:{claim.Id}:reimbursed", claim.EmployeeId, null, false, message, payload, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Conflict(new ProblemDetails { Title = "Claim was updated by another HR user." }); }
        return Ok(ClaimResponse.From(claim));
    }

    [Authorize(Roles = "HR,Manager")]
    [HttpGet("hr/claims/{id:guid}/receipt")]
    public async Task<IActionResult> ClaimReceipt(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.ReimbursementClaims.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.ReceiptContent, x.ReceiptMediaType }).SingleOrDefaultAsync(cancellationToken);
        return receipt?.ReceiptContent is null ? NotFound() : File(receipt.ReceiptContent, receipt.ReceiptMediaType ?? "application/octet-stream");
    }

    private static bool VersionMatches(byte[] current, string provided)
    {
        try { return CryptographicOperations.FixedTimeEquals(current, Convert.FromBase64String(provided)); }
        catch (FormatException) { return false; }
    }
}

public sealed record CreateLeaveRequest(OfficeLocation Office, DateOnly StartDate, DateOnly EndDate);
public sealed record UpdateLeaveRequest(DateOnly StartDate, DateOnly EndDate);
public sealed record LeaveResponse(Guid Id, OfficeLocation Office, DateOnly StartDate, DateOnly EndDate, EmployeeLeaveStatus Status);
public sealed record EmployeePurchaseResponse(Guid PurchaseId, OfficeLocation Office, string Item, decimal Amount, Currency Currency,
    DateTimeOffset PurchasedAt, string Description, Guid? ClaimId, ReimbursementClaimStatus? ClaimStatus,
    int? SubmissionAttempt, string? DecisionReason);
public sealed record CreateClaimRequest(string IdempotencyKey, Guid PurchaseId, OfficeLocation Office, decimal Amount, Currency Currency, string Description,
    Guid? RefillId = null, string? ReceiptBase64 = null, string? ReceiptMediaType = null);
public sealed record ClaimForProcessingRequest(string RowVersion);
public sealed record DecideClaimRequest(string RowVersion, bool Approve, string? Reason);
public sealed record ReimburseClaimRequest(string RowVersion, decimal PayrollAmount, Currency PayrollCurrency,
    decimal? ExchangeRate = null, DateTimeOffset? ExchangeRateAt = null, string? ExchangeRateSource = null);
public sealed record ClaimResponse(Guid Id, string EmployeeId, OfficeLocation Office, Guid PurchaseId, int SubmissionAttempt, Guid? PreviousClaimId, Guid? RefillId, decimal Amount, Currency Currency,
    string Description, ReimbursementClaimStatus Status, string? ProcessingByEmployeeId, string? DecisionReason, Guid? ExternalHrClaimId,
    DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt, bool HasReceipt, string RowVersion)
{
    public static ClaimResponse From(ReimbursementClaim x) => new(x.Id, x.EmployeeId, x.Office, x.PurchaseId, x.SubmissionAttempt, x.PreviousClaimId, x.RefillId, x.Amount, x.Currency,
        x.Description, x.Status, x.ProcessingByEmployeeId, x.DecisionReason, x.ExternalHrClaimId, x.SubmittedAt, x.DecidedAt,
        x.ReceiptSizeBytes > 0, Convert.ToBase64String(x.RowVersion));
}
