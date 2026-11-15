using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using API.SnackCounter.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Tests;

[TestFixture]
public sealed class ExternalHrWebhookTests
{
    private const string WebhookSecret = "test-webhook-secret";

    [Test]
    public async Task ValidDecisionWebhook_UpdatesClaimAndNotifiesOnlyTheEmployee_AndDeduplicatesRetries()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"hr-webhook-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var claimId = Guid.NewGuid();
        var externalClaimId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        const string employeeId = "employee-webhook";
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Volunteers.Add(new Volunteer(employeeId, OfficeLocation.Oslo, Currency.NOK));
            setup.ReimbursementClaims.Add(new ReimbursementClaim
            {
                Id = claimId, EmployeeId = employeeId, Office = OfficeLocation.Oslo, PurchaseId = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid().ToString("D"), Amount = 45m, Currency = Currency.NOK,
                Description = "Coffee", Status = ReimbursementClaimStatus.Submitted,
                ExternalHrClaimId = externalClaimId, SubmittedAt = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        var callback = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId, claimId, externalClaimId, decision = "Approved", reason = (string?)null,
            occurredAt = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var first = await SendAsync(factory, callback, Sign(callback));
        var duplicate = await SendAsync(factory, callback, Sign(callback));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.TypeOf<OkObjectResult>());
            Assert.That(duplicate, Is.TypeOf<OkObjectResult>());
        });
        await using var verify = await factory.CreateDbContextAsync();
        var claim = await verify.ReimbursementClaims.SingleAsync(x => x.Id == claimId);
        Assert.Multiple(() =>
        {
            Assert.That(claim.Status, Is.EqualTo(ReimbursementClaimStatus.Approved));
            Assert.That(verify.ProcessedHrWebhooks.Count(x => x.EventId == eventId), Is.EqualTo(1));
            Assert.That(verify.Notifications.Count(x => x.EventType == "ClaimDecided" && x.RecipientEmployeeId == employeeId), Is.EqualTo(1));
            Assert.That(verify.Notifications.Count(x => x.IsHrAudience), Is.Zero);
        });
    }

    [Test]
    public async Task InvalidWebhookSignature_IsRejectedWithoutChangingClaim()
    {
        var options = new DbContextOptionsBuilder<SnackCounterDbContext>()
            .UseInMemoryDatabase($"hr-webhook-invalid-{Guid.NewGuid():N}").Options;
        var factory = new TestDbContextFactory(options);
        var body = Encoding.UTF8.GetBytes("{}");

        var result = await SendAsync(factory, body, "00");

        Assert.That(result, Is.TypeOf<UnauthorizedResult>());
    }

    private static string Sign(byte[] body) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), body));

    private static async Task<IActionResult> SendAsync(TestDbContextFactory factory, byte[] body, string signature)
    {
        var controller = new ExternalHrWebhookController(factory,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockHr:WebhookSecret"] = WebhookSecret
            }).Build(), TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Body = new MemoryStream(body);
        controller.Request.Headers["X-Mock-HR-Signature"] = signature;
        return await controller.Receive(CancellationToken.None);
    }

    private sealed class TestDbContextFactory(DbContextOptions<SnackCounterDbContext> options)
        : IDbContextFactory<SnackCounterDbContext>
    {
        public SnackCounterDbContext CreateDbContext() => new(options);
        public Task<SnackCounterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnackCounterDbContext(options));
    }
}
