using AutoMapper;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
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
/// Regression tests for bug #708 ("NoTracking-default silent-mutation sweep").
///
/// The production DbContext (PostgreSqlProvider) sets
/// <see cref="QueryTrackingBehavior.NoTracking"/> as the global default. Any service
/// that loads an entity via FirstOrDefaultAsync/FindAsync WITHOUT calling
/// <c>AsTracking()</c> receives a DETACHED entity. Mutations to that detached entity
/// are silently dropped on SaveChanges — the API returns success but the DB row is
/// unchanged.
///
/// Each test in this class:
///   1. Uses KoinonDbContext configured with NoTracking (mirrors prod).
///   2. Invokes the service method that mutates.
///   3. Calls <c>ChangeTracker.Clear()</c> so the subsequent read is a FRESH DB read,
///      not a tracker-cache hit.
///   4. Reloads via AsNoTracking and asserts the mutation actually persisted.
///
/// These tests FAIL against the pre-fix service and PASS after the fix.
///
/// Previous sweeps: #664 (batch), #694/#695 (device), #707 (12 admin/config services).
/// This sweep (#708): Person photo+note, Family primary, Group add/remove/schedules,
/// BatchDonationEntry contributions, MyProfile (self + admin-on-child), UserSettings
/// (2FA/password/prefs/session — security-critical), + sweep-found services.
/// </summary>
public class NoTrackingSweepTests
{
    private static KoinonDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"NoTrackingSweep_{Guid.NewGuid()}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var ctx = new KoinonDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    // -----------------------------------------------------------------------
    // PersonService.UpdatePhotoAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PersonService_UpdatePhotoAsync_UnderNoTracking_PersistsPhotoIdToDatabase()
    {
        using var context = CreateContext();

        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Jane",
            LastName = "Doe",
            Gender = Gender.Female,
            CreatedDateTime = DateTime.UtcNow
        });
        context.BinaryFiles.Add(new BinaryFile
        {
            Id = 10,
            FileName = "headshot.jpg",
            MimeType = "image/jpeg",
            StorageKey = "photos/headshot.jpg",
            FileSizeBytes = 12345,
            CreatedDateTime = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var personIdKey = IdKeyHelper.Encode(1);
        var photoIdKey = IdKeyHelper.Encode(10);

        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(x => x.CurrentPersonId).Returns(1);
        userCtx.Setup(x => x.IsAuthenticated).Returns(true);
        userCtx.Setup(x => x.CanAccessPerson(1)).Returns(true);

        var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile<PersonMappingProfile>(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var service = new PersonService(
            context,
            mapperConfig.CreateMapper(),
            new CreatePersonRequestValidator(),
            new UpdatePersonRequestValidator(),
            userCtx.Object,
            Mock.Of<ILogger<PersonService>>());

        var result = await service.UpdatePhotoAsync(personIdKey, photoIdKey);

        result.IsSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        var reloaded = await context.People
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == 1);
        reloaded.Should().NotBeNull();
        reloaded!.PhotoId.Should().Be(10,
            "PhotoId must be persisted; pre-fix the mutation was silently dropped under NoTracking.");
    }

    // -----------------------------------------------------------------------
    // FamilyService.SetPrimaryFamilyAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FamilyService_SetPrimaryFamilyAsync_UnderNoTracking_PersistsIsPrimaryFlip()
    {
        using var context = CreateContext();

        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Child",
            LastName = "Doe",
            Gender = Gender.Male,
            CreatedDateTime = DateTime.UtcNow
        });
        var familyA = new Family { Id = 100, Name = "Doe Family A", CreatedDateTime = DateTime.UtcNow };
        var familyB = new Family { Id = 101, Name = "Doe Family B", CreatedDateTime = DateTime.UtcNow };
        context.Families.AddRange(familyA, familyB);
        context.FamilyMembers.AddRange(
            new FamilyMember
            {
                Id = 500,
                FamilyId = 100,
                PersonId = 1,
                IsPrimary = true, // currently primary
                CreatedDateTime = DateTime.UtcNow
            },
            new FamilyMember
            {
                Id = 501,
                FamilyId = 101,
                PersonId = 1,
                IsPrimary = false,
                CreatedDateTime = DateTime.UtcNow
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var personIdKey = IdKeyHelper.Encode(1);
        var newPrimaryIdKey = IdKeyHelper.Encode(101);

        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(x => x.CurrentPersonId).Returns(1);
        userCtx.Setup(x => x.IsAuthenticated).Returns(true);
        userCtx.Setup(x => x.CanAccessPerson(It.IsAny<int>())).Returns(true);
        userCtx.Setup(x => x.IsInRole(It.IsAny<string>())).Returns(true);

        var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile<FamilyMappingProfile>(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var createFamilyValidator = new Mock<IValidator<CreateFamilyRequest>>();
        createFamilyValidator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateFamilyRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var addMemberValidator = new Mock<IValidator<AddFamilyMemberRequest>>();
        addMemberValidator
            .Setup(v => v.ValidateAsync(It.IsAny<AddFamilyMemberRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var service = new FamilyService(
            context,
            mapperConfig.CreateMapper(),
            userCtx.Object,
            createFamilyValidator.Object,
            addMemberValidator.Object,
            Mock.Of<ILogger<FamilyService>>());

        var result = await service.SetPrimaryFamilyAsync(personIdKey, newPrimaryIdKey);

        result.IsSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        var members = await context.FamilyMembers
            .AsNoTracking()
            .Where(fm => fm.PersonId == 1)
            .ToListAsync();
        members.Single(m => m.FamilyId == 101).IsPrimary.Should().BeTrue(
            "new primary family membership must be persisted; pre-fix the IsPrimary=true mutation was silently dropped.");
        members.Single(m => m.FamilyId == 100).IsPrimary.Should().BeFalse(
            "previous primary must be persisted as non-primary; pre-fix the IsPrimary=false mutation was silently dropped.");
    }

    // -----------------------------------------------------------------------
    // GroupService.RemoveMemberAsync (soft delete via status mutation)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GroupService_RemoveMemberAsync_UnderNoTracking_PersistsInactiveStatus()
    {
        using var context = CreateContext();

        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Alice",
            LastName = "Smith",
            Gender = Gender.Female,
            CreatedDateTime = DateTime.UtcNow
        });
        context.GroupTypes.Add(new GroupType
        {
            Id = 10,
            Name = "Small Group",
            CreatedDateTime = DateTime.UtcNow
        });
        context.Groups.Add(new Group
        {
            Id = 200,
            Name = "Tuesday Night",
            GroupTypeId = 10,
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        });
        context.GroupTypeRoles.Add(new GroupTypeRole
        {
            Id = 55,
            GroupTypeId = 10,
            Name = "Member",
            CreatedDateTime = DateTime.UtcNow
        });
        context.GroupMembers.Add(new GroupMember
        {
            Id = 300,
            GroupId = 200,
            PersonId = 1,
            GroupRoleId = 55,
            GroupMemberStatus = GroupMemberStatus.Active,
            CreatedDateTime = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var groupIdKey = IdKeyHelper.Encode(200);
        var personIdKey = IdKeyHelper.Encode(1);

        var addMemberValidator = new Mock<IValidator<AddGroupMemberRequest>>();
        addMemberValidator
            .Setup(v => v.ValidateAsync(It.IsAny<AddGroupMemberRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var createValidator = new Mock<IValidator<CreateGroupRequest>>();
        createValidator
            .Setup(v => v.ValidateAsync(It.IsAny<CreateGroupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var updateValidator = new Mock<IValidator<UpdateGroupRequest>>();
        updateValidator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateGroupRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile<GroupMappingProfile>(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var service = new GroupService(
            context,
            mapperConfig.CreateMapper(),
            createValidator.Object,
            updateValidator.Object,
            addMemberValidator.Object,
            Mock.Of<ILogger<GroupService>>());

        var result = await service.RemoveMemberAsync(groupIdKey, personIdKey);

        result.IsSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        var reloaded = await context.GroupMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(gm => gm.Id == 300);
        reloaded.Should().NotBeNull();
        reloaded!.GroupMemberStatus.Should().Be(GroupMemberStatus.Inactive,
            "soft-delete mutation must be persisted; pre-fix the status mutation was silently dropped.");
    }

    // -----------------------------------------------------------------------
    // MyProfileService.UpdateMyProfileAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MyProfileService_UpdateMyProfileAsync_UnderNoTracking_PersistsEmailAndNickName()
    {
        using var context = CreateContext();

        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Pat",
            LastName = "Example",
            Email = "old@example.com",
            NickName = "OldNick",
            Gender = Gender.Unknown,
            EmailPreference = EmailPreference.EmailAllowed,
            CreatedDateTime = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(x => x.CurrentPersonId).Returns(1);
        userCtx.Setup(x => x.IsAuthenticated).Returns(true);

        var updateValidator = new Mock<IValidator<UpdateMyProfileRequest>>();
        updateValidator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateMyProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var updateFamilyMemberValidator = new Mock<IValidator<UpdateFamilyMemberRequest>>();
        updateFamilyMemberValidator
            .Setup(v => v.ValidateAsync(It.IsAny<UpdateFamilyMemberRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var service = new MyProfileService(
            context,
            userCtx.Object,
            updateValidator.Object,
            updateFamilyMemberValidator.Object,
            Mock.Of<ILogger<MyProfileService>>());

        var request = new UpdateMyProfileRequest
        {
            Email = "new@example.com",
            NickName = "NewNick"
        };

        var result = await service.UpdateMyProfileAsync(request);

        result.IsSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        var reloaded = await context.People
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == 1);
        reloaded.Should().NotBeNull();
        reloaded!.Email.Should().Be("new@example.com",
            "Email must be persisted; pre-fix the mutation was silently dropped under NoTracking.");
        reloaded.NickName.Should().Be("NewNick");
    }

    // -----------------------------------------------------------------------
    // BatchDonationEntryService.UpdateContributionAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BatchDonationEntryService_UpdateContributionAsync_UnderNoTracking_PersistsMutation()
    {
        using var context = CreateContext();

        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Giver",
            LastName = "One",
            Gender = Gender.Unknown,
            CreatedDateTime = DateTime.UtcNow
        });
        context.PersonAliases.Add(new PersonAlias
        {
            Id = 1000,
            PersonId = 1,
            CreatedDateTime = DateTime.UtcNow
        });

        // Defined type/value for transaction type
        context.DefinedTypes.Add(new DefinedType
        {
            Id = 10,
            Name = "Transaction Type",
            IsSystem = true,
            CreatedDateTime = DateTime.UtcNow
        });
        context.DefinedValues.Add(new DefinedValue
        {
            Id = 100,
            DefinedTypeId = 10,
            Value = "Contribution",
            IsActive = true,
            CreatedDateTime = DateTime.UtcNow
        });

        context.Funds.Add(new Fund
        {
            Id = 20,
            Name = "General",
            IsActive = true,
            Order = 1,
            CreatedDateTime = DateTime.UtcNow
        });

        var batch = new ContributionBatch
        {
            Id = 50,
            Name = "Sunday Batch",
            BatchDate = DateTime.UtcNow.Date,
            Status = BatchStatus.Open,
            CreatedDateTime = DateTime.UtcNow
        };
        context.ContributionBatches.Add(batch);

        var contribution = new Contribution
        {
            Id = 60,
            BatchId = 50,
            TransactionDateTime = DateTime.UtcNow,
            TransactionCode = "OLD-CODE",
            TransactionTypeValueId = 100,
            SourceTypeValueId = 100,
            Summary = "Old summary",
            CreatedDateTime = DateTime.UtcNow
        };
        context.Contributions.Add(contribution);
        context.ContributionDetails.Add(new ContributionDetail
        {
            Id = 70,
            ContributionId = 60,
            FundId = 20,
            Amount = 100m,
            CreatedDateTime = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var userCtx = new Mock<IUserContext>();
        userCtx.Setup(x => x.CurrentPersonId).Returns(1);
        userCtx.Setup(x => x.IsAuthenticated).Returns(true);

        var createBatchValidator = new Mock<IValidator<CreateBatchRequest>>();
        createBatchValidator.Setup(v => v.ValidateAsync(It.IsAny<CreateBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var addContribValidator = new Mock<IValidator<AddContributionRequest>>();
        addContribValidator.Setup(v => v.ValidateAsync(It.IsAny<AddContributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        var updateContribValidator = new Mock<IValidator<UpdateContributionRequest>>();
        updateContribValidator.Setup(v => v.ValidateAsync(It.IsAny<UpdateContributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var service = new BatchDonationEntryService(
            context,
            userCtx.Object,
            createBatchValidator.Object,
            addContribValidator.Object,
            updateContribValidator.Object,
            Mock.Of<ILogger<BatchDonationEntryService>>());

        var updateRequest = new UpdateContributionRequest
        {
            TransactionDateTime = DateTime.UtcNow,
            TransactionCode = "NEW-CODE",
            TransactionTypeValueIdKey = IdKeyHelper.Encode(100),
            Summary = "New summary",
            Details = new List<ContributionDetailRequest>
            {
                new() { FundIdKey = IdKeyHelper.Encode(20), Amount = 200m }
            }
        };

        var result = await service.UpdateContributionAsync(IdKeyHelper.Encode(60), updateRequest);

        result.IsSuccess.Should().BeTrue();

        context.ChangeTracker.Clear();
        var reloaded = await context.Contributions
            .AsNoTracking()
            .Include(c => c.ContributionDetails)
            .FirstOrDefaultAsync(c => c.Id == 60);
        reloaded.Should().NotBeNull();
        reloaded!.TransactionCode.Should().Be("NEW-CODE",
            "TransactionCode mutation must be persisted; pre-fix it was silently dropped under NoTracking.");
        reloaded.Summary.Should().Be("New summary");
        reloaded.ContributionDetails.Sum(d => d.Amount).Should().Be(200m);
    }
}
