using System.Security.Claims;
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
public sealed class ChatController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    TimeProvider clock) : ControllerBase
{
    [HttpGet("me/conversations")]
    [ProducesResponseType<IReadOnlyList<ConversationSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConversationSummaryResponse>>> ListMyConversations(
        [FromQuery] OfficeLocation? office,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await database.Offices.AsNoTracking().Where(location => location.IsActive)
            .Select(location => location.Location).ToArrayAsync(cancellationToken);
        if (office is not null)
        {
            if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office.Value, cancellationToken)) return Forbid();
            offices = [office.Value];
        }
        var conversations = await database.Conversations.AsNoTracking()
            .Where(conversation =>
                (conversation.Kind == ConversationKind.Employee && conversation.EmployeeId == employeeId) ||
                (conversation.Kind == ConversationKind.Office && conversation.Office != null && offices.Contains(conversation.Office.Value)) ||
                (conversation.Kind == ConversationKind.Hr && User.IsInRole("HR")))
            .OrderBy(conversation => conversation.Kind)
            .ThenBy(conversation => conversation.Office)
            .ToArrayAsync(cancellationToken);

        var results = new List<ConversationSummaryResponse>(conversations.Length);
        foreach (var conversation in conversations)
        {
            var latest = await database.ConversationMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id)
                .OrderByDescending(message => message.CreatedAt)
                .ThenByDescending(message => message.Id)
                .Select(message => new MessageResponse(
                    message.Id, message.ConversationId, message.SenderEmployeeId,
                    message.RecipientEmployeeId, message.Audience, message.Office,
                    message.Kind, message.Text, message.PayloadJson, message.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
            var unread = await UnreadQuery(database, employeeId, offices)
                .Where(notification => notification.ConversationMessageId != null &&
                    database.ConversationMessages.Any(message =>
                        message.Id == notification.ConversationMessageId && message.ConversationId == conversation.Id))
                .CountAsync(cancellationToken);
            results.Add(new ConversationSummaryResponse(
                conversation.Id,
                conversation.Kind,
                conversation.Office,
                conversation.Kind switch
                {
                    ConversationKind.Employee => "My chat",
                    ConversationKind.Office => conversation.Office?.ToString() ?? "Office chat",
                    ConversationKind.Hr => "HR claims chat",
                    _ => "Chat"
                },
                latest,
                unread));
        }

        return Ok(results);
    }

    [HttpGet("conversations/{conversationId:guid}/messages")]
    [ProducesResponseType<MessagePageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MessagePageResponse>> ListMessages(
        Guid conversationId,
        [FromQuery] DateTimeOffset? before,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await database.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            return NotFound(Problem("Conversation not found."));
        }
        if (!await CanAccessConversationAsync(database, conversation, cancellationToken))
        {
            return Forbid();
        }

        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = database.ConversationMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId);
        if (before is not null)
        {
            query = query.Where(message => message.CreatedAt < before);
        }

        var items = await query.OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(pageSize + 1)
            .Select(message => new MessageResponse(
                message.Id, message.ConversationId, message.SenderEmployeeId,
                message.RecipientEmployeeId, message.Audience, message.Office,
                message.Kind, message.Text, message.PayloadJson, message.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var hasMore = items.Length > pageSize;
        var page = items.Take(pageSize).Reverse().ToArray();
        return Ok(new MessagePageResponse(page, hasMore, page.Length == 0 ? null : page[0].CreatedAt));
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> SendMessage(
        Guid conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 8_000)
        {
            return BadRequest(Problem("Message text must contain 1 to 8,000 characters."));
        }

        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await database.Conversations.SingleOrDefaultAsync(
            item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            return NotFound(Problem("Conversation not found."));
        }
        if (!await CanAccessConversationAsync(database, conversation, cancellationToken))
        {
            return Forbid();
        }

        var audience = conversation.Kind switch
        {
            ConversationKind.Employee => MessageAudience.Employee,
            ConversationKind.Office => MessageAudience.Office,
            ConversationKind.Hr => MessageAudience.Hr,
            _ => throw new InvalidOperationException("Unsupported conversation kind.")
        };
        var message = FrontendApiSupport.AddMessage(
            database,
            conversation,
            employeeId,
            conversation.Kind == ConversationKind.Employee ? employeeId : null,
            conversation.Office,
            audience,
            ConversationMessageKind.Text,
            request.Text.Trim(),
            null,
            now);

        if (conversation.Kind != ConversationKind.Employee)
        {
            var eventType = conversation.Kind == ConversationKind.Hr ? "HRChatMessage" : "OfficeChatMessage";
            var payload = FrontendApiSupport.Serialize(new { messageId = message.Id, senderEmployeeId = employeeId });
            FrontendApiSupport.AddNotificationAndOutbox(
                database,
                eventType,
                $"chat:{message.Id:N}",
                null,
                conversation.Office,
                conversation.Kind == ConversationKind.Hr,
                message,
                payload,
                now);
        }

        await database.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(message));
    }

