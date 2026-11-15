using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MockHr.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextFactory<MockHrDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("MockHr") ?? throw new InvalidOperationException("ConnectionStrings:MockHr is required.")));
builder.Services.AddHttpClient("SnackCounter", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<DecisionWebhookWorker>();

var app = builder.Build();
if (app.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup") ?? true)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<MockHrDbContext>().Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mock-hr" }));

app.MapPost("/api/claims", async (HttpRequest http, ClaimSubmissionRequest request,
    IDbContextFactory<MockHrDbContext> factory, IConfiguration configuration, CancellationToken ct) =>
{
    if (!Authorized(http, configuration)) return Results.Unauthorized();
    if (request.ClaimId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
        request.IdempotencyKey != request.ClaimId.ToString("D") || request.Amount <= 0 ||
        string.IsNullOrWhiteSpace(request.EmployeeId) || string.IsNullOrWhiteSpace(request.Description))
        return Results.BadRequest(new { title = "Invalid claim submission." });

    await using var db = await factory.CreateDbContextAsync(ct);
    var existing = await db.Claims.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
    if (existing is not null)
    {
        if (existing.SnackCounterClaimId != request.ClaimId) return Results.Conflict(new { title = "Idempotency key is already in use." });
        return Results.Ok(ToResponse(existing));
    }

    var claim = new ExternalClaim
    {
        Id = Guid.NewGuid(), SnackCounterClaimId = request.ClaimId, IdempotencyKey = request.IdempotencyKey,
        EmployeeId = request.EmployeeId, Office = request.Office, Amount = request.Amount, Currency = request.Currency,
        Description = request.Description, ReceiptBase64 = request.ReceiptBase64,
        ReceiptMediaType = request.ReceiptMediaType, Status = "Pending", SubmittedAt = DateTimeOffset.UtcNow
    };
    db.Claims.Add(claim);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException)
    {
        var raced = await db.Claims.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        return raced is null ? Results.Conflict(new { title = "Claim submission conflicted." }) : Results.Ok(ToResponse(raced));
    }
    return Results.Created($"/api/claims/{claim.Id}", ToResponse(claim));
});

app.MapGet("/api/claims", async (HttpRequest http, string? status, IDbContextFactory<MockHrDbContext> factory,
    IConfiguration configuration, CancellationToken ct) =>
{
    if (!Authorized(http, configuration)) return Results.Unauthorized();
    await using var db = await factory.CreateDbContextAsync(ct);
    var query = db.Claims.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
    var claims = await query.OrderBy(x => x.SubmittedAt).ToArrayAsync(ct);
    return Results.Ok(claims.Select(ToResponse));
});

app.MapGet("/api/claims/{id:guid}", async (Guid id, HttpRequest http, IDbContextFactory<MockHrDbContext> factory,
    IConfiguration configuration, CancellationToken ct) =>
{
    if (!Authorized(http, configuration)) return Results.Unauthorized();
    await using var db = await factory.CreateDbContextAsync(ct);
    var claim = await db.Claims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return claim is null ? Results.NotFound() : Results.Ok(ToResponse(claim));
});

app.MapPost("/api/claims/{id:guid}/decision", async (Guid id, HttpRequest http, DecisionRequest request,
    IDbContextFactory<MockHrDbContext> factory, IConfiguration configuration, CancellationToken ct) =>
{
    if (!Authorized(http, configuration)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Decision) ||
        !(request.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase) || request.Decision.Equals("Denied", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { title = "Decision must be Approved or Denied." });

    await using var db = await factory.CreateDbContextAsync(ct);
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var claim = await db.Claims.SingleOrDefaultAsync(x => x.Id == id, ct);
    if (claim is null) return Results.NotFound();
    var normalizedDecision = request.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase) ? "Approved" : "Denied";
    if (claim.Status != "Pending")
    {
        return claim.Status == normalizedDecision
            ? Results.Ok(ToResponse(claim))
            : Results.Conflict(new { title = "This claim already has a different decision." });
    }
    if (normalizedDecision == "Denied" && string.IsNullOrWhiteSpace(request.Reason))
        return Results.BadRequest(new { title = "A denial reason is required." });

    claim.Status = normalizedDecision;
    claim.DecisionReason = request.Reason?.Trim();
    claim.DecidedAt = DateTimeOffset.UtcNow;
    var eventId = Guid.NewGuid();
    var callback = new DecisionWebhook(eventId, claim.SnackCounterClaimId, claim.Id,
        normalizedDecision, claim.DecisionReason, claim.DecidedAt.Value);
    var payload = JsonSerializer.SerializeToUtf8Bytes(callback, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    db.WebhookOutbox.Add(new WebhookOutbox
    {
        Id = Guid.NewGuid(), EventId = eventId, ExternalClaimId = claim.Id,
        PayloadJson = Encoding.UTF8.GetString(payload), OccurredAt = claim.DecidedAt.Value, AvailableAt = claim.DecidedAt.Value
    });
    try
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { title = "Another HR action changed this claim." }); }
    return Results.Accepted($"/api/claims/{claim.Id}", ToResponse(claim));
});

