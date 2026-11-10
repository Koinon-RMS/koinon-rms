using AutoMapper;
using FluentAssertions;
using Koinon.Application.DTOs;
using Koinon.Application.Interfaces;
using Koinon.Application.Services;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

public class LabelGenerationServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly LabelGenerationService _sut;
    private readonly Mock<ILogger<LabelGenerationService>> _mockLogger;
    private readonly Mock<IUserContext> _mockUserContext;
    private readonly Mock<IMapper> _mockMapper;

    public LabelGenerationServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"KoinonTestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new KoinonDbContext(options);
        _context.Database.EnsureCreated();

        _mockLogger = new Mock<ILogger<LabelGenerationService>>();
        _mockUserContext = new Mock<IUserContext>();

        // Setup real AutoMapper configuration
        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.AddProfile<Koinon.Application.Mapping.LabelMappingProfile>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _mockMapper = new Mock<IMapper>();
        _mockMapper.Setup(m => m.ConfigurationProvider).Returns(mapperConfig);

        // Setup default user context behavior for tests
        _mockUserContext.Setup(x => x.IsAuthenticated).Returns(true);
        _mockUserContext.Setup(x => x.CanAccessPerson(It.IsAny<int>())).Returns(true);
        _mockUserContext.Setup(x => x.CanAccessLocation(It.IsAny<int>())).Returns(true);

        // Seed test label templates
        SeedLabelTemplates();

        _sut = new LabelGenerationService(_context, _mockUserContext.Object, _mockMapper.Object, _mockLogger.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedLabelTemplates()
    {
        var childNameTemplate = new LabelTemplate
        {
            Name = "Child Name Label (Standard)",
            Type = LabelType.ChildName,
            Format = "ZPL",
            Template = "^XA^FO50,30^A0N,50,50^FD{NickName} {LastName}^FS^XZ",
            WidthMm = 101,
            HeightMm = 51,
            IsActive = true,
            IsSystem = true
        };

        var parentClaimTemplate = new LabelTemplate
        {
            Name = "Parent Claim Ticket (Standard)",
            Type = LabelType.ParentClaim,
            Format = "ZPL",
            Template = "^XA^FO50,20^A0N,100,100^FD{SecurityCode}^FS^XZ",
            WidthMm = 76,
            HeightMm = 51,
            IsActive = true,
            IsSystem = true
        };

        var allergyTemplate = new LabelTemplate
        {
            Name = "Allergy Alert Label",
            Type = LabelType.Allergy,
            Format = "ZPL",
            Template = "^XA^FO50,20^A0N,40,40^FDALLERGY ALERT^FS^XZ",
            WidthMm = 101,
            HeightMm = 51,
            IsActive = true,
            IsSystem = true
        };

        var securityTemplate = new LabelTemplate
        {
            Name = "Child Security Label",
            Type = LabelType.ChildSecurity,
            Format = "ZPL",
            Template = "^XA^FO50,50^A0N,150,150^FD{SecurityCode}^FS^XZ",
            WidthMm = 51,
            HeightMm = 25,
            IsActive = true,
            IsSystem = true
        };

        var visitorTemplate = new LabelTemplate
        {
            Name = "Visitor Name Badge",
            Type = LabelType.VisitorName,
            Format = "ZPL",
            Template = "^XA^FO50,30^A0N,60,60^FD{FullName}^FS^XZ",
            WidthMm = 101,
            HeightMm = 51,
            IsActive = true,
            IsSystem = true
        };

        _context.LabelTemplates.AddRange(
            childNameTemplate,
            parentClaimTemplate,
            allergyTemplate,
            securityTemplate,
            visitorTemplate
        );
        _context.SaveChanges();
    }

    [Fact]
    public async Task GenerateLabelsAsync_ForChild_GeneratesChildNameAndParentClaimLabels()
    {
        // Arrange
        var (attendance, person) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.AttendanceIdKey.Should().Be(attendance.IdKey);
        result.PersonIdKey.Should().Be(person.IdKey);
        result.Labels.Should().HaveCountGreaterThan(0);
        result.Labels.Should().Contain(l => l.Type == LabelType.ChildName);
        result.Labels.Should().Contain(l => l.Type == LabelType.ParentClaim);
    }

    [Fact]
    public async Task GenerateLabelsAsync_ForAdult_GeneratesVisitorNameLabel()
    {
        // Arrange
        var (attendance, person) = await CreateTestAttendanceAsync(isChild: false);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.AttendanceIdKey.Should().Be(attendance.IdKey);
        result.PersonIdKey.Should().Be(person.IdKey);
        result.Labels.Should().HaveCount(1);
        result.Labels.Should().Contain(l => l.Type == LabelType.VisitorName);
    }

    [Fact]
    public async Task GenerateLabelsAsync_WithSecurityCode_IncludesCodeInLabels()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true, securityCode: "A1B");
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().NotBeEmpty();
        foreach (var label in result.Labels)
        {
            label.Fields.Should().ContainKey("SecurityCode");
            label.Fields["SecurityCode"].Should().Be("A1B");
            label.Content.Should().Contain("A1B");
        }
    }

    [Fact]
    public async Task GenerateLabelsAsync_WithCustomFields_IncludesCustomFieldsInLabels()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey,
            CustomFields = new Dictionary<string, string>
            {
                ["Allergies"] = "Peanuts, Dairy",
                ["EmergencyContact"] = "555-1234"
            }
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().NotBeEmpty();
        foreach (var label in result.Labels)
        {
            label.Fields.Should().ContainKey("Allergies");
            label.Fields["Allergies"].Should().Be("Peanuts, Dairy");
            label.Fields.Should().ContainKey("EmergencyContact");
            label.Fields["EmergencyContact"].Should().Be("555-1234");
        }
    }

    [Fact]
    public async Task GenerateLabelsAsync_WithSpecificLabelTypes_GeneratesOnlyRequestedLabels()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey,
            LabelTypes = new[] { LabelType.ParentClaim }
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().HaveCount(1);
        result.Labels.First().Type.Should().Be(LabelType.ParentClaim);
    }

    [Fact]
    public async Task GenerateLabelsAsync_GeneratesZPLFormat()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().NotBeEmpty();
        foreach (var label in result.Labels)
        {
            label.Format.Should().Be("ZPL");
            label.Content.Should().StartWith("^XA"); // ZPL start command
            label.Content.Should().EndWith("^XZ\n"); // ZPL end command
        }
    }

    [Fact]
    public async Task GenerateLabelsAsync_IncludesPersonNameInFields()
    {
        // Arrange
        var (attendance, person) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().NotBeEmpty();
        foreach (var label in result.Labels)
        {
            label.Fields.Should().ContainKey("FirstName");
            label.Fields["FirstName"].Should().Be(person.FirstName);
            label.Fields.Should().ContainKey("LastName");
            label.Fields["LastName"].Should().Be(person.LastName);
            label.Fields.Should().ContainKey("FullName");
        }
    }

    [Fact]
    public async Task GenerateLabelsAsync_IncludesGroupInformation()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true, groupName: "Preschool - Room 101");
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var result = await _sut.GenerateLabelsAsync(request);

        // Assert
        result.Labels.Should().NotBeEmpty();
        foreach (var label in result.Labels)
        {
            label.Fields.Should().ContainKey("GroupName");
            label.Fields["GroupName"].Should().Be("Preschool - Room 101");
        }
    }

    [Fact]
    public async Task GenerateLabelsAsync_WithInvalidAttendanceIdKey_ThrowsArgumentException()
    {
        // Arrange
        var request = new LabelRequestDto
        {
            AttendanceIdKey = "invalid-key"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.GenerateLabelsAsync(request));
    }

    [Fact]
    public async Task GenerateLabelsAsync_WithNonExistentAttendance_ThrowsInvalidOperationException()
    {
        // Arrange
        var request = new LabelRequestDto
        {
            AttendanceIdKey = IdKeyHelper.Encode(99999)
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.GenerateLabelsAsync(request));
    }

    [Fact]
    public async Task GenerateBatchLabelsAsync_GeneratesLabelsForAllAttendances()
    {
        // Arrange
        var attendance1 = (await CreateTestAttendanceAsync(isChild: true, personName: "Child One")).Attendance;
        var attendance2 = (await CreateTestAttendanceAsync(isChild: true, personName: "Child Two")).Attendance;
        var attendance3 = (await CreateTestAttendanceAsync(isChild: false, personName: "Adult One")).Attendance;

        var request = new BatchLabelRequestDto
        {
            AttendanceIdKeys = new[]
            {
                attendance1.IdKey,
                attendance2.IdKey,
                attendance3.IdKey
            }
        };

        // Act
        var results = await _sut.GenerateBatchLabelsAsync(request);

        // Assert
        results.Should().HaveCount(3);
        results.Should().Contain(r => r.AttendanceIdKey == attendance1.IdKey);
        results.Should().Contain(r => r.AttendanceIdKey == attendance2.IdKey);
        results.Should().Contain(r => r.AttendanceIdKey == attendance3.IdKey);
    }

    [Fact]
    public async Task GenerateBatchLabelsAsync_WithEmptyList_ReturnsEmptyCollection()
    {
        // Arrange
        var request = new BatchLabelRequestDto
        {
            AttendanceIdKeys = Array.Empty<string>()
        };

        // Act
        var results = await _sut.GenerateBatchLabelsAsync(request);

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTemplatesAsync_ReturnsAvailableTemplates()
    {
        // Act
        var templates = await _sut.GetTemplatesAsync();

        // Assert
        templates.Should().NotBeEmpty();
        templates.Should().Contain(t => t.Type == LabelType.ChildName);
        templates.Should().Contain(t => t.Type == LabelType.ParentClaim);
        templates.Should().Contain(t => t.Type == LabelType.Allergy);
    }

    [Fact]
    public async Task GetTemplatesAsync_TemplatesHaveValidDimensions()
    {
        // Act
        var templates = await _sut.GetTemplatesAsync();

        // Assert
        templates.Should().NotBeEmpty();
        foreach (var template in templates)
        {
            template.WidthMm.Should().BeGreaterThan(0);
            template.HeightMm.Should().BeGreaterThan(0);
            template.Template.Should().NotBeNullOrEmpty();
            template.Format.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task PreviewLabelAsync_GeneratesHtmlPreview()
    {
        // Arrange
        var request = new LabelPreviewRequestDto
        {
            Type = LabelType.ChildName,
            Fields = new Dictionary<string, string>
            {
                ["NickName"] = "Johnny",
                ["LastName"] = "Smith",
                ["GroupName"] = "Preschool",
                ["ServiceTime"] = "9:00 AM",
                ["SecurityCode"] = "A1B"
            }
        };

        // Act
        var preview = await _sut.PreviewLabelAsync(request);

        // Assert
        preview.Should().NotBeNull();
        preview.Type.Should().Be(LabelType.ChildName);
        preview.Format.Should().Be("HTML");
        preview.PreviewHtml.Should().Contain("Johnny");
        preview.PreviewHtml.Should().Contain("Smith");
        preview.PreviewHtml.Should().Contain("A1B");
    }

    [Fact]
    public async Task PreviewLabelAsync_ForParentClaim_IncludesSecurityCode()
    {
        // Arrange
        var request = new LabelPreviewRequestDto
        {
            Type = LabelType.ParentClaim,
            Fields = new Dictionary<string, string>
            {
                ["SecurityCode"] = "XYZ",
                ["FullName"] = "Emily Johnson",
                ["ServiceTime"] = "11:00 AM",
                ["CheckInTime"] = "10:45 AM"
            }
        };

        // Act
        var preview = await _sut.PreviewLabelAsync(request);

        // Assert
        preview.PreviewHtml.Should().Contain("XYZ");
        preview.PreviewHtml.Should().Contain("Emily Johnson");
        preview.PreviewHtml.Should().Contain("11:00 AM");
    }

    [Fact]
    public async Task PreviewLabelAsync_ForAllergy_ShowsAllergyAlert()
    {
        // Arrange
        var request = new LabelPreviewRequestDto
        {
            Type = LabelType.Allergy,
            Fields = new Dictionary<string, string>
            {
                ["FullName"] = "Tommy Brown",
                ["Allergies"] = "Peanuts, Tree Nuts"
            }
        };

        // Act
        var preview = await _sut.PreviewLabelAsync(request);

        // Assert
        preview.PreviewHtml.Should().Contain("ALLERGY ALERT");
        preview.PreviewHtml.Should().Contain("Tommy Brown");
        preview.PreviewHtml.Should().Contain("Peanuts, Tree Nuts");
    }

    [Fact]
    public async Task GenerateLabelsAsync_CompletesWithinPerformanceTarget()
    {
        // Arrange
        var (attendance, _) = await CreateTestAttendanceAsync(isChild: true);
        var request = new LabelRequestDto
        {
            AttendanceIdKey = attendance.IdKey
        };

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _sut.GenerateLabelsAsync(request);
        stopwatch.Stop();

        // Assert
        result.Should().NotBeNull();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000, "label generation should complete in <1000ms");
    }

    // Helper methods

    private async Task<(Attendance Attendance, Person Person)> CreateTestAttendanceAsync(
        bool isChild,
        string? securityCode = null,
        string? groupName = null,
        string personName = "Test Person")
    {
        // Create group type
        var groupType = new GroupType
        {
            Name = "Check-In Area",
        };
        await _context.GroupTypes.AddAsync(groupType);
        await _context.SaveChangesAsync();

        // Create group
        var group = new Group
        {
            GroupTypeId = groupType.Id,
            Name = groupName ?? "Children's Ministry"
        };
        await _context.Groups.AddAsync(group);
        await _context.SaveChangesAsync();

        // Create location
        var location = new Location
        {
            Name = "Room 101"
        };
        await _context.Locations.AddAsync(location);
        await _context.SaveChangesAsync();

        // Create schedule
        var schedule = new Schedule
        {
            Name = "9:00 AM Service"
        };
        await _context.Schedules.AddAsync(schedule);
        await _context.SaveChangesAsync();

        // Create attendance occurrence
        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            LocationId = location.Id,
            ScheduleId = schedule.Id,
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SundayDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        await _context.AttendanceOccurrences.AddAsync(occurrence);
        await _context.SaveChangesAsync();

        // Create person
        var birthDate = isChild
            ? DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-8)) // 8 years old
            : DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-35)); // 35 years old

        var names = personName.Split(' ');
        var person = new Person
        {
            FirstName = names[0],
            LastName = names.Length > 1 ? names[1] : "Doe",
            BirthYear = birthDate.Year,
            BirthMonth = birthDate.Month,
            BirthDay = birthDate.Day
        };
        await _context.People.AddAsync(person);
        await _context.SaveChangesAsync();

        // Create person alias
        var personAlias = new PersonAlias
        {
            PersonId = person.Id
        };
        await _context.PersonAliases.AddAsync(personAlias);
        await _context.SaveChangesAsync();

        // Create attendance code if specified
        AttendanceCode? attendanceCode = null;
        if (securityCode != null)
        {
            attendanceCode = new AttendanceCode
            {
                Code = securityCode,
                IssueDateTime = DateTime.UtcNow,
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow)
            };
            await _context.AttendanceCodes.AddAsync(attendanceCode);
            await _context.SaveChangesAsync();
        }

        // Create attendance
        var attendance = new Attendance
        {
            OccurrenceId = occurrence.Id,
            PersonAliasId = personAlias.Id,
            AttendanceCodeId = attendanceCode?.Id,
            StartDateTime = DateTime.UtcNow,
            DidAttend = true
        };
        await _context.Attendances.AddAsync(attendance);
        await _context.SaveChangesAsync();

        // Reload with navigation properties
        // SYNC OK: Test data reload
        attendance = await _context.Attendances
            .Include(a => a.Occurrence)
                .ThenInclude(o => o!.Group)
                .ThenInclude(g => g!.Schedule)
            .Include(a => a.Occurrence)
                .ThenInclude(o => o!.Location)
            .Include(a => a.AttendanceCode)
            .FirstAsync(a => a.Id == attendance.Id);

        return (attendance, person);
    }
}
