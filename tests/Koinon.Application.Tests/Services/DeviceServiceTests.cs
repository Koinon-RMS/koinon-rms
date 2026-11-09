using AutoMapper;
using FluentAssertions;
using FluentValidation;
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
/// Tests for DeviceService.
///
/// IMPORTANT: The in-memory DbContext is configured with
/// <see cref="QueryTrackingBehavior.NoTracking"/> to mirror the production
/// PostgreSqlProvider default. This is critical: without it, these tests
/// would silently pass even if the service forgot to call
/// <c>AsTracking()</c> — which was the root cause of bugs #694 and #695.
/// </summary>
public class DeviceServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly IValidator<CreateDeviceRequest> _createValidator;
    private readonly IValidator<UpdateDeviceRequest> _updateValidator;
    private readonly Mock<ILogger<DeviceService>> _mockLogger;
    private readonly DeviceService _service;

    public DeviceServiceTests()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_{Guid.NewGuid()}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);
        _context.Database.EnsureCreated();

        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<DeviceMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        _createValidator = new CreateDeviceRequestValidator();
        _updateValidator = new UpdateDeviceRequestValidator();

        _mockLogger = new Mock<ILogger<DeviceService>>();

        _service = new DeviceService(
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
        var device1 = new Device
        {
            Id = 1,
            Name = "Main Lobby Kiosk",
            Description = "Primary check-in kiosk",
            IpAddress = "10.0.0.11",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Devices.Add(device1);

        var device2 = new Device
        {
            Id = 2,
            Name = "Childrens Wing Kiosk",
            IpAddress = "10.0.0.12",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Devices.Add(device2);

        _context.SaveChanges();
    }

    // ---------------------------------------------------------------------
    // Bug #694 — UpdateAsync must persist the Name field.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_Name_PersistsToDatabase()
    {
        // Arrange
        var device = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        var idKey = device.IdKey;
        var request = new UpdateDeviceRequest { Name = "Renamed Kiosk" };

        // Act
        var result = await _service.UpdateAsync(idKey, request);

        // Assert — response reflects new name
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Renamed Kiosk");

        // Assert — DB actually has the new name (fresh read bypasses any local tracking)
        var reloaded = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        reloaded.Name.Should().Be("Renamed Kiosk");
    }

    [Fact]
    public async Task UpdateAsync_Name_IsReflectedInSubsequentGetByIdKey()
    {
        // Arrange
        var device = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        var idKey = device.IdKey;
        var request = new UpdateDeviceRequest { Name = "renamed-test" };

        // Act
        await _service.UpdateAsync(idKey, request);
        var getResult = await _service.GetByIdKeyAsync(idKey);

        // Assert — regression guard for the #694 curl repro
        getResult.IsSuccess.Should().BeTrue();
        getResult.Value!.Name.Should().Be("renamed-test");
    }

    [Fact]
    public async Task UpdateAsync_Name_PreservesOtherFields()
    {
        // Arrange
        var device = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        var idKey = device.IdKey;
        var originalIp = device.IpAddress;
        var originalDescription = device.Description;
        var originalIsActive = device.IsActive;

        var request = new UpdateDeviceRequest { Name = "Only Name Changed" };

        // Act
        var result = await _service.UpdateAsync(idKey, request);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var reloaded = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        reloaded.Name.Should().Be("Only Name Changed");
        reloaded.IpAddress.Should().Be(originalIp);
        reloaded.Description.Should().Be(originalDescription);
        reloaded.IsActive.Should().Be(originalIsActive);
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidIdKey_ReturnsNotFound()
    {
        // Act
        var result = await _service.UpdateAsync("INVALID", new UpdateDeviceRequest { Name = "x" });

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    // ---------------------------------------------------------------------
    // Bug #695 — DeleteAsync must actually mutate the row so the device
    // is absent from the subsequent list query (which filters on IsActive).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_SoftDeletes_AndIsAbsentFromDefaultList()
    {
        // Arrange
        var device = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        var idKey = device.IdKey;

        // Act
        var result = await _service.DeleteAsync(idKey);

        // Assert — command succeeded
        result.IsSuccess.Should().BeTrue();

        // Assert — row is actually deactivated in the DB (not just a 204 no-op)
        var reloaded = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 1);
        reloaded.IsActive.Should().BeFalse();
        reloaded.ModifiedDateTime.Should().NotBeNull();

        // Assert — default list query (includeInactive=false) no longer returns it.
        // This is the regression guard for the #695 curl repro.
        var list = await _service.GetAllAsync(campusIdKey: null, includeInactive: false);
        list.Should().NotContain(d => d.IdKey == idKey);
    }

    [Fact]
    public async Task DeleteAsync_PreservesRowForIncludeInactiveList()
    {
        // Arrange
        var device = await _context.Devices.AsNoTracking().FirstAsync(d => d.Id == 2);
        var idKey = device.IdKey;

        // Act
        await _service.DeleteAsync(idKey);

        // Assert — soft-delete keeps the row visible when includeInactive=true
        var list = await _service.GetAllAsync(campusIdKey: null, includeInactive: true);
        list.Should().Contain(d => d.IdKey == idKey);
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

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
