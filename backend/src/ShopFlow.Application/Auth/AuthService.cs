using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Common;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Application.Auth;

public record RegisterRequest(string Email, string Password, string FullName, string TenantCode);
public record LoginRequest(string Email, string Password);
public record UserDto(Guid Id, string Email, string FullName, string Role);
public record AuthResult(string AccessToken, int ExpiresIn, string RefreshToken, UserDto User);

public class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.TenantCode).NotEmpty().MaximumLength(50);
        // NIST guidance: length beats composition rules. No "must contain a symbol" theatre.
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).MaximumLength(128);
    }
}

public class AuthService(
    IAppDbContext db, IPasswordHasher hasher, ITokenService tokens, TimeProvider clock,
    IValidator<RegisterRequest> registerValidator, ILogger<AuthService> log)
{
    // Hash verified against this when the e-mail is unknown, so "unknown user" and "wrong password" take the same time
    // (prevents user enumeration through response timing).
    private readonly string _dummyHash = hasher.Hash("not-a-real-password");

    public async Task<AuthResult> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        await registerValidator.ValidateAndThrowAsync(req, ct);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Code == req.TenantCode.ToLower(), ct)
                     ?? throw new NotFoundException("Tenant");
        var email = req.Email.Trim().ToLowerInvariant();

        // Friendly check; the UNIQUE index on Users.Email is the real guard against a race between two registrations.
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("That e-mail is already registered.", "email_taken");

        var user = new User(tenant.Id, email, req.FullName.Trim(), hasher.Hash(req.Password), Role.Customer);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("That e-mail is already registered.", "email_taken"); }

        return await IssueAsync(user, Guid.NewGuid(), ct);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        var ok = hasher.Verify(user?.PasswordHash ?? _dummyHash, req.Password ?? "", out var needsRehash);
        if (user is null || !ok) throw new UnauthorizedAppException();   // identical message either way

        if (needsRehash) { user.SetPasswordHash(hasher.Hash(req.Password!)); await db.SaveChangesAsync(ct); }
        return await IssueAsync(user, Guid.NewGuid(), ct);
    }

    /// <summary>
    /// REFRESH TOKEN ROTATION: every refresh returns a NEW refresh token and burns the old one.
    /// If an already-used token shows up again, someone copied it => revoke the entire family (all devices of that login chain).
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) throw new UnauthorizedAppException("Missing refresh token.");
        var hash = tokens.HashToken(rawToken);
        var now = clock.GetUtcNow().UtcDateTime;

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                     ?? throw new UnauthorizedAppException("Invalid refresh token.");

        if (stored.UsedAt is not null || stored.RevokedAt is not null)
        {
            log.LogWarning("Refresh token reuse detected for user {UserId}; revoking family {FamilyId}", stored.UserId, stored.FamilyId);
            await db.RefreshTokens.Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            throw new UnauthorizedAppException("Refresh token was already used.");
        }
        if (stored.IsExpired(now)) throw new UnauthorizedAppException("Refresh token expired.");

        // Atomic claim: only ONE concurrent request can flip UsedAt from NULL. (Two tabs refreshing at once: the loser is rejected.)
        var claimed = await db.RefreshTokens.Where(t => t.Id == stored.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (claimed == 0) throw new UnauthorizedAppException("Refresh token was already used.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct)
                   ?? throw new UnauthorizedAppException();
        return await IssueAsync(user, stored.FamilyId, ct);
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken)) return;
        var hash = tokens.HashToken(rawToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens.Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<AuthResult> IssueAsync(User user, Guid familyId, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user);
        var (raw, hash) = tokens.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken(user.Id, familyId, hash, clock.GetUtcNow().UtcDateTime + tokens.RefreshTokenLifetime));
        await db.SaveChangesAsync(ct);
        return new AuthResult(access.Value, access.ExpiresInSeconds, raw, new UserDto(user.Id, user.Email, user.FullName, user.Role.ToString()));
    }
}
