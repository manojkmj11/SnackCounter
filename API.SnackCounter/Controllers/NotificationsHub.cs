using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[Authorize(Roles = "Employee,HR,Manager")]
public sealed class NotificationsHub(IDbContextFactory<SnackCounterDbContext> contextFactory) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var employeeId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isHr = Context.User?.IsInRole("HR") == true;
        if (string.IsNullOrWhiteSpace(employeeId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, EmployeeGroup(employeeId));
        if (Context.User?.IsInRole("HR") == true) await Groups.AddToGroupAsync(Context.ConnectionId, HrGroup);
        await using var db = await contextFactory.CreateDbContextAsync(Context.ConnectionAborted);
        var offices = await db.Offices.AsNoTracking().Where(office => office.IsActive)
            .Select(office => office.Location).ToArrayAsync(Context.ConnectionAborted);
        foreach (var office in offices) await Groups.AddToGroupAsync(Context.ConnectionId, OfficeGroup(office.ToString()));

        // Replay unread durable notifications; clients should de-duplicate by notification id.
        var unread = await db.Notifications.AsNoTracking().Where(notification => notification.ReadAt == null &&
                (notification.RecipientEmployeeId == employeeId ||
                 (notification.Office != null && offices.Contains(notification.Office.Value)) ||
                 (notification.IsHrAudience && isHr)))
            .OrderBy(notification => notification.CreatedAt).Take(500)
            .Select(notification => new HubNotification(notification.Id, notification.EventType, notification.RecipientEmployeeId,
                notification.Office, notification.IsHrAudience, notification.ConversationMessageId,
                notification.PayloadJson, notification.CreatedAt, notification.ReadAt))
            .ToArrayAsync(Context.ConnectionAborted);
        foreach (var notification in unread)
            await Clients.Caller.SendAsync("notification", notification, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public static string EmployeeGroup(string employeeId) => $"employee:{employeeId}";
    public static string OfficeGroup(string office) => $"office:{office}";
    public const string HrGroup = "hr";
}

public sealed record HubNotification(Guid Id, string EventType, string? RecipientEmployeeId,
    global::SnackCounter.Domain.OfficeLocation? Office, bool IsHrAudience, Guid? ConversationMessageId,
    string? PayloadJson, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