    [HttpPost("me/chat/report-empty")]
    [ProducesResponseType<EmptyItemReportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmptyItemReportResponse>> ReportEmptyFromPersonalChat(
        ReportEmptyItemRequest request,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        var now = clock.GetUtcNow();
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, request.Office, cancellationToken)) return Forbid();
        if (request.ReportedConsumedQuantity is <= 0)
            return BadRequest(new ProblemDetails { Title = "Invalid reported quantity.", Detail = "Reported consumption must be greater than zero when provided." });
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        var item = await database.SnackItems.SingleOrDefaultAsync(x => x.Id == request.SnackItemId &&
            x.Office == request.Office && x.IsActive, cancellationToken);
        if (item is null) return NotFound(Problem("The item is not active in the selected office."));

        var wasInStock = item.QuantityOnHand > 0;
        decimal? inferredConsumedQuantity = null;
        decimal? recordedReportedQuantity = null;
        if (wasInStock)
        {
            if (!VersionMatches(item.RowVersion, request.ItemRowVersion))
                return Conflict(Problem("The item stock changed. Refresh the pantry and report it again if it is still empty."));
            var consumedQuantity = item.QuantityOnHand;
            inferredConsumedQuantity = consumedQuantity;
            recordedReportedQuantity = request.ReportedConsumedQuantity;
            item.QuantityOnHand = 0;
            database.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), SnackItemId = item.Id, SnackItem = item,
                Kind = StockMovementKind.Consumption, Quantity = -consumedQuantity,
                ReportedConsumedQuantity = recordedReportedQuantity,
                OccurredAt = now, ActorEmployeeId = employeeId, Note = "Employee reported empty from personal chat"
            });
        }

        var refill = await database.Refills.Include(x => x.AssignedVolunteer)
            .SingleOrDefaultAsync(x => x.Office == request.Office && x.SnackItemId == item.Id &&
                (x.Status == RefillStatus.Open || x.Status == RefillStatus.Assigned), cancellationToken);
        var createdRefill = refill is null;
        if (refill is null)
        {
            if (item.IsClaimed) return Conflict(Problem("The item is marked claimed but has no active refill request. Refresh and contact an administrator."));
            refill = new Refill(Guid.NewGuid(), request.Office, item.Name, now)
            {
                RequesterEmployeeId = employeeId,
                SnackItemId = item.Id
            };
            database.Refills.Add(refill);
        }

        var personalChat = await FrontendApiSupport.GetOrCreateEmployeeConversationAsync(
            database, employeeId, now, cancellationToken);
        var userText = $"I reported {item.Name} empty at {request.Office}.";
        var userMessage = FrontendApiSupport.AddMessage(database, personalChat, employeeId, employeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Text, userText,
            FrontendApiSupport.Serialize(new { action = "ReportEmpty", office = request.Office, itemId = item.Id, refillId = refill.Id }), now);
        var officeConfig = await database.Offices.SingleAsync(x => x.Location == request.Office, cancellationToken);
        var budgetRemaining = await GetBudgetRemainingAsync(database, officeConfig, now, cancellationToken);
        var acknowledgement = createdRefill
            ? $"{item.Name} is marked empty and added to the {officeConfig.Name} refill queue."
            : refill.Status == RefillStatus.Assigned
                ? $"{item.Name} is empty. Its refill is already claimed by {refill.AssignedVolunteer?.DisplayName ?? "another employee"}."
                : $"{item.Name} is empty and already in the {officeConfig.Name} refill queue.";
        var payload = FrontendApiSupport.Serialize(new
        {
            office = request.Office.ToString(), itemId = item.Id, itemName = item.Name,
            refillId = refill.Id, refillStatus = refill.Status.ToString(),
            assignedEmployeeId = refill.AssignedVolunteer?.EmployeeId,
            reportedConsumedQuantity = recordedReportedQuantity,
            estimatedConsumedQuantity = recordedReportedQuantity is null ? inferredConsumedQuantity : null,
            budgetCurrency = officeConfig.BudgetCurrency.ToString(), budgetRemaining
        });
        var replyAt = now.AddTicks(1);
        var reply = FrontendApiSupport.AddMessage(database, personalChat, null, employeeId, null,
            MessageAudience.Employee, ConversationMessageKind.Notification, acknowledgement, payload, replyAt);
        FrontendApiSupport.AddNotificationAndOutbox(database, "EmptyItemReported",
            $"empty-report:{userMessage.Id:N}", employeeId, null, false, reply, payload, now);

        if (wasInStock)
        {
            var officeChat = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(
                database, request.Office, now, cancellationToken);
            var text = $"{item.Name} is out of stock at {officeConfig.Name}. Remaining monthly budget: {budgetRemaining} {officeConfig.BudgetCurrency}.";
            var officeMessage = FrontendApiSupport.AddMessage(database, officeChat, null, null, request.Office,
                MessageAudience.Office, ConversationMessageKind.Notification, text, payload, now);
            FrontendApiSupport.AddNotificationAndOutbox(database, "StockDepleted",
                $"stock-depleted:{item.Id}:{Convert.ToHexString(item.RowVersion)}", null, request.Office,
                false, officeMessage, payload, now);
        }
        if (createdRefill)
        {
            var officeChat = await FrontendApiSupport.GetOrCreateOfficeConversationAsync(
                database, request.Office, now, cancellationToken);
            var officeMessage = FrontendApiSupport.AddMessage(database, officeChat, null, null, request.Office,
                MessageAudience.Office, ConversationMessageKind.Notification,
                $"{item.Name} needs refilling at {officeConfig.Name}.", payload, now);
            FrontendApiSupport.AddNotificationAndOutbox(database, "RefillNeeded",
                $"refill-needed:{refill.Id}", null, request.Office, false, officeMessage, payload, now);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem("The item changed while it was being reported. Refresh the pantry and try again."));
        }
        catch (DbUpdateException)
        {
            return Conflict(Problem("The item already has an active refill request. Refresh the office queue."));
        }

        return Ok(new EmptyItemReportResponse(
            item.Id, item.Name, item.Office, item.QuantityOnHand, Convert.ToBase64String(item.RowVersion),
            RefillResponse.From(refill), acknowledgement, budgetRemaining, officeConfig.BudgetCurrency,
            recordedReportedQuantity, recordedReportedQuantity is null ? inferredConsumedQuantity : null));
    }

    [HttpGet("me/notifications")]
    [ProducesResponseType<IReadOnlyList<NotificationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> ListNotifications(
        [FromQuery] OfficeLocation? office = null,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? before = null,
        CancellationToken cancellationToken = default)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var offices = await database.Offices.AsNoTracking().Where(location => location.IsActive)
            .Select(location => location.Location).ToArrayAsync(cancellationToken);
        if (office is not null)
        {
            if (!await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, office.Value, cancellationToken)) return Forbid();
            offices = [office.Value];
        }
        var query = UnreadQuery(database, employeeId, offices);
        if (unreadOnly)
        {
            query = query.Where(notification => notification.ReadAt == null);
        }
        if (before is not null)
        {
            query = query.Where(notification => notification.CreatedAt < before);
        }

        var items = await query.OrderByDescending(notification => notification.CreatedAt)
            .Take(Math.Clamp(pageSize, 1, 100))
            .Select(notification => new NotificationResponse(
                notification.Id, notification.RecipientEmployeeId, notification.Office,
                notification.IsHrAudience, notification.ConversationMessageId, notification.EventType,
                notification.PayloadJson, notification.CreatedAt, notification.ReadAt))
            .ToArrayAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("me/notifications/{notificationId:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid notificationId, CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var notification = await database.Notifications.SingleOrDefaultAsync(
            item => item.Id == notificationId, cancellationToken);
        if (notification is null || !await CanReadNotificationAsync(database, notification, employeeId, cancellationToken))
        {
            return NotFound();
        }

        notification.ReadAt ??= clock.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> CanAccessConversationAsync(
        SnackCounterDbContext database,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        var employeeId = FrontendApiSupport.EmployeeId(User);
        return conversation.Kind switch
        {
            ConversationKind.Employee => conversation.EmployeeId == employeeId,
            ConversationKind.Office when conversation.Office is not null =>
                await FrontendApiSupport.HasOfficeAccessAsync(database, employeeId, conversation.Office.Value, cancellationToken),
            ConversationKind.Hr => User.IsInRole("HR"),
            _ => false
        };
    }

    private async Task<bool> CanReadNotificationAsync(
        SnackCounterDbContext database,
        Notification notification,
        string employeeId,
        CancellationToken cancellationToken)
    {
        if (notification.RecipientEmployeeId == employeeId)
        {
            return true;
        }
        if (notification.IsHrAudience)
        {
            return User.IsInRole("HR");
        }
        return notification.Office is not null && await FrontendApiSupport.HasOfficeAccessAsync(
            database, employeeId, notification.Office.Value, cancellationToken);
    }

    private IQueryable<Notification> UnreadQuery(
        SnackCounterDbContext database,
        string employeeId,
        OfficeLocation[] offices) =>
        database.Notifications.AsNoTracking().Where(notification =>
            notification.RecipientEmployeeId == employeeId ||
            (notification.Office != null && offices.Contains(notification.Office.Value)) ||
            (notification.IsHrAudience && User.IsInRole("HR")));

    private static MessageResponse ToResponse(ConversationMessage message) => new(
        message.Id, message.ConversationId, message.SenderEmployeeId,
        message.RecipientEmployeeId, message.Audience, message.Office,
        message.Kind, message.Text, message.PayloadJson, message.CreatedAt);

    private static bool VersionMatches(byte[] current, string supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        try { return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(current, Convert.FromBase64String(supplied)); }
        catch (FormatException) { return false; }
    }

    private static async Task<decimal> GetBudgetRemainingAsync(
        SnackCounterDbContext database,
        Office office,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId));
        var localMonth = new DateOnly(localNow.Year, localNow.Month, 1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMonth.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localMonth.AddMonths(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
        var budget = await database.MonthlyBudgets.AsNoTracking().Where(x => x.Office == office.Location &&
                x.Year == localMonth.Year && x.Month == localMonth.Month)
            .Select(x => (decimal?)x.BudgetAmount).SingleOrDefaultAsync(cancellationToken) ?? office.MonthlyBudget;
        var spent = await database.Refills.AsNoTracking().Where(x => x.Office == office.Location &&
                x.PurchasedAt >= start && x.PurchasedAt < end)
            .SumAsync(x => x.PurchaseAmount ?? 0m, cancellationToken);
        return budget - spent;
    }
}

