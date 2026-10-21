using System.Security.Cryptography;
using System.Text;
using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Koinon.Application.Services;

/// <summary>
/// Service for managing authorized pickup persons and pickup verification.
/// Handles child safety by tracking who is authorized to pick up children during checkout.
/// </summary>
public class AuthorizedPickupService(
    IApplicationDbContext context,
    ILogger<AuthorizedPickupService> logger) : IAuthorizedPickupService
{
    public async Task<List<AuthorizedPickupDto>> GetAuthorizedPickupsAsync(
        string childIdKey,
        CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(childIdKey, out int childId))
        {
            logger.LogWarning("Invalid child IdKey provided: {IdKey}", childIdKey);
            return new List<AuthorizedPickupDto>();
        }

        var pickups = await context.AuthorizedPickups
            .AsNoTracking()
            .Include(p => p.ChildPerson)
            .Include(p => p.AuthorizedPerson)
            .Where(p => p.ChildPersonId == childId && p.IsActive)
            .OrderBy(p => p.Relationship)
            .ThenBy(p => p.AuthorizedPerson != null ? p.AuthorizedPerson.LastName : p.Name)
            .ToListAsync(ct);

        return pickups
            .Where(p => p.ChildPerson != null)
            .Select(p => new AuthorizedPickupDto(
                IdKey: p.IdKey,
                ChildIdKey: p.ChildPerson!.IdKey,
                ChildName: p.ChildPerson.FullName,
                AuthorizedPersonIdKey: p.AuthorizedPerson?.IdKey,
                AuthorizedPersonName: p.AuthorizedPerson?.FullName,
                Name: p.Name,
                PhoneNumber: p.PhoneNumber,
                Relationship: p.Relationship,
                AuthorizationLevel: p.AuthorizationLevel,
                PhotoUrl: p.PhotoUrl,
                IsActive: p.IsActive
            ))
            .ToList();
    }

    public async Task<AuthorizedPickupDto> AddAuthorizedPickupAsync(
        string childIdKey,
        CreateAuthorizedPickupRequest request,
        CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(childIdKey, out int childId))
        {
            throw new ArgumentException($"Invalid child IdKey: {childIdKey}", nameof(childIdKey));
        }

        // Validate that either AuthorizedPersonIdKey OR Name is provided
        int? authorizedPersonId = null;
        if (!string.IsNullOrWhiteSpace(request.AuthorizedPersonIdKey))
        {
            if (!IdKeyHelper.TryDecode(request.AuthorizedPersonIdKey, out int personId))
            {
                throw new ArgumentException(
                    $"Invalid authorized person IdKey: {request.AuthorizedPersonIdKey}",
                    nameof(request));
            }
            authorizedPersonId = personId;

            // Verify the person exists
            var personExists = await context.People
                .AsNoTracking()
                .AnyAsync(p => p.Id == personId, ct);

            if (!personExists)
            {
                throw new ArgumentException(
                    $"Person with IdKey {request.AuthorizedPersonIdKey} not found",
                    nameof(request));
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException(
                "Either AuthorizedPersonIdKey or Name must be provided",
                nameof(request));
        }

        // Verify the child exists
        var child = await context.People
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == childId, ct);

        if (child == null)
        {
            throw new ArgumentException($"Child with IdKey {childIdKey} not found", nameof(childIdKey));
        }

        var pickup = new AuthorizedPickup
        {
            ChildPersonId = childId,
            AuthorizedPersonId = authorizedPersonId,
            Name = request.Name,
            PhoneNumber = request.PhoneNumber,
            Relationship = request.Relationship,
            AuthorizationLevel = request.AuthorizationLevel,
            PhotoUrl = request.PhotoUrl,
            CustodyNotes = request.CustodyNotes,
            IsActive = true
        };

        context.AuthorizedPickups.Add(pickup);
        await context.SaveChangesAsync(ct);

        // Reload with navigation properties
        var created = await context.AuthorizedPickups
            .AsNoTracking()
            .Include(p => p.ChildPerson)
            .Include(p => p.AuthorizedPerson)
            .FirstAsync(p => p.Id == pickup.Id, ct);

        logger.LogInformation(
            "Added authorized pickup {PickupId} for child {ChildId}: {PersonName}",
            pickup.Id,
            childId,
            created.AuthorizedPerson?.FullName ?? created.Name ?? "Unknown");

        return new AuthorizedPickupDto(
            IdKey: created.IdKey,
            ChildIdKey: created.ChildPerson!.IdKey,
            ChildName: created.ChildPerson.FullName,
            AuthorizedPersonIdKey: created.AuthorizedPerson?.IdKey,
            AuthorizedPersonName: created.AuthorizedPerson?.FullName,
            Name: created.Name,
            PhoneNumber: created.PhoneNumber,
            Relationship: created.Relationship,
            AuthorizationLevel: created.AuthorizationLevel,
            PhotoUrl: created.PhotoUrl,
            IsActive: created.IsActive
        );
    }

    public async Task<AuthorizedPickupDto> UpdateAuthorizedPickupAsync(
        string pickupIdKey,
        UpdateAuthorizedPickupRequest request,
        CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(pickupIdKey, out int pickupId))
        {
            throw new ArgumentException($"Invalid pickup IdKey: {pickupIdKey}", nameof(pickupIdKey));
        }

        // AsTracking: global default is NoTracking (PostgreSqlProvider); without it the
        // Relationship/AuthorizationLevel/etc. mutations below would be silently dropped on
        // SaveChanges (#708).
        var pickup = await context.AuthorizedPickups
            .AsTracking()
            .Include(p => p.ChildPerson)
            .Include(p => p.AuthorizedPerson)
            .FirstOrDefaultAsync(p => p.Id == pickupId, ct);

        if (pickup == null)
        {
            throw new InvalidOperationException(
                $"Authorized pickup with IdKey {pickupIdKey} not found");
        }

        // Update fields if provided
        if (request.Relationship.HasValue)
        {
            pickup.Relationship = request.Relationship.Value;
        }

        if (request.AuthorizationLevel.HasValue)
        {
            pickup.AuthorizationLevel = request.AuthorizationLevel.Value;
        }

        if (request.PhotoUrl != null)
        {
            pickup.PhotoUrl = request.PhotoUrl;
        }

        if (request.CustodyNotes != null)
        {
            pickup.CustodyNotes = request.CustodyNotes;
        }

        if (request.IsActive.HasValue)
        {
            pickup.IsActive = request.IsActive.Value;
        }

        pickup.ModifiedDateTime = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Updated authorized pickup {PickupId} for child {ChildId}",
            pickupId,
            pickup.ChildPersonId);

        return new AuthorizedPickupDto(
            IdKey: pickup.IdKey,
            ChildIdKey: pickup.ChildPerson!.IdKey,
            ChildName: pickup.ChildPerson.FullName,
            AuthorizedPersonIdKey: pickup.AuthorizedPerson?.IdKey,
            AuthorizedPersonName: pickup.AuthorizedPerson?.FullName,
            Name: pickup.Name,
            PhoneNumber: pickup.PhoneNumber,
            Relationship: pickup.Relationship,
            AuthorizationLevel: pickup.AuthorizationLevel,
            PhotoUrl: pickup.PhotoUrl,
            IsActive: pickup.IsActive
        );
    }

    public async Task DeleteAuthorizedPickupAsync(string pickupIdKey, CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(pickupIdKey, out int pickupId))
        {
            throw new ArgumentException($"Invalid pickup IdKey: {pickupIdKey}", nameof(pickupIdKey));
        }

        // AsTracking: global default is NoTracking (PostgreSqlProvider); without it the
        // IsActive=false soft-delete mutation below would be silently dropped on SaveChanges (#708).
        var pickup = await context.AuthorizedPickups
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == pickupId, ct);

        if (pickup == null)
        {
            throw new InvalidOperationException(
                $"Authorized pickup with IdKey {pickupIdKey} not found");
        }

        // Soft delete by marking as inactive
        pickup.IsActive = false;
        pickup.ModifiedDateTime = DateTime.UtcNow;

        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Deleted (deactivated) authorized pickup {PickupId} for child {ChildId}",
            pickupId,
            pickup.ChildPersonId);
    }

    public async Task<PickupVerificationResultDto> VerifyPickupAsync(
        VerifyPickupRequest request,
        CancellationToken ct = default)
    {
        var validationResult = ValidateVerificationRequest(request);
        if (validationResult != null)
        {
            return validationResult;
        }

        var attendanceIdResult = DecodeAttendanceIdKey(request.AttendanceIdKey);
        if (!attendanceIdResult.HasValue)
        {
            return CreateInvalidAttendanceResult();
        }

        var attendance = await GetAttendanceWithPersonAsync(attendanceIdResult.Value, ct);
        var attendanceResult = ValidateAttendance(attendance, attendanceIdResult.Value);
        if (attendanceResult != null)
        {
            return attendanceResult;
        }

        var securityCodeResult = VerifySecurityCode(attendance!, request.SecurityCode, attendanceIdResult.Value);
        if (securityCodeResult != null)
        {
            return securityCodeResult;
        }

        var child = attendance!.PersonAlias!.Person!;
        var pickupPersonIdResult = ValidatePickupPersonIdKey(request.PickupPersonIdKey);
        if (pickupPersonIdResult.errorResult != null)
        {
            return pickupPersonIdResult.errorResult;
        }

        var authorizedPickup = await FindAuthorizedPickupAsync(
            child.Id,
            pickupPersonIdResult.personId,
            request.PickupPersonName,
            ct);

        return CheckAuthorizationLevel(authorizedPickup, child, request.PickupPersonName);
    }

    public async Task<Result<PickupLogDto>> RecordPickupAsync(
        RecordPickupRequest request,
        CancellationToken ct = default)
    {
        var requestCheck = ValidateRecordPickupRequest(request);
        if (requestCheck.IsFailure)
        {
            return Result<PickupLogDto>.Failure(requestCheck.Error!);
        }

        if (!IdKeyHelper.TryDecode(request.AttendanceIdKey, out int attendanceId))
        {
            return Result<PickupLogDto>.Failure(
                Error.Validation($"Invalid attendance IdKey: {request.AttendanceIdKey}"));
        }

        var attendance = await GetAttendanceForPickupAsync(attendanceId, ct);
        if (attendance?.PersonAlias?.Person == null)
        {
            return Result<PickupLogDto>.Failure(
                Error.NotFound("Attendance", request.AttendanceIdKey));
        }

        var child = attendance.PersonAlias.Person;
        var pickupPersonIdResult = DecodePickupPersonIdKeyOrNull(request.PickupPersonIdKey);
        if (pickupPersonIdResult.IsFailure)
        {
            return Result<PickupLogDto>.Failure(pickupPersonIdResult.Error!);
        }

        var pickupPersonId = pickupPersonIdResult.Value;
        var authorizedPickupIdResult = await ValidateAuthorizedPickupIdAsync(
            request.AuthorizedPickupIdKey,
            ct);
        if (authorizedPickupIdResult.IsFailure)
        {
            return Result<PickupLogDto>.Failure(authorizedPickupIdResult.Error!);
        }

        var authorizedPickupId = authorizedPickupIdResult.Value;
        var supervisorPersonIdResult = DecodeSupervisorPersonIdKey(request.SupervisorPersonIdKey);
        if (supervisorPersonIdResult.IsFailure)
        {
            return Result<PickupLogDto>.Failure(supervisorPersonIdResult.Error!);
        }

        var supervisorPersonId = supervisorPersonIdResult.Value;
        var overrideCheck = ValidateSupervisorOverride(request, supervisorPersonId);
        if (overrideCheck.IsFailure)
        {
            return Result<PickupLogDto>.Failure(overrideCheck.Error!);
        }

        var blockingCheck = await ValidateAuthorizedPickupBlocking(
            request,
            child.Id,
            pickupPersonId,
            authorizedPickupId,
            ct);
        if (blockingCheck.IsFailure)
        {
            return Result<PickupLogDto>.Failure(blockingCheck.Error!);
        }

        // CRITICAL (#710): the server decides. record-pickup is kiosk-device-authorized, so
        // the client's WasAuthorized, SupervisorOverride and AuthorizedPickupIdKey are claims,
        // not facts: re-verify the security code and the authorization exactly as
        // verify-pickup does, and refuse any claim the server's own result does not support.
        var reverification = await ReverifyPickupClaimAsync(
            request,
            attendance,
            attendanceId,
            child,
            pickupPersonId,
            authorizedPickupId,
            ct);
        if (reverification.IsFailure)
        {
            return Result<PickupLogDto>.Failure(reverification.Error!);
        }

        var log = CreatePickupLogEntry(
            request,
            attendanceId,
            child.Id,
            pickupPersonId,
            reverification.Value?.Id,
            supervisorPersonId);

        context.PickupLogs.Add(log);
        attendance.EndDateTime = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);

        return Result<PickupLogDto>.Success(
            await BuildPickupLogDtoAsync(log.Id, request.AttendanceIdKey, child.Id, ct));
    }

    public async Task<List<PickupLogDto>> GetPickupHistoryAsync(
        string childIdKey,
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(childIdKey, out int childId))
        {
            logger.LogWarning("Invalid child IdKey provided: {IdKey}", childIdKey);
            return new List<PickupLogDto>();
        }

        var query = context.PickupLogs
            .AsNoTracking()
            .Include(pl => pl.ChildPerson)
            .Include(pl => pl.PickupPerson)
            .Include(pl => pl.SupervisorPerson)
            .Include(pl => pl.Attendance)
            .Where(pl => pl.ChildPersonId == childId);

        if (fromDate.HasValue)
        {
            query = query.Where(pl => pl.CheckoutDateTime >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(pl => pl.CheckoutDateTime <= toDate.Value);
        }

        var logs = await query
            .OrderByDescending(pl => pl.CheckoutDateTime)
            .ToListAsync(ct);

        return logs
            .Where(pl => pl.ChildPerson != null && pl.Attendance != null)
            .Select(pl => new PickupLogDto(
                IdKey: pl.IdKey,
                AttendanceIdKey: pl.Attendance!.IdKey,
                ChildName: pl.ChildPerson!.FullName,
                PickupPersonName: pl.PickupPerson?.FullName ?? pl.PickupPersonName ?? "Unknown",
                WasAuthorized: pl.WasAuthorized,
                SupervisorOverride: pl.SupervisorOverride,
                SupervisorName: pl.SupervisorPerson?.FullName,
                CheckoutDateTime: pl.CheckoutDateTime,
                Notes: pl.Notes
            ))
            .ToList();
    }

    public async Task AutoPopulateFamilyMembersAsync(
        string childIdKey,
        CancellationToken ct = default)
    {
        if (!IdKeyHelper.TryDecode(childIdKey, out int childId))
        {
            throw new ArgumentException($"Invalid child IdKey: {childIdKey}", nameof(childIdKey));
        }

        // Get the child and verify they exist
        var childExists = await context.People
            .AnyAsync(p => p.Id == childId, ct);

        if (!childExists)
        {
            throw new ArgumentException($"Child with IdKey {childIdKey} not found", nameof(childIdKey));
        }

        // Find child's family via FamilyMember (preferring primary if multiple)
        var childFamilyMember = await context.FamilyMembers
            .AsNoTracking()
            .Where(fm => fm.PersonId == childId)
            .OrderByDescending(fm => fm.IsPrimary)
            .FirstOrDefaultAsync(ct);

        if (childFamilyMember == null)
        {
            logger.LogWarning("Child {ChildId} has no family membership", childId);
            return;
        }

        var familyId = childFamilyMember.FamilyId;

        // Get adult family members (parents/guardians)
        // Filter to adult roles in the database query using EF.Functions.Like for case-insensitive matching
        var adultMembers = await context.FamilyMembers
            .AsNoTracking()
            .Include(fm => fm.Person)
            .Include(fm => fm.FamilyRole)
            .Where(fm =>
                fm.FamilyId == familyId &&
                fm.Person != null &&
                fm.PersonId != childId && // Exclude the child themselves
                fm.FamilyRole != null &&
                (EF.Functions.Like(fm.FamilyRole.Name, "%Adult%") ||
                 EF.Functions.Like(fm.FamilyRole.Name, "%Parent%") ||
                 EF.Functions.Like(fm.FamilyRole.Name, "%Guardian%")))
            .ToListAsync(ct);

        if (!adultMembers.Any())
        {
            logger.LogInformation("No adult family members found for child {ChildId}", childId);
            return;
        }

        // Get existing authorized pickups to avoid duplicates
        var existingPickups = await context.AuthorizedPickups
            .AsNoTracking()
            .Where(p => p.ChildPersonId == childId && p.IsActive)
            .Select(p => p.AuthorizedPersonId)
            .Where(id => id.HasValue)
            .ToListAsync(ct);

        var existingPersonIds = existingPickups.Select(id => id!.Value).ToHashSet();

        // Add adult family members who aren't already authorized
        var newPickups = new List<AuthorizedPickup>();
        foreach (var member in adultMembers)
        {
            if (existingPersonIds.Contains(member.PersonId))
            {
                continue; // Already authorized
            }

            var pickup = new AuthorizedPickup
            {
                ChildPersonId = childId,
                AuthorizedPersonId = member.PersonId,
                Relationship = PickupRelationship.Parent, // Default to Parent for family members
                AuthorizationLevel = Domain.Enums.AuthorizationLevel.Always,
                IsActive = true
            };

            newPickups.Add(pickup);
        }

        if (newPickups.Any())
        {
            context.AuthorizedPickups.AddRange(newPickups);
            await context.SaveChangesAsync(ct);

            logger.LogInformation(
                "Auto-populated {Count} family members as authorized pickups for child {ChildId}",
                newPickups.Count,
                childId);
        }
        else
        {
            logger.LogInformation(
                "All adult family members already authorized for child {ChildId}",
                childId);
        }
    }

    #region Private Helper Methods - Verification

    private PickupVerificationResultDto? ValidateVerificationRequest(VerifyPickupRequest request)
    {
        // Currently no additional validation needed beyond what's in the main method
        // This method is a placeholder for future validation logic
        return null;
    }

    private int? DecodeAttendanceIdKey(string attendanceIdKey)
    {
        if (!IdKeyHelper.TryDecode(attendanceIdKey, out int attendanceId))
        {
            logger.LogWarning("Invalid attendance IdKey: {IdKey}", attendanceIdKey);
            return null;
        }
        return attendanceId;
    }

    private PickupVerificationResultDto CreateInvalidAttendanceResult()
    {
        return new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: null,
            AuthorizedPickupIdKey: null,
            Message: "Invalid attendance record",
            RequiresSupervisorOverride: false
        );
    }

    private async Task<Attendance?> GetAttendanceWithPersonAsync(
        int attendanceId,
        CancellationToken ct)
    {
        return await context.Attendances
            .AsNoTracking()
            .Include(a => a.PersonAlias)
            .ThenInclude(pa => pa!.Person)
            .Include(a => a.AttendanceCode)
            .FirstOrDefaultAsync(a => a.Id == attendanceId, ct);
    }

    private PickupVerificationResultDto? ValidateAttendance(Attendance? attendance, int attendanceId)
    {
        if (attendance?.PersonAlias?.Person == null)
        {
            logger.LogWarning("Attendance {AttendanceId} not found or missing person", attendanceId);
            return new PickupVerificationResultDto(
                IsAuthorized: false,
                AuthorizationLevel: null,
                AuthorizedPickupIdKey: null,
                Message: "Attendance record not found",
                RequiresSupervisorOverride: false
            );
        }
        return null;
    }

    private PickupVerificationResultDto? VerifySecurityCode(
        Attendance attendance,
        string? securityCode,
        int attendanceId)
    {
        if (attendance.AttendanceCode?.Code == null ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(attendance.AttendanceCode.Code),
                Encoding.UTF8.GetBytes(securityCode ?? "")))
        {
            logger.LogWarning("Invalid security code for attendance {AttendanceId}", attendanceId);
            return new PickupVerificationResultDto(
                IsAuthorized: false,
                AuthorizationLevel: null,
                AuthorizedPickupIdKey: null,
                Message: "Invalid security code",
                RequiresSupervisorOverride: false
            );
        }

        return null;
    }

    private (int? personId, PickupVerificationResultDto? errorResult) ValidatePickupPersonIdKey(
        string? pickupPersonIdKey)
    {
        if (string.IsNullOrWhiteSpace(pickupPersonIdKey))
        {
            return (null, null);
        }

        if (!IdKeyHelper.TryDecode(pickupPersonIdKey, out int personId))
        {
            logger.LogWarning("Invalid pickup person IdKey: {IdKey}", pickupPersonIdKey);
            var errorResult = new PickupVerificationResultDto(
                IsAuthorized: false,
                AuthorizationLevel: null,
                AuthorizedPickupIdKey: null,
                Message: "Invalid pickup person",
                RequiresSupervisorOverride: true
            );
            return (null, errorResult);
        }

        return (personId, null);
    }

    private async Task<AuthorizedPickup?> FindAuthorizedPickupAsync(
        int childId,
        int? pickupPersonId,
        string? pickupPersonName,
        CancellationToken ct)
    {
        if (pickupPersonId.HasValue)
        {
            return await context.AuthorizedPickups
                .AsNoTracking()
                .Include(p => p.AuthorizedPerson)
                .FirstOrDefaultAsync(p =>
                    p.ChildPersonId == childId &&
                    p.IsActive &&
                    p.AuthorizedPersonId == pickupPersonId.Value,
                    ct);
        }

        // Kiosk path (#710): only a typed name arrives. It must match name-only rows AND
        // person-linked rows (Name is NULL; every auto-populated adult is one) by the linked
        // person's first or nick name plus last name. When several rows match, the most
        // restrictive level wins, so a person blocked with Never is never offered an override.
        var typed = NormalizePickupName(pickupPersonName);
        if (typed.Length == 0)
        {
            return null;
        }

        var candidates = await context.AuthorizedPickups
            .AsNoTracking()
            .Include(p => p.AuthorizedPerson)
            .Where(p => p.ChildPersonId == childId && p.IsActive)
            .ToListAsync(ct);

        var matches = candidates.Where(p => PickupNames(p).Contains(typed)).ToList();

        return matches.FirstOrDefault(p => p.AuthorizationLevel == Domain.Enums.AuthorizationLevel.Never)
            ?? matches.FirstOrDefault(p => p.AuthorizationLevel == Domain.Enums.AuthorizationLevel.EmergencyOnly)
            ?? matches.FirstOrDefault();
    }

    private static IEnumerable<string> PickupNames(AuthorizedPickup pickup)
    {
        if (!string.IsNullOrWhiteSpace(pickup.Name))
        {
            yield return NormalizePickupName(pickup.Name);
        }

        var person = pickup.AuthorizedPerson;
        if (person != null)
        {
            yield return NormalizePickupName($"{person.FirstName} {person.LastName}");
            if (!string.IsNullOrWhiteSpace(person.NickName))
            {
                yield return NormalizePickupName($"{person.NickName} {person.LastName}");
            }
        }
    }

    private static string NormalizePickupName(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private PickupVerificationResultDto CheckAuthorizationLevel(
        AuthorizedPickup? authorizedPickup,
        Person child,
        string? pickupPersonName)
    {
        if (authorizedPickup == null)
        {
            return CreateNotOnListResult(child.Id);
        }

        return authorizedPickup.AuthorizationLevel switch
        {
            Domain.Enums.AuthorizationLevel.Never => CreateNeverAuthorizedResult(authorizedPickup),
            Domain.Enums.AuthorizationLevel.EmergencyOnly => CreateEmergencyOnlyResult(authorizedPickup),
            Domain.Enums.AuthorizationLevel.Always => CreateAlwaysAuthorizedResult(authorizedPickup, child),
            _ => CreateUnknownAuthorizationResult()
        };
    }

    private PickupVerificationResultDto CreateNotOnListResult(int childId)
    {
        logger.LogInformation("Pickup person not on authorized list for child {ChildId}", childId);
        return new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: null,
            AuthorizedPickupIdKey: null,
            Message: "Person not on authorized pickup list. Supervisor approval required.",
            RequiresSupervisorOverride: true
        );
    }

    private PickupVerificationResultDto CreateNeverAuthorizedResult(AuthorizedPickup pickup)
    {
        return new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: Domain.Enums.AuthorizationLevel.Never,
            AuthorizedPickupIdKey: pickup.IdKey,
            Message: "This person is not authorized to pick up this child.",
            RequiresSupervisorOverride: false
        );
    }

    private PickupVerificationResultDto CreateEmergencyOnlyResult(AuthorizedPickup pickup)
    {
        return new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: Domain.Enums.AuthorizationLevel.EmergencyOnly,
            AuthorizedPickupIdKey: pickup.IdKey,
            Message: "Emergency-only authorization. Supervisor approval required.",
            RequiresSupervisorOverride: true
        );
    }

    private PickupVerificationResultDto CreateAlwaysAuthorizedResult(
        AuthorizedPickup pickup,
        Person child)
    {
        var personName = pickup.AuthorizedPerson?.FullName ?? pickup.Name;
        return new PickupVerificationResultDto(
            IsAuthorized: true,
            AuthorizationLevel: Domain.Enums.AuthorizationLevel.Always,
            AuthorizedPickupIdKey: pickup.IdKey,
            Message: $"{personName} is authorized to pick up {child.FullName}.",
            RequiresSupervisorOverride: false
        );
    }

    private PickupVerificationResultDto CreateUnknownAuthorizationResult()
    {
        return new PickupVerificationResultDto(
            IsAuthorized: false,
            AuthorizationLevel: null,
            AuthorizedPickupIdKey: null,
            Message: "Unknown authorization level",
            RequiresSupervisorOverride: true
        );
    }

    #endregion

    #region Private Helper Methods - Recording

    private Result ValidateRecordPickupRequest(RecordPickupRequest request)
    {
        // Validate supervisor override rules
        if (request.WasAuthorized && request.SupervisorOverride)
        {
            return Result.Failure(
                Error.Validation("SupervisorOverride must be false when WasAuthorized is true"));
        }

        // CRITICAL #4: Prevent unauthorized pickups without supervisor override
        if (!request.WasAuthorized && !request.SupervisorOverride)
        {
            return Result.Failure(
                Error.Validation("SupervisorOverride is required when WasAuthorized is false"));
        }

        return Result.Success();
    }

    private async Task<Attendance?> GetAttendanceForPickupAsync(
        int attendanceId,
        CancellationToken ct)
    {
        return await context.Attendances
            .AsTracking()
            .Include(a => a.PersonAlias)
            .ThenInclude(pa => pa!.Person)
            .Include(a => a.AttendanceCode)
            .FirstOrDefaultAsync(a => a.Id == attendanceId, ct);
    }

    private Result<int?> DecodePickupPersonIdKeyOrNull(string? pickupPersonIdKey)
    {
        if (string.IsNullOrWhiteSpace(pickupPersonIdKey))
        {
            return Result<int?>.Success(null);
        }

        if (!IdKeyHelper.TryDecode(pickupPersonIdKey, out int personId))
        {
            return Result<int?>.Failure(
                Error.Validation($"Invalid pickup person IdKey: {pickupPersonIdKey}"));
        }

        return Result<int?>.Success(personId);
    }

    private async Task<Result<int?>> ValidateAuthorizedPickupIdAsync(
        string? authorizedPickupIdKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizedPickupIdKey))
        {
            return Result<int?>.Success(null);
        }

        if (!IdKeyHelper.TryDecode(authorizedPickupIdKey, out int apId))
        {
            return Result<int?>.Failure(
                Error.Validation($"Invalid authorized pickup IdKey: {authorizedPickupIdKey}"));
        }

        // CRITICAL #1: Prevent recording pickup for "Never" authorization level
        var authorizedPickup = await context.AuthorizedPickups
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == apId, ct);

        if (authorizedPickup?.AuthorizationLevel == AuthorizationLevel.Never)
        {
            return Result<int?>.Failure(Error.UnprocessableEntity(
                "Cannot record pickup for a person with 'Never' authorization level. " +
                "This person is blocked from pickup and cannot be overridden."));
        }

        return Result<int?>.Success(apId);
    }

    private Result<int?> DecodeSupervisorPersonIdKey(string? supervisorPersonIdKey)
    {
        if (string.IsNullOrWhiteSpace(supervisorPersonIdKey))
        {
            return Result<int?>.Success(null);
        }

        if (!IdKeyHelper.TryDecode(supervisorPersonIdKey, out int supId))
        {
            return Result<int?>.Failure(
                Error.Validation($"Invalid supervisor person IdKey: {supervisorPersonIdKey}"));
        }

        return Result<int?>.Success(supId);
    }

    private Result ValidateSupervisorOverride(RecordPickupRequest request, int? supervisorPersonId)
    {
        if (request.SupervisorOverride && !supervisorPersonId.HasValue)
        {
            return Result.Failure(
                Error.Validation("SupervisorPersonIdKey is required when SupervisorOverride is true"));
        }

        return Result.Success();
    }

    private async Task<Result> ValidateAuthorizedPickupBlocking(
        RecordPickupRequest request,
        int childId,
        int? pickupPersonId,
        int? authorizedPickupId,
        CancellationToken ct)
    {
        // CRITICAL #1: Additional check - verify supervisor override isn't for a "Never" person
        if (!request.SupervisorOverride)
        {
            return Result.Success();
        }

        var blockedPickup = await context.AuthorizedPickups
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.ChildPersonId == childId &&
                p.AuthorizationLevel == AuthorizationLevel.Never &&
                p.IsActive &&
                (p.AuthorizedPersonId == pickupPersonId ||
                 (!p.AuthorizedPersonId.HasValue && p.Name == request.PickupPersonName)), ct);

        if (blockedPickup != null)
        {
            return Result.Failure(Error.UnprocessableEntity(
                "Cannot override pickup for a blocked person. This person is on the 'Never' list."));
        }

        return Result.Success();
    }

    private async Task<Result<AuthorizedPickup?>> ReverifyPickupClaimAsync(
        RecordPickupRequest request,
        Attendance attendance,
        int attendanceId,
        Person child,
        int? pickupPersonId,
        int? claimedAuthorizedPickupId,
        CancellationToken ct)
    {
        if (VerifySecurityCode(attendance, request.SecurityCode, attendanceId) != null)
        {
            return Result<AuthorizedPickup?>.Failure(Error.UnprocessableEntity(
                "The security code does not match this attendance; the pickup cannot be recorded."));
        }

        var authorizedPickup = await FindAuthorizedPickupAsync(
            child.Id,
            pickupPersonId,
            request.PickupPersonName,
            ct);
        var verification = CheckAuthorizationLevel(authorizedPickup, child, request.PickupPersonName);

        if (request.WasAuthorized && !verification.IsAuthorized)
        {
            return Result<AuthorizedPickup?>.Failure(Error.UnprocessableEntity(
                "The pickup person is not authorized for this child; the pickup cannot be recorded as authorized."));
        }

        if (request.SupervisorOverride && !verification.RequiresSupervisorOverride)
        {
            return Result<AuthorizedPickup?>.Failure(Error.UnprocessableEntity(
                "A supervisor override is not allowed for this pickup person."));
        }

        if (claimedAuthorizedPickupId.HasValue && claimedAuthorizedPickupId != authorizedPickup?.Id)
        {
            return Result<AuthorizedPickup?>.Failure(Error.UnprocessableEntity(
                "The authorized pickup does not match the server's verification for this child."));
        }

        return Result<AuthorizedPickup?>.Success(authorizedPickup);
    }

    private PickupLog CreatePickupLogEntry(
        RecordPickupRequest request,
        int attendanceId,
        int childId,
        int? pickupPersonId,
        int? authorizedPickupId,
        int? supervisorPersonId)
    {
        return new PickupLog
        {
            AttendanceId = attendanceId,
            ChildPersonId = childId,
            PickupPersonId = pickupPersonId,
            PickupPersonName = request.PickupPersonName,
            WasAuthorized = request.WasAuthorized,
            AuthorizedPickupId = authorizedPickupId,
            SupervisorOverride = request.SupervisorOverride,
            SupervisorPersonId = supervisorPersonId,
            CheckoutDateTime = DateTime.UtcNow,
            Notes = request.Notes
        };
    }

    private async Task<PickupLogDto> BuildPickupLogDtoAsync(
        int logId,
        string attendanceIdKey,
        int childId,
        CancellationToken ct)
    {
        var created = await context.PickupLogs
            .AsNoTracking()
            .Include(pl => pl.ChildPerson)
            .Include(pl => pl.PickupPerson)
            .Include(pl => pl.SupervisorPerson)
            .FirstAsync(pl => pl.Id == logId, ct);

        var pickupPersonName = created.PickupPerson?.FullName ?? created.PickupPersonName ?? "Unknown";

        logger.LogInformation(
            "Recorded pickup {PickupLogId} for child {ChildId} by {PickupPerson} (Authorized: {WasAuthorized}, Override: {SupervisorOverride})",
            logId,
            childId,
            pickupPersonName,
            created.WasAuthorized,
            created.SupervisorOverride);

        return new PickupLogDto(
            IdKey: created.IdKey,
            AttendanceIdKey: attendanceIdKey,
            ChildName: created.ChildPerson!.FullName,
            PickupPersonName: pickupPersonName,
            WasAuthorized: created.WasAuthorized,
            SupervisorOverride: created.SupervisorOverride,
            SupervisorName: created.SupervisorPerson?.FullName,
            CheckoutDateTime: created.CheckoutDateTime,
            Notes: created.Notes
        );
    }

    #endregion
}
