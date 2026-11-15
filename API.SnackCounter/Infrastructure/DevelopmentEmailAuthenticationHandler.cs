using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SnackCounter.Infrastructure;

public sealed class DevelopmentEmailAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IWebHostEnvironment environment,
    IDbContextFactory<SnackCounterDbContext> contextFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationScheme = "DevelopmentEmail";
    public const string HeaderName = "X-Demo-Email";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
        {
            return AuthenticateResult.NoResult();
        }

        var suppliedEmail = Request.Headers[HeaderName].ToString();
        // Browser WebSocket APIs cannot attach arbitrary headers. SignalR's access-token
        // factory sends this value as a bearer token; allow that only on this dev hub.
        if (string.IsNullOrWhiteSpace(suppliedEmail) &&
            Request.Path.StartsWithSegments("/hubs/notifications"))
        {
            suppliedEmail = Request.Query["access_token"].ToString();
            if (string.IsNullOrWhiteSpace(suppliedEmail) &&
                Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                suppliedEmail = Request.Headers.Authorization.ToString()[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(suppliedEmail))
        {
            return AuthenticateResult.NoResult();
        }

        var email = suppliedEmail.Trim();
        await using var database = await contextFactory.CreateDbContextAsync(Context.RequestAborted);
        var employee = await database.Volunteers.AsNoTracking()
            .SingleOrDefaultAsync(person => person.Email == email && person.IsActive, Context.RequestAborted);
        if (employee is null)
        {
            return AuthenticateResult.Fail("The demo email does not match an active employee.");
        }

        var roles = await database.EmployeeRoles.AsNoTracking()
            .Where(role => role.EmployeeId == employee.EmployeeId)
            .Select(role => role.RoleName)
            .ToArrayAsync(Context.RequestAborted);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, employee.EmployeeId),
            new(ClaimTypes.Name, employee.DisplayName),
            new(ClaimTypes.Email, email)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme));
    }
}
