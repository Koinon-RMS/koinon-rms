using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Koinon.Application.Services;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

public class AuthorizedPickupServiceTests : IDisposable
{
    private readonly TestDbContext _context;
    private readonly Mock<ILogger<AuthorizedPickupService>> _mockLogger;
    private readonly AuthorizedPickupService _service;

    public AuthorizedPickupServiceTests()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TestDbContext(options);
        _mockLogger = new Mock<ILogger<AuthorizedPickupService>>();

        _service = new AuthorizedPickupService(
            _context,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Authorization Level Tests

    [Fact]
    public async Task VerifyPickupAsync_WhenAlwaysAuthorized_ReturnsAuthorized()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: attendance.AttendanceCode!.Code!
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.True(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.Always, result.AuthorizationLevel);
        Assert.False(result.RequiresSupervisorOverride);
        Assert.NotNull(result.AuthorizedPickupIdKey);
    }

    [Fact]
    public async Task VerifyPickupAsync_WhenEmergencyOnly_RequiresSupervisorOverride()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Grandparent,
            AuthorizationLevel = AuthorizationLevel.EmergencyOnly,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: attendance.AttendanceCode!.Code!
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.EmergencyOnly, result.AuthorizationLevel);
        Assert.True(result.RequiresSupervisorOverride);
        Assert.Contains("Emergency-only", result.Message);
    }

    [Fact]
    public async Task VerifyPickupAsync_WhenNeverAuthorized_ReturnsNotAuthorized()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: attendance.AttendanceCode!.Code!
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.Never, result.AuthorizationLevel);
        Assert.False(result.RequiresSupervisorOverride);
        Assert.Contains("not authorized", result.Message);
    }

    [Fact]
    public async Task VerifyPickupAsync_ByTypedName_MatchesPersonLinkedAlwaysPickup()
    {
        // Arrange: a person-linked row (Name NULL), as auto-populate creates for every adult
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        // Act: the kiosk sends only a typed name, in any case and spacing
        var result = await _service.VerifyPickupAsync(new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "  testparent   SMITH ",
            SecurityCode: attendance.AttendanceCode!.Code!));

        // Assert
        Assert.True(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.Always, result.AuthorizationLevel);
        Assert.False(result.RequiresSupervisorOverride);
    }

    [Fact]
    public async Task VerifyPickupAsync_ByTypedName_PersonLinkedNeverIsHardDeny()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.VerifyPickupAsync(new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "TestParent Smith",
            SecurityCode: attendance.AttendanceCode!.Code!));

        // Assert: the blocked person gets the hard deny, never the override path
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.Never, result.AuthorizationLevel);
        Assert.False(result.RequiresSupervisorOverride);
    }

    [Fact]
    public async Task VerifyPickupAsync_ByTypedName_NeverWinsOverAnotherMatchingRow()
    {
        // Arrange: a name-only Always row and a person-linked Never row for the same name
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            Name = "TestParent Smith",
            Relationship = PickupRelationship.Friend,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        });
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.VerifyPickupAsync(new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "TestParent Smith",
            SecurityCode: attendance.AttendanceCode!.Code!));

        // Assert: the most restrictive level decides
        Assert.False(result.IsAuthorized);
        Assert.Equal(AuthorizationLevel.Never, result.AuthorizationLevel);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenNeverAuthorizationLevel_ReturnsUnprocessable()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: false,
            AuthorizedPickupIdKey: authorizedPickup.IdKey,
            SupervisorOverride: true,
            SupervisorPersonIdKey: authorizedPerson.IdKey,
            Notes: null
        );

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.Contains("Never", result.Error.Message);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenNeverLevelWithSupervisorOverride_StillRefused()
    {
        // Arrange
        var (child, blockedPerson, attendance) = await SetupTestDataAsync();
        var supervisor = await CreatePersonAsync("Supervisor", "Smith");

        // Create a blocked person (Never level)
        var blockedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = blockedPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(blockedPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: blockedPerson.IdKey,
            PickupPersonName: blockedPerson.FullName,
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: supervisor.IdKey,
            Notes: "Attempting to override blocked person"
        );

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.Contains("blocked", result.Error.Message);
    }

    #endregion

    #region Supervisor Override Tests

    [Fact]
    public async Task RecordPickupAsync_WhenUnauthorizedWithoutOverride_ReturnsValidationError()
    {
        // Arrange
        var (child, unauthorizedPerson, attendance) = await SetupTestDataAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: unauthorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: false, // No override
            SupervisorPersonIdKey: null,
            Notes: null
        );

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
        Assert.Contains("SupervisorOverride is required", result.Error.Message);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenUnauthorizedWithOverride_Succeeds()
    {
        // Arrange
        var (child, unauthorizedPerson, attendance) = await SetupTestDataAsync();
        var supervisor = await CreatePersonAsync("Supervisor", "Johnson");

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: unauthorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: supervisor.IdKey,
            Notes: "Supervisor approved emergency pickup"
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        var recorded = result.Value!;
        Assert.False(recorded.WasAuthorized);
        Assert.True(recorded.SupervisorOverride);
        Assert.NotNull(recorded.SupervisorName);

        // Verify attendance was updated
        var updatedAttendance = await _context.Attendances.FindAsync(attendance.Id);
        Assert.NotNull(updatedAttendance?.EndDateTime);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenOverrideWithoutSupervisorId_ReturnsValidationError()
    {
        // Arrange
        var (child, unauthorizedPerson, attendance) = await SetupTestDataAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: unauthorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: null, // Missing supervisor ID
            Notes: null
        );

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
        Assert.Contains("SupervisorPersonIdKey is required", result.Error.Message);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenAuthorizedWithOverride_ReturnsValidationError()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        var supervisor = await CreatePersonAsync("Supervisor", "Brown");

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: true,
            AuthorizedPickupIdKey: authorizedPickup.IdKey,
            SupervisorOverride: true, // Should not be true when authorized
            SupervisorPersonIdKey: supervisor.IdKey,
            Notes: null
        );

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
        Assert.Contains("SupervisorOverride must be false when WasAuthorized is true", result.Error.Message);
    }

    #endregion

    #region Security Code Tests

    [Fact]
    public async Task VerifyPickupAsync_WithValidSecurityCode_ReturnsSuccess()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: attendance.AttendanceCode!.Code!
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.True(result.IsAuthorized);
    }

    [Fact]
    public async Task VerifyPickupAsync_WithInvalidSecurityCode_ReturnsNotAuthorized()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: "WRONG-CODE"
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.False(result.IsAuthorized);
        Assert.Contains("Invalid security code", result.Message);
    }

    [Fact]
    public async Task VerifyPickupAsync_WithMissingSecurityCode_ReturnsNotAuthorized()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: ""
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.False(result.IsAuthorized);
        Assert.Contains("Invalid security code", result.Message);
    }

    #endregion

    #region Auto-populate Tests

    [Fact]
    public async Task AutoPopulateFamilyMembersAsync_CreatesAuthorizedPickups()
    {
        // Arrange
        var groupType = new GroupType
        {
            Name = "Family",
            Guid = SystemGuid.GroupType.Family,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypes.Add(groupType);

        _context.SaveChanges();

        var adultRole = new GroupTypeRole
        {
            GroupTypeId = groupType.Id,
            Name = "Adult",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypeRoles.Add(adultRole);

        var childRole = new GroupTypeRole
        {
            GroupTypeId = groupType.Id,
            Name = "Child",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypeRoles.Add(childRole);
        _context.SaveChanges();

        var family = new Family
        {
            Name = "Smith Family",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Families.Add(family);
        _context.SaveChanges();

        var child = await CreatePersonAsync("Tommy", "Smith");

        var parent1 = await CreatePersonAsync("John", "Smith");
        var parent2 = await CreatePersonAsync("Jane", "Smith");

        var childMember = new FamilyMember
        {
            FamilyId = family.Id,
            PersonId = child.Id,
            FamilyRoleId = childRole.Id,
            IsPrimary = false,
            DateAdded = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.FamilyMembers.Add(childMember);

        var parentMember1 = new FamilyMember
        {
            FamilyId = family.Id,
            PersonId = parent1.Id,
            FamilyRoleId = adultRole.Id,
            IsPrimary = true,
            DateAdded = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.FamilyMembers.Add(parentMember1);

        var parentMember2 = new FamilyMember
        {
            FamilyId = family.Id,
            PersonId = parent2.Id,
            FamilyRoleId = adultRole.Id,
            IsPrimary = false,
            DateAdded = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.FamilyMembers.Add(parentMember2);

        await _context.SaveChangesAsync();

        // Act
        await _service.AutoPopulateFamilyMembersAsync(child.IdKey);

        // Assert
        var authorizedPickups = await _context.AuthorizedPickups
            .Where(ap => ap.ChildPersonId == child.Id && ap.IsActive)
            .ToListAsync();

        Assert.Equal(2, authorizedPickups.Count);
        Assert.All(authorizedPickups, ap =>
        {
            Assert.Equal(PickupRelationship.Parent, ap.Relationship);
            Assert.Equal(AuthorizationLevel.Always, ap.AuthorizationLevel);
        });
    }

    [Fact]
    public async Task AutoPopulateFamilyMembersAsync_DoesNotDuplicateExisting()
    {
        // Arrange
        var groupType = new GroupType
        {
            Name = "Family",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypes.Add(groupType);

        _context.SaveChanges();

        var adultRole = new GroupTypeRole
        {
            GroupTypeId = groupType.Id,
            Name = "Adult",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypeRoles.Add(adultRole);
        _context.SaveChanges();

        var family = new Group
        {
            GroupTypeId = groupType.Id,
            Name = "Johnson Family",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Groups.Add(family);
        _context.SaveChanges();

        var child = await CreatePersonAsync("Sarah", "Johnson");

        var parent = await CreatePersonAsync("Bob", "Johnson");

        var parentMember = new GroupMember
        {
            GroupId = family.Id,
            PersonId = parent.Id,
            GroupRoleId = adultRole.Id,
            GroupMemberStatus = GroupMemberStatus.Active,
            IsArchived = false,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMembers.Add(parentMember);

        // Create an existing authorized pickup for the parent
        var existingPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = parent.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(existingPickup);

        await _context.SaveChangesAsync();

        // Act
        await _service.AutoPopulateFamilyMembersAsync(child.IdKey);

        // Assert
        var authorizedPickups = await _context.AuthorizedPickups
            .Where(ap => ap.ChildPersonId == child.Id && ap.IsActive)
            .ToListAsync();

        // Should still be just one (not duplicated)
        Assert.Single(authorizedPickups);
    }

    #endregion

    #region CRUD Tests

    [Fact]
    public async Task GetAuthorizedPickupsAsync_ReturnsOnlyActivePickups()
    {
        // Arrange
        var child = await CreatePersonAsync("Emily", "Davis");
        var parent = await CreatePersonAsync("Mike", "Davis");

        var activePickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = parent.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(activePickup);

        var inactivePickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            Name = "Revoked Person",
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Never,
            IsActive = false // Inactive
        };
        _context.AuthorizedPickups.Add(inactivePickup);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAuthorizedPickupsAsync(child.IdKey);

        // Assert
        Assert.Single(result);
        Assert.True(result[0].IsActive);
        Assert.Equal(parent.FullName, result[0].AuthorizedPersonName);
    }

    [Fact]
    public async Task DeleteAuthorizedPickupAsync_SoftDeletes()
    {
        // Arrange
        var child = await CreatePersonAsync("Alex", "Martinez");
        var pickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            Name = "Test Person",
            Relationship = PickupRelationship.Friend,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(pickup);
        await _context.SaveChangesAsync();

        // Act
        await _service.DeleteAuthorizedPickupAsync(pickup.IdKey);

        // Assert
        var deletedPickup = await _context.AuthorizedPickups.FindAsync(pickup.Id);
        Assert.NotNull(deletedPickup);
        Assert.False(deletedPickup.IsActive);
        Assert.NotNull(deletedPickup.ModifiedDateTime);

        // Verify it doesn't show up in GetAuthorizedPickups
        var activePickups = await _service.GetAuthorizedPickupsAsync(child.IdKey);
        Assert.Empty(activePickups);
    }

    #endregion

    #region Additional Edge Case Tests

    [Fact]
    public async Task VerifyPickupAsync_WhenPersonNotOnList_RequiresSupervisorOverride()
    {
        // Arrange
        var (child, unauthorizedPerson, attendance) = await SetupTestDataAsync();

        var request = new VerifyPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: unauthorizedPerson.IdKey,
            PickupPersonName: null,
            SecurityCode: attendance.AttendanceCode!.Code!
        );

        // Act
        var result = await _service.VerifyPickupAsync(request);

        // Assert
        Assert.False(result.IsAuthorized);
        Assert.True(result.RequiresSupervisorOverride);
        Assert.Contains("not on authorized pickup list", result.Message);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenAuthorized_Succeeds()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: true,
            AuthorizedPickupIdKey: authorizedPickup.IdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: "Normal pickup"
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        var recorded = result.Value!;
        Assert.True(recorded.WasAuthorized);
        Assert.False(recorded.SupervisorOverride);
        Assert.Null(recorded.SupervisorName);

        // Verify pickup log was created
        var pickupLog = await _context.PickupLogs
            .FirstOrDefaultAsync(pl => pl.AttendanceId == attendance.Id);
        Assert.NotNull(pickupLog);
        Assert.Equal(authorizedPerson.Id, pickupLog.PickupPersonId);
    }

    [Fact]
    public async Task RecordPickupAsync_WhenNoTrackingDefault_PersistsEndDateTime()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        // Mirror the production default (PostgreSqlProvider.UseQueryTrackingBehavior)
        _context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: true,
            AuthorizedPickupIdKey: authorizedPickup.IdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: "Normal pickup"
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        await _service.RecordPickupAsync(request);

        // Assert — re-read from the store with a fresh no-tracking query so we
        // observe what was persisted, not a cached change-tracker instance
        var storedAttendance = await _context.Attendances
            .AsNoTracking()
            .FirstAsync(a => a.Id == attendance.Id);
        Assert.NotNull(storedAttendance.EndDateTime);
    }

    [Fact]
    public async Task RecordPickupAsync_WithWrongSecurityCode_RefusesAndRecordsNothing()
    {
        // Arrange: an Always-authorized guardian, but the code typed at the kiosk is wrong
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        var authorizedPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(authorizedPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: true,
            AuthorizedPickupIdKey: authorizedPickup.IdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null
        )
        {
            SecurityCode = "WRONG1"
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.False(await _context.PickupLogs.AnyAsync(pl => pl.AttendanceId == attendance.Id));
        var storedAttendance = await _context.Attendances
            .AsNoTracking()
            .FirstAsync(a => a.Id == attendance.Id);
        Assert.Null(storedAttendance.EndDateTime);
    }

    [Fact]
    public async Task RecordPickupAsync_ClaimingAuthorizedForUnlistedPerson_RefusesAndRecordsNothing()
    {
        // Arrange: nobody is on the child's list; a client skips verify-pickup and claims authorized
        var (_, _, attendance) = await SetupTestDataAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: null,
            PickupPersonName: "Anyone At All",
            WasAuthorized: true,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.False(await _context.PickupLogs.AnyAsync(pl => pl.AttendanceId == attendance.Id));
    }

    [Fact]
    public async Task RecordPickupAsync_OverrideForAnAlwaysAuthorizedPerson_RefusesAndRecordsNothing()
    {
        // Arrange: the server's verification authorizes this guardian outright, so an override is not allowed
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        var supervisor = await CreatePersonAsync("Supervisor", "Green");
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: false,
            AuthorizedPickupIdKey: null,
            SupervisorOverride: true,
            SupervisorPersonIdKey: supervisor.IdKey,
            Notes: null
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.Contains("override is not allowed", result.Error.Message);
        Assert.False(await _context.PickupLogs.AnyAsync(pl => pl.AttendanceId == attendance.Id));
    }

    [Fact]
    public async Task RecordPickupAsync_ClaimingAnotherPersonsAuthorizedPickup_RefusesAndRecordsNothing()
    {
        // Arrange: the client names one guardian but claims the authorized pickup row of another
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();
        var otherPerson = await CreatePersonAsync("Other", "Guardian");
        _context.AuthorizedPickups.Add(new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = authorizedPerson.Id,
            Relationship = PickupRelationship.Parent,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        });
        var otherPickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            AuthorizedPersonId = otherPerson.Id,
            Relationship = PickupRelationship.Other,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(otherPickup);
        await _context.SaveChangesAsync();

        var request = new RecordPickupRequest(
            AttendanceIdKey: attendance.IdKey,
            PickupPersonIdKey: authorizedPerson.IdKey,
            PickupPersonName: null,
            WasAuthorized: true,
            AuthorizedPickupIdKey: otherPickup.IdKey,
            SupervisorOverride: false,
            SupervisorPersonIdKey: null,
            Notes: null
        )
        {
            SecurityCode = attendance.AttendanceCode!.Code
        };

        // Act
        var result = await _service.RecordPickupAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal("UNPROCESSABLE_ENTITY", result.Error!.Code);
        Assert.Contains("does not match", result.Error.Message);
        Assert.False(await _context.PickupLogs.AnyAsync(pl => pl.AttendanceId == attendance.Id));
    }

    [Fact]
    public async Task AddAuthorizedPickupAsync_WithValidData_CreatesPickup()
    {
        // Arrange
        var child = await CreatePersonAsync("Jack", "Wilson");
        var authorizedPerson = await CreatePersonAsync("Uncle", "Wilson");

        var request = new CreateAuthorizedPickupRequest(
            AuthorizedPersonIdKey: authorizedPerson.IdKey,
            Name: null,
            PhoneNumber: null,
            Relationship: PickupRelationship.Uncle,
            AuthorizationLevel: AuthorizationLevel.Always,
            PhotoUrl: "https://example.com/photo.jpg",
            CustodyNotes: null
        );

        // Act
        var result = await _service.AddAuthorizedPickupAsync(child.IdKey, request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(child.IdKey, result.ChildIdKey);
        Assert.Equal(authorizedPerson.IdKey, result.AuthorizedPersonIdKey);
        Assert.Equal(PickupRelationship.Uncle, result.Relationship);
        Assert.Equal(AuthorizationLevel.Always, result.AuthorizationLevel);
    }

    [Fact]
    public async Task UpdateAuthorizedPickupAsync_UpdatesFields()
    {
        // Arrange
        var child = await CreatePersonAsync("Lucy", "Taylor");
        var pickup = new AuthorizedPickup
        {
            ChildPersonId = child.Id,
            Name = "Test Person",
            Relationship = PickupRelationship.Friend,
            AuthorizationLevel = AuthorizationLevel.Always,
            IsActive = true
        };
        _context.AuthorizedPickups.Add(pickup);
        await _context.SaveChangesAsync();

        var request = new UpdateAuthorizedPickupRequest(
            Relationship: PickupRelationship.Guardian,
            AuthorizationLevel: AuthorizationLevel.EmergencyOnly,
            PhotoUrl: "https://example.com/new-photo.jpg",
            CustodyNotes: "Updated notes",
            IsActive: true
        );

        // Act
        var result = await _service.UpdateAuthorizedPickupAsync(pickup.IdKey, request);

        // Assert
        Assert.Equal(PickupRelationship.Guardian, result.Relationship);
        Assert.Equal(AuthorizationLevel.EmergencyOnly, result.AuthorizationLevel);
    }

    [Fact]
    public async Task GetPickupHistoryAsync_ReturnsLogsForChild()
    {
        // Arrange
        var (child, authorizedPerson, attendance) = await SetupTestDataAsync();

        var pickupLog = new PickupLog
        {
            AttendanceId = attendance.Id,
            ChildPersonId = child.Id,
            PickupPersonId = authorizedPerson.Id,
            WasAuthorized = true,
            SupervisorOverride = false,
            CheckoutDateTime = DateTime.UtcNow
        };
        _context.PickupLogs.Add(pickupLog);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPickupHistoryAsync(child.IdKey, null, null);

        // Assert
        Assert.Single(result);
        Assert.Equal(child.FullName, result[0].ChildName);
        Assert.True(result[0].WasAuthorized);
    }

    #endregion

    #region Helper Methods

    private async Task<Person> CreatePersonAsync(string firstName, string lastName)
    {
        var person = new Person
        {
            FirstName = firstName,
            LastName = lastName,
            Gender = Gender.Unknown,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person);
        await _context.SaveChangesAsync();
        return person;
    }

    private async Task<(Person child, Person authorizedPerson, Attendance attendance)> SetupTestDataAsync()
    {
        var child = await CreatePersonAsync("TestChild", "Smith");
        var authorizedPerson = await CreatePersonAsync("TestParent", "Smith");

        var personAlias = new PersonAlias
        {
            PersonId = child.Id,
            AliasPersonId = child.Id,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.PersonAliases.Add(personAlias);

        var attendanceCode = new AttendanceCode
        {
            Code = "ABC123",
            IssueDateTime = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.AttendanceCodes.Add(attendanceCode);

        var groupType = new GroupType
        {
            Name = "Check-in Group",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypes.Add(groupType);
        await _context.SaveChangesAsync();

        var group = new Group
        {
            GroupTypeId = groupType.Id,
            Name = "Test Group",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Groups.Add(group);

        var schedule = new Schedule
        {
            Name = "Test Schedule",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);

        var location = new Location
        {
            Name = "Test Location",
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Locations.Add(location);
        await _context.SaveChangesAsync();

        var today = DateTime.UtcNow;
        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            LocationId = location.Id,
            ScheduleId = schedule.Id,
            OccurrenceDate = DateOnly.FromDateTime(today),
            SundayDate = DateOnly.FromDateTime(today.AddDays(-(int)today.DayOfWeek)),
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.AttendanceOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();

        var attendance = new Attendance
        {
            PersonAliasId = personAlias.Id,
            OccurrenceId = occurrence.Id,
            DidAttend = true,
            StartDateTime = DateTime.UtcNow,
            AttendanceCodeId = attendanceCode.Id,
            Guid = Guid.NewGuid(),
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Attendances.Add(attendance);

        await _context.SaveChangesAsync();

        return (child, authorizedPerson, attendance);
    }

    #endregion

    // Test DbContext for in-memory testing
    private class TestDbContext : DbContext, IApplicationDbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

        public DbSet<Person> People { get; set; } = null!;
        public DbSet<PersonAlias> PersonAliases { get; set; } = null!;
        public DbSet<PhoneNumber> PhoneNumbers { get; set; } = null!;
        public DbSet<PersonNote> PersonNotes { get; set; } = null!;
        public DbSet<Group> Groups { get; set; } = null!;
        public DbSet<GroupType> GroupTypes { get; set; } = null!;
        public DbSet<GroupTypeRole> GroupTypeRoles { get; set; } = null!;
        public DbSet<GroupMember> GroupMembers { get; set; } = null!;
        public DbSet<GroupMemberRequest> GroupMemberRequests { get; set; } = null!;
        public DbSet<FamilyMember> FamilyMembers { get; set; } = null!;
        public DbSet<Family> Families { get; set; } = null!;
        public DbSet<GroupSchedule> GroupSchedules { get; set; } = null!;
        public DbSet<Campus> Campuses { get; set; } = null!;
        public DbSet<Location> Locations { get; set; } = null!;
        public DbSet<DefinedType> DefinedTypes { get; set; } = null!;
        public DbSet<DefinedValue> DefinedValues { get; set; } = null!;
        public DbSet<Schedule> Schedules { get; set; } = null!;
        public DbSet<Attendance> Attendances { get; set; } = null!;
        public DbSet<AttendanceOccurrence> AttendanceOccurrences { get; set; } = null!;
        public DbSet<AttendanceCode> AttendanceCodes { get; set; } = null!;
        public DbSet<Device> Devices { get; set; } = null!;
        public DbSet<LabelTemplate> LabelTemplates { get; set; } = null!;
        public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
        public DbSet<UserPreference> UserPreferences { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<TwoFactorConfig> TwoFactorConfigs { get; set; } = null!;
        public DbSet<SupervisorSession> SupervisorSessions { get; set; } = null!;
        public DbSet<SupervisorAuditLog> SupervisorAuditLogs { get; set; } = null!;
        public DbSet<FollowUp> FollowUps { get; set; } = null!;
        public DbSet<PagerAssignment> PagerAssignments { get; set; } = null!;
        public DbSet<PagerMessage> PagerMessages { get; set; } = null!;
        public DbSet<AuthorizedPickup> AuthorizedPickups { get; set; } = null!;
        public DbSet<PickupLog> PickupLogs { get; set; } = null!;
        public DbSet<Communication> Communications { get; set; } = null!;
        public DbSet<CommunicationRecipient> CommunicationRecipients { get; set; } = null!;
        public DbSet<CommunicationTemplate> CommunicationTemplates { get; set; } = null!;
        public DbSet<CommunicationPreference> CommunicationPreferences { get; set; } = null!;
        public DbSet<BinaryFile> BinaryFiles { get; set; } = null!;
        public DbSet<ImportTemplate> ImportTemplates { get; set; } = null!;
        public DbSet<ImportJob> ImportJobs { get; set; } = null!;
        public DbSet<ReportDefinition> ReportDefinitions { get; set; } = null!;
        public DbSet<ReportRun> ReportRuns { get; set; } = null!;
        public DbSet<ReportSchedule> ReportSchedules { get; set; } = null!;
        public DbSet<ExportJob> ExportJobs { get; set; } = null!;
        public DbSet<Fund> Funds { get; set; } = null!;
        public DbSet<ContributionBatch> ContributionBatches { get; set; } = null!;
        public DbSet<Contribution> Contributions { get; set; } = null!;
        public DbSet<ContributionDetail> ContributionDetails { get; set; } = null!;
        public DbSet<ContributionStatement> ContributionStatements { get; set; } = null!;
        public DbSet<FinancialAuditLog> FinancialAuditLogs { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;

        // Person merge and duplicate tracking
        public DbSet<PersonMergeHistory> PersonMergeHistories { get; set; } = null!;
        public DbSet<PersonDuplicateIgnore> PersonDuplicateIgnores { get; set; } = null!;

        // In-app notifications
        public DbSet<Notification> Notifications { get; set; } = null!;
        public DbSet<NotificationPreference> NotificationPreferences { get; set; } = null!;

        // Security
        public DbSet<SecurityRole> SecurityRoles { get; set; } = null!;
        public DbSet<SecurityClaim> SecurityClaims { get; set; } = null!;
        public DbSet<PersonSecurityRole> PersonSecurityRoles { get; set; } = null!;
        public DbSet<RoleSecurityClaim> RoleSecurityClaims { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Location self-referential relationships
            modelBuilder.Entity<Location>()
                .HasOne(l => l.ParentLocation)
                .WithMany(l => l.ChildLocations)
                .HasForeignKey(l => l.ParentLocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Location>()
                .HasOne(l => l.OverflowLocation)
                .WithMany()
                .HasForeignKey(l => l.OverflowLocationId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
