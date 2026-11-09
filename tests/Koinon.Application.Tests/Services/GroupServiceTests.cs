using AutoMapper;
using FluentAssertions;
using FluentValidation;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Application.Validators;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Tests for GroupService.
/// </summary>
public class GroupServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreateGroupRequest> _createValidator;
    private readonly IValidator<UpdateGroupRequest> _updateValidator;
    private readonly IValidator<AddGroupMemberRequest> _addMemberValidator;
    private readonly Mock<ILogger<GroupService>> _mockLogger;
    private readonly GroupService _service;

    public GroupServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);

        // Manually initialize the context
        _context.Database.EnsureCreated();

        // Setup AutoMapper
        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<GroupMappingProfile>();
            cfg.AddProfile<PersonMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Setup validators
        _createValidator = new CreateGroupRequestValidator();
        _updateValidator = new UpdateGroupRequestValidator();
        _addMemberValidator = new AddGroupMemberRequestValidator();

        // Setup logger mock
        _mockLogger = new Mock<ILogger<GroupService>>();

        // Create service
        _service = new GroupService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            _addMemberValidator,
            _mockLogger.Object
        );

        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        // Add campuses
        var campus1 = new Campus
        {
            Id = 1,
            Name = "Main Campus",
            ShortCode = "MAIN",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Campuses.Add(campus1);

        // Add group types
        var servingGroupType = new GroupType
        {
            Id = 1,
            Name = "Serving Team",
            AllowMultipleLocations = false,
            IsSystem = false,
            CreatedDateTime = DateTime.UtcNow
        };

        var familyGroupType = new GroupType
        {
            Id = 2,
            Name = "Family",
            Guid = SystemGuid.GroupType.Family,
            AllowMultipleLocations = false,
            IsSystem = true,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.GroupTypes.Add(servingGroupType);
        _context.GroupTypes.Add(familyGroupType);

        // Add roles for serving group type
        var leaderRole = new GroupTypeRole
        {
            Id = 1,
            GroupTypeId = 1,
            Name = "Leader",
            IsLeader = true,
            Order = 0,
            CreatedDateTime = DateTime.UtcNow
        };

        var memberRole = new GroupTypeRole
        {
            Id = 2,
            GroupTypeId = 1,
            Name = "Member",
            IsLeader = false,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.GroupTypeRoles.AddRange(leaderRole, memberRole);

        // Add test people
        var person1 = new Person
        {
            Id = 1,
            FirstName = "John",
            LastName = "Doe",
            Gender = Gender.Male,
            IsEmailActive = true,
            EmailPreference = EmailPreference.EmailAllowed,
            IsDeceased = false,
            CreatedDateTime = DateTime.UtcNow
        };

        var person2 = new Person
        {
            Id = 2,
            FirstName = "Jane",
            LastName = "Smith",
            Gender = Gender.Female,
            IsEmailActive = true,
            EmailPreference = EmailPreference.EmailAllowed,
            IsDeceased = false,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.People.AddRange(person1, person2);

        // Add test groups
        var group1 = new Group
        {
            Id = 1,
            GroupTypeId = 1,
            CampusId = 1,
            Name = "Worship Team",
            Description = "Sunday worship team",
            IsActive = true,
            IsArchived = false,
            IsSecurityRole = false,
            IsPublic = true,
            AllowGuests = false,
            Order = 0,
            CreatedDateTime = DateTime.UtcNow
        };

        var group2 = new Group
        {
            Id = 2,
            GroupTypeId = 1,
            ParentGroupId = 1,
            Name = "Audio Team",
            Description = "Audio/visual team",
            IsActive = true,
            IsArchived = false,
            IsSecurityRole = false,
            IsPublic = false,
            AllowGuests = false,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };

        // Family group (should be excluded from GroupService)
        var familyGroup = new Group
        {
            Id = 3,
            GroupTypeId = 2,
            Name = "Doe Family",
            IsActive = true,
            IsArchived = false,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.Groups.AddRange(group1, group2, familyGroup);

        // Add group members
        var member1 = new GroupMember
        {
            Id = 1,
            GroupId = 1,
            PersonId = 1,
            GroupRoleId = 1,
            GroupMemberStatus = GroupMemberStatus.Active,
            DateTimeAdded = DateTime.UtcNow,
            CreatedDateTime = DateTime.UtcNow
        };

        var member2 = new GroupMember
        {
            Id = 2,
            GroupId = 2,
            PersonId = 2,
            GroupRoleId = 2,
            GroupMemberStatus = GroupMemberStatus.Active,
            DateTimeAdded = DateTime.UtcNow,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.GroupMembers.AddRange(member1, member2);

        _context.SaveChanges();
    }

    [Fact]
    public async Task GetByIdAsync_ExistingGroup_ReturnsGroup()
    {
        // Act
        var result = await _service.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Worship Team");
        result.Description.Should().Be("Sunday worship team");
        result.IsActive.Should().BeTrue();
        result.GroupType.Name.Should().Be("Serving Team");
        result.Campus.Should().NotBeNull();
        result.Campus!.Name.Should().Be("Main Campus");
        result.Members.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByIdAsync_FamilyGroup_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdAsync(3);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdKeyAsync_ValidIdKey_ReturnsGroup()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var idKey = group!.IdKey;

        // Act
        var result = await _service.GetByIdKeyAsync(idKey);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Worship Team");
    }

    [Fact]
    public async Task GetByIdKeyAsync_InvalidIdKey_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdKeyAsync("invalid");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_NoFilters_ReturnsAllNonFamilyGroups()
    {
        // Arrange
        var parameters = new GroupSearchParameters
        {
            Page = 1,
            PageSize = 50
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(2); // Excludes family group
        result.Items.Should().HaveCount(2);
        result.Items.Should().NotContain(g => g.Name == "Doe Family");
    }

    [Fact]
    public async Task SearchAsync_WithQuery_ReturnsMatchingGroups()
    {
        // Arrange
        var parameters = new GroupSearchParameters
        {
            Query = "Worship",
            Page = 1,
            PageSize = 50
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(1);
        result.Items.First().Name.Should().Be("Worship Team");
    }

    [Fact]
    public async Task SearchAsync_FilterByCampus_ReturnsMatchingGroups()
    {
        // Arrange
        var campus = await _context.Campuses.FindAsync(1);
        var parameters = new GroupSearchParameters
        {
            CampusId = campus!.IdKey,
            Page = 1,
            PageSize = 50
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(1);
        result.Items.First().Name.Should().Be("Worship Team");
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesGroup()
    {
        // Arrange
        var groupType = await _context.GroupTypes.FindAsync(1);
        var request = new CreateGroupRequest
        {
            Name = "Youth Team",
            Description = "Youth ministry team",
            GroupTypeId = groupType!.IdKey,
            IsActive = true,
            IsPublic = true
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("Youth Team");
        result.Value.Description.Should().Be("Youth ministry team");
        result.Value.IsActive.Should().BeTrue();
        result.Value.IsPublic.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_FamilyGroupType_ReturnsError()
    {
        // Arrange
        var familyGroupType = await _context.GroupTypes.FirstAsync(gt => gt.Guid == SystemGuid.GroupType.Family);
        var request = new CreateGroupRequest
        {
            Name = "Test Family",
            GroupTypeId = familyGroupType.IdKey
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Message.Should().Contain("FamilyService");
    }

    [Fact]
    public async Task CreateAsync_InvalidGroupTypeId_ReturnsError()
    {
        // Arrange
        var request = new CreateGroupRequest
        {
            Name = "Test Group",
            GroupTypeId = "invalid"
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CreateAsync_WithParentGroup_CreatesGroupWithParent()
    {
        // Arrange
        var groupType = await _context.GroupTypes.FindAsync(1);
        var parentGroup = await _context.Groups.FindAsync(1);
        var request = new CreateGroupRequest
        {
            Name = "Child Team",
            GroupTypeId = groupType!.IdKey,
            ParentGroupId = parentGroup!.IdKey
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Value!.ParentGroup.Should().NotBeNull();
        result.Value.ParentGroup!.Name.Should().Be("Worship Team");
    }

    [Fact]
    public async Task UpdateAsync_ValidRequest_UpdatesGroup()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var request = new UpdateGroupRequest
        {
            Name = "Updated Worship Team",
            Description = "Updated description",
            IsActive = false
        };

        // Act
        var result = await _service.UpdateAsync(group!.IdKey, request);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Updated Worship Team");
        result.Value.Description.Should().Be("Updated description");
        result.Value.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_NonExistentGroup_ReturnsError()
    {
        // Arrange
        var request = new UpdateGroupRequest
        {
            Name = "Updated Name"
        };

        // Act
        var result = await _service.UpdateAsync("invalid", request);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteAsync_ExistingGroup_ArchivesGroup()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);

        // Act
        var result = await _service.DeleteAsync(group!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify group is archived
        var archivedGroup = await _context.Groups.FindAsync(1);
        archivedGroup!.IsArchived.Should().BeTrue();
        archivedGroup.ArchivedDateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_SystemGroup_ReturnsError()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        group!.IsSystem = true;
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteAsync(group.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Message.Should().Contain("system-protected");
    }

    [Fact]
    public async Task AddMemberAsync_ValidRequest_AddsMember()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var person = await _context.People.FindAsync(2);
        var role = await _context.GroupTypeRoles.FindAsync(2);
        var request = new AddGroupMemberRequest
        {
            PersonId = person!.IdKey,
            RoleId = role!.IdKey,
            Note = "Test note"
        };

        // Act
        var result = await _service.AddMemberAsync(group!.IdKey, request);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Person.FullName.Should().Be(person.FullName);
        result.Value.Role.Name.Should().Be("Member");
        result.Value.Note.Should().Be("Test note");
    }

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_ReturnsError()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var person = await _context.People.FindAsync(1);
        var role = await _context.GroupTypeRoles.FindAsync(1);
        var request = new AddGroupMemberRequest
        {
            PersonId = person!.IdKey,
            RoleId = role!.IdKey
        };

        // Act
        var result = await _service.AddMemberAsync(group!.IdKey, request);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task AddMemberAsync_InvalidRole_ReturnsError()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var person = await _context.People.FindAsync(2);
        var request = new AddGroupMemberRequest
        {
            PersonId = person!.IdKey,
            RoleId = "invalid"
        };

        // Act
        var result = await _service.AddMemberAsync(group!.IdKey, request);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task RemoveMemberAsync_ExistingMember_RemovesMember()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var person = await _context.People.FindAsync(1);

        // Act
        var result = await _service.RemoveMemberAsync(group!.IdKey, person!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify member is inactive
        var member = await _context.GroupMembers
            .FirstAsync(gm => gm.GroupId == 1 && gm.PersonId == 1);
        member.GroupMemberStatus.Should().Be(GroupMemberStatus.Inactive);
        member.InactiveDateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task GetMembersAsync_ExistingGroup_ReturnsMembers()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);

        // Act
        var result = await _service.GetMembersAsync(group!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result.First().Person.FullName.Should().Be("John Doe");
    }

    [Fact]
    public async Task GetParentGroupAsync_GroupWithParent_ReturnsParent()
    {
        // Arrange
        var childGroup = await _context.Groups.FindAsync(2);

        // Act
        var result = await _service.GetParentGroupAsync(childGroup!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Worship Team");
    }

    [Fact]
    public async Task GetChildGroupsAsync_GroupWithChildren_ReturnsChildren()
    {
        // Arrange
        var parentGroup = await _context.Groups.FindAsync(1);

        // Act
        var result = await _service.GetChildGroupsAsync(parentGroup!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result.First().Name.Should().Be("Audio Team");
    }

    [Fact]
    public async Task GetChildGroupsAsync_GroupWithoutChildren_ReturnsEmpty()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(2);

        // Act
        var result = await _service.GetChildGroupsAsync(group!.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAttendanceHistoryAsync_ReturnsOccurrencesForGroup()
    {
        // Arrange — create an occurrence for group 1 with two attendees (one attended, one did not)
        var group = await _context.Groups.FindAsync(1);
        var occurrence = new AttendanceOccurrence
        {
            Id = 100,
            GroupId = group!.Id,
            OccurrenceDate = new DateOnly(2024, 1, 14),
            SundayDate = new DateOnly(2024, 1, 14),
            AnonymousAttendanceCount = 3,
            DidNotOccur = false
        };
        _context.AttendanceOccurrences.Add(occurrence);

        var personAlias1 = new PersonAlias { Id = 10, PersonId = 1 };
        var personAlias2 = new PersonAlias { Id = 11, PersonId = 2 };
        _context.PersonAliases.AddRange(personAlias1, personAlias2);

        _context.Attendances.Add(new Attendance
        {
            Id = 200,
            OccurrenceId = 100,
            PersonAliasId = 10,
            StartDateTime = new DateTime(2024, 1, 14, 9, 0, 0, DateTimeKind.Utc),
            DidAttend = true,
            CreatedDateTime = DateTime.UtcNow
        });
        _context.Attendances.Add(new Attendance
        {
            Id = 201,
            OccurrenceId = 100,
            PersonAliasId = 11,
            StartDateTime = new DateTime(2024, 1, 14, 9, 0, 0, DateTimeKind.Utc),
            DidAttend = false,
            CreatedDateTime = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAttendanceHistoryAsync(group.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.OccurrenceDate.Should().Be(new DateOnly(2024, 1, 14));
        dto.AttendeeCount.Should().Be(1); // Only DidAttend == true
        dto.AnonymousCount.Should().Be(3);
        dto.DidNotOccur.Should().BeFalse();
    }

    [Fact]
    public async Task GetAttendanceDetailAsync_ReturnsAttendancesForOccurrence()
    {
        // Arrange
        var group = await _context.Groups.FindAsync(1);
        var occurrence = new AttendanceOccurrence
        {
            Id = 101,
            GroupId = group!.Id,
            OccurrenceDate = new DateOnly(2024, 2, 11),
            SundayDate = new DateOnly(2024, 2, 11)
        };
        _context.AttendanceOccurrences.Add(occurrence);

        var personAlias = new PersonAlias { Id = 20, PersonId = 1 };
        _context.PersonAliases.Add(personAlias);

        var presentTime = new DateTime(2024, 2, 11, 9, 5, 0, DateTimeKind.Utc);
        _context.Attendances.Add(new Attendance
        {
            Id = 300,
            OccurrenceId = 101,
            PersonAliasId = 20,
            StartDateTime = new DateTime(2024, 2, 11, 9, 0, 0, DateTimeKind.Utc),
            DidAttend = true,
            PresentDateTime = presentTime,
            CreatedDateTime = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAttendanceDetailAsync(group.IdKey, occurrence.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        var detail = result.Value![0];
        detail.FullName.Should().Be("John Doe");
        detail.DidAttend.Should().BeTrue();
        detail.PresentDateTime.Should().Be(presentTime);
    }

    [Fact]
    public async Task GetAttendanceDetailAsync_ReturnsNotFound_WhenOccurrenceNotInGroup()
    {
        // Arrange — occurrence belongs to group 2, but we query against group 1
        var group1 = await _context.Groups.FindAsync(1);
        var occurrence = new AttendanceOccurrence
        {
            Id = 102,
            GroupId = 2, // Belongs to group 2
            OccurrenceDate = new DateOnly(2024, 3, 10),
            SundayDate = new DateOnly(2024, 3, 10)
        };
        _context.AttendanceOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAttendanceDetailAsync(group1!.IdKey, occurrence.IdKey);

        // Assert
        result.Should().NotBeNull();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
