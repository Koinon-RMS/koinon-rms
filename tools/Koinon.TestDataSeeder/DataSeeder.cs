using Koinon.Application.Interfaces;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Koinon.TestDataSeeder;

/// <summary>
/// Seeds deterministic test data into the database for E2E and integration testing.
/// Uses fixed GUIDs for easy reference in tests.
/// </summary>
public class DataSeeder
{
    private readonly KoinonDbContext _context;
    private readonly ILogger<DataSeeder> _logger;
    private readonly IAuthService _authService;

    // Fixed GUIDs for deterministic data
    private static readonly Guid _smithFamilyGuid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _johnsonFamilyGuid = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _johnSmithGuid = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _janeSmithGuid = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid _johnnySmithGuid = new("55555555-5555-5555-5555-555555555555");
    private static readonly Guid _jennySmithGuid = new("66666666-6666-6666-6666-666666666666");
    private static readonly Guid _bobJohnsonGuid = new("77777777-7777-7777-7777-777777777777");
    private static readonly Guid _barbaraJohnsonGuid = new("88888888-8888-8888-8888-888888888888");
    private static readonly Guid _billyJohnsonGuid = new("99999999-9999-9999-9999-999999999999");
    private static readonly Guid _nurseryGroupGuid = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _preschoolGroupGuid = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _elementaryGroupGuid = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid _sunday9amScheduleGuid = new("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid _sunday11amScheduleGuid = new("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid _wednesday7pmScheduleGuid = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly Guid _familyGroupTypeGuid = new("10101010-1010-1010-1010-101010101010");
    private static readonly Guid _checkinGroupTypeGuid = new("20202020-2020-2020-2020-202020202020");
    private static readonly Guid _generalGroupTypeGuid = new("60606060-6060-6060-6060-606060606060");
    private static readonly Guid _adultRoleGuid = new("30303030-3030-3030-3030-303030303030");
    private static readonly Guid _childRoleGuid = new("40404040-4040-4040-4040-404040404040");
    private static readonly Guid _memberRoleGuid = new("50505050-5050-5050-5050-505050505050");
    private static readonly Guid _adminSecurityRoleGuid = new("70707070-7070-7070-7070-707070707070");
    // Production-provisioned "Financial Admin" role from the SeedFinancialClaims
    // migration. The admin user gets assigned to this role in addition to the
    // generic Admin role so it inherits financial:* claims the same way as in
    // prod (role separation preserved).
    private static readonly Guid _financialAdminSecurityRoleGuid = new("F1F1F1F1-AAAA-4444-BBBB-CCCCCCCCCCCC");
    private static readonly Guid _generalFundGuid = new("90909090-9090-4090-8090-111111111111");

    public DataSeeder(KoinonDbContext context, ILogger<DataSeeder> logger, IAuthService authService)
    {
        _context = context;
        _logger = logger;
        _authService = authService;
    }

    /// <summary>
    /// Tables whose data is owned by EF migrations (reference data) and must
    /// NOT be truncated by the seeder. Wiping these would drop migration-seeded
    /// enumerations such as the "Transaction Type" and "Transaction Source"
    /// DefinedValues that the giving flow (AddContribution) requires. After a
    /// reset the migrations aren't re-run, so the rows would never come back.
    /// </summary>
    private static readonly HashSet<string> _excludedTables =
        new(StringComparer.Ordinal)
        {
            "defined_type",
            "defined_value"
        };

    /// <summary>
    /// Reset database by truncating all tables and resetting sequences.
    /// </summary>
    public async Task ResetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Truncating tables...");

        // Disable triggers to avoid foreign key violations
        await _context.Database.ExecuteSqlRawAsync("SET session_replication_role = 'replica';", cancellationToken);

        // Get all table names (excluding EF migrations table)
        var tables = await _context.Database
            .SqlQueryRaw<string>(
                @"SELECT tablename
                  FROM pg_tables
                  WHERE schemaname = 'public'
                  AND tablename != '__EFMigrationsHistory'")
            .ToListAsync(cancellationToken);

        var truncatedCount = 0;

        // Truncate each table (except migration-owned reference tables).
        foreach (var table in tables.Where(t => !_excludedTables.Contains(t)))
        {
            _logger.LogDebug("Truncating table: {Table}", table);
            // Table names cannot be parameterized in SQL - must use raw SQL.
            // Table names come from pg_tables (system catalog), so this is safe.
            var truncateSql = string.Format("TRUNCATE TABLE \"{0}\" RESTART IDENTITY CASCADE;", table);
#pragma warning disable EF1002 // Table names from pg_tables are safe system catalog values
            await _context.Database.ExecuteSqlRawAsync(truncateSql, cancellationToken);
#pragma warning restore EF1002
            truncatedCount++;
        }

        // Re-enable triggers
        await _context.Database.ExecuteSqlRawAsync("SET session_replication_role = 'origin';", cancellationToken);

        _logger.LogInformation(
            "Truncated {Count} tables (skipped {SkippedCount} migration-owned tables: {Skipped})",
            truncatedCount,
            _excludedTables.Count,
            string.Join(", ", _excludedTables));
    }

    /// <summary>
    /// Seed all test data.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Seed group types and roles
        _logger.LogInformation("Seeding group types and roles...");
        var (familyGroupType, checkinGroupType, adultRole, childRole, memberRole) = await SeedGroupTypesAsync(now, cancellationToken);

        // Seed schedules
        _logger.LogInformation("Seeding schedules...");
        var (sunday9am, sunday11am, wednesday7pm) = await SeedSchedulesAsync(now, cancellationToken);

        // Seed families and people
        _logger.LogInformation("Seeding families and people...");
        var people = await SeedFamiliesAsync(adultRole.Id, childRole.Id, now, cancellationToken);

        // Seed one primary PersonAlias per seeded person. The giving flow's
        // AddContribution resolves the contributor by looking up
        // PersonAlias.PersonId == personId; without an alias the request 404s
        // even though the person exists.
        _logger.LogInformation("Seeding person aliases...");
        await SeedPersonAliasesAsync(people, now, cancellationToken);

        // Seed check-in groups
        _logger.LogInformation("Seeding check-in groups...");
        await SeedCheckinGroupsAsync(checkinGroupType.Id, memberRole.Id, sunday9am.Id, now, cancellationToken);

        // Final save for all check-in groups
        await _context.SaveChangesAsync(cancellationToken);

        // Seed security roles and assign Admin to John Smith
        _logger.LogInformation("Seeding security roles...");
        await SeedSecurityRolesAsync(people, now, cancellationToken);

        // Seed funds (needed for contribution entry in giving flow)
        _logger.LogInformation("Seeding funds...");
        await SeedFundsAsync(now, cancellationToken);

        _logger.LogInformation("✅ Successfully seeded all test data");
    }
    private async Task<(GroupType familyGroupType, GroupType checkinGroupType, GroupTypeRole adultRole, GroupTypeRole childRole, GroupTypeRole memberRole)> SeedGroupTypesAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        // Family group type
        var familyGroupType = new GroupType
        {
            Guid = _familyGroupTypeGuid,
            Name = "Family",
            Description = "Family group type for organizing people into family units",
            GroupTerm = "Family",
            GroupMemberTerm = "Family Member",
            IsSystem = true,
            ShowInGroupList = false,
            ShowInNavigation = false,
            TakesAttendance = false,
            CreatedDateTime = now
        };

