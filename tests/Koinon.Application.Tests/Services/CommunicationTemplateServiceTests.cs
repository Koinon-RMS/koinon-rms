using AutoMapper;
using FluentAssertions;
using Koinon.Application.Common;
using Koinon.Application.DTOs;
using Koinon.Application.Interfaces;
using Koinon.Application.Mapping;
using Koinon.Application.Services;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Tests for CommunicationTemplateService.
///
/// IMPORTANT: The in-memory DbContext is configured with
/// <see cref="QueryTrackingBehavior.NoTracking"/> to mirror the production
/// PostgreSqlProvider default. This is critical: without it, these tests
/// would silently pass even if the service forgot to call
/// <c>AsTracking()</c> — which was the root cause of bug #685 (PUT
/// appearing to succeed but subsequent GET returning stale data because
/// the mutations were never persisted to the database).
/// </summary>
public class CommunicationTemplateServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IMapper _mapper;
    private readonly Mock<IMergeFieldService> _mockMergeFieldService;
    private readonly Mock<ILogger<CommunicationTemplateService>> _mockLogger;
    private readonly CommunicationTemplateService _service;

    public CommunicationTemplateServiceTests()
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
            cfg.AddProfile<CommunicationTemplateMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mapper = mapperConfig.CreateMapper();

        _mockMergeFieldService = new Mock<IMergeFieldService>();
        _mockMergeFieldService
            .Setup(m => m.ValidateMergeFields(It.IsAny<string>()))
            .Returns(Result.Success());

        _mockLogger = new Mock<ILogger<CommunicationTemplateService>>();

        _service = new CommunicationTemplateService(
            _context,
            _mapper,
            _mockMergeFieldService.Object,
            _mockLogger.Object
        );

        SeedTestData();
    }

    private void SeedTestData()
    {
        _context.CommunicationTemplates.Add(new CommunicationTemplate
        {
            Id = 1,
            Name = "Welcome Email",
            CommunicationType = CommunicationType.Email,
            Subject = "Welcome!",
            Body = "Hello {{FirstName}}",
            Description = "Initial welcome email",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        });

        _context.CommunicationTemplates.Add(new CommunicationTemplate
        {
            Id = 2,
            Name = "Event Reminder",
            CommunicationType = CommunicationType.Sms,
            Body = "Reminder: event tonight",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        });

        _context.SaveChanges();
        // Detach all seeded entities so they behave like rows loaded fresh from the DB.
        // Critical for a NoTracking context: without this, DbSet.Remove(detachedCopy) on an
        // entity whose seed copy is still tracked throws an identity-conflict exception.
        _context.ChangeTracker.Clear();
    }

    // ---------------------------------------------------------------------
    // Bug #685 — UpdateAsync must persist mutations to the database.
    //
    // Before the fix, CommunicationTemplateService.UpdateAsync loaded the
    // entity without .AsTracking(). Because the global QueryTrackingBehavior
    // is NoTracking (see PostgreSqlProvider), the returned entity was
    // detached; subsequent property mutations and SaveChanges were no-ops.
    // The PUT response reflected the (in-memory) mutations, but the DB
    // retained the original values — so the next GET returned stale data.
    //
    // These tests fail against the pre-fix code and pass against the fixed
    // code.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_Name_PersistsToDatabase()
    {
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        var idKey = existing.IdKey;
        var dto = new UpdateCommunicationTemplateDto { Name = "Renamed Welcome" };

        // Act
        var result = await _service.UpdateAsync(idKey, dto);

        // Assert — response reflects new name
        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Renamed Welcome");

        // Assert — DB actually has the new name (fresh read bypasses any local tracking)
        var reloaded = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        reloaded.Name.Should().Be("Renamed Welcome");
    }

    [Fact]
    public async Task UpdateAsync_Body_PersistsToDatabase()
    {
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        var idKey = existing.IdKey;
        var dto = new UpdateCommunicationTemplateDto { Body = "Updated body content" };

        // Act
        var result = await _service.UpdateAsync(idKey, dto);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        reloaded.Body.Should().Be("Updated body content");
    }

    [Fact]
    public async Task UpdateAsync_Name_IsReflectedInSubsequentGetByIdKey()
    {
        // Reproduces the end-to-end symptom reported in #685: PUT then GET must show fresh data.
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 2);
        var idKey = existing.IdKey;
        var dto = new UpdateCommunicationTemplateDto { Name = "Event Reminder v2" };

        // Act
        var updateResult = await _service.UpdateAsync(idKey, dto);
        updateResult.IsSuccess.Should().BeTrue();

        var getResult = await _service.GetByIdKeyAsync(idKey);

        // Assert
        getResult.Should().NotBeNull();
        getResult!.Name.Should().Be("Event Reminder v2");
    }

    [Fact]
    public async Task UpdateAsync_IsActiveFalse_PersistsToDatabase()
    {
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        var idKey = existing.IdKey;
        var dto = new UpdateCommunicationTemplateDto { IsActive = false };

        // Act
        var result = await _service.UpdateAsync(idKey, dto);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        reloaded.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ModifiedDateTime_IsSet()
    {
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        var idKey = existing.IdKey;
        var before = DateTime.UtcNow;
        var dto = new UpdateCommunicationTemplateDto { Name = "Stamp test" };

        // Act
        var result = await _service.UpdateAsync(idKey, dto);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        reloaded.ModifiedDateTime.Should().NotBeNull();
        reloaded.ModifiedDateTime!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task UpdateAsync_InvalidIdKey_ReturnsNotFound()
    {
        // Arrange
        var dto = new UpdateCommunicationTemplateDto { Name = "Ignored" };

        // Act
        var result = await _service.UpdateAsync("not-a-real-idkey", dto);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteAsync_RemovesTemplateFromDatabase()
    {
        // Arrange
        var existing = await _context.CommunicationTemplates.AsNoTracking().FirstAsync(t => t.Id == 1);
        var idKey = existing.IdKey;

        // Act
        var result = await _service.DeleteAsync(idKey);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await _context.CommunicationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == 1);
        reloaded.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NewTemplate_IsPersistedAndRetrievable()
    {
        // Arrange
        var dto = new CreateCommunicationTemplateDto
        {
            Name = "New Template",
            CommunicationType = "Email",
            Subject = "Hi",
            Body = "Test body",
            IsActive = true
        };

        // Act
        var createResult = await _service.CreateAsync(dto);

        // Assert
        createResult.IsSuccess.Should().BeTrue();
        var getResult = await _service.GetByIdKeyAsync(createResult.Value!.IdKey);
        getResult.Should().NotBeNull();
        getResult!.Name.Should().Be("New Template");
        getResult.Body.Should().Be("Test body");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
