using AutoMapper;
using FluentAssertions;
using FluentValidation;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Interfaces;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Application.Tests.Fakes;
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
/// Tests for FamilyService.
/// </summary>
public class FamilyServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IUserContext _userContext;
    private readonly IValidator<CreateFamilyRequest> _createFamilyValidator;
    private readonly IValidator<AddFamilyMemberRequest> _addMemberValidator;
    private readonly Mock<ILogger<FamilyService>> _mockLogger;
    private readonly FamilyService _service;

    private int _adultRoleId;
    private int _childRoleId;

    public FamilyServiceTests()
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
            cfg.AddProfile<PersonMappingProfile>();
            cfg.AddProfile<FamilyMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Setup validators
        _createFamilyValidator = new CreateFamilyRequestValidator();
        _addMemberValidator = new AddFamilyMemberRequestValidator();

        // Setup user context (default allows all access)
        _userContext = new FakeUserContext();

        // Setup logger mock
        _mockLogger = new Mock<ILogger<FamilyService>>();

        // Create service
        _service = new FamilyService(
            _context,
            _mapper,
            _userContext,
            _createFamilyValidator,
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

        // Add family group type and roles
        var familyGroupType = new GroupType
        {
            Id = 1,
            Name = "Family",
            Guid = SystemGuid.GroupType.Family,
            IsSystem = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypes.Add(familyGroupType);

        var adultRole = new GroupTypeRole
        {
            Id = 1,
            GroupTypeId = 1,
            Name = "Adult",
            Guid = SystemGuid.GroupTypeRole.FamilyAdult,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypeRoles.Add(adultRole);
        _adultRoleId = 1;

        var childRole = new GroupTypeRole
        {
            Id = 2,
            GroupTypeId = 1,
            Name = "Child",
            Guid = SystemGuid.GroupTypeRole.FamilyChild,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.GroupTypeRoles.Add(childRole);
        _childRoleId = 2;

        // Add test people
        var person1 = new Person
        {
            Id = 1,
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            Gender = Gender.Male,
            IsEmailActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person1);

        var person2 = new Person
        {
            Id = 2,
            FirstName = "Jane",
            LastName = "Doe",
            Email = "jane.doe@example.com",
            Gender = Gender.Female,
            IsEmailActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person2);

        var person3 = new Person
        {
            Id = 3,
            FirstName = "Jimmy",
            LastName = "Doe",
            Email = "jimmy.doe@example.com",
            Gender = Gender.Male,
            BirthYear = 2015,
            BirthMonth = 3,
            BirthDay = 15,
            IsEmailActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person3);

        // Add test family
        var testFamily = new Family
        {
            Id = 1,
            Name = "Doe Family",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Families.Add(testFamily);

        // Add family members
        var member1 = new FamilyMember
        {
            Id = 1,
            FamilyId = 1,
            PersonId = 1,
            FamilyRoleId = 1,
            IsPrimary = false,
            DateAdded = DateTime.UtcNow,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.FamilyMembers.Add(member1);

        var member2 = new FamilyMember
        {
            Id = 2,
            FamilyId = 1,
            PersonId = 2,
            FamilyRoleId = 1,
            IsPrimary = false,
            DateAdded = DateTime.UtcNow,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.FamilyMembers.Add(member2);

        _context.SaveChanges();
    }

    [Fact]
    public async Task GetByIdAsync_WithValidId_ReturnsFamily()
    {
        // Act
        var result = await _service.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Doe Family");
        result.Members.Should().HaveCount(2);
        result.Members.Should().Contain(m => m.Person.FirstName == "John");
        result.Members.Should().Contain(m => m.Person.FirstName == "Jane");
    }

    [Fact]
    public async Task GetByIdAsync_WithInvalidId_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithValidIdKey_ReturnsFamily()
    {
        // Arrange
        var idKey = Domain.Data.IdKeyHelper.Encode(1);

        // Act
        var result = await _service.GetByIdKeyAsync(idKey);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Doe Family");
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithInvalidIdKey_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdKeyAsync("invalid");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateFamilyAsync_WithValidRequest_CreatesFamily()
    {
        // Arrange
        var request = new CreateFamilyRequest
        {
            Name = "Smith Family",
            Description = "The Smith household"
        };

        // Act
        var result = await _service.CreateFamilyAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("Smith Family");
        result.Value.Description.Should().BeNull(); // Family entity doesn't support Description

        // Verify in database
        var family = await _context.Families
            .FirstOrDefaultAsync(f => f.Name == "Smith Family");
        family.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateFamilyAsync_WithAddress_CreatesFamilyWithAddress()
    {
        // Arrange
        var campusIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var request = new CreateFamilyRequest
        {
            Name = "Jones Family",
            CampusId = campusIdKey,
            Address = new CreateFamilyAddressRequest
            {
                Street1 = "123 Main St",
                City = "Springfield",
                State = "IL",
                PostalCode = "62701"
            }
        };

        // Act
        var result = await _service.CreateFamilyAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("Jones Family");

        // Verify campus is set
        var family = await _context.Families
            .FirstOrDefaultAsync(f => f.Name == "Jones Family");
        family.Should().NotBeNull();
        family!.CampusId.Should().Be(1);

        // Verify location was created
        var location = await _context.Locations
            .FirstOrDefaultAsync(l => l.Street1 == "123 Main St");
        location.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateFamilyAsync_WithInvalidName_ReturnsValidationError()
    {
        // Arrange
        var request = new CreateFamilyRequest
        {
            Name = "" // Empty name
        };

        // Act
        var result = await _service.CreateFamilyAsync(request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task AddFamilyMemberAsync_WithValidRequest_AddsMember()
    {
        // Arrange
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var personIdKey = Domain.Data.IdKeyHelper.Encode(3); // Jimmy
        var roleIdKey = Domain.Data.IdKeyHelper.Encode(_childRoleId);

        var request = new AddFamilyMemberRequest
        {
            PersonId = personIdKey,
            RoleId = roleIdKey
        };

        // Act
        var result = await _service.AddFamilyMemberAsync(familyIdKey, request);

        // Assert
        if (!result.IsSuccess)
        {
            throw new Exception($"Expected success but got error: {result.Error?.Code} - {result.Error?.Message}");
        }
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Person.FirstName.Should().Be("Jimmy");
        result.Value.Role.Name.Should().Be("Child");

        // Verify in database
        var member = await _context.FamilyMembers
            .FirstOrDefaultAsync(fm => fm.FamilyId == 1 && fm.PersonId == 3);
        member.Should().NotBeNull();
    }

    [Fact]
    public async Task AddFamilyMemberAsync_WhenPersonAlreadyMember_ReturnsConflictError()
    {
        // Arrange
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var personIdKey = Domain.Data.IdKeyHelper.Encode(1); // John (already a member)
        var roleIdKey = Domain.Data.IdKeyHelper.Encode(_adultRoleId);

        var request = new AddFamilyMemberRequest
        {
            PersonId = personIdKey,
            RoleId = roleIdKey
        };

        // Act
        var result = await _service.AddFamilyMemberAsync(familyIdKey, request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task AddFamilyMemberAsync_WithInvalidFamily_ReturnsNotFoundError()
    {
        // Arrange
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(999);
        var personIdKey = Domain.Data.IdKeyHelper.Encode(3);
        var roleIdKey = Domain.Data.IdKeyHelper.Encode(_childRoleId);

        var request = new AddFamilyMemberRequest
        {
            PersonId = personIdKey,
            RoleId = roleIdKey
        };

        // Act
        var result = await _service.AddFamilyMemberAsync(familyIdKey, request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task RemoveFamilyMemberAsync_WithValidIds_RemovesMember()
    {
        // Arrange
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var personIdKey = Domain.Data.IdKeyHelper.Encode(2); // Jane

        // Act
        var result = await _service.RemoveFamilyMemberAsync(familyIdKey, personIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify in database - should be deleted (FamilyMember uses hard delete)
        var member = await _context.FamilyMembers
            .FirstOrDefaultAsync(fm => fm.FamilyId == 1 && fm.PersonId == 2);
        member.Should().BeNull();
    }

    [Fact]
    public async Task RemoveFamilyMemberAsync_WithInvalidPerson_ReturnsNotFoundError()
    {
        // Arrange
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var personIdKey = Domain.Data.IdKeyHelper.Encode(999);

        // Act
        var result = await _service.RemoveFamilyMemberAsync(familyIdKey, personIdKey);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task SetPrimaryFamilyAsync_WithValidIds_SetsPrimaryFamily()
    {
        // Arrange
        var personIdKey = Domain.Data.IdKeyHelper.Encode(1); // John
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);

        // Act
        var result = await _service.SetPrimaryFamilyAsync(personIdKey, familyIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var person = await _context.People.FindAsync(1);
        person.Should().NotBeNull();
    }

    [Fact]
    public async Task SetPrimaryFamilyAsync_WhenPersonNotMember_ReturnsUnprocessableEntityError()
    {
        // Arrange
        var personIdKey = Domain.Data.IdKeyHelper.Encode(3); // Jimmy (not in family yet)
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);

        // Act
        var result = await _service.SetPrimaryFamilyAsync(personIdKey, familyIdKey);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("UNPROCESSABLE_ENTITY");
    }

    [Fact]
    public async Task SetPrimaryFamilyAsync_WithInvalidPerson_ReturnsNotFoundError()
    {
        // Arrange
        var personIdKey = Domain.Data.IdKeyHelper.Encode(999);
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);

        // Act
        var result = await _service.SetPrimaryFamilyAsync(personIdKey, familyIdKey);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task UpdateAddressAsync_ReturnsNotImplementedError()
    {
        // Arrange - Address management is not yet implemented
        var familyIdKey = Domain.Data.IdKeyHelper.Encode(1);
        var request = new UpdateFamilyAddressRequest
        {
            Street1 = "456 Oak Ave",
            City = "Portland",
            State = "OR",
            PostalCode = "97201"
        };

        // Act
        var result = await _service.UpdateAddressAsync(familyIdKey, request);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NOT_IMPLEMENTED");
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
