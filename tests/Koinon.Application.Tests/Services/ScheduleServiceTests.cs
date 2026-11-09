using AutoMapper;
using Koinon.Application.DTOs.Requests;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Application.Validators;
using Koinon.Domain.Entities;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Koinon.Application.Tests.Services;

public class ScheduleServiceTests : IAsyncLifetime
{
    private KoinonDbContext _context = null!;
    private IMapper _mapper = null!;
    private ScheduleService _service = null!;

    public async Task InitializeAsync()
    {
        // Create in-memory database
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"ScheduleServiceTest_{Guid.NewGuid()}")
            .Options;

        _context = new KoinonDbContext(options);

        // Configure AutoMapper
        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<ScheduleMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        // Create validators
        var createValidator = new CreateScheduleRequestValidator();
        var updateValidator = new UpdateScheduleRequestValidator();

        // Create service
        _service = new ScheduleService(
            _context,
            _mapper,
            createValidator,
            updateValidator,
            NullLogger<ScheduleService>.Instance);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_WithValidWeeklySchedule_ReturnsSuccess()
    {
        // Arrange
        var request = new CreateScheduleRequest
        {
            Name = "Sunday Service",
            Description = "Main service time",
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0), // 10:00 AM
            CheckInStartOffsetMinutes = 60,
            CheckInEndOffsetMinutes = 30,
            IsActive = true,
            IsPublic = true
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Sunday Service", result.Value.Name);
        Assert.Equal(DayOfWeek.Sunday, result.Value.WeeklyDayOfWeek);
        Assert.Equal(new TimeSpan(10, 0, 0), result.Value.WeeklyTimeOfDay);
    }

    [Fact]
    public async Task CreateAsync_WithoutDayOfWeek_ReturnsValidationError()
    {
        // Arrange
        var request = new CreateScheduleRequest
        {
            Name = "Incomplete Schedule",
            WeeklyDayOfWeek = null, // Missing required field
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0)
        };

        // Act
        var result = await _service.CreateAsync(request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Equal("VALIDATION_ERROR", result.Error.Code);
    }

    [Fact]
    public async Task GetByIdKeyAsync_WithExistingSchedule_ReturnsSchedule()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Test Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Monday,
            WeeklyTimeOfDay = new TimeSpan(9, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetByIdKeyAsync(schedule.IdKey);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(schedule.IdKey, result.IdKey);
        Assert.Equal("Test Schedule", result.Name);
    }

    [Fact]
    public async Task SearchAsync_ReturnsActiveSchedules()
    {
        // Arrange
        var schedule1 = new Schedule
        {
            Name = "Active Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(9, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        var schedule2 = new Schedule
        {
            Name = "Inactive Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(11, 0, 0),
            IsActive = false,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.AddRange(schedule1, schedule2);
        await _context.SaveChangesAsync();

        var parameters = new ScheduleSearchParameters
        {
            IncludeInactive = false,
            Page = 1,
            PageSize = 25
        };

        // Act
        var result = await _service.SearchAsync(parameters);

        // Assert
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Active Schedule", result.Items[0].Name);
    }

    [Fact]
    public async Task UpdateAsync_WithValidData_ReturnsSuccess()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Original Name",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        var request = new UpdateScheduleRequest
        {
            Name = "Updated Name",
            IsActive = false
        };

        // Act
        var result = await _service.UpdateAsync(schedule.IdKey, request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Updated Name", result.Value!.Name);
        Assert.False(result.Value.IsActive);
    }

    [Fact]
    public async Task DeleteAsync_DeactivatesSchedule()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Schedule to Delete",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteAsync(schedule.IdKey);

        // Assert
        Assert.True(result.IsSuccess);

        var deletedSchedule = await _context.Schedules.FindAsync(schedule.Id);
        Assert.NotNull(deletedSchedule);
        Assert.False(deletedSchedule.IsActive);
    }

    [Fact]
    public async Task GetOccurrencesAsync_ReturnsCorrectDates()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Weekly Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0),
            CheckInStartOffsetMinutes = 60,
            CheckInEndOffsetMinutes = 30,
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        var startDate = DateOnly.FromDateTime(DateTime.Today);

        // Act
        var occurrences = await _service.GetOccurrencesAsync(schedule.IdKey, startDate, 4);

        // Assert
        Assert.NotEmpty(occurrences);
        Assert.All(occurrences, o => Assert.Equal(DayOfWeek.Sunday, o.OccurrenceDateTime.DayOfWeek));
        Assert.All(occurrences, o => Assert.Equal(10, o.OccurrenceDateTime.Hour));
        Assert.All(occurrences, o => Assert.NotNull(o.CheckInWindowStart));
        Assert.All(occurrences, o => Assert.NotNull(o.CheckInWindowEnd));
    }

    [Fact]
    public async Task UpdateAsync_WithInvalidDateRange_ReturnsValidationError()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Test Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(10, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        var request = new UpdateScheduleRequest
        {
            Name = "Updated Schedule",
            EffectiveStartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(10)),
            EffectiveEndDate = DateOnly.FromDateTime(DateTime.Today) // End before start
        };

        // Act
        var result = await _service.UpdateAsync(schedule.IdKey, request);

        // Assert
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Equal("VALIDATION_ERROR", result.Error.Code);
        Assert.NotNull(result.Error.Details);
        Assert.True(result.Error.Details.ContainsKey(string.Empty), "Validation error should be in root-level details");
        Assert.Contains("Effective start date must be before or equal to effective end date",
            string.Join(" ", result.Error.Details[string.Empty]));
    }

    [Fact]
    public async Task GetOccurrencesAsync_WithCountExceeding52_LimitsTo52()
    {
        // Arrange
        var schedule = new Schedule
        {
            Name = "Weekly Schedule",
            Guid = Guid.NewGuid(),
            WeeklyDayOfWeek = DayOfWeek.Monday,
            WeeklyTimeOfDay = new TimeSpan(14, 0, 0),
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        };
        _context.Schedules.Add(schedule);
        await _context.SaveChangesAsync();

        var startDate = DateOnly.FromDateTime(DateTime.Today);

        // Act
        var occurrences = await _service.GetOccurrencesAsync(schedule.IdKey, startDate, 100);

        // Assert
        Assert.NotEmpty(occurrences);
        Assert.True(occurrences.Count <= 52, "Service should limit occurrences to 52 weeks maximum");
    }
}
