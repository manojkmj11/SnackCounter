using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnackCounter.Application;
using SnackCounter.Domain;

namespace API.SnackCounter.Controllers;

[ApiController]
[Authorize(Roles = "HR,Manager")]
[Route("api/refills")]
public sealed class RefillsController(
    IRefillStore store,
    IVolunteerDirectory volunteers,
    RefillLifecycleService lifecycle) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RefillResponse>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RefillResponse>> List(
        [FromQuery] OfficeLocation? office,
        [FromQuery] RefillStatus? status) =>
        Ok(store.List(office, status).Select(RefillResponse.From).ToArray());

    [HttpGet("{refillId:guid}")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public ActionResult<RefillResponse> Get(Guid refillId)
    {
        var refill = store.Get(refillId);
        return refill is null ? NotFound(Problem("Refill not found.")) : Ok(RefillResponse.From(refill));
    }

    [HttpPost]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<RefillResponse> Create(CreateRefillRequest request)
    {
        try
        {
            var refill = lifecycle.CreateRefill(request.Office, request.Item);
            return CreatedAtAction(nameof(Get), new { refillId = refill.Id }, RefillResponse.From(refill));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message));
        }
    }

    [HttpPost("{refillId:guid}/commit")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<RefillResponse> Commit(Guid refillId, CommitRefillRequest request)
    {
        if (store.Get(refillId) is null)
        {
            return NotFound(Problem("Refill not found."));
        }
        var volunteer = volunteers.GetVolunteer(request.EmployeeId);
        if (volunteer is null)
        {
            return NotFound(Problem("Volunteer not found."));
        }

        if (!lifecycle.Commit(refillId, volunteer, request.ExpectedVersion))
        {
            return Conflict(Problem("The refill is no longer open or its version is stale."));
        }

        return Get(refillId);
    }

    [HttpPost("{refillId:guid}/purchase")]
    [RequestSizeLimit(4_000_000)]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<RefillResponse> CompletePurchase(Guid refillId, PurchaseRequest request)
    {
        byte[] receipt;
        try
        {
            receipt = Convert.FromBase64String(request.ReceiptImageBase64);
        }
        catch (FormatException)
        {
            return BadRequest(Problem("ReceiptImageBase64 must contain a valid base64 image payload."));
        }

        try
        {
            lifecycle.CompletePurchase(
                refillId,
                request.EmployeeId,
                request.Amount,
                request.Description,
                receipt,
                request.ExpectedVersion);
            return Get(refillId);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(Problem("Refill not found."));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message));
        }
    }

    [HttpPost("{refillId:guid}/claim-status")]
    [ProducesResponseType<RefillResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<RefillResponse> UpdateClaimStatus(Guid refillId, ClaimStatusRequest request)
    {
        if (store.Get(refillId) is null)
        {
            return NotFound(Problem("Refill not found."));
        }
        try
        {
            lifecycle.UpdateClaimStatus(refillId, request.Status, request.ExpectedVersion);
            return Get(refillId);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(Problem(exception.Message));
        }
    }
}
