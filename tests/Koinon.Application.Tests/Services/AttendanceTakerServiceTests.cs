using Koinon.Application.DTOs;
using Koinon.Application.Interfaces;
using Koinon.Application.Services;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

public class AttendanceTakerServiceTests : IDisposable
{
    private readonly KoinonDbContext _context;
    private readonly IAttendanceTakerService _service;
    private readonly Mock<ILogger<AttendanceTakerService>> _loggerMock;

    public AttendanceTakerServiceTests()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new KoinonDbContext(options);
        _loggerMock = new Mock<ILogger<AttendanceTakerService>>();

        _service = new AttendanceTakerService(_context, _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MarkAttendedAsync_ValidRequest_CreatesAttendance()
    {
        // Arrange
        var (person, occurrence) = await SetupTestDataAsync();

        // Act
        var result = await _service.MarkAttendedAsync(occurrence.IdKey, person.IdKey);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.AttendanceIdKey);
        Assert.NotNull(result.PresentDateTime);

        // Verify attendance was created
        var attendanceId = IdKeyHelper.Decode(result.AttendanceIdKey!);
        var attendance = await _context.Attendances
            .FirstOrDefaultAsync(a => a.Id == attendanceId);
        Assert.NotNull(attendance);
        Assert.True(attendance.DidAttend);
        Assert.NotNull(attendance.PresentDateTime);
    }

    [Fact]
    public async Task MarkAttendedAsync_ExistingAttendance_UpdatesRecord()
    {
        // Arrange
        var (person, occurrence) = await SetupTestDataAsync();

        // Create initial attendance with DidAttend=false
        var personAlias = await _context.PersonAliases
            .FirstOrDefaultAsync(pa => pa.PersonId == person.Id && pa.AliasPersonId == null);

        var existingAttendance = new Attendance
        {
            OccurrenceId = occurrence.Id,
            PersonAliasId = personAlias!.Id,
            StartDateTime = DateTime.UtcNow.AddHours(-1),
            DidAttend = false
        };
        _context.Attendances.Add(existingAttendance);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.MarkAttendedAsync(occurrence.IdKey, person.IdKey, "Updated note");

        // Assert
        Assert.True(result.Success);

        // Verify attendance was updated
        var updated = await _context.Attendances.FindAsync(existingAttendance.Id);
        Assert.NotNull(updated);
        Assert.True(updated.DidAttend);
        Assert.NotNull(updated.PresentDateTime);
        Assert.Equal("Updated note", updated.Note);
    }

    [Fact]
    public async Task MarkAttendedAsync_InvalidOccurrenceId_ReturnsFalse()
    {
        // Arrange
        var (person, _) = await SetupTestDataAsync();

        // Act
        var result = await _service.MarkAttendedAsync("invalid-key", person.IdKey);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Invalid occurrence ID", result.ErrorMessage);
    }

    [Fact]
    public async Task MarkAttendedAsync_InvalidPersonId_ReturnsFalse()
    {
        // Arrange
        var (_, occurrence) = await SetupTestDataAsync();

        // Act
        var result = await _service.MarkAttendedAsync(occurrence.IdKey, "invalid-key");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Invalid person ID", result.ErrorMessage);
    }