public sealed record SendMessageRequest(string Text);
public sealed record ReportEmptyItemRequest(OfficeLocation Office, Guid SnackItemId, string ItemRowVersion,
    decimal? ReportedConsumedQuantity = null);
public sealed record EmptyItemReportResponse(Guid SnackItemId, string ItemName, OfficeLocation Office,
    decimal QuantityOnHand, string ItemRowVersion, RefillResponse Refill, string Confirmation,
    decimal BudgetRemaining, Currency BudgetCurrency, decimal? ReportedConsumedQuantity, decimal? EstimatedConsumedQuantity);
public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    string? SenderEmployeeId,
    string? RecipientEmployeeId,
    MessageAudience Audience,
    OfficeLocation? Office,
    ConversationMessageKind Kind,
    string? Text,
    string? PayloadJson,
    DateTimeOffset CreatedAt);
public sealed record MessagePageResponse(IReadOnlyList<MessageResponse> Items, bool HasMore, DateTimeOffset? NextBefore);
public sealed record ConversationSummaryResponse(
    Guid Id,
    ConversationKind Kind,
    OfficeLocation? Office,
    string Title,
    MessageResponse? LatestMessage,
    int UnreadCount);
public sealed record NotificationResponse(
    Guid Id,
    string? RecipientEmployeeId,
    OfficeLocation? Office,
    bool IsHrAudience,
    Guid? ConversationMessageId,
    string EventType,
    string? PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
