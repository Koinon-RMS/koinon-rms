using AutoMapper;
using FluentAssertions;
using FluentValidation;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Application.Validators;
using Koinon.Domain.Entities;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Tests for DefinedTypeService.
/// </summary>
public class DefinedTypeServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreateDefinedValueRequest> _createValidator;
    private readonly IValidator<UpdateDefinedValueRequest> _updateValidator;
    private readonly Mock<ILogger<DefinedTypeService>> _mockLogger;
    private readonly DefinedTypeService _service;

    public DefinedTypeServiceTests()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"DefinedTypeTestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);
        _context.Database.EnsureCreated();

        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<DefinedTypeMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        _createValidator = new CreateDefinedValueRequestValidator();
        _updateValidator = new UpdateDefinedValueRequestValidator();
        _mockLogger = new Mock<ILogger<DefinedTypeService>>();

        _service = new DefinedTypeService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            _mockLogger.Object);

        SeedTestData();
    }

    private void SeedTestData()
    {
        var personType = new DefinedType
        {
            Id = 1,
            Name = "Record Status",
            Category = "Person",
            IsSystem = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow,
            // Seed Inactive (Order=2) BEFORE Active (Order=1) so insertion order ≠ expected order.
            // Ordering tests must fail if the service drops OrderBy(v => v.Order).
            DefinedValues =
            [
                new DefinedValue
                {
                    Id = 11,
                    DefinedTypeId = 1,
                    Value = "Inactive",
                    Order = 2,
                    IsActive = true,
                    CreatedDateTime = DateTime.UtcNow
                },
                new DefinedValue
                {
                    Id = 10,
                    DefinedTypeId = 1,
                    Value = "Active",
                    Order = 1,
                    IsActive = true,
                    CreatedDateTime = DateTime.UtcNow
                }
            ]
        };

        var communicationType = new DefinedType
        {
            Id = 2,
            Name = "Phone Type",
            Category = "Communication",
            IsSystem = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };

        _context.DefinedTypes.Add(personType);
        _context.DefinedTypes.Add(communicationType);
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task GetAllTypesAsync_ReturnsTypesOrderedByCategoryThenName()
    {
        // Act
        var result = await _service.GetAllTypesAsync();

        // Assert
        result.Should().HaveCount(2);

        // "Communication" sorts before "Person"
        result[0].Category.Should().Be("Communication");
        result[1].Category.Should().Be("Person");
    }

    [Fact]
    public async Task GetAllTypesAsync_IncludesValueCount()
    {
        // Act
        var result = await _service.GetAllTypesAsync();

        // Assert
        var personType = result.First(t => t.Name == "Record Status");
        personType.ValueCount.Should().Be(2);

        var communicationType = result.First(t => t.Name == "Phone Type");
        communicationType.ValueCount.Should().Be(0);
    }

    [Fact]
    public async Task GetTypeByIdKeyAsync_WithValidIdKey_ReturnsTypeWithValues()
    {
        // Arrange
        var type = await _context.DefinedTypes.FindAsync(1);
        var idKey = type!.IdKey;

        // Act
        var result = await _service.GetTypeByIdKeyAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Record Status");
        result.Value.DefinedValues.Should().HaveCount(2);
        result.Value.DefinedValues[0].Value.Should().Be("Active");
    }

    [Fact]
    public async Task GetTypeByIdKeyAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.GetTypeByIdKeyAsync("INVALID");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetValuesByTypeAsync_WithValidIdKey_ReturnsValuesInOrder()
    {
        // Arrange
        var type = await _context.DefinedTypes.FindAsync(1);
        var idKey = type!.IdKey;

        // Act
        var result = await _service.GetValuesByTypeAsync(idKey);

        // Assert — frontend-facing endpoint used to populate dropdowns; must succeed by IdKey.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value![0].Value.Should().Be("Active");
        result.Value[1].Value.Should().Be("Inactive");
    }

    [Fact]
    public async Task GetValuesByTypeAsync_WithValidGuid_ReturnsValues()
    {
        // Arrange — frontend calls this with a stable GUID from seed data, not the Hashid IdKey.
        var type = await _context.DefinedTypes.FindAsync(1);

        // Act
        var result = await _service.GetValuesByTypeAsync(type!.Guid.ToString());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetValuesByTypeAsync_WithUnknownGuid_ReturnsNotFound()
    {
        // Act
        var result = await _service.GetValuesByTypeAsync(Guid.NewGuid().ToString());

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GetValuesByTypeAsync_WithEmptyInput_ReturnsNotFound()
    {
        // Act
        var result = await _service.GetValuesByTypeAsync(string.Empty);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CreateValueAsync_WithValidRequest_CreatesValueWithAutoOrder()
    {
        // Arrange
        var type = await _context.DefinedTypes.FindAsync(1);
        var typeIdKey = type!.IdKey;

        var request = new CreateDefinedValueRequest
        {
            Value = "Pending",
            Description = "Pending review"
        };

        // Act — Order is 0, so service should assign max+1 = 3
        var result = await _service.CreateValueAsync(typeIdKey, request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("Pending");
        result.Value.Order.Should().Be(3);
        result.Value.IsActive.Should().BeTrue();

        var inDb = await _context.DefinedValues.FirstOrDefaultAsync(v => v.Value == "Pending");
        inDb.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateValueAsync_WithInvalidRequest_ReturnsValidationError()
    {
        // Arrange
        var type = await _context.DefinedTypes.FindAsync(1);
        var typeIdKey = type!.IdKey;

        var request = new CreateDefinedValueRequest
        {
            Value = "" // Empty value - invalid
        };

        // Act
        var result = await _service.CreateValueAsync(typeIdKey, request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Details.Should().ContainKey("Value");
    }

    [Fact]
    public async Task CreateValueAsync_WithUnknownTypeIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.CreateValueAsync("UNKNOWNKEY", new CreateDefinedValueRequest { Value = "Test" });

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteValueAsync_SetsIsActiveToFalse_DoesNotHardDelete()
    {
        // Arrange
        var value = await _context.DefinedValues.FindAsync(10);
        var valueIdKey = value!.IdKey;

        // Act
        var result = await _service.DeleteValueAsync(valueIdKey);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var inDb = await _context.DefinedValues.FindAsync(10);
        inDb.Should().NotBeNull("soft delete should not remove the row");
        inDb!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteValueAsync_WithUnknownIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.DeleteValueAsync("INVALID");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }
}
