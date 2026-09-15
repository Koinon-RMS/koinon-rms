using FluentAssertions;
using Koinon.Application.Interfaces;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Koinon.TestDataSeeder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Koinon.Infrastructure.Tests;

/// <summary>
/// #710: persistence evidence for the seeder's authorized-pickup insert.
/// Invokes the production DataSeeder.SeedAuthorizedPickupAsync through the
/// actual KoinonDbContext (no test subclass, no InMemory provider) against a
/// unique file-backed SQLite database with foreign keys enabled. The context
/// and connection are disposed and the file reopened between operations, so
/// every assertion is made against durably persisted rows, not tracked state.
/// </summary>
public class AuthorizedPickupSeederTests
{
    // Same fixed GUIDs the seeder uses for the Smith family.
    private static readonly Guid SmithFamilyGuid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JaneSmithGuid = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid JohnnySmithGuid = new("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task SeedAuthorizedPickupAsync_persists_exactly_one_active_parent_always_row()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var now = DateTime.UtcNow;
            int janeId;
            int johnnyId;

            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                context.Database.EnsureCreated();
                (janeId, johnnyId) = await SeedFixturesAsync(context, now);

                var seeder = CreateSeeder(context);
                await seeder.SeedAuthorizedPickupAsync(now);
            }

            // Dispose and reopen: assert against the file on disk.
            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                var rows = await context.AuthorizedPickups
                    .Include(ap => ap.ChildPerson)
                    .Include(ap => ap.AuthorizedPerson)
                    .ToListAsync();

                rows.Should().HaveCount(1);
                var row = rows.Single();
                row.Relationship.Should().Be(PickupRelationship.Parent);
                row.AuthorizationLevel.Should().Be(AuthorizationLevel.Always);
                row.IsActive.Should().BeTrue();
                row.ChildPersonId.Should().Be(johnnyId);
                row.AuthorizedPersonId.Should().Be(janeId);
                row.ChildPerson.Should().NotBeNull();
                row.ChildPerson!.Guid.Should().Be(JohnnySmithGuid);
                row.AuthorizedPerson.Should().NotBeNull();
                row.AuthorizedPerson!.Guid.Should().Be(JaneSmithGuid);
            }
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SeedAuthorizedPickupAsync_repeated_invocation_creates_no_duplicate()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var now = DateTime.UtcNow;

            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                context.Database.EnsureCreated();
                await SeedFixturesAsync(context, now);

                var seeder = CreateSeeder(context);
                await seeder.SeedAuthorizedPickupAsync(now);
            }

            // Second invocation through a fresh context on the same database.
            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                var seeder = CreateSeeder(context);
                await seeder.SeedAuthorizedPickupAsync(now);
            }

            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                var rows = await context.AuthorizedPickups
                    .AsNoTracking()
                    .ToListAsync();

                rows.Should().HaveCount(1);
                rows.Single().Relationship.Should().Be(PickupRelationship.Parent);
                rows.Single().AuthorizationLevel.Should().Be(AuthorizationLevel.Always);
                rows.Single().IsActive.Should().BeTrue();
            }
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task SeedAuthorizedPickupAsync_does_not_resurrect_revoked_authorization()
    {
        var dbPath = NewTempDbPath();
        try
        {
            var now = DateTime.UtcNow;

            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                context.Database.EnsureCreated();
                var (janeId, johnnyId) = await SeedFixturesAsync(context, now);

                // A previously granted authorization that was later revoked
                // (soft-deleted). The guard must treat any existing row for the
                // pair as authoritative and not re-insert an active one.
                // Note: the fixture itself must use the insert-then-update
                // pattern — an initializer-set Parent (0) would be persisted
                // as Other (7) by the sentinel behavior under test, and the
                // guard would then (correctly) not match it.
                var revoked = new AuthorizedPickup
                {
                    ChildPersonId = johnnyId,
                    AuthorizedPersonId = janeId,
                    Relationship = PickupRelationship.Other,
                    AuthorizationLevel = AuthorizationLevel.EmergencyOnly,
                    IsActive = true,
                    CreatedDateTime = now
                };
                context.AuthorizedPickups.Add(revoked);
                await context.SaveChangesAsync();
                revoked.Relationship = PickupRelationship.Parent;
                revoked.AuthorizationLevel = AuthorizationLevel.Always;
                revoked.IsActive = false;
                await context.SaveChangesAsync();

                var seeder = CreateSeeder(context);
                await seeder.SeedAuthorizedPickupAsync(now);
            }

            using (var connection = OpenForeignKeyedConnection(dbPath))
            using (var context = CreateContext(connection))
            {
                var rows = await context.AuthorizedPickups
                    .AsNoTracking()
                    .ToListAsync();

                rows.Should().HaveCount(1);
                rows.Single().IsActive.Should().BeFalse();
            }
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static string NewTempDbPath()
    {
        // Unique test-owned temp file per test run; never a shared database.
        return Path.Combine(
            Path.GetTempPath(),
            $"koinon-authorized-pickup-{Guid.NewGuid():N}.db");
    }

    private static SqliteConnection OpenForeignKeyedConnection(string dbPath)
    {
        // Microsoft.Data.Sqlite disables foreign keys by default; the card
        // requires them ON, so enforce the pragma on every connection we open.
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static KoinonDbContext CreateContext(SqliteConnection connection)
    {
        // The actual production context. PostgreSQL-only model extras are
        // gated on the Npgsql provider name inside KoinonDbContext, so they do
        // not apply under SQLite and no test subclass is needed.
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseSqlite(connection)
            .Options;
        return new KoinonDbContext(options);
    }

    private static DataSeeder CreateSeeder(KoinonDbContext context)
    {
        // The auth service participates only in the full SeedAsync path
        // (password hashing); SeedAuthorizedPickupAsync never calls it.
        return new DataSeeder(
            context,
            NullLogger<DataSeeder>.Instance,
            new Mock<IAuthService>().Object);
    }

    private static async Task<(int JaneId, int JohnnyId)> SeedFixturesAsync(
        KoinonDbContext context, DateTime now)
    {
        var family = new Family
        {
            Guid = SmithFamilyGuid,
            Name = "Smith Family",
            IsActive = true,
            CreatedDateTime = now
        };
        context.Families.Add(family);

        var jane = new Person
        {
            Guid = JaneSmithGuid,
            FirstName = "Jane",
            LastName = "Smith",
            Gender = Gender.Female,
            CreatedDateTime = now
        };
        var johnny = new Person
        {
            Guid = JohnnySmithGuid,
            FirstName = "Johnny",
            LastName = "Smith",
            Gender = Gender.Male,
            CreatedDateTime = now
        };
        context.People.AddRange(jane, johnny);
        await context.SaveChangesAsync();

        return (jane.Id, johnny.Id);
    }

    private static void TryDelete(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch (IOException)
        {
            // Temp file cleanup is best-effort; never fail a green test on it.
        }
    }
}
