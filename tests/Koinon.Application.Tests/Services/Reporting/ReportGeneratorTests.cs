using FluentAssertions;
using Koinon.Application.Interfaces;
using Koinon.Application.Services.Reporting;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using QuestPDF.Infrastructure;
using Xunit;

namespace Koinon.Application.Tests.Services.Reporting;

/// <summary>
/// Unit tests for report generators (Attendance, Giving, Directory).
/// Tests all three output formats (PDF, Excel, CSV) for each generator.
/// </summary>
public class ReportGeneratorTests
{
    private readonly IApplicationDbContext _context;
    private readonly Mock<ILogger<AttendanceSummaryReportGenerator>> _attendanceLoggerMock;
    private readonly Mock<ILogger<GivingSummaryReportGenerator>> _givingLoggerMock;
    private readonly Mock<ILogger<DirectoryReportGenerator>> _directoryLoggerMock;

    public ReportGeneratorTests()
    {
        // Configure QuestPDF community license for tests
        QuestPDF.Settings.License = LicenseType.Community;

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new TestDbContext(options);

        _attendanceLoggerMock = new Mock<ILogger<AttendanceSummaryReportGenerator>>();
        _givingLoggerMock = new Mock<ILogger<GivingSummaryReportGenerator>>();
        _directoryLoggerMock = new Mock<ILogger<DirectoryReportGenerator>>();

        SeedTestData();
    }

    private void SeedTestData()
    {
        // Add test data for attendance
        var group = new Group
        {
            Id = 1,
            Name = "Test Group",
            GroupTypeId = 1,
            IsActive = true
        };

        var occurrence = new AttendanceOccurrence
        {
            Id = 1,
            GroupId = 1,
            Group = group,
            OccurrenceDate = new DateOnly(2024, 1, 15),
            SundayDate = new DateOnly(2024, 1, 14),
            LocationId = 1
        };

        var attendances = new List<Attendance>
        {
            new() { Id = 1, OccurrenceId = 1, Occurrence = occurrence, DidAttend = true, PersonAliasId = 1, StartDateTime = DateTime.UtcNow },
            new() { Id = 2, OccurrenceId = 1, Occurrence = occurrence, DidAttend = true, PersonAliasId = 2, StartDateTime = DateTime.UtcNow },
            new() { Id = 3, OccurrenceId = 1, Occurrence = occurrence, DidAttend = false, PersonAliasId = 3, StartDateTime = DateTime.UtcNow }
        };

        _context.Groups.Add(group);
        _context.AttendanceOccurrences.Add(occurrence);
        foreach (var attendance in attendances)
        {
            _context.Attendances.Add(attendance);
        }

        // Add test data for giving
        var fund = new Fund
        {
            Id = 1,
            Name = "General Fund",
            IsActive = true
        };

        var contribution = new Contribution
        {
            Id = 1,
            TransactionDateTime = DateTime.UtcNow.AddDays(-10),
            PersonAliasId = 1,
            TransactionTypeValueId = 1,
            SourceTypeValueId = 1
        };

        var contributionDetails = new List<ContributionDetail>
        {
            new() { Id = 1, ContributionId = 1, Contribution = contribution, FundId = 1, Fund = fund, Amount = 100.00m },
            new() { Id = 2, ContributionId = 1, Contribution = contribution, FundId = 1, Fund = fund, Amount = 50.00m }
        };

        _context.Funds.Add(fund);
        _context.Contributions.Add(contribution);
        foreach (var detail in contributionDetails)
        {
            _context.ContributionDetails.Add(detail);
        }

        // Add test data for directory
        var recordStatusValue = new DefinedValue
        {
            Id = 1,
            Value = "Active",
            DefinedTypeId = 1
        };

        var person = new Person
        {
            Id = 1,
            FirstName = "John",
            LastName = "Doe",
            Email = "john.doe@example.com",
            Gender = Gender.Male,
            RecordStatusValueId = 1,
            RecordStatusValue = recordStatusValue
        };

        var phoneNumber = new PhoneNumber
        {
            Id = 1,
            PersonId = 1,
            Number = "555-1234",
            NumberTypeValueId = 1
        };

        var family = new Family
        {
            Id = 1,
            Name = "Doe Family"
        };

        var familyMember = new FamilyMember
        {
            Id = 1,
            PersonId = 1,
            FamilyId = 1,
            Family = family
        };

        _context.DefinedValues.Add(recordStatusValue);
        _context.People.Add(person);
        _context.PhoneNumbers.Add(phoneNumber);
        _context.Families.Add(family);
        _context.FamilyMembers.Add(familyMember);

        _context.SaveChangesAsync().Wait();
    }

