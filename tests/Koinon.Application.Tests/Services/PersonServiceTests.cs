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
/// Tests for PersonService.
/// </summary>
public class PersonServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreatePersonRequest> _createValidator;
    private readonly IValidator<UpdatePersonRequest> _updateValidator;
    private readonly Mock<ILogger<PersonService>> _mockLogger;
    private readonly PersonService _service;

    public PersonServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);

        // Manually initialize the context (bypassing EF configuration)
        _context.Database.EnsureCreated();

        // Setup AutoMapper
        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<PersonMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Setup validators
        _createValidator = new CreatePersonRequestValidator();
        _updateValidator = new UpdatePersonRequestValidator();

        // Setup logger mock
        _mockLogger = new Mock<ILogger<PersonService>>();

        // Create service
        _service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            Mock.Of<IUserContext>(),
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

        // Add defined values for statuses
        var recordStatusType = new DefinedType
        {
            Id = 1,
            Name = "Record Status",
            IsSystem = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.DefinedTypes.Add(recordStatusType);

        var activeStatus = new DefinedValue
        {
            Id = 1,
            DefinedTypeId = 1,
            Value = "Active",
            IsActive = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.DefinedValues.Add(activeStatus);

        // Add test people
        var person1 = new Person
        {
            Id = 1,
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            Gender = Gender.Male,
            IsEmailActive = true,
            RecordStatusValueId = 1,
            PrimaryCampusId = 1,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person1);

        var person2 = new Person
        {
            Id = 2,
            FirstName = "Jane",
            LastName = "Smith",
            NickName = "Janey",
            Email = "jane.smith@example.com",
            Gender = Gender.Female,
            BirthYear = 1990,
            BirthMonth = 5,
            BirthDay = 15,
            IsEmailActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.People.Add(person2);

        _context.SaveChanges();
    }

    [Fact]
    public async Task GetByIdAsync_WithValidId_ReturnsPerson()
    {
        // Act
        var result = await _service.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.FirstName.Should().Be("John");
        result.LastName.Should().Be("Doe");
        result.Email.Should().Be("john.doe@example.com");
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
    public async Task GetByIdKeyAsync_WithValidIdKey_ReturnsPerson()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Act
        var result = await _service.GetByIdKeyAsync(idKey);

        // Assert
        result.Should().NotBeNull();
        result!.FirstName.Should().Be("John");
        result.IdKey.Should().Be(idKey);
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithInvalidIdKey_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdKeyAsync("INVALID");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_WithoutQuery_ReturnsAllPeople()
    {
        // Arrange
        var parameters = new PersonSearchParameters
        {
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task SearchAsync_WithQuery_ReturnsFilteredResults()
    {
        // Arrange
        var parameters = new PersonSearchParameters
        {
            Query = "Jane",
            Page = 1,
            PageSize = 10
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].FirstName.Should().Be("Jane");
    }

    [Fact]
    public async Task SearchAsync_WithPagination_ReturnsCorrectPage()
    {
        // Arrange
        var parameters = new PersonSearchParameters
        {
            Page = 1,
            PageSize = 1
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(1);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
        result.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_CreatesPersonSuccessfully()
    {
        // Arrange
        var request = new CreatePersonRequest
        {
            FirstName = "Alice",
            LastName = "Johnson",
            Email = "alice.johnson@example.com",
            Gender = "Female",
            BirthDate = new DateOnly(1985, 3, 20)
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.FirstName.Should().Be("Alice");
        result.Value.LastName.Should().Be("Johnson");
        result.Value.Age.Should().BeGreaterThan(30);

        // Verify in database
        var person = await _context.People
            .FirstOrDefaultAsync(p => p.Email == "alice.johnson@example.com");
        person.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithPhoneNumbers_CreatesPersonWithPhones()
    {
        // Arrange
        var request = new CreatePersonRequest
        {
            FirstName = "Bob",
            LastName = "Williams",
            Email = "bob.williams@example.com",
            PhoneNumbers = new List<CreatePhoneNumberRequest>
            {
                new() { Number = "5551234567", IsMessagingEnabled = true },
                new() { Number = "5559876543", IsMessagingEnabled = false }
            }
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.PhoneNumbers.Should().HaveCount(2);

        // Verify in database
        var person = await _context.People
            .Include(p => p.PhoneNumbers)
            .FirstOrDefaultAsync(p => p.Email == "bob.williams@example.com");
        person!.PhoneNumbers.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidRequest_ReturnsValidationError()
    {
        // Arrange
        var request = new CreatePersonRequest
        {
            FirstName = "", // Empty first name - invalid
            LastName = "Test"
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Details.Should().ContainKey("FirstName");
    }

    [Fact]
    public async Task UpdateAsync_WithValidRequest_UpdatesPersonSuccessfully()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        var request = new UpdatePersonRequest
        {
            FirstName = "Jonathan",
            Email = "jonathan.doe@example.com"
        };

        // Act
        var result = await _service.UpdateAsync(idKey, request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.FirstName.Should().Be("Jonathan");
        result.Value.Email.Should().Be("jonathan.doe@example.com");
        result.Value.LastName.Should().Be("Doe"); // Unchanged

        // Verify in database
        var updatedPerson = await _context.People.FindAsync(1);
        updatedPerson!.FirstName.Should().Be("Jonathan");
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Arrange
        var request = new UpdatePersonRequest
        {
            FirstName = "Test"
        };

        // Act
        var result = await _service.UpdateAsync("INVALID", request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidEmail_ReturnsValidationError()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        var request = new UpdatePersonRequest
        {
            Email = "not-an-email"
        };

        // Act
        var result = await _service.UpdateAsync(idKey, request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Details.Should().ContainKey("Email");
    }

    [Fact]
    public async Task DeleteAsync_WithValidIdKey_SoftDeletesPerson()
    {
        // Arrange
        var person = await _context.People.FindAsync(2);
        var idKey = person!.IdKey;

        // Act
        var result = await _service.DeleteAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify soft delete
        var deletedPerson = await _context.People.FindAsync(2);
        deletedPerson!.IsDeceased.Should().BeTrue();
        deletedPerson.ModifiedDateTime.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.DeleteAsync("INVALID");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetFamilyAsync_WithNoFamily_ReturnsSuccessWithNull()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Act
        var result = await _service.GetFamilyAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_UsesProvidedPageSize()
    {
        // Arrange - PageSize validation is now handled by FluentValidation at API layer
        // Service trusts the validated input
        var parameters = new PersonSearchParameters
        {
            Page = 1,
            PageSize = 50
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert - service uses the provided page size
        result.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task CreateAsync_CalculatesAgeCorrectly()
    {
        // Arrange
        var birthDate = DateOnly.FromDateTime(DateTime.Today.AddYears(-25));
        var request = new CreatePersonRequest
        {
            FirstName = "Test",
            LastName = "Person",
            BirthDate = birthDate
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Age.Should().Be(25);
    }

    [Fact]
    public async Task UpdatePhotoAsync_UserUpdatingOwnPhoto_Succeeds()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Create a binary file (photo)
        var photo = new BinaryFile
        {
            Id = 1,
            FileName = "profile.jpg",
            MimeType = "image/jpeg",
            StorageKey = "photos/profile.jpg",
            FileSizeBytes = 12345,
            CreatedDateTime = DateTime.UtcNow
        };
        await _context.BinaryFiles.AddAsync(photo);
        await _context.SaveChangesAsync();

        var photoIdKey = photo.IdKey;

        // Mock user context to simulate user updating their own photo
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(1)).Returns(true);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, photoIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.PhotoUrl.Should().NotBeNull();

        // Verify in database
        var updatedPerson = await _context.People.FindAsync(1);
        updatedPerson!.PhotoId.Should().Be(1);
    }

    [Fact]
    public async Task UpdatePhotoAsync_AdminUpdatingOtherPersonPhoto_Succeeds()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Create a binary file (photo)
        var photo = new BinaryFile
        {
            Id = 2,
            FileName = "admin-uploaded.jpg",
            MimeType = "image/jpeg",
            StorageKey = "photos/admin-uploaded.jpg",
            FileSizeBytes = 23456,
            CreatedDateTime = DateTime.UtcNow
        };
        await _context.BinaryFiles.AddAsync(photo);
        await _context.SaveChangesAsync();

        var photoIdKey = photo.IdKey;

        // Mock user context to simulate admin updating another person's photo
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(99); // Different person ID
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(1)).Returns(true); // Admin can access
        mockUserContext.Setup(x => x.IsInRole("Admin")).Returns(true);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, photoIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.PhotoUrl.Should().NotBeNull();

        // Verify in database
        var updatedPerson = await _context.People.FindAsync(1);
        updatedPerson!.PhotoId.Should().Be(2);
    }

    [Fact]
    public async Task UpdatePhotoAsync_StaffUpdatingOtherPersonPhoto_Succeeds()
    {
        // Arrange
        var person = await _context.People.FindAsync(2);
        var idKey = person!.IdKey;

        // Create a binary file (photo)
        var photo = new BinaryFile
        {
            Id = 3,
            FileName = "staff-uploaded.jpg",
            MimeType = "image/jpeg",
            StorageKey = "photos/staff-uploaded.jpg",
            FileSizeBytes = 34567,
            CreatedDateTime = DateTime.UtcNow
        };
        await _context.BinaryFiles.AddAsync(photo);
        await _context.SaveChangesAsync();

        var photoIdKey = photo.IdKey;

        // Mock user context to simulate staff updating another person's photo
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(88); // Different person ID
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(2)).Returns(true); // Staff can access
        mockUserContext.Setup(x => x.IsInRole("Staff")).Returns(true);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, photoIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.PhotoUrl.Should().NotBeNull();

        // Verify in database
        var updatedPerson = await _context.People.FindAsync(2);
        updatedPerson!.PhotoId.Should().Be(3);
    }

    [Fact]
    public async Task UpdatePhotoAsync_RegularUserUpdatingOtherPersonPhoto_Fails()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Create a binary file (photo)
        var photo = new BinaryFile
        {
            Id = 4,
            FileName = "unauthorized.jpg",
            MimeType = "image/jpeg",
            StorageKey = "photos/unauthorized.jpg",
            FileSizeBytes = 45678,
            CreatedDateTime = DateTime.UtcNow
        };
        await _context.BinaryFiles.AddAsync(photo);
        await _context.SaveChangesAsync();

        var photoIdKey = photo.IdKey;

        // Mock user context to simulate regular user trying to update another person's photo
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(2); // Different person ID
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(1)).Returns(false); // No access
        mockUserContext.Setup(x => x.IsInRole("Admin")).Returns(false);
        mockUserContext.Setup(x => x.IsInRole("Staff")).Returns(false);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, photoIdKey);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("FORBIDDEN");
        result.Error.Message.Should().Contain("permission");

        // Verify photo was not updated in database
        var unchangedPerson = await _context.People.FindAsync(1);
        unchangedPerson!.PhotoId.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePhotoAsync_UnauthenticatedUser_Fails()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Mock user context to simulate unauthenticated user
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns((int?)null);
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(false);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, "photo-idkey");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("FORBIDDEN");
        result.Error.Message.Should().Contain("Authentication required");
    }

    [Fact]
    public async Task UpdatePhotoAsync_WithNullPhotoIdKey_RemovesPhoto()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        person!.PhotoId = 999; // Set existing photo
        await _context.SaveChangesAsync();

        var idKey = person.IdKey;

        // Mock user context
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(1)).Returns(true);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, null);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify photo was removed in database
        var updatedPerson = await _context.People.FindAsync(1);
        updatedPerson!.PhotoId.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePhotoAsync_WithNonexistentPhotoId_ReturnsNotFound()
    {
        // Arrange
        var person = await _context.People.FindAsync(1);
        var idKey = person!.IdKey;

        // Create a valid IdKey for a photo that doesn't exist in database
        var nonexistentPhotoIdKey = IdKeyHelper.Encode(9999);

        // Mock user context
        var mockUserContext = new Mock<IUserContext>();
        mockUserContext.Setup(x => x.CurrentPersonId).Returns(1);
        mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        mockUserContext.Setup(x => x.CanAccessPerson(1)).Returns(true);

        var service = new PersonService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            mockUserContext.Object,
            _mockLogger.Object
        );

        // Act
        var result = await service.UpdatePhotoAsync(idKey, nonexistentPhotoIdKey);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
        result.Error.Message.Should().Contain("Photo");
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