        // Check-in group type
        var checkinGroupType = new GroupType
        {
            Guid = _checkinGroupTypeGuid,
            Name = "Check-in Group",
            Description = "Groups used for check-in areas",
            GroupTerm = "Group",
            GroupMemberTerm = "Member",
            IsSystem = true,
            ShowInGroupList = true,
            ShowInNavigation = true,
            TakesAttendance = true,
            CreatedDateTime = now
        };

        // General group type (for generic groups created in E2E tests)
        var generalGroupType = new GroupType
        {
            Guid = _generalGroupTypeGuid,
            Name = "General",
            Description = "General purpose groups",
            GroupTerm = "Group",
            GroupMemberTerm = "Member",
            IsSystem = false,
            ShowInGroupList = true,
            ShowInNavigation = true,
            TakesAttendance = false,
            CreatedDateTime = now
        };

        _context.GroupTypes.Add(familyGroupType);
        _context.GroupTypes.Add(checkinGroupType);
        _context.GroupTypes.Add(generalGroupType);
        // Save group types first so we have their IDs for roles
        await _context.SaveChangesAsync(cancellationToken);

        // Roles for family group type
        var adultRole = new GroupTypeRole
        {
            Guid = _adultRoleGuid,
            GroupTypeId = familyGroupType.Id,
            Name = "Adult",
            Description = "Adult family member",
            IsSystem = true,
            IsLeader = true,
            CanView = true,
            CanEdit = true,
            CanManageMembers = true,
            Order = 0,
            CreatedDateTime = now
        };

