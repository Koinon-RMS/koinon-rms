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

public class LocationServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreateLocationRequest> _createValidator;
    private readonly IValidator<UpdateLocationRequest> _updateValidator;
    private readonly Mock<ILogger<LocationService>> _mockLogger;
    private readonly LocationService _service;

    public LocationServiceTests()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_Loc_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);
        _context.Database.EnsureCreated();

        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<LocationMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        _createValidator = new CreateLocationRequestValidator();
        _updateValidator = new UpdateLocationRequestValidator();
        _mockLogger = new Mock<ILogger<LocationService>>();

        _service = new LocationService(
            _context,
            _mapper,
            _createValidator,
            _updateValidator,
            _mockLogger.Object
        );

        SeedTestData();
    }

    private void SeedTestData()
    {
        // Add Campus
        var campus = new Campus { Id = 1, Name = "Main Campus", ShortCode = "MAIN", IsActive = true };
        _context.Campuses.Add(campus);

        // Add DefinedValue for Type
        var locType = new DefinedValue { Id = 1, Value = "Building", IsActive = true, DefinedTypeId = 1 };
        _context.DefinedValues.Add(locType);

        // Add Locations
        var loc1 = new Location // Parent
        {
            Id = 1,
            Name = "Building A",
            CampusId = 1,
            IsActive = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Locations.Add(loc1);
        _context.SaveChanges(); // Save to get IdKey/Guid

        var loc2 = new Location // Child
        {
            Id = 2,
            Name = "Room 101",
            ParentLocationId = 1,
            CampusId = 1,
            IsActive = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Locations.Add(loc2);

        var loc3 = new Location // Inactive
        {
            Id = 3,
            Name = "Old Building",
            IsActive = false,
            Order = 2,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Locations.Add(loc3);

        _context.SaveChanges();
    }

    [Fact]
    public async Task GetAllAsync_WhenExcludeInactive_ReturnsOnlyActiveLocations()
    {
        var result = await _service.GetAllAsync(null, false);
        result.Should().HaveCount(2); // Loc1, Loc2
        result.Should().Contain(l => l.Name == "Building A");
        result.Should().Contain(l => l.Name == "Room 101");
        result.Should().NotContain(l => l.Name == "Old Building");
    }

    [Fact]
    public async Task GetTreeAsync_ReturnsHierarchicalStructure()
    {
        var result = await _service.GetTreeAsync(null, false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1); // Only root (Building A)

        var root = result.Value![0];
        root.Name.Should().Be("Building A");
        root.Children.Should().HaveCount(1);
        root.Children[0].Name.Should().Be("Room 101");
    }

    [Fact]
    public async Task CreateAsync_WithValidParent_CreatesChildLocation()
    {
        var parent = await _context.Locations.FindAsync(1);

        var request = new CreateLocationRequest
        {
            Name = "Room 102",
            ParentLocationIdKey = parent!.IdKey,
            CampusIdKey = parent.Campus!.IdKey
        };

        var result = await _service.CreateAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Room 102");
        result.Value.ParentLocationName.Should().Be("Building A");

        // Verify DB
        var created = await _context.Locations.FirstOrDefaultAsync(l => l.Name == "Room 102");
        created.Should().NotBeNull();
        created!.ParentLocationId.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidParent_ReturnsError()
    {
        var request = new CreateLocationRequest
        {
            Name = "Room X",
            ParentLocationIdKey = "INVALID"
        };

        var result = await _service.CreateAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("UNPROCESSABLE_ENTITY");
    }

    [Fact]
    public async Task UpdateAsync_CircularReference_ReturnsError()
    {
        // Try to set Child (Loc2) as Parent of Parent (Loc1)
        var loc1 = await _context.Locations.FindAsync(1);
        var loc2 = await _context.Locations.FindAsync(2);

        var request = new UpdateLocationRequest
        {
            ParentLocationIdKey = loc2!.IdKey
        };

        var result = await _service.UpdateAsync(loc1!.IdKey, request);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("UNPROCESSABLE_ENTITY");
        result.Error.Message.Should().Contain("circular reference");
    }

    [Fact]
    public async Task DeleteAsync_WithActiveChildren_ReturnsError()
    {
        var loc1 = await _context.Locations.FindAsync(1); // Has active child loc2

        var result = await _service.DeleteAsync(loc1!.IdKey);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("UNPROCESSABLE_ENTITY");
        result.Error.Message.Should().Contain("active child locations");
    }

    [Fact]
    public async Task DeleteAsync_LeafNode_SoftDeletes()
    {
        var loc2 = await _context.Locations.FindAsync(2); // Leaf node

        var result = await _service.DeleteAsync(loc2!.IdKey);

        result.IsSuccess.Should().BeTrue();

        var deleted = await _context.Locations.FindAsync(2);
        deleted!.IsActive.Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
