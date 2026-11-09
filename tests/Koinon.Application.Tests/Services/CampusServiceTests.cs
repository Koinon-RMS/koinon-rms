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
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Tests for CampusService.
/// </summary>
public class CampusServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreateCampusRequest> _createValidator;
    private readonly IValidator<UpdateCampusRequest> _updateValidator;
    private readonly Mock<ILogger<CampusService>> _mockLogger;
    private readonly CampusService _service;

    public CampusServiceTests()
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
            cfg.AddProfile<CampusMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Setup validators
        _createValidator = new CreateCampusRequestValidator();
        _updateValidator = new UpdateCampusRequestValidator();

        // Setup logger mock
        _mockLogger = new Mock<ILogger<CampusService>>();

        // Create service
        _service = new CampusService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            _mockLogger.Object
        );

        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        var campus1 = new Campus
        {
            Id = 1,
            Name = "Main Campus",
            ShortCode = "MAIN",
            IsActive = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Campuses.Add(campus1);

        var campus2 = new Campus
        {
            Id = 2,
            Name = "North Campus",
            ShortCode = "NORTH",
            IsActive = false,
            Order = 2,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Campuses.Add(campus2);

        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAllAsync_WhenExcludeInactive_ReturnsOnlyActiveCampuses()
    {
        // Act
        var result = await _service.GetAllAsync(includeInactive: false);

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Main Campus");
    }

    [Fact]
    public async Task GetAllAsync_WhenIncludeInactive_ReturnsAllCampuses()
    {
        // Act
        var result = await _service.GetAllAsync(includeInactive: true);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithValidIdKey_ReturnsCampus()
    {
        // Arrange
        var campus = await _context.Campuses.FindAsync(1);
        var idKey = campus!.IdKey;

        // Act
        var result = await _service.GetByIdKeyAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("Main Campus");
        result.Value.IdKey.Should().Be(idKey);
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.GetByIdKeyAsync("INVALID");

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_CreatesCampusSuccessfully()
    {
        // Arrange
        var request = new CreateCampusRequest
        {
            Name = "South Campus",
            ShortCode = "SOUTH",
            Order = 3
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("South Campus");
        result.Value.IsActive.Should().BeTrue();

        // Verify in database
        var campus = await _context.Campuses
            .FirstOrDefaultAsync(c => c.Name == "South Campus");
        campus.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithInvalidRequest_ReturnsValidationError()
    {
        // Arrange
        var request = new CreateCampusRequest
        {
            Name = "" // Empty name - invalid
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("VALIDATION_ERROR");
        result.Error.Details.Should().ContainKey("Name");
    }

    [Fact]
    public async Task UpdateAsync_WithValidRequest_UpdatesCampusSuccessfully()
    {
        // Arrange
        var campus = await _context.Campuses.FindAsync(1);
        var idKey = campus!.IdKey;

        var request = new UpdateCampusRequest
        {
            Name = "Main Campus Updated",
            ShortCode = "M-UPD"
        };

        // Act
        var result = await _service.UpdateAsync(idKey, request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Main Campus Updated");
        result.Value.ShortCode.Should().Be("M-UPD");

        // Verify in database
        var updatedCampus = await _context.Campuses.FindAsync(1);
        updatedCampus!.Name.Should().Be("Main Campus Updated");
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Arrange
        var request = new UpdateCampusRequest
        {
            Name = "Test"
        };

        // Act
        var result = await _service.UpdateAsync("INVALID", request);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteAsync_WithValidIdKey_SoftDeletesCampus()
    {
        // Arrange
        var campus = await _context.Campuses.FindAsync(1);
        var idKey = campus!.IdKey;

        // Act
        var result = await _service.DeleteAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify soft delete
        var deletedCampus = await _context.Campuses.FindAsync(1);
        deletedCampus!.IsActive.Should().BeFalse();
        deletedCampus.ModifiedDateTime.Should().NotBeNull();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
