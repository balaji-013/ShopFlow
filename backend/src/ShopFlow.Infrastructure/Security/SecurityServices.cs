using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ShopFlow.Application.Abstractions;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Infrastructure.Security;

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "shopflow";
    public string Audience { get; set; } = "shopflow-web";
    /// <summary>HS256 secret, >= 32 bytes. Comes from user-secrets / env var / Key Vault - NEVER from source control.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;     // short: a stolen access token expires quickly
    public int RefreshTokenDays { get; set; } = 7;
}

public class WebhookOptions { public const string Section = "Webhooks"; public string Secret { get; set; } = ""; }

/// <summary>
/// PBKDF2-HMAC-SHA512 (ASP.NET Core Identity v3 format: random per-password salt + iteration count embedded in the hash).
/// Deliberately slow, salted, one-way. NOT SHA256/MD5 (too fast to brute-force) and NOT encryption (reversible).
/// </summary>
public class Pbkdf2PasswordHasher : Application.Abstractions.IPasswordHasher
{
    private readonly PasswordHasher<object> _inner = new();
    public string Hash(string password) => _inner.HashPassword(null!, password);
    public bool Verify(string hash, string password, out bool needsRehash)
    {
        var r = _inner.VerifyHashedPassword(null!, hash, password);
        needsRehash = r == PasswordVerificationResult.SuccessRehashNeeded;
        return r != PasswordVerificationResult.Failed;
    }
}

public class TokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _o = options.Value;
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_o.RefreshTokenDays);

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("tenant_id", user.TenantId.ToString()),
            new Claim("role", user.Role.ToString()),   // literal "role": the JwtSecurityToken ctor does NOT apply the legacy outbound claim map
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var token = new JwtSecurityToken(_o.Issuer, _o.Audience, claims, now, now.AddMinutes(_o.AccessTokenMinutes),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), _o.AccessTokenMinutes * 60);
    }

    /// <summary>256 bits from a CSPRNG. The raw value goes to the client (httpOnly cookie); only its SHA-256 is stored.</summary>
    public (string Raw, string Hash) NewRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (raw, HashToken(raw));
    }

    // Plain SHA-256 is right HERE (unlike passwords): the input is already 256 bits of randomness, so there is nothing to brute-force.
    public string HashToken(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

/// <summary>Stripe-style signature:  "t=<unix>,v1=<hex(HMAC_SHA256(secret, t + '.' + body))>". Timestamp blocks replay of old captures.</summary>
public class HmacWebhookSignatureVerifier(IOptions<WebhookOptions> options, TimeProvider clock) : IWebhookSignatureVerifier
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public string Sign(string rawBody, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={Compute(t, rawBody)}";
    }

    public bool IsValid(string rawBody, string signatureHeader)
    {
        string? t = null, v1 = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t") t = kv[1]; else if (kv[0] == "v1") v1 = kv[1];
        }
        if (!long.TryParse(t, out var ts) || v1 is null) return false;
        if ((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > Tolerance) return false;

        var expected = Encoding.ASCII.GetBytes(Compute(ts, rawBody));
        var given = Encoding.ASCII.GetBytes(v1);
        return CryptographicOperations.FixedTimeEquals(expected, given);   // constant-time: no timing side channel
    }

    private string Compute(long t, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.Secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();
    }
}