app.Run();

static bool Authorized(HttpRequest request, IConfiguration configuration) =>
    request.Headers.TryGetValue("X-Mock-HR-Key", out var supplied) &&
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied.ToString()),
        Encoding.UTF8.GetBytes(configuration["MockHr:ServiceKey"] ?? ""));

static ExternalClaimResponse ToResponse(ExternalClaim claim) => new(claim.Id, claim.SnackCounterClaimId,
    claim.EmployeeId, claim.Office, claim.Amount, claim.Currency, claim.Description, claim.Status,
    claim.DecisionReason, claim.SubmittedAt, claim.DecidedAt, Convert.ToBase64String(claim.RowVersion));

public sealed record ClaimSubmissionRequest(Guid ClaimId, string IdempotencyKey, string EmployeeId, string Office,
    decimal Amount, string Currency, string Description, string? ReceiptBase64, string? ReceiptMediaType);
public sealed record DecisionRequest(string Decision, string? Reason);
public sealed record ExternalClaimResponse(Guid Id, Guid SnackCounterClaimId, string EmployeeId, string Office,
    decimal Amount, string Currency, string Description, string Status, string? DecisionReason,
    DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt, string RowVersion);
public sealed record DecisionWebhook(Guid EventId, Guid ClaimId, Guid ExternalClaimId, string Decision,
    string? Reason, DateTimeOffset OccurredAt);

public sealed class DecisionWebhookWorker(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration configuration, ILogger<DecisionWebhookWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Mock HR webhook dispatch failed."); }
            try { await Task.Delay(PollDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task DeliverBatchAsync(CancellationToken ct)
    {
        for (var count = 0; count < 20 && !ct.IsCancellationRequested; count++)
        {
            using var scope = scopes.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MockHrDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var candidateId = await db.WebhookOutbox.AsNoTracking()
                .Where(x => x.DeliveredAt == null && x.AvailableAt <= now && (x.LockedUntil == null || x.LockedUntil <= now))
                .OrderBy(x => x.AvailableAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (candidateId is null) return;
            var locked = await db.WebhookOutbox.Where(x => x.Id == candidateId && x.DeliveredAt == null &&
                    (x.LockedUntil == null || x.LockedUntil <= now))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LockedUntil, now.AddMinutes(1)), ct);
            if (locked == 0) continue;
            var row = await db.WebhookOutbox.SingleOrDefaultAsync(x => x.Id == candidateId, ct);
            if (row is null || row.DeliveredAt is not null) continue;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(row.PayloadJson);
                var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(configuration["MockHr:WebhookSecret"] ?? ""), bytes));
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    configuration["MockHr:SnackCounterWebhookUrl"] ?? throw new InvalidOperationException("SnackCounter webhook URL is required."))
                { Content = new ByteArrayContent(bytes) };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Headers.Add("X-Mock-HR-Signature", signature);
                var response = await clients.CreateClient("SnackCounter").SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                row.DeliveredAt = DateTimeOffset.UtcNow;
                row.LockedUntil = null;
                row.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                row.Attempts++;
                row.LastError = exception.Message.Length > 2_000 ? exception.Message[..2_000] : exception.Message;
                row.LockedUntil = null;
                row.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, Math.Pow(2, Math.Min(row.Attempts, 9))));
                logger.LogWarning(exception, "Webhook for mock HR claim {ClaimId} will retry.", row.ExternalClaimId);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
