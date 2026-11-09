using AutoMapper;
using FluentAssertions;
using FluentValidation;
using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Application.Validators;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Tests for GroupMemberRequestService.
/// </summary>
public class GroupMemberRequestServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<SubmitMembershipRequestDto> _submitValidator;
    private readonly IValidator<ProcessMembershipRequestDto> _processValidator;
    private readonly Mock<IUserContext> _mockUserContext;
    private readonly Mock<ILogger<GroupMemberRequestService>> _mockLogger;
    private readonly GroupMemberRequestService _service;

    public GroupMemberRequestServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);
        _context.Database.EnsureCreated();

        // Setup AutoMapper
        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<GroupMappingProfile>();
            cfg.AddProfile<PersonMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Setup validators
        _submitValidator = new SubmitMembershipRequestDtoValidator();
        _processValidator = new ProcessMembershipRequestDtoValidator();

        // Setup mocks
        _mockUserContext = new Mock<IUserContext>();
        _mockLogger = new Mock<ILogger<GroupMemberRequestService>>();

        // Create service
        _service = new GroupMemberRequestService(
            _context,
            _mockUserContext.Object,
            _mapper,
            _submitValidator,
            _processValidator,
            _mockLogger.Object
        );

        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        // Add group type
        var groupType = new GroupType
        {
            Id = 1,
            Name = "Small Group",
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypes.Add(groupType);

        // Add group type roles
        var leaderRole = new GroupTypeRole
        {
            Id = 1,
            GroupTypeId = 1,
            Name = "Leader",
            IsLeader = true,
            CreatedDateTime = DateTime.UtcNow
        };

        var memberRole = new GroupTypeRole
        {
            Id = 2,
            GroupTypeId = 1,
            Name = "Member",
            IsLeader = false,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.GroupTypeRoles.AddRange(leaderRole, memberRole);

        // Add test group
        var group = new Group
        {
            Id = 1,
            Name = "Bible Study Group",
            GroupTypeId = 1,
            IsActive = true,
            IsArchived = false,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Groups.Add(group);

        // Add test people
        var requester = new Person
        {
            Id = 1,
            FirstName = "Jane",
            LastName = "Requester",
            Gender = Gender.Female,
            EmailPreference = EmailPreference.EmailAllowed,
            CreatedDateTime = DateTime.UtcNow
        };

        var leader = new Person
        {
            Id = 2,
            FirstName = "John",
            LastName = "Leader",
            Gender = Gender.Male,
            EmailPreference = EmailPreference.EmailAllowed,
            CreatedDateTime = DateTime.UtcNow
        };

        var processor = new Person
        {
            Id = 3,
            FirstName = "Admin",
            LastName = "User",
            Gender = Gender.Male,
            EmailPreference = EmailPreference.EmailAllowed,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.People.AddRange(requester, leader, processor);

        // Add leader as group member
        var groupMember = new GroupMember
        {
            Id = 1,
            GroupId = 1,
            PersonId = 2,
            GroupRoleId = 1,
            GroupMemberStatus = GroupMemberStatus.Active,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMembers.Add(groupMember);

        _context.SaveChanges();
    }

    [Fact]
    public async Task SubmitRequestAsync_WithValidRequest_ShouldCreateRequest()
    {
        // Arrange
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;
        var request = new SubmitMembershipRequestDto
        {
            Note = "I would like to join this Bible study group."
        };

        // Act
        var result = await _service.SubmitRequestAsync(groupIdKey, request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be("Pending");
        result.Value.RequestNote.Should().Be(request.Note);
        result.Value.Requester.Should().NotBeNull();
        result.Value.Group.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitRequestAsync_WhenNotAuthenticated_ShouldReturnForbidden()
    {
        // Arrange
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns((int?)null);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(false);

        var groupIdKey = _context.Groups.First().IdKey;
        var request = new SubmitMembershipRequestDto();

        // Act
        var result = await _service.SubmitRequestAsync(groupIdKey, request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task SubmitRequestAsync_WhenAlreadyMember_ShouldReturnConflict()
    {
        // Arrange - Person ID 2 is already a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;
        var request = new SubmitMembershipRequestDto();

        // Act
        var result = await _service.SubmitRequestAsync(groupIdKey, request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("CONFLICT");
        result.Error.Message.Should().Contain("already a member");
    }

    [Fact]
    public async Task SubmitRequestAsync_WhenPendingRequestExists_ShouldReturnConflict()
    {
        // Arrange
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;
        var request = new SubmitMembershipRequestDto();

        // Create first request
        await _service.SubmitRequestAsync(groupIdKey, request);

        // Act - Try to create another request
        var result = await _service.SubmitRequestAsync(groupIdKey, request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("CONFLICT");
        result.Error.Message.Should().Contain("pending request");
    }

    [Fact]
    public async Task SubmitRequestAsync_WithInvalidGroupIdKey_ShouldReturnNotFound()
    {
        // Arrange
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var request = new SubmitMembershipRequestDto();

        // Act
        var result = await _service.SubmitRequestAsync("invalid-key", request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetPendingRequestsAsync_AsLeader_ShouldReturnRequests()
    {
        // Arrange - Person 2 is a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create a pending request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Pending,
            RequestNote = "Test request",
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPendingRequestsAsync(groupIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Count.Should().Be(1);
        result.Value[0].Status.Should().Be("Pending");
    }

    [Fact]
    public async Task GetPendingRequestsAsync_AsStaff_ShouldReturnRequests()
    {
        // Arrange - Person 3 is staff (not a group leader)
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(3);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        _mockUserContext.Setup(x => x.IsInRole("Staff")).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create a pending request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Pending,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPendingRequestsAsync(groupIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetPendingRequestsAsync_AsNonLeader_ShouldReturnForbidden()
    {
        // Arrange - Person 1 is not a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        _mockUserContext.Setup(x => x.IsInRole("Staff")).Returns(false);
        _mockUserContext.Setup(x => x.IsInRole("Admin")).Returns(false);

        var groupIdKey = _context.Groups.First().IdKey;

        // Act
        var result = await _service.GetPendingRequestsAsync(groupIdKey);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task ProcessRequestAsync_AsLeader_Approve_ShouldCreateGroupMember()
    {
        // Arrange - Person 2 is a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create a pending request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Pending,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        var requestIdKey = memberRequest.IdKey;
        var processRequest = new ProcessMembershipRequestDto
        {
            Status = "Approved",
            Note = "Welcome to the group!"
        };

        // Act
        var result = await _service.ProcessRequestAsync(groupIdKey, requestIdKey, processRequest);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be("Approved");
        result.Value.ResponseNote.Should().Be("Welcome to the group!");
        result.Value.ProcessedByPerson.Should().NotBeNull();
        result.Value.ProcessedDateTime.Should().NotBeNull();

        // Verify group member was created
        var groupMember = await _context.GroupMembers
            .FirstOrDefaultAsync(gm => gm.PersonId == 1 && gm.GroupId == 1);
        groupMember.Should().NotBeNull();
        groupMember!.GroupMemberStatus.Should().Be(GroupMemberStatus.Active);
    }

    [Fact]
    public async Task ProcessRequestAsync_AsLeader_Deny_ShouldNotCreateGroupMember()
    {
        // Arrange - Person 2 is a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create a pending request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Pending,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        var requestIdKey = memberRequest.IdKey;
        var processRequest = new ProcessMembershipRequestDto
        {
            Status = "Denied",
            Note = "Group is full"
        };

        // Act
        var result = await _service.ProcessRequestAsync(groupIdKey, requestIdKey, processRequest);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Status.Should().Be("Denied");
        result.Value.ResponseNote.Should().Be("Group is full");

        // Verify no group member was created
        var groupMember = await _context.GroupMembers
            .FirstOrDefaultAsync(gm => gm.PersonId == 1 && gm.GroupId == 1);
        groupMember.Should().BeNull();
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenAlreadyProcessed_ShouldReturnConflict()
    {
        // Arrange - Person 2 is a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create an already-approved request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Approved,
            ProcessedByPersonId = 2,
            ProcessedDateTime = DateTime.UtcNow,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        var requestIdKey = memberRequest.IdKey;
        var processRequest = new ProcessMembershipRequestDto
        {
            Status = "Denied"
        };

        // Act
        var result = await _service.ProcessRequestAsync(groupIdKey, requestIdKey, processRequest);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("CONFLICT");
        result.Error.Message.Should().Contain("already been processed");
    }

    [Fact]
    public async Task ProcessRequestAsync_AsNonLeader_ShouldReturnForbidden()
    {
        // Arrange - Person 1 is not a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        _mockUserContext.Setup(x => x.IsInRole("Staff")).Returns(false);
        _mockUserContext.Setup(x => x.IsInRole("Admin")).Returns(false);

        var groupIdKey = _context.Groups.First().IdKey;

        // Create a pending request
        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 3,
            Status = GroupMemberRequestStatus.Pending,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        var requestIdKey = memberRequest.IdKey;
        var processRequest = new ProcessMembershipRequestDto
        {
            Status = "Approved"
        };

        // Act
        var result = await _service.ProcessRequestAsync(groupIdKey, requestIdKey, processRequest);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task ProcessRequestAsync_WithInvalidStatus_ShouldReturnValidationError()
    {
        // Arrange - Person 2 is a leader
        _mockUserContext.Setup(x => x.CurrentPersonId).Returns(2);
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);

        var groupIdKey = _context.Groups.First().IdKey;

        var memberRequest = new GroupMemberRequest
        {
            GroupId = 1,
            PersonId = 1,
            Status = GroupMemberRequestStatus.Pending,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupMemberRequests.Add(memberRequest);
        await _context.SaveChangesAsync();

        var requestIdKey = memberRequest.IdKey;
        var processRequest = new ProcessMembershipRequestDto
        {
            Status = "InvalidStatus"
        };

        // Act
        var result = await _service.ProcessRequestAsync(groupIdKey, requestIdKey, processRequest);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
