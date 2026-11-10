using Koinon.Domain.Enums;

namespace Koinon.Application.DTOs.Requests;

/// <summary>
/// Request to create a new authorized pickup person for a child.
/// </summary>
public record CreateAuthorizedPickupRequest(
    string? AuthorizedPersonIdKey,
    string? Name,
    string? PhoneNumber,
    PickupRelationship Relationship,
    AuthorizationLevel AuthorizationLevel,
    string? PhotoUrl,
    string? CustodyNotes
);

/// <summary>
/// Request to update an existing authorized pickup person.
/// </summary>
public record UpdateAuthorizedPickupRequest(
    PickupRelationship? Relationship,
    AuthorizationLevel? AuthorizationLevel,
    string? PhotoUrl,
    string? CustodyNotes,
    bool? IsActive
);

/// <summary>
/// Request to verify if a person is authorized to pick up a child.
/// </summary>
public record VerifyPickupRequest(
    string AttendanceIdKey,
    string? PickupPersonIdKey,
    string? PickupPersonName,
    string SecurityCode
);

/// <summary>
/// Request to record a pickup event in the log.
/// </summary>
public record RecordPickupRequest(
    string AttendanceIdKey,
    string? PickupPersonIdKey,
    string? PickupPersonName,
    bool WasAuthorized,
    string? AuthorizedPickupIdKey,
    bool SupervisorOverride,
    string? SupervisorPersonIdKey,
    string? Notes
)
{
    /// <summary>
    /// The child's security code as entered at the kiosk. record-pickup re-runs the
    /// verify-pickup checks with it and never trusts WasAuthorized from the client (#710).
    /// </summary>
    public string? SecurityCode { get; init; }
}