    #region AttendanceSummaryReportGenerator Tests

    [Fact]
    public async Task AttendanceSummaryReportGenerator_Pdf_ProducesNonEmptyStream()
    {
        // Arrange
        var generator = new AttendanceSummaryReportGenerator(_context, _attendanceLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Pdf
        };

        // Act
        var result = await generator.GenerateAsync(
            "Attendance Summary",
            ReportType.AttendanceSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".pdf");
        result.MimeType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task AttendanceSummaryReportGenerator_Excel_ProducesValidXlsx()
    {
        // Arrange
        var generator = new AttendanceSummaryReportGenerator(_context, _attendanceLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Excel
        };

        // Act
        var result = await generator.GenerateAsync(
            "Attendance Summary",
            ReportType.AttendanceSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".xlsx");
        result.MimeType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        // Check for PK zip header (XLSX is a zip file)
        result.Stream.Position = 0;
        var header = new byte[2];
        await result.Stream.ReadExactlyAsync(header, 0, 2);
        header.Should().BeEquivalentTo(new byte[] { 0x50, 0x4B }); // "PK"
    }

    [Fact]
    public async Task AttendanceSummaryReportGenerator_Csv_ProducesValidCsvWithHeaders()
    {
        // Arrange
        var generator = new AttendanceSummaryReportGenerator(_context, _attendanceLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Attendance Summary",
            ReportType.AttendanceSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".csv");
        result.MimeType.Should().Be("text/csv");

        // Verify CSV has headers
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var firstLine = await reader.ReadLineAsync();
        firstLine.Should().Contain("Group");
        firstLine.Should().Contain("Date");
        firstLine.Should().Contain("Present Count");
        firstLine.Should().Contain("Absent Count");
    }

    [Fact]
    public async Task AttendanceSummaryReportGenerator_ParsesParameters()
    {
        // Arrange
        var generator = new AttendanceSummaryReportGenerator(_context, _attendanceLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        var parameters = "{\"StartDate\":\"2024-01-01\",\"EndDate\":\"2024-12-31\",\"GroupId\":1}";

        // Act
        var result = await generator.GenerateAsync(
            "Attendance Summary",
            ReportType.AttendanceSummary,
            Array.Empty<object>(),
            parameters);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
    }

    [Fact]
    public async Task AttendanceSummaryReportGenerator_EmptyData_ProducesValidReport()
    {
        // Arrange
        var emptyContext = CreateEmptyContext();
        var generator = new AttendanceSummaryReportGenerator(emptyContext, _attendanceLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Attendance Summary",
            ReportType.AttendanceSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();

        // Should still have headers even with no data
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var content = await reader.ReadToEndAsync();
        content.Should().Contain("Group");
    }

    #endregion

    #region GivingSummaryReportGenerator Tests

    [Fact]
    public async Task GivingSummaryReportGenerator_Pdf_ProducesNonEmptyStream()
    {
        // Arrange
        var generator = new GivingSummaryReportGenerator(_context, _givingLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Pdf
        };

        // Act
        var result = await generator.GenerateAsync(
            "Giving Summary",
            ReportType.GivingSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".pdf");
        result.MimeType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task GivingSummaryReportGenerator_Excel_ProducesValidXlsx()
    {
        // Arrange
        var generator = new GivingSummaryReportGenerator(_context, _givingLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Excel
        };

        // Act
        var result = await generator.GenerateAsync(
            "Giving Summary",
            ReportType.GivingSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".xlsx");
        result.MimeType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        // Check for PK zip header
        result.Stream.Position = 0;
        var header = new byte[2];
        await result.Stream.ReadExactlyAsync(header, 0, 2);
        header.Should().BeEquivalentTo(new byte[] { 0x50, 0x4B });
    }

    [Fact]
    public async Task GivingSummaryReportGenerator_Csv_ProducesValidCsvWithHeaders()
    {
        // Arrange
        var generator = new GivingSummaryReportGenerator(_context, _givingLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Giving Summary",
            ReportType.GivingSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".csv");
        result.MimeType.Should().Be("text/csv");

        // Verify CSV has headers
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var firstLine = await reader.ReadLineAsync();
        firstLine.Should().Contain("Fund");
        firstLine.Should().Contain("Contribution Count");
        firstLine.Should().Contain("Total Amount");
    }

    [Fact]
    public async Task GivingSummaryReportGenerator_ParsesParameters()
    {
        // Arrange
        var generator = new GivingSummaryReportGenerator(_context, _givingLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        var parameters = "{\"StartDate\":\"2024-01-01\",\"EndDate\":\"2024-12-31\",\"FundId\":1}";

        // Act
        var result = await generator.GenerateAsync(
            "Giving Summary",
            ReportType.GivingSummary,
            Array.Empty<object>(),
            parameters);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
    }

    [Fact]
    public async Task GivingSummaryReportGenerator_EmptyData_ProducesValidReport()
    {
        // Arrange
        var emptyContext = CreateEmptyContext();
        var generator = new GivingSummaryReportGenerator(emptyContext, _givingLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Giving Summary",
            ReportType.GivingSummary,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();

        // Should still have headers even with no data
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var content = await reader.ReadToEndAsync();
        content.Should().Contain("Fund");
    }

    #endregion

    #region DirectoryReportGenerator Tests

    [Fact]
    public async Task DirectoryReportGenerator_Pdf_ProducesNonEmptyStream()
    {
        // Arrange
        var generator = new DirectoryReportGenerator(_context, _directoryLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Pdf
        };

        // Act
        var result = await generator.GenerateAsync(
            "Directory",
            ReportType.Directory,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".pdf");
        result.MimeType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task DirectoryReportGenerator_Excel_ProducesValidXlsx()
    {
        // Arrange
        var generator = new DirectoryReportGenerator(_context, _directoryLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Excel
        };

        // Act
        var result = await generator.GenerateAsync(
            "Directory",
            ReportType.Directory,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".xlsx");
        result.MimeType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        // Check for PK zip header
        result.Stream.Position = 0;
        var header = new byte[2];
        await result.Stream.ReadExactlyAsync(header, 0, 2);
        header.Should().BeEquivalentTo(new byte[] { 0x50, 0x4B });
    }

    [Fact]
    public async Task DirectoryReportGenerator_Csv_ProducesValidCsvWithHeaders()
    {
        // Arrange
        var generator = new DirectoryReportGenerator(_context, _directoryLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Directory",
            ReportType.Directory,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
        result.Stream.Length.Should().BeGreaterThan(0);
        result.FileName.Should().EndWith(".csv");
        result.MimeType.Should().Be("text/csv");

        // Verify CSV has headers
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var firstLine = await reader.ReadLineAsync();
        firstLine.Should().Contain("Name");
        firstLine.Should().Contain("Email");
        firstLine.Should().Contain("Phone");
    }

    [Fact]
    public async Task DirectoryReportGenerator_ParsesParameters()
    {
        // Arrange
        var generator = new DirectoryReportGenerator(_context, _directoryLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        var parameters = "{\"IncludePhotos\":true,\"GroupId\":1}";

        // Act
        var result = await generator.GenerateAsync(
            "Directory",
            ReportType.Directory,
            Array.Empty<object>(),
            parameters);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();
    }

    [Fact]
    public async Task DirectoryReportGenerator_EmptyData_ProducesValidReport()
    {
        // Arrange
        var emptyContext = CreateEmptyContext();
        var generator = new DirectoryReportGenerator(emptyContext, _directoryLoggerMock.Object)
        {
            OutputFormat = ReportOutputFormat.Csv
        };

        // Act
        var result = await generator.GenerateAsync(
            "Directory",
            ReportType.Directory,
            Array.Empty<object>(),
            null);

        // Assert
        result.Should().NotBeNull();
        result.Stream.Should().NotBeNull();

        // Should still have headers even with no data
        result.Stream.Position = 0;
        using var reader = new StreamReader(result.Stream);
        var content = await reader.ReadToEndAsync();
        content.Should().Contain("Name");
    }

    #endregion

    #region Helper Methods

    private IApplicationDbContext CreateEmptyContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    #endregion

    #region Test DbContext

    private class TestDbContext : DbContext, IApplicationDbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

        public DbSet<Person> People { get; set; } = null!;
        public DbSet<PersonAlias> PersonAliases { get; set; } = null!;
        public DbSet<PhoneNumber> PhoneNumbers { get; set; } = null!;
        public DbSet<PersonNote> PersonNotes { get; set; } = null!;
        public DbSet<Group> Groups { get; set; } = null!;
        public DbSet<GroupType> GroupTypes { get; set; } = null!;
        public DbSet<GroupTypeRole> GroupTypeRoles { get; set; } = null!;
        public DbSet<GroupMember> GroupMembers { get; set; } = null!;
        public DbSet<GroupMemberRequest> GroupMemberRequests { get; set; } = null!;
        public DbSet<FamilyMember> FamilyMembers { get; set; } = null!;
        public DbSet<Family> Families { get; set; } = null!;
        public DbSet<GroupSchedule> GroupSchedules { get; set; } = null!;
        public DbSet<Campus> Campuses { get; set; } = null!;
        public DbSet<Location> Locations { get; set; } = null!;
        public DbSet<DefinedType> DefinedTypes { get; set; } = null!;
        public DbSet<DefinedValue> DefinedValues { get; set; } = null!;
        public DbSet<Schedule> Schedules { get; set; } = null!;
        public DbSet<Attendance> Attendances { get; set; } = null!;
        public DbSet<AttendanceOccurrence> AttendanceOccurrences { get; set; } = null!;
        public DbSet<AttendanceCode> AttendanceCodes { get; set; } = null!;
        public DbSet<Device> Devices { get; set; } = null!;
        public DbSet<LabelTemplate> LabelTemplates { get; set; } = null!;
        public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
        public DbSet<UserPreference> UserPreferences { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<TwoFactorConfig> TwoFactorConfigs { get; set; } = null!;
        public DbSet<SupervisorSession> SupervisorSessions { get; set; } = null!;
        public DbSet<SupervisorAuditLog> SupervisorAuditLogs { get; set; } = null!;
        public DbSet<FollowUp> FollowUps { get; set; } = null!;
        public DbSet<PagerAssignment> PagerAssignments { get; set; } = null!;
        public DbSet<PagerMessage> PagerMessages { get; set; } = null!;
        public DbSet<AuthorizedPickup> AuthorizedPickups { get; set; } = null!;
        public DbSet<PickupLog> PickupLogs { get; set; } = null!;
        public DbSet<Communication> Communications { get; set; } = null!;
        public DbSet<CommunicationRecipient> CommunicationRecipients { get; set; } = null!;
        public DbSet<CommunicationTemplate> CommunicationTemplates { get; set; } = null!;
        public DbSet<CommunicationPreference> CommunicationPreferences { get; set; } = null!;
        public DbSet<BinaryFile> BinaryFiles { get; set; } = null!;
        public DbSet<ImportTemplate> ImportTemplates { get; set; } = null!;
        public DbSet<ImportJob> ImportJobs { get; set; } = null!;
        public DbSet<ReportDefinition> ReportDefinitions { get; set; } = null!;
        public DbSet<ReportRun> ReportRuns { get; set; } = null!;
        public DbSet<ReportSchedule> ReportSchedules { get; set; } = null!;
        public DbSet<ExportJob> ExportJobs { get; set; } = null!;

        public DbSet<Fund> Funds { get; set; } = null!;
        public DbSet<ContributionBatch> ContributionBatches { get; set; } = null!;
        public DbSet<Contribution> Contributions { get; set; } = null!;
        public DbSet<ContributionDetail> ContributionDetails { get; set; } = null!;
        public DbSet<ContributionStatement> ContributionStatements { get; set; } = null!;
        public DbSet<FinancialAuditLog> FinancialAuditLogs { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;

        public DbSet<PersonMergeHistory> PersonMergeHistories { get; set; } = null!;
        public DbSet<PersonDuplicateIgnore> PersonDuplicateIgnores { get; set; } = null!;
        public DbSet<Notification> Notifications { get; set; } = null!;
        public DbSet<NotificationPreference> NotificationPreferences { get; set; } = null!;

        public DbSet<SecurityRole> SecurityRoles { get; set; } = null!;
        public DbSet<SecurityClaim> SecurityClaims { get; set; } = null!;
        public DbSet<PersonSecurityRole> PersonSecurityRoles { get; set; } = null!;
        public DbSet<RoleSecurityClaim> RoleSecurityClaims { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Location>()
                .HasOne(l => l.ParentLocation)
                .WithMany(l => l.ChildLocations)
                .HasForeignKey(l => l.ParentLocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Location>()
                .HasOne(l => l.OverflowLocation)
                .WithMany()
                .HasForeignKey(l => l.OverflowLocationId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    #endregion
}
