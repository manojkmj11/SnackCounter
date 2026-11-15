using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/integrations/hr/claims/decision")]
public sealed class ExternalHrWebhookController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    IConfiguration configuration,
    TimeProvider clock) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();
        if (!Request.Headers.TryGetValue("X-Mock-HR-Signature", out var signature) ||
            !IsValidSignature(body, signature.ToString(), configuration["MockHr:WebhookSecret"]))
            return Unauthorized();

        MockHrDecisionEvent? decision;
        try { decision = JsonSerializer.Deserialize<MockHrDecisionEvent>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return BadRequest(new ProblemDetails { Title = "Invalid webhook payload." }); }
        if (decision is null || decision.EventId == Guid.Empty || decision.ClaimId == Guid.Empty || decision.ExternalClaimId == Guid.Empty ||
            !(decision.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase) || decision.Decision.Equals("Denied", StringComparison.OrdinalIgnoreCase)) ||
            (decision.Decision.Equals("Denied", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.Reason)))
            return BadRequest(new ProblemDetails { Title = "Incomplete reimbursement decision event." });

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ProcessedHrWebhooks.AnyAsync(x => x.EventId == decision.EventId, cancellationToken))
            return Ok(new { accepted = true, duplicate = true });
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var claim = await db.ReimbursementClaims.SingleOrDefaultAsync(x => x.Id == decision.ClaimId, cancellationToken);
        if (claim is null) return NotFound(new ProblemDetails { Title = "Claim was not found." });
        if (claim.ExternalHrClaimId != decision.ExternalClaimId)
            return Conflict(new ProblemDetails { Title = "External claim reference does not match." });
        if (claim.Status is not (ReimbursementClaimStatus.Submitted or ReimbursementClaimStatus.Processing))
            return Conflict(new ProblemDetails { Title = "Claim already has a different final state." });

        var now = clock.GetUtcNow();
        claim.Status = decision.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase)
            ? ReimbursementClaimStatus.Approved : ReimbursementClaimStatus.Denied;
        claim.DecisionReason = decision.Reason?.Trim();
        claim.DecidedAt = decision.OccurredAt;
        claim.ProcessingByEmployeeId = null;
        claim.ProcessingAt = null;
        claim.ProcessingLeaseExpiresAt = null;
        db.ProcessedHrWebhooks.Add(new ProcessedHrWebhook { EventId = decision.EventId, ClaimId = claim.Id, ReceivedAt = now });

        var employeeChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(db, claim.EmployeeId, now, cancellationToken);
        var approved = claim.Status == ReimbursementClaimStatus.Approved;
        var text = approved
            ? $"Your reimbursement claim was approved ({claim.Amount} {claim.Currency})."
            : $"Your reimbursement claim was denied. {claim.DecisionReason}";
        var payload = FrontendApiSupport.Serialize(new
        {
            claimId = claim.Id, status = claim.Status, amount = claim.Amount, currency = claim.Currency,
            reason = claim.DecisionReason, externalHrClaimId = claim.ExternalHrClaimId
        });
        var message = FrontendApiSupport.AddMessage(db, employeeChat, null, claim.EmployeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification, text, payload, now);
        FrontendApiSupport.AddNotificationAndOutbox(db, "ClaimDecided",
            $"claim:{claim.Id}:external-decision", claim.EmployeeId, null, false, message, payload, now);
        db.AuditRecords.Add(new AuditRecord
        {
            ActorId = "MockHrWebhook", Action = "ExternalClaimDecision", EntityType = nameof(ReimbursementClaim),
            EntityId = claim.Id.ToString("D"), ChangeSummaryJson = payload, OccurredAt = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ProblemDetails { Title = "Claim changed while applying the HR decision." });
        }
        catch (DbUpdateException)
        {
            await using var verify = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (await verify.ProcessedHrWebhooks.AnyAsync(x => x.EventId == decision.EventId, cancellationToken))
                return Ok(new { accepted = true, duplicate = true });
            return Conflict(new ProblemDetails { Title = "Webhook event conflicted with another update." });
        }
        return Ok(new { accepted = true, duplicate = false });
    }

    private static bool IsValidSignature(byte[] body, string suppliedHex, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(suppliedHex); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}

public sealed record MockHrDecisionEvent(Guid EventId, Guid ClaimId, Guid ExternalClaimId,
    string Decision, string? Reason, DateTimeOffset OccurredAt);