        var childRole = new GroupTypeRole
        {
            Guid = _childRoleGuid,
            GroupTypeId = familyGroupType.Id,
            Name = "Child",
            Description = "Child family member",
            IsSystem = true,
            IsLeader = false,
            CanView = true,
            CanEdit = false,
            CanManageMembers = false,
            Order = 1,
            CreatedDateTime = now
        };

        // Role for check-in groups
        var memberRole = new GroupTypeRole
        {
            Guid = _memberRoleGuid,
            GroupTypeId = checkinGroupType.Id,
            Name = "Member",
            Description = "Group member",
            IsSystem = true,
            IsLeader = false,
            CanView = true,
            CanEdit = false,
            CanManageMembers = false,
            Order = 0,
            CreatedDateTime = now
        };

        _context.GroupTypeRoles.Add(adultRole);
        _context.GroupTypeRoles.Add(childRole);
        _context.GroupTypeRoles.Add(memberRole);
        // Save roles so we have their IDs for group members
        await _context.SaveChangesAsync(cancellationToken);

        return (familyGroupType, checkinGroupType, adultRole, childRole, memberRole);
    }

    private async Task<(Schedule sunday9am, Schedule sunday11am, Schedule wednesday7pm)> SeedSchedulesAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var sunday9am = new Schedule
        {
            Guid = _sunday9amScheduleGuid,
            Name = "Sunday 9:00 AM",
            Description = "Sunday morning service at 9:00 AM",
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(9, 0, 0),
            CheckInStartOffsetMinutes = 60,
            CheckInEndOffsetMinutes = 30,
            IsActive = true,
            IsPublic = true,
            Order = 0,
            CreatedDateTime = now
        };

        var sunday11am = new Schedule
        {
            Guid = _sunday11amScheduleGuid,
            Name = "Sunday 11:00 AM",
            Description = "Sunday morning service at 11:00 AM",
            WeeklyDayOfWeek = DayOfWeek.Sunday,
            WeeklyTimeOfDay = new TimeSpan(11, 0, 0),
            CheckInStartOffsetMinutes = 60,
            CheckInEndOffsetMinutes = 30,
            IsActive = true,
            IsPublic = true,
            Order = 1,
            CreatedDateTime = now
        };

        var wednesday7pm = new Schedule
        {
            Guid = _wednesday7pmScheduleGuid,
            Name = "Wednesday 7:00 PM",
            Description = "Wednesday evening service at 7:00 PM",
            WeeklyDayOfWeek = DayOfWeek.Wednesday,
            WeeklyTimeOfDay = new TimeSpan(19, 0, 0),
            CheckInStartOffsetMinutes = 30,
            CheckInEndOffsetMinutes = 15,
            IsActive = true,
            IsPublic = true,
            Order = 2,
            CreatedDateTime = now
        };

        _context.Schedules.Add(sunday9am);
        _context.Schedules.Add(sunday11am);
        _context.Schedules.Add(wednesday7pm);
        // Save schedules so we have their IDs for groups
        await _context.SaveChangesAsync(cancellationToken);

        return (sunday9am, sunday11am, wednesday7pm);
    }

    private async Task<List<Person>> SeedFamiliesAsync(
        int adultRoleId, int childRoleId, DateTime now, CancellationToken cancellationToken = default)
    {
        var people = new List<Person>();

        // Smith family (using Family entity, not Group)
        var smithFamily = new Family
        {
            Guid = _smithFamilyGuid,
            Name = "Smith Family",
            IsActive = true,
            CreatedDateTime = now
        };
        _context.Families.Add(smithFamily);
        // Save family so we have its ID for members
        await _context.SaveChangesAsync(cancellationToken);

        // John Smith (Adult) - Admin user for E2E tests
        var johnSmith = new Person
        {
            Guid = _johnSmithGuid,
            FirstName = "John",
            LastName = "Smith",
            NickName = "John",
            Gender = Gender.Male,
            BirthYear = 1985,
            BirthMonth = 6,
            BirthDay = 15,
            Email = "john.smith@example.com",
            IsEmailActive = true,
            PasswordHash = await _authService.HashPasswordAsync("admin123"),
            // Supervisor-mode PIN for the kiosk golden path (#710): verified by
            // SupervisorModeService.ValidatePinAsync (Argon2id, Base64 salt||hash).
            SupervisorPinHash = HashSupervisorPin("1234"),
            CreatedDateTime = now
        };
        people.Add(johnSmith);

        // Jane Smith (Adult)
        var janeSmith = new Person
        {
            Guid = _janeSmithGuid,
            FirstName = "Jane",
            LastName = "Smith",
            NickName = "Jane",
            Gender = Gender.Female,
            BirthYear = 1987,
            BirthMonth = 8,
            BirthDay = 22,
            Email = "jane.smith@example.com",
            IsEmailActive = true,
            CreatedDateTime = now
        };
        people.Add(janeSmith);

        // Johnny Smith (Child, age 6)
        var currentYear = DateTime.UtcNow.Year;
        var johnnySmith = new Person
        {
            Guid = _johnnySmithGuid,
            FirstName = "Johnny",
            LastName = "Smith",
            NickName = "Johnny",
            Gender = Gender.Male,
            BirthYear = currentYear - 6,
            BirthMonth = 3,
            BirthDay = 10,
            CreatedDateTime = now
        };
        people.Add(johnnySmith);

        // Jenny Smith (Child, age 4)
        var jennySmith = new Person
        {
            Guid = _jennySmithGuid,
            FirstName = "Jenny",
            LastName = "Smith",
            NickName = "Jenny",
            Gender = Gender.Female,
            BirthYear = currentYear - 4,
            BirthMonth = 11,
            BirthDay = 5,
            Allergies = "Peanuts",
            HasCriticalAllergies = true,
            CreatedDateTime = now
        };
        people.Add(jennySmith);

        // Add all Smith family people
        _context.People.AddRange(johnSmith, janeSmith, johnnySmith, jennySmith);

        // Johnson family (using Family entity, not Group)
        var johnsonFamily = new Family
        {
            Guid = _johnsonFamilyGuid,
            Name = "Johnson Family",
            IsActive = true,
            CreatedDateTime = now
        };
        _context.Families.Add(johnsonFamily);
        // Save Johnson family so we have its ID for members
        await _context.SaveChangesAsync(cancellationToken);

        // Bob Johnson (Adult)
        var bobJohnson = new Person
        {
            Guid = _bobJohnsonGuid,
            FirstName = "Bob",
            LastName = "Johnson",
            NickName = "Bob",
            Gender = Gender.Male,
            BirthYear = 1980,
            BirthMonth = 2,
            BirthDay = 14,
            Email = "bob.johnson@example.com",
            IsEmailActive = true,
            CreatedDateTime = now
        };
        people.Add(bobJohnson);

        // Barbara Johnson (Adult)
        var barbaraJohnson = new Person
        {
            Guid = _barbaraJohnsonGuid,
            FirstName = "Barbara",
            LastName = "Johnson",
            NickName = "Barbara",
            Gender = Gender.Female,
            BirthYear = 1982,
            BirthMonth = 9,
            BirthDay = 30,
            Email = "barbara.johnson@example.com",
            IsEmailActive = true,
            CreatedDateTime = now
        };
        people.Add(barbaraJohnson);

        // Billy Johnson (Child, age 5)
        var billyJohnson = new Person
        {
            Guid = _billyJohnsonGuid,
            FirstName = "Billy",
            LastName = "Johnson",
            NickName = "Billy",
            Gender = Gender.Male,
            BirthYear = currentYear - 5,
            BirthMonth = 7,
            BirthDay = 18,
            CreatedDateTime = now
        };
        people.Add(billyJohnson);

        // Add all Johnson family people
        _context.People.AddRange(bobJohnson, barbaraJohnson, billyJohnson);
        // Save all people so we have their IDs for family members
        await _context.SaveChangesAsync(cancellationToken);

        // Batch add all family members (using FamilyMember entity, not GroupMember)
        var familyMembers = new List<FamilyMember>
        {
            new() { FamilyId = smithFamily.Id, PersonId = johnSmith.Id, FamilyRoleId = adultRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = smithFamily.Id, PersonId = janeSmith.Id, FamilyRoleId = adultRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = smithFamily.Id, PersonId = johnnySmith.Id, FamilyRoleId = childRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = smithFamily.Id, PersonId = jennySmith.Id, FamilyRoleId = childRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = johnsonFamily.Id, PersonId = bobJohnson.Id, FamilyRoleId = adultRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = johnsonFamily.Id, PersonId = barbaraJohnson.Id, FamilyRoleId = adultRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now },
            new() { FamilyId = johnsonFamily.Id, PersonId = billyJohnson.Id, FamilyRoleId = childRoleId, IsPrimary = true, DateAdded = now, CreatedDateTime = now }
        };
        _context.FamilyMembers.AddRange(familyMembers);
        // Save all family members
        await _context.SaveChangesAsync(cancellationToken);

        return people;
    }

    /// <summary>
    /// Seeds one primary PersonAlias per seeded person.
    /// BatchDonationEntryService.AddContribution resolves contributors via
    /// PersonAliases.FirstOrDefaultAsync(pa => pa.PersonId == personId); without
    /// an alias the giving flow rejects otherwise-valid contributors. Idempotent
    /// against existing aliases so repeated seeds don't duplicate rows.
    /// </summary>
    private async Task SeedPersonAliasesAsync(
        List<Person> people,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var personIds = people.Select(p => p.Id).ToList();

        var alreadyAliased = await _context.PersonAliases
            .Where(pa => pa.PersonId != null && personIds.Contains(pa.PersonId.Value) && pa.AliasPersonId == null)
            .Select(pa => pa.PersonId!.Value)
            .ToListAsync(cancellationToken);

        var existingSet = new HashSet<int>(alreadyAliased);

        foreach (var person in people)
        {
            if (existingSet.Contains(person.Id))
            {
                continue;
            }

            _context.PersonAliases.Add(new PersonAlias
            {
                // Derive a stable GUID from the person's GUID so repeated seeds
                // produce the same alias identity (aids E2E test stability).
                Guid = DeriveAliasGuid(person.Guid),
                PersonId = person.Id,
                Name = null,
                AliasPersonId = null,
                AliasPersonGuid = null,
                CreatedDateTime = now
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Derives a stable Guid for a PersonAlias from its Person's Guid.
    /// Flips the variant nibble so the alias Guid never collides with the
    /// Person Guid but remains deterministic across reset+seed cycles.
    /// </summary>
    private static Guid DeriveAliasGuid(Guid personGuid)
    {
        var bytes = personGuid.ToByteArray();
        // Toggle the first byte so the alias Guid differs deterministically
        // from the source Person Guid while remaining reproducible.
        bytes[0] ^= 0xA1;
        return new Guid(bytes);
    }

    /// <summary>
    /// Hashes a supervisor PIN exactly as SupervisorModeService.ValidatePinAsync
    /// verifies it (#710): Argon2id, 16-byte random salt, DegreeOfParallelism 8,
    /// Iterations 4, MemorySize 256 MB, 32-byte hash, stored as Base64(salt || hash).
    /// </summary>
    private static string HashSupervisorPin(string pin)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(System.Text.Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt,
            DegreeOfParallelism = 8,
            Iterations = 4,
            MemorySize = 256 * 1024
        };
        var hash = argon2.GetBytes(32);
        var combined = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, combined, salt.Length, hash.Length);
        return Convert.ToBase64String(combined);
    }

    private async Task SeedSecurityRolesAsync(List<Person> people, DateTime now, CancellationToken cancellationToken = default)
    {
        var adminRole = new SecurityRole
        {
            Guid = _adminSecurityRoleGuid,
            Name = "Admin",
            Description = "Full administrative access",
            IsSystemRole = true,
            IsActive = true,
            CreatedDateTime = now
        };

        _context.SecurityRoles.Add(adminRole);
        await _context.SaveChangesAsync(cancellationToken);

        // Seed the "security:manage" claim used by the Security Roles admin UI (#497)
        // and link it to the Admin role so admins can manage roles and permissions.
        var securityManageClaimGuid = Guid.Parse("A1B2C3D4-E5F6-4A1B-8C9D-AAAAAAAAAAAA");
        var securityManageClaim = await _context.SecurityClaims
            .FirstOrDefaultAsync(c => c.Guid == securityManageClaimGuid, cancellationToken);
        if (securityManageClaim is null)
        {
            securityManageClaim = new SecurityClaim
            {
                Guid = securityManageClaimGuid,
                ClaimType = "security",
                ClaimValue = "manage",
                Description = "Manage security roles, claims, and role memberships",
                CreatedDateTime = now
            };
            _context.SecurityClaims.Add(securityManageClaim);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _context.RoleSecurityClaims.Add(new RoleSecurityClaim
        {
            SecurityRoleId = adminRole.Id,
            SecurityClaimId = securityManageClaim.Id,
            AllowOrDeny = 'A',
            CreatedDateTime = now
        });
        await _context.SaveChangesAsync(cancellationToken);

        // The "Financial Admin" role and financial:* claims are provisioned by the
        // SeedFinancialClaims migration, but ResetDatabaseAsync truncates that data
        // ahead of every seed. Re-provision both here so the role row (with its
        // canonical GUID from production) and its claim links round-trip across
        // reset+seed deterministically. This preserves the production role
        // separation (Admin != Financial Admin) — we just give the seeded admin
        // user BOTH roles below so giving endpoints are reachable in tests/dev.
        var financialAdminRole = await EnsureFinancialAdminRoleAsync(now, cancellationToken);

        // Assign Admin AND Financial Admin roles to John Smith (the E2E test admin user).
        // Matches production semantics: to exercise /api/v1/giving/* an admin must
        // also hold the Financial Admin role.
        var johnSmith = people.First(p => p.Email == "john.smith@example.com");

        _context.PersonSecurityRoles.Add(new PersonSecurityRole
        {
            PersonId = johnSmith.Id,
            SecurityRoleId = adminRole.Id,
            CreatedDateTime = now
        });

        _context.PersonSecurityRoles.Add(new PersonSecurityRole
        {
            PersonId = johnSmith.Id,
            SecurityRoleId = financialAdminRole.Id,
            CreatedDateTime = now
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Ensures the "Financial Admin" SecurityRole and its three financial SecurityClaims
    /// exist (re-seeding what ResetDatabaseAsync truncated from the SeedFinancialClaims
    /// migration) and that the role is linked (ALLOW) to all three claims. The GUIDs
    /// below intentionally match the production migration so prod and dev/test share
    /// a single definition of the Financial Admin role.
    /// </summary>
    private async Task<SecurityRole> EnsureFinancialAdminRoleAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        // GUIDs match 20260103165412_SeedFinancialClaims.cs so data round-trips
        // identically whether provisioned by the migration (prod) or by this
        // seeder (tests/dev) after a reset.
        var financialViewClaimGuid = Guid.Parse("A1B2C3D4-E5F6-4A1B-8C9D-111111111111");
        var financialEditClaimGuid = Guid.Parse("A1B2C3D4-E5F6-4A1B-8C9D-222222222222");
        var financialBatchCloseClaimGuid = Guid.Parse("A1B2C3D4-E5F6-4A1B-8C9D-333333333333");
        var financialViewRscGuid = Guid.Parse("11111111-1111-4AAA-BBBB-111111111111");
        var financialEditRscGuid = Guid.Parse("22222222-2222-4AAA-BBBB-222222222222");
        var financialBatchCloseRscGuid = Guid.Parse("33333333-3333-4AAA-BBBB-333333333333");

        // 1. Ensure the Financial Admin role exists.
        var financialAdminRole = await _context.SecurityRoles
            .FirstOrDefaultAsync(r => r.Guid == _financialAdminSecurityRoleGuid, cancellationToken);

        if (financialAdminRole is null)
        {
            financialAdminRole = new SecurityRole
            {
                Guid = _financialAdminSecurityRoleGuid,
                Name = "Financial Admin",
                Description = "Administrator role for financial operations",
                IsSystemRole = true,
                IsActive = true,
                CreatedDateTime = now
            };
            _context.SecurityRoles.Add(financialAdminRole);
            await _context.SaveChangesAsync(cancellationToken);
        }

        // 2. Ensure the three financial claims exist.
        var claimSpecs = new (Guid Guid, string Value, string Description)[]
        {
            (financialViewClaimGuid, "view", "View financial data including contributions and batches"),
            (financialEditClaimGuid, "edit", "Create and edit contributions and batches"),
            (financialBatchCloseClaimGuid, "batch.close", "Close contribution batches")
        };

        var claimGuids = claimSpecs.Select(s => s.Guid).ToList();
        var existingClaims = await _context.SecurityClaims
            .Where(c => claimGuids.Contains(c.Guid))
            .ToDictionaryAsync(c => c.Guid, cancellationToken);

        foreach (var (guid, value, description) in claimSpecs)
        {
            if (!existingClaims.ContainsKey(guid))
            {
                var claim = new SecurityClaim
                {
                    Guid = guid,
                    ClaimType = "financial",
                    ClaimValue = value,
                    Description = description,
                    CreatedDateTime = now
                };
                _context.SecurityClaims.Add(claim);
                existingClaims[guid] = claim;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);

        // 3. Ensure the role -> claim links exist (ALLOW).
        var rscSpecs = new (Guid RscGuid, Guid ClaimGuid)[]
        {
            (financialViewRscGuid, financialViewClaimGuid),
            (financialEditRscGuid, financialEditClaimGuid),
            (financialBatchCloseRscGuid, financialBatchCloseClaimGuid)
        };

        foreach (var (rscGuid, claimGuid) in rscSpecs)
        {
            var claim = existingClaims[claimGuid];
            var alreadyLinked = await _context.RoleSecurityClaims
                .AnyAsync(
                    r => r.SecurityRoleId == financialAdminRole.Id && r.SecurityClaimId == claim.Id,
                    cancellationToken);
            if (alreadyLinked)
            {
                continue;
            }

            _context.RoleSecurityClaims.Add(new RoleSecurityClaim
            {
                Guid = rscGuid,
                SecurityRoleId = financialAdminRole.Id,
                SecurityClaimId = claim.Id,
                AllowOrDeny = 'A',
                CreatedDateTime = now
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return financialAdminRole;
    }

    /// <summary>
    /// Seeds a default General Fund so the Contribution entry form has at least one
    /// selectable fund. Uses a fixed GUID for deterministic test targeting.
    /// </summary>
    private async Task SeedFundsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Funds.AnyAsync(f => f.Guid == _generalFundGuid, cancellationToken);
        if (exists)
        {
            return;
        }

        var generalFund = new Fund
        {
            Guid = _generalFundGuid,
            Name = "General Fund",
            PublicName = "General Fund",
            Description = "Default fund for undesignated contributions and tithes.",
            IsActive = true,
            IsPublic = true,
            IsTaxDeductible = true,
            Order = 0,
            CreatedDateTime = now
        };

        _context.Funds.Add(generalFund);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private Task SeedCheckinGroupsAsync(int checkinGroupTypeId, int memberRoleId, int scheduleId, DateTime now, CancellationToken cancellationToken = default)
    {
        // Nursery group (ages 0-2)
        var nursery = new Group
        {
            Guid = _nurseryGroupGuid,
            GroupTypeId = checkinGroupTypeId,
            Name = "Nursery",
            Description = "Nursery for infants and toddlers",
            IsSystem = false,
            IsActive = true,
            GroupCapacity = 15,
            MinAgeMonths = 0,
            MaxAgeMonths = 24,
            ScheduleId = scheduleId,
            CreatedDateTime = now
        };

        // Preschool group (ages 3-5)
        var preschool = new Group
        {
            Guid = _preschoolGroupGuid,
            GroupTypeId = checkinGroupTypeId,
            Name = "Preschool",
            Description = "Preschool ministry for ages 3-5",
            IsSystem = false,
            IsActive = true,
            GroupCapacity = 20,
            MinAgeMonths = 36,
            MaxAgeMonths = 71,
            ScheduleId = scheduleId,
            CreatedDateTime = now
        };

        // Elementary group (grades K-5)
        var elementary = new Group
        {
            Guid = _elementaryGroupGuid,
            GroupTypeId = checkinGroupTypeId,
            Name = "Elementary",
            Description = "Elementary ministry for grades K-5",
            IsSystem = false,
            IsActive = true,
            GroupCapacity = 30,
            MinGrade = 0,  // Kindergarten
            MaxGrade = 5,  // 5th grade
            ScheduleId = scheduleId,
            CreatedDateTime = now
        };

        // Batch add all check-in groups
        _context.Groups.AddRange(nursery, preschool, elementary);
        // Note: SaveChangesAsync is called in SeedAsync() after this method returns
        return Task.CompletedTask;
    }
}
