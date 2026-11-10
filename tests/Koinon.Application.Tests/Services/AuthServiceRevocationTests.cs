using FluentAssertions;
using Koinon.Application.DTOs.Auth;
using Koinon.Application.Services;
using Koinon.Domain.Entities;
using Koinon.Domain.Enums;
using Koinon.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Koinon.Application.Tests.Services;

/// <summary>
/// Regression tests for issue #712: AuthService.RefreshTokenAsync and
/// LogoutAsync loaded refresh tokens under the global QueryTrackingBehavior
/// .NoTracking default, so RevokedAt mutations were silently dropped —
/// token rotation never revoked the old token and logout was a server-side
/// no-op.
///
/// Mirrors the NoTrackingSweepTests convention: InMemory KoinonDbContext
/// with NoTracking (as in production), mutate via the service, Clear() the
/// change tracker, then reload via AsNoTracking and assert the revocation
/// actually reached the database. These tests fail against pre-fix code.
/// </summary>
public class AuthServiceRevocationTests
{
    private static KoinonDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<KoinonDbContext>()
            .UseInMemoryDatabase(databaseName: $"AuthRevocation_{Guid.NewGuid()}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var ctx = new KoinonDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-key-that-is-at-least-32-characters-long-for-testing",
                ["Jwt:Issuer"] = "Koinon.Api.Test",
                ["Jwt:Audience"] = "Koinon.Web.Test",
                ["Jwt:AccessTokenExpirationMinutes"] = "15",
                ["Jwt:RefreshTokenExpirationDays"] = "7"
            })
            .Build();

    private static AuthService CreateService(KoinonDbContext context) =>
        new(context, CreateConfiguration(), Mock.Of<ILogger<AuthService>>());

    // RefreshTokenAsync validates format before any DB lookup: the token
    // must be base64 decoding to exactly 64 bytes, like real tokens are.
    private static readonly string _oldToken =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));

    private static async Task<KoinonDbContext> SeedActiveTokenAsync()
    {
        var context = CreateContext();
        context.People.Add(new Person
        {
            Id = 1,
            FirstName = "Jane",
            LastName = "Doe",
            Email = "jane@example.com",
            Gender = Gender.Female,
            CreatedDateTime = DateTime.UtcNow
        });
        context.RefreshTokens.Add(new RefreshToken
        {
            PersonId = 1,
            Token = _oldToken,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = "127.0.0.1"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    // -----------------------------------------------------------------------
    // RefreshTokenAsync — rotation must actually revoke the old token
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshTokenAsync_UnderNoTracking_PersistsRevocationToDatabase()
    {
        using var context = await SeedActiveTokenAsync();
        var service = CreateService(context);

        var result = await service.RefreshTokenAsync(_oldToken, "10.0.0.1");

        result.Should().NotBeNull("the token is active and unexpired");

        context.ChangeTracker.Clear();
        var reloaded = await context.RefreshTokens
            .AsNoTracking()
            .SingleAsync(rt => rt.Token == _oldToken);

        reloaded.RevokedAt.Should().NotBeNull(
            "rotation must persist RevokedAt; pre-fix the mutation was silently dropped under NoTracking (issue #712).");
        reloaded.RevokedByIp.Should().Be("10.0.0.1");
        reloaded.ReplacedByToken.Should().Be(result!.RefreshToken,
            "the replacement pointer must persist for audit/detection of token reuse.");
    }

    [Fact]
    public async Task RefreshTokenAsync_SecondUseOfRotatedToken_IsRejected()
    {
        using var context = await SeedActiveTokenAsync();
        var service = CreateService(context);

        var first = await service.RefreshTokenAsync(_oldToken, "10.0.0.1");
        first.Should().NotBeNull();

        var second = await service.RefreshTokenAsync(_oldToken, "10.0.0.2");

        second.Should().BeNull(
            "a rotated (revoked) token must not refresh again; pre-fix the revoked state never persisted, so a stolen token stayed valid forever.");
    }

    // -----------------------------------------------------------------------
    // LogoutAsync — logout must actually revoke the token
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LogoutAsync_UnderNoTracking_PersistsRevocationToDatabase()
    {
        using var context = await SeedActiveTokenAsync();
        var service = CreateService(context);

        var loggedOut = await service.LogoutAsync(_oldToken, "10.0.0.3");

        loggedOut.Should().BeTrue();

        context.ChangeTracker.Clear();
        var reloaded = await context.RefreshTokens
            .AsNoTracking()
            .SingleAsync(rt => rt.Token == _oldToken);

        reloaded.RevokedAt.Should().NotBeNull(
            "logout must persist RevokedAt; pre-fix logout was a silent server-side no-op (issue #712).");
        reloaded.RevokedByIp.Should().Be("10.0.0.3");
    }

    [Fact]
    public async Task RefreshTokenAsync_AfterLogout_IsRejected()
    {
        using var context = await SeedActiveTokenAsync();
        var service = CreateService(context);

        await service.LogoutAsync(_oldToken, "10.0.0.3");

        var refresh = await service.RefreshTokenAsync(_oldToken, "10.0.0.4");

        refresh.Should().BeNull(
            "a logged-out token must be rejected on refresh (API surfaces this as 401).");
    }
}
