using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;

namespace Koinon.Application.Interfaces;

/// <summary>
/// Service interface for managing authorized pickup persons and pickup verification.
/// Handles child safety by tracking who is authorized to pick up children during checkout.
/// </summary>
public interface IAuthorizedPickupService
{
    /// <summary>
    /// Gets all authorized pickup persons for a child.
    /// </summary>
    /// <param name="childIdKey">The child's IdKey.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of authorized pickup persons.</returns>
    Task<List<AuthorizedPickupDto>> GetAuthorizedPickupsAsync(
        string childIdKey,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a new authorized pickup person for a child.
    /// </summary>
    /// <param name="childIdKey">The child's IdKey.</param>
    /// <param name="request">The authorized pickup details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created authorized pickup DTO.</returns>
    Task<AuthorizedPickupDto> AddAuthorizedPickupAsync(
        string childIdKey,
        CreateAuthorizedPickupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Updates an existing authorized pickup person.
    /// </summary>
    /// <param name="pickupIdKey">The authorized pickup's IdKey.</param>
    /// <param name="request">The update details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated authorized pickup DTO.</returns>
    Task<AuthorizedPickupDto> UpdateAuthorizedPickupAsync(
        string pickupIdKey,
        UpdateAuthorizedPickupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes (deactivates) an authorized pickup person.
    /// </summary>
    /// <param name="pickupIdKey">The authorized pickup's IdKey.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAuthorizedPickupAsync(
        string pickupIdKey,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies if a person is authorized to pick up a child.
    /// Checks the authorized pickup list and authorization levels.
    /// </summary>
    /// <param name="request">The verification request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result with authorization status.</returns>
    Task<PickupVerificationResultDto> VerifyPickupAsync(
        VerifyPickupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Records a pickup event in the audit log.
    /// </summary>
    /// <param name="request">The pickup details to record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created pickup log DTO, or a failure when the request is invalid, the
    /// attendance is not found, or the server's own verification refuses the claim.</returns>
    Task<Result<PickupLogDto>> RecordPickupAsync(
        RecordPickupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the pickup history for a child.
    /// </summary>
    /// <param name="childIdKey">The child's IdKey.</param>
    /// <param name="fromDate">Optional start date filter.</param>
    /// <param name="toDate">Optional end date filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of pickup log entries.</returns>
    Task<List<PickupLogDto>> GetPickupHistoryAsync(
        string childIdKey,
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken ct = default);

    /// <summary>
    /// Automatically populates the authorized pickup list with adult family members.
    /// Adds parents and guardians from the child's family as authorized pickups.
    /// </summary>
    /// <param name="childIdKey">The child's IdKey.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AutoPopulateFamilyMembersAsync(
        string childIdKey,
        CancellationToken ct = default);
}
