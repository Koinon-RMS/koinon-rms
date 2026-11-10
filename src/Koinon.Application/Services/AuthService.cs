using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Koinon.Application.Constants;
using Koinon.Application.DTOs;
using Koinon.Application.DTOs.Auth;
using Koinon.Application.Interfaces;
using Koinon.Domain.Data;
using Koinon.Domain.Entities;
using Konscious.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Koinon.Application.Services;

/// <summary>
/// Service for authentication operations including login, token refresh, and logout.
/// Uses JWT for access tokens and database-stored refresh tokens for security.
/// Passwords are hashed using Argon2id for maximum security.
/// </summary>
public class AuthService(
    IApplicationDbContext context,
    IConfiguration configuration,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly string _jwtSecret = configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("JWT Secret not configured");
    private readonly string _jwtIssuer = configuration["Jwt:Issuer"] ?? "Koinon.Api";
    private readonly string _jwtAudience = configuration["Jwt:Audience"] ?? "Koinon.Web";
    private readonly int _accessTokenExpirationMinutes = int.Parse(configuration["Jwt:AccessTokenExpirationMinutes"] ?? "15");
    private readonly int _refreshTokenExpirationDays = int.Parse(configuration["Jwt:RefreshTokenExpirationDays"] ?? "7");

    public async Task<TokenResponse?> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken ct = default)
    {
        logger.LogInformation("Login attempt for email: {Email}", request.Email);

        // Find person by email
        var person = await context.People
            .Include(p => p.ConnectionStatusValue)
            .Include(p => p.RecordStatusValue)
            .Include(p => p.Photo)
            .FirstOrDefaultAsync(p => p.Email == request.Email, ct);

        if (person == null)
        {
            logger.LogWarning("Login failed: Person not found for email {Email}", request.Email);
            return null;
        }

        // Verify password
        if (!await ValidatePasswordAsync(person, request.Password, ct))
        {
            // Add jitter to prevent timing attacks (100-300ms random delay)
            await Task.Delay(Random.Shared.Next(100, 300), ct);
            logger.LogWarning("Login failed: Invalid credentials");
            return null;
        }

        // Check if person is active
        if (person.RecordStatusValueId.HasValue)
        {
            var recordStatus = person.RecordStatusValue?.Value;
            if (recordStatus != null && !recordStatus.Equals("Active", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Login failed: Person {PersonId} has inactive record status: {Status}",
                    person.Id, recordStatus);
                return null;
            }
        }

        logger.LogInformation("Login successful for person {PersonId}", person.Id);

        // Generate tokens
        return await GenerateTokenResponseAsync(person, ipAddress, ct);
    }

    public async Task<TokenResponse?> RefreshTokenAsync(string refreshToken, string? ipAddress, CancellationToken ct = default)
    {
        logger.LogInformation("Refresh token attempt from IP: {IP}", ipAddress ?? "unknown");

        // Validate token format before database query
        if (string.IsNullOrWhiteSpace(refreshToken) || !IsValidBase64(refreshToken))
        {
            logger.LogWarning("Refresh token attempt failed: Invalid token format");
            return null;
        }

        // Find refresh token (AsTracking: revocation mutations must persist
        // under the global NoTracking default — issue #712)
        var token = await context.RefreshTokens
            .AsTracking()
            .Include(rt => rt.Person)
                .ThenInclude(p => p!.ConnectionStatusValue)
            .Include(rt => rt.Person)
                .ThenInclude(p => p!.RecordStatusValue)
            .Include(rt => rt.Person)
                .ThenInclude(p => p!.Photo)
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken, ct);

        if (token == null || !token.IsActive)
        {
            logger.LogWarning("Refresh token attempt failed: Invalid or expired token");
            return null;
        }

        // Revoke old token and generate new one (token rotation)
        var newRefreshToken = GenerateRefreshTokenString();
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedByIp = ipAddress;
        token.ReplacedByToken = newRefreshToken;

        logger.LogInformation("Refresh token rotated for person {PersonId}", token.PersonId);

        await context.SaveChangesAsync(ct);

        // Generate new token response
        return await GenerateTokenResponseAsync(token.Person!, ipAddress, ct, newRefreshToken);
    }

    public async Task<bool> LogoutAsync(string refreshToken, string? ipAddress, CancellationToken ct = default)
    {
        logger.LogInformation("Logout attempt");

        // AsTracking: the revocation mutation must persist under the global
        // NoTracking default — issue #712 (logout was a silent no-op)
        var token = await context.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(rt => rt.Token == refreshToken, ct);

        if (token == null)
        {
            logger.LogWarning("Logout failed: Refresh token not found");
            return false;
        }

        if (!token.IsActive)
        {
            logger.LogWarning("Logout failed: Refresh token already revoked");
            return false;
        }

        // Revoke the token
        token.RevokedAt = DateTime.UtcNow;
        token.RevokedByIp = ipAddress;

        await context.SaveChangesAsync(ct);

        logger.LogInformation("Logout successful for person {PersonId}", token.PersonId);
        return true;
    }

    /// <summary>
    /// Validates a password against a person's stored password hash.
    /// Uses Argon2id for secure password verification with constant-time comparison.
    /// </summary>
    private async Task<bool> ValidatePasswordAsync(Person person, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(person.PasswordHash))
        {
            return false;
        }

        return await VerifyPasswordAsync(password, person.PasswordHash, ct);
    }

    /// <summary>
    /// Verifies a password against a stored hash.
    /// Uses Argon2id for secure password verification with constant-time comparison.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string password, string storedHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        try
        {
            var combined = Convert.FromBase64String(storedHash);
            if (combined.Length < 48) // 16 salt + 32 hash minimum
            {
                return false;
            }

            var salt = new byte[16];
            var hash = new byte[combined.Length - 16];
            Buffer.BlockCopy(combined, 0, salt, 0, 16);
            Buffer.BlockCopy(combined, 16, hash, 0, combined.Length - 16);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                DegreeOfParallelism = 8,
                Iterations = 4,
                MemorySize = 128 * 1024
            };

            var computedHash = await argon2.GetBytesAsync(32);
            return CryptographicOperations.FixedTimeEquals(hash, computedHash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Password verification error");
            return false;
        }
    }

    /// <summary>
    /// Hashes a password using Argon2id for secure storage.
    /// </summary>
    public async Task<string> HashPasswordAsync(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 8,
            Iterations = 4,
            MemorySize = 128 * 1024 // 128 MB
        };

        var hash = await argon2.GetBytesAsync(32);

        // Combine salt and hash for storage
        var combined = new byte[salt.Length + hash.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(hash, 0, combined, salt.Length, hash.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Generates a complete token response with access token and refresh token.
    /// </summary>
    private async Task<TokenResponse> GenerateTokenResponseAsync(
        Person person,
        string? ipAddress,
        CancellationToken ct,
        string? existingRefreshToken = null)
    {
        var accessToken = await GenerateAccessTokenAsync(person, ct);
        var refreshToken = existingRefreshToken ?? GenerateRefreshTokenString();
        var expiresAt = DateTime.UtcNow.AddMinutes(_accessTokenExpirationMinutes);

        // Store refresh token if it's new
        if (existingRefreshToken == null)
        {
            var refreshTokenEntity = new RefreshToken
            {
                PersonId = person.Id,
                Token = refreshToken,
                ExpiresAt = DateTime.UtcNow.AddDays(_refreshTokenExpirationDays),
                CreatedByIp = ipAddress
            };

            context.RefreshTokens.Add(refreshTokenEntity);
            await context.SaveChangesAsync(ct);
        }

        // Create user summary
        var userDto = new PersonSummaryDto
        {
            IdKey = IdKeyHelper.Encode(person.Id),
            FirstName = person.FirstName,
            NickName = person.NickName,
            LastName = person.LastName,
            FullName = person.FullName,
            Email = person.Email,
            PhotoUrl = person.Photo != null ? ApiPaths.GetFileUrl(person.Photo.IdKey) : null,
            Age = CalculateAge(person.BirthDate),
            Gender = person.Gender.ToString(),
            ConnectionStatus = person.ConnectionStatusValue != null
                ? new DefinedValueDto
                {
                    IdKey = IdKeyHelper.Encode(person.ConnectionStatusValue.Id),
                    Guid = person.ConnectionStatusValue.Guid,
                    Value = person.ConnectionStatusValue.Value,
                    Description = person.ConnectionStatusValue.Description,
                    IsActive = person.ConnectionStatusValue.IsActive,
                    Order = person.ConnectionStatusValue.Order
                }
                : null,
            RecordStatus = person.RecordStatusValue != null
                ? new DefinedValueDto
                {
                    IdKey = IdKeyHelper.Encode(person.RecordStatusValue.Id),
                    Guid = person.RecordStatusValue.Guid,
                    Value = person.RecordStatusValue.Value,
                    Description = person.RecordStatusValue.Description,
                    IsActive = person.RecordStatusValue.IsActive,
                    Order = person.RecordStatusValue.Order
                }
                : null
        };

        return new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt,
            User: userDto
        );
    }

    /// <summary>
    /// Generates a JWT access token for a person, including their security roles.
    /// </summary>
    private async Task<string> GenerateAccessTokenAsync(Person person, CancellationToken ct = default)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, person.Id.ToString()),
            new(ClaimTypes.Email, person.Email ?? string.Empty),
            new(ClaimTypes.Name, person.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("idKey", IdKeyHelper.Encode(person.Id)),
            new("person_id", person.Id.ToString())
        };

        // Add security role claims
        var roleNames = await context.PersonSecurityRoles
            .Where(psr => psr.PersonId == person.Id
                && (!psr.ExpiresDateTime.HasValue || psr.ExpiresDateTime > DateTime.UtcNow))
            .Include(psr => psr.SecurityRole)
            .Where(psr => psr.SecurityRole.IsActive)
            .Select(psr => psr.SecurityRole.Name)
            .ToListAsync(ct);

        foreach (var roleName in roleNames)
        {
            claims.Add(new Claim(ClaimTypes.Role, roleName));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_accessTokenExpirationMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generates a cryptographically secure random refresh token string.
    /// </summary>
    private static string GenerateRefreshTokenString()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Validates that a string is a properly formatted Base64-encoded refresh token.
    /// </summary>
    private static bool IsValidBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 64; // Expected size for our tokens
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calculates age from birth date.
    /// </summary>
    private static int? CalculateAge(DateOnly? birthDate)
    {
        if (!birthDate.HasValue)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - birthDate.Value.Year;

        if (birthDate.Value.AddYears(age) > today)
        {
            age--;
        }

        return age;
    }
}