    [Fact]
    public async Task MarkAttendedAsync_FirstTimeAttendee_SetsIsFirstTime()
    {
        // Arrange
        var (person, occurrence) = await SetupTestDataAsync();

        // Act
        var result = await _service.MarkAttendedAsync(occurrence.IdKey, person.IdKey);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsFirstTime);
    }

    [Fact]
    public async Task MarkFamilyAttendedAsync_ValidFamily_MarksAllMembers()
    {
        // Arrange
        var (family, occurrence, members) = await SetupFamilyTestDataAsync(3);

        // Act
        var result = await _service.MarkFamilyAttendedAsync(occurrence.IdKey, family.IdKey);

        // Assert
        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(3, result.Results.Count);

        // Verify all members have attendance
        foreach (var member in members)
        {
            var personAlias = await _context.PersonAliases
                .FirstOrDefaultAsync(pa => pa.PersonId == member.Id && pa.AliasPersonId == null);

            var attendance = await _context.Attendances
                .FirstOrDefaultAsync(a => a.OccurrenceId == occurrence.Id && a.PersonAliasId == personAlias!.Id);

            Assert.NotNull(attendance);
            Assert.True(attendance.DidAttend);
        }
    }

    [Fact]
    public async Task MarkFamilyAttendedAsync_InvalidFamilyId_ReturnsFalse()
    {
        // Arrange
        var (_, occurrence) = await SetupTestDataAsync();

        // Act
        var result = await _service.MarkFamilyAttendedAsync(occurrence.IdKey, "invalid-key");

        // Assert
        Assert.False(result.AllSucceeded);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
    }

    [Fact]
    public async Task UnmarkAttendedAsync_ExistingAttendance_SetsDidAttendFalse()
    {
        // Arrange
        var (person, occurrence) = await SetupTestDataAsync();
        await _service.MarkAttendedAsync(occurrence.IdKey, person.IdKey);

        // Act
        var result = await _service.UnmarkAttendedAsync(occurrence.IdKey, person.IdKey);

        // Assert
        Assert.True(result);

        // Verify attendance was updated
        var personAlias = await _context.PersonAliases
            .FirstOrDefaultAsync(pa => pa.PersonId == person.Id && pa.AliasPersonId == null);

        var attendance = await _context.Attendances
            .FirstOrDefaultAsync(a => a.OccurrenceId == occurrence.Id && a.PersonAliasId == personAlias!.Id);

        Assert.NotNull(attendance);
        Assert.False(attendance.DidAttend);
        Assert.Null(attendance.PresentDateTime);
    }

    [Fact]
    public async Task UnmarkAttendedAsync_NoAttendance_ReturnsFalse()
    {
        // Arrange
        var (person, occurrence) = await SetupTestDataAsync();

        // Act
        var result = await _service.UnmarkAttendedAsync(occurrence.IdKey, person.IdKey);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetOccurrenceRosterAsync_ValidOccurrence_ReturnsRoster()
    {
        // Arrange
        var (group, occurrence, members) = await SetupGroupTestDataAsync(3);

        // Mark one member as attended
        await _service.MarkAttendedAsync(occurrence.IdKey, members[0].IdKey);

        // Act
        var roster = await _service.GetOccurrenceRosterAsync(occurrence.IdKey);

        // Assert
        Assert.Equal(3, roster.Count);
        Assert.Single(roster, r => r.IsAttending);
        Assert.Equal(2, roster.Count(r => !r.IsAttending));

        var attendedMember = roster.First(r => r.IsAttending);
        Assert.NotNull(attendedMember.AttendanceIdKey);
        Assert.NotNull(attendedMember.PresentDateTime);
    }

    [Fact]
    public async Task GetFamilyGroupedRosterAsync_ValidOccurrence_ReturnsFamilyGroups()
    {
        // Arrange
        var (group, occurrence, family1, family2) = await SetupMultipleFamiliesTestDataAsync();

        // Mark one person from family1
        var family1Members = await _context.FamilyMembers
            .Where(fm => fm.FamilyId == family1.Id)
            .Select(fm => fm.Person)
            .ToListAsync();

        await _service.MarkAttendedAsync(occurrence.IdKey, family1Members[0].IdKey);

        // Act
        var groups = await _service.GetFamilyGroupedRosterAsync(occurrence.IdKey);

        // Assert
        Assert.Equal(2, groups.Count);

        var family1Group = groups.First(g => g.FamilyIdKey == family1.IdKey);
        Assert.Equal(1, family1Group.AttendingCount);
        Assert.Equal(2, family1Group.TotalCount);
    }

    [Fact]
    public async Task GetFamilyGroupedRosterAsync_WithSearchTerm_FiltersResults()
    {
        // Arrange
        var (group, occurrence, family1, family2) = await SetupMultipleFamiliesTestDataAsync();

        // Act
        var groups = await _service.GetFamilyGroupedRosterAsync(occurrence.IdKey, family1.Name);

        // Assert
        Assert.Single(groups);
        Assert.Equal(family1.IdKey, groups[0].FamilyIdKey);
    }

    [Fact]
    public async Task BulkMarkAttendedAsync_ValidPersons_MarksAll()
    {
        // Arrange
        var (group, occurrence, members) = await SetupGroupTestDataAsync(3);
        var personIdKeys = members.Select(m => m.IdKey).ToArray();

        // Act
        var result = await _service.BulkMarkAttendedAsync(occurrence.IdKey, personIdKeys);

        // Assert
        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);

        // Verify all have attendance
        foreach (var member in members)
        {
            var personAlias = await _context.PersonAliases
                .FirstOrDefaultAsync(pa => pa.PersonId == member.Id && pa.AliasPersonId == null);

            var attendance = await _context.Attendances
                .FirstOrDefaultAsync(a => a.OccurrenceId == occurrence.Id && a.PersonAliasId == personAlias!.Id);

            Assert.NotNull(attendance);
            Assert.True(attendance.DidAttend);
        }
    }

    [Fact]
    public async Task BulkMarkAttendedAsync_MixedValidInvalid_ReturnsPartialSuccess()
    {
        // Arrange
        var (group, occurrence, members) = await SetupGroupTestDataAsync(2);
        var personIdKeys = new[] { members[0].IdKey, "invalid-key", members[1].IdKey };

        // Act
        var result = await _service.BulkMarkAttendedAsync(occurrence.IdKey, personIdKeys);

        // Assert
        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
    }

    // Helper methods

    private async Task<(Person person, AttendanceOccurrence occurrence)> SetupTestDataAsync()
    {
        var person = new Person
        {
            FirstName = "John",
            LastName = "Doe",
            RecordStatusValueId = 1
        };
        _context.People.Add(person);

        var personAlias = new PersonAlias
        {
            Person = person,
            PersonId = person.Id
        };
        _context.PersonAliases.Add(personAlias);

        var group = new Group
        {
            Name = "Test Group",
            GroupTypeId = 1,
            IsActive = true
        };
        _context.Groups.Add(group);

        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SundayDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _context.AttendanceOccurrences.Add(occurrence);

        await _context.SaveChangesAsync();

        return (person, occurrence);
    }

    private async Task<(Family family, AttendanceOccurrence occurrence, List<Person> members)> SetupFamilyTestDataAsync(int memberCount)
    {
        var family = new Family
        {
            Name = "Smith Family",
            IsActive = true
        };
        _context.Families.Add(family);

        var group = new Group
        {
            Name = "Test Group",
            GroupTypeId = 1,
            IsActive = true
        };
        _context.Groups.Add(group);

        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SundayDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _context.AttendanceOccurrences.Add(occurrence);

        var members = new List<Person>();
        for (int i = 0; i < memberCount; i++)
        {
            var person = new Person
            {
                FirstName = $"Member{i}",
                LastName = "Smith",
                RecordStatusValueId = 1
            };
            _context.People.Add(person);

            var personAlias = new PersonAlias
            {
                Person = person,
                PersonId = person.Id
            };
            _context.PersonAliases.Add(personAlias);

            var familyMember = new FamilyMember
            {
                Family = family,
                Person = person,
                FamilyRoleId = 1,
                IsPrimary = true,
                DateAdded = DateTime.UtcNow
            };
            _context.FamilyMembers.Add(familyMember);

            members.Add(person);
        }

        await _context.SaveChangesAsync();

        return (family, occurrence, members);
    }

    private async Task<(Group group, AttendanceOccurrence occurrence, List<Person> members)> SetupGroupTestDataAsync(int memberCount)
    {
        var group = new Group
        {
            Name = "Test Group",
            GroupTypeId = 1,
            IsActive = true
        };
        _context.Groups.Add(group);

        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SundayDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _context.AttendanceOccurrences.Add(occurrence);

        var members = new List<Person>();
        for (int i = 0; i < memberCount; i++)
        {
            var person = new Person
            {
                FirstName = $"Member{i}",
                LastName = $"User{i}",
                RecordStatusValueId = 1
            };
            _context.People.Add(person);

            var personAlias = new PersonAlias
            {
                Person = person,
                PersonId = person.Id
            };
            _context.PersonAliases.Add(personAlias);

            var groupMember = new GroupMember
            {
                PersonId = person.Id,
                GroupId = group.Id,
                GroupRoleId = 1
            };
            _context.GroupMembers.Add(groupMember);

            members.Add(person);
        }

        await _context.SaveChangesAsync();

        return (group, occurrence, members);
    }

    private async Task<(Group group, AttendanceOccurrence occurrence, Family family1, Family family2)> SetupMultipleFamiliesTestDataAsync()
    {
        var group = new Group
        {
            Name = "Test Group",
            GroupTypeId = 1,
            IsActive = true
        };
        _context.Groups.Add(group);

        var occurrence = new AttendanceOccurrence
        {
            GroupId = group.Id,
            OccurrenceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SundayDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        _context.AttendanceOccurrences.Add(occurrence);

        var family1 = new Family { Name = "Smith Family", IsActive = true };
        var family2 = new Family { Name = "Jones Family", IsActive = true };
        _context.Families.AddRange(family1, family2);

        // Add 2 members to each family
        for (int i = 0; i < 2; i++)
        {
            var person1 = new Person
            {
                FirstName = $"Smith{i}",
                LastName = "Smith",
                RecordStatusValueId = 1
            };
            _context.People.Add(person1);

            var personAlias1 = new PersonAlias
            {
                Person = person1,
                PersonId = person1.Id
            };
            _context.PersonAliases.Add(personAlias1);

            _context.FamilyMembers.Add(new FamilyMember
            {
                Family = family1,
                Person = person1,
                FamilyRoleId = 1,
                IsPrimary = true,
                DateAdded = DateTime.UtcNow
            });

            _context.GroupMembers.Add(new GroupMember
            {
                PersonId = person1.Id,
                GroupId = group.Id,
                GroupRoleId = 1
            });

            var person2 = new Person
            {
                FirstName = $"Jones{i}",
                LastName = "Jones",
                RecordStatusValueId = 1
            };
            _context.People.Add(person2);

            var personAlias2 = new PersonAlias
            {
                Person = person2,
                PersonId = person2.Id
            };
            _context.PersonAliases.Add(personAlias2);

            _context.FamilyMembers.Add(new FamilyMember
            {
                Family = family2,
                Person = person2,
                FamilyRoleId = 1,
                IsPrimary = true,
                DateAdded = DateTime.UtcNow
            });

            _context.GroupMembers.Add(new GroupMember
            {
                PersonId = person2.Id,
                GroupId = group.Id,
                GroupRoleId = 1
            });
        }

        await _context.SaveChangesAsync();

        return (group, occurrence, family1, family2);
    }
}
