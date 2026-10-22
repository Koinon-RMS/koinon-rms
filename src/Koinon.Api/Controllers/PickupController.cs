using Koinon.Api.Attributes;
using Koinon.Api.Filters;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Koinon.Api.Controllers;

/// <summary>
/// Controller for pickup verification and checkout operations.
/// Handles child safety by verifying authorized pickup persons during checkout.
/// </summary>
[ApiController]
[Route("api/v1/checkin")]
[Authorize]
[ValidateIdKey]
public class PickupController(
    IAuthorizedPickupService authorizedPickupService,
    ISupervisorModeService supervisorModeService,
    ILogger<PickupController> logger) : ControllerBase
{
    /// <summary>
    /// Verifies if a person is authorized to pick up a child.
    /// Checks the authorized pickup list and authorization levels.
    /// CRITICAL #5: Rate limiting tracked in issue #106
    /// (max 5 attempts per attendance per 15 minutes to prevent brute-force attacks on security codes)
    /// </summary>
    /// <param name="request">Pickup verification request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Verification result with authorization status</returns>
    /// <response code="200">Returns verification result (authorized or not)</response>
    /// <response code="400">Validation failed</response>
    /// <response code="401">Missing or invalid kiosk authentication</response>
    [HttpPost("verify-pickup")]
    [AllowAnonymous]
    [KioskAuthorize]
    [ProducesResponseType(typeof(PickupVerificationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyPickup(
        [FromBody] VerifyPickupRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AttendanceIdKey))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "AttendanceIdKey is required",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        if (string.IsNullOrWhiteSpace(request.SecurityCode))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "SecurityCode is required",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        var result = await authorizedPickupService.VerifyPickupAsync(request, ct);

        logger.LogInformation(
            "Pickup verification completed: AttendanceIdKey={AttendanceIdKey}, IsAuthorized={IsAuthorized}, RequiresOverride={RequiresOverride}",
            request.AttendanceIdKey, result.IsAuthorized, result.RequiresSupervisorOverride);

        return Ok(new { data = result });
    }

    /// <summary>
    /// Records a pickup event in the audit log and checks out the child.
    /// </summary>
    /// <param name="request">Pickup recording request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Created pickup log entry</returns>
    /// <response code="201">Pickup recorded successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="401">Missing or invalid kiosk authentication</response>
    /// <response code="403">Supervisor override without a valid supervisor session</response>
    /// <response code="404">Attendance not found</response>
    /// <response code="422">Pickup refused by a business rule: a blocked person, a wrong security code, or a claim the server's verification does not support</response>
    [HttpPost("record-pickup")]
    [AllowAnonymous]
    [KioskAuthorize]
    [ProducesResponseType(typeof(PickupLogDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordPickup(
        [FromBody] RecordPickupRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AttendanceIdKey))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = "AttendanceIdKey is required",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        // CRITICAL #3 (#710): a supervisor override must establish the
        // supervisor's identity server-side. JWT supervisors (admin path)
        // keep their client-supplied SupervisorPersonIdKey; a kiosk derives
        // the supervisor from the X-Supervisor-Session header and never
        // trusts a client-supplied identity.
        var effectiveRequest = request;
        if (request.SupervisorOverride)
        {
            if (User.IsInRole("Supervisor"))
            {
                // JWT supervisor (admin path): keep the client-supplied SupervisorPersonIdKey
            }
            else
            {
                if (!HttpContext.Request.Headers.TryGetValue("X-Supervisor-Session", out var sessionToken) ||
                    string.IsNullOrWhiteSpace(sessionToken))
                {
                    return Problem(
                        detail: "Supervisor override requires an active supervisor session",
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden");
                }

                var supervisor = await supervisorModeService.ValidateSessionAsync(sessionToken.ToString(), ct);
                if (supervisor is null)
                {
                    return Problem(
                        detail: "Supervisor session is invalid or expired",
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden");
                }

                effectiveRequest = request with { SupervisorPersonIdKey = supervisor.IdKey };
            }
        }

        var result = await authorizedPickupService.RecordPickupAsync(effectiveRequest, ct);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Pickup refused: AttendanceIdKey={AttendanceIdKey}, Code={Code}, Message={Message}",
                effectiveRequest.AttendanceIdKey, result.Error!.Code, result.Error.Message);

            return result.Error.Code switch
            {
                "NOT_FOUND" => NotFound(new ProblemDetails
                {
                    Title = "Attendance not found",
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status404NotFound,
                    Instance = HttpContext.Request.Path
                }),
                "VALIDATION_ERROR" => BadRequest(new ProblemDetails
                {
                    Title = "Invalid request",
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status400BadRequest,
                    Instance = HttpContext.Request.Path
                }),
                _ => UnprocessableEntity(new ProblemDetails
                {
                    Title = result.Error.Code,
                    Detail = result.Error.Message,
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Instance = HttpContext.Request.Path
                })
            };
        }

        var pickupLog = result.Value!;

        logger.LogInformation(
            "Pickup recorded: AttendanceIdKey={AttendanceIdKey}, PickupPerson={PickupPerson}, WasAuthorized={WasAuthorized}, SupervisorOverride={SupervisorOverride}, SupervisorPersonIdKey={SupervisorPersonIdKey}",
            effectiveRequest.AttendanceIdKey,
            effectiveRequest.PickupPersonName ?? "Unknown",
            effectiveRequest.WasAuthorized,
            effectiveRequest.SupervisorOverride,
            effectiveRequest.SupervisorPersonIdKey ?? "none");

        return CreatedAtAction(
            nameof(GetPickupHistory),
            new { childIdKey = "placeholder" }, // Will be determined from attendance
            new { data = pickupLog });
    }

    /// <summary>
    /// Gets the pickup history for a child.
    /// </summary>
    /// <param name="childIdKey">The child's IdKey</param>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of pickup log entries</returns>
    /// <response code="200">Returns pickup history</response>
    /// <response code="400">Invalid IdKey format or date range</response>
    /// <response code="401">Not authenticated</response>
    /// <response code="403">Requires Supervisor role</response>
    [HttpGet("people/{childIdKey}/pickup-history")]
    [Authorize(Roles = "Supervisor")]
    [ProducesResponseType(typeof(List<PickupLogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPickupHistory(
        string childIdKey,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken ct = default)
    {
        if (toDate.HasValue && fromDate.HasValue && toDate.Value < fromDate.Value)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid date range",
                Detail = "The 'toDate' must be greater than or equal to 'fromDate'",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        var history = await authorizedPickupService.GetPickupHistoryAsync(
            childIdKey,
            fromDate,
            toDate,
            ct);

        logger.LogInformation(
            "Pickup history retrieved: ChildIdKey={ChildIdKey}, Count={Count}, FromDate={FromDate}, ToDate={ToDate}",
            childIdKey, history.Count, fromDate, toDate);

        return Ok(new { data = history });
    }
}
