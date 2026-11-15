using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

namespace API.SnackCounter.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class IdentityController(
    IDbContextFactory<SnackCounterDbContext> contextFactory,
    IWebHostEnvironment environment) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("dev-login")]
    [ProducesResponseType<DemoLoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DemoLoginResponse>> DevelopmentLogin(DemoLoginRequest request, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new ProblemDetails { Title = "Email is required." });
        }
        if (request.WorkingOffice is null)
        {
            return BadRequest(new ProblemDetails { Title = "Choose the office you are working from." });
        }

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var email = request.Email.Trim();
        var employee = await database.Volunteers.AsNoTracking()
            .SingleOrDefaultAsync(person => person.Email == email && person.IsActive, cancellationToken);
        if (employee is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Demo employee not found.",
                Detail = "Use one of the seeded @snackcounter.local email addresses."
            });
        }

        var roles = await database.EmployeeRoles.AsNoTracking()
            .Where(role => role.EmployeeId == employee.EmployeeId)
            .OrderBy(role => role.RoleName)
            .Select(role => role.RoleName)
            .ToArrayAsync(cancellationToken);

        return Ok(new DemoLoginResponse(
            new CurrentUserResponse(employee.EmployeeId, employee.DisplayName, employee.Email!, employee.HomeOffice,
                employee.PayrollCurrency, roles),
            DevelopmentEmailAuthenticationHandler.HeaderName,
            employee.Email!,
            request.WorkingOffice.Value));
    }

    [Authorize]
    [HttpGet("~/api/me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var employeeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (employeeId is null)
        {
            return Unauthorized();
        }

        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        var employee = await database.Volunteers.AsNoTracking()
            .SingleOrDefaultAsync(person => person.EmployeeId == employeeId, cancellationToken);
        if (employee is null)
        {
            return Unauthorized();
        }

        var roles = await database.EmployeeRoles.AsNoTracking()
            .Where(role => role.EmployeeId == employeeId)
            .OrderBy(role => role.RoleName)
            .Select(role => role.RoleName)
            .ToArrayAsync(cancellationToken);
        return Ok(new CurrentUserResponse(employee.EmployeeId, employee.DisplayName, employee.Email ?? string.Empty,
            employee.HomeOffice, employee.PayrollCurrency, roles));
    }
}

public sealed record DemoLoginRequest(string Email, OfficeLocation? WorkingOffice);
public sealed record CurrentUserResponse(
    string EmployeeId,
    string DisplayName,
    string Email,
    OfficeLocation HomeOffice,
    Currency PayrollCurrency,
    IReadOnlyList<string> Roles);
public sealed record DemoLoginResponse(
    CurrentUserResponse User,
    string HeaderName,
    string HeaderValue,
    OfficeLocation SelectedWorkingOffice);
